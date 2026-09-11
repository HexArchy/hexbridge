using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using HexBridge.Localization;

namespace HexBridge;

/// <summary>
/// The shared transport and the host for everything built on it: one socket, one key, one
/// session, and a list of features that each claim a few packet types.
///
/// The host knows nothing about audio or gamepads. Both front ends (console and desktop
/// app) drive this and differ only in how they render <see cref="Updated"/> and
/// <see cref="Log"/>.
///
/// <para>
/// It runs in either <see cref="BridgeRole"/>. The difference is small and entirely here:
/// which direction packets are sealed with, whether the socket waits on a known port or
/// dials an address on an ephemeral one, and who answers HELLO with PONG. Everything above
/// this class — the features, the pages, the wizard — is written once and works both ways.
/// </para>
/// </summary>
public sealed class ReceiverService : IAsyncDisposable
{
    /// <summary>How often the observable snapshot is refreshed. Deliberately not per packet.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Silence longer than this means the sender is gone rather than between frames.</summary>
    private static readonly TimeSpan SenderTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A HELLO stamp further from our clock than this says the two machines disagree about
    /// the time, not that the network is that slow — better to report nothing than a lie.
    /// </summary>
    private const double MaxPlausibleDelayMs = 1000;

    private readonly object _gate = new();
    private readonly IFeature[] _features;
    private Run? _run;
    private ReceiverSnapshot _snapshot = new();
    private int _muted;

    /// <summary>Fires from background threads; front ends marshal as they need.</summary>
    public event Action<LogEntry>? Log;

    /// <summary>Fires on the tick timer while running, at most ten times a second.</summary>
    public event Action<ReceiverSnapshot>? Updated;

    /// <summary>Every feature this host was built with, enabled or not, in registration order.</summary>
    public IReadOnlyList<IFeature> Features => _features;

    public ReceiverSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Mute, as the wire sees it: the flag on every outgoing AUDIO and HELLO packet.
    ///
    /// <para>
    /// It lives on the transport rather than inside the microphone because HELLO has to
    /// carry it as well, and HELLO keeps going during exactly the silence mute produces.
    /// That is what makes the far end say «микрофон заглушен» instead of «Mac замолчал».
    /// Survives a stop and start, so muting and then restarting does not unmute.
    /// </para>
    /// </summary>
    public bool Muted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    public bool IsRunning
    {
        get { lock (_gate) return _run is not null; }
    }

    public ReceiverService(params IFeature[] features)
    {
        _features = features;

        var claimed = new Dictionary<PacketType, string>();
        foreach (var feature in features)
        {
            foreach (var type in feature.HandledTypes)
            {
                // Two features sharing a packet type would make delivery depend on
                // registration order, which is exactly the kind of bug that only shows up
                // on someone else's machine.
                if (claimed.TryGetValue(type, out var owner))
                {
                    throw new ArgumentException(
                        $"пакет {type} запрошен и «{owner}», и «{feature.Id}»", nameof(features));
                }
                claimed[type] = feature.Id;
            }
        }
    }

    /// <summary>
    /// Claims the port and starts every enabled feature. Throws with a message meant for the
    /// user if the port or a required feature is unavailable; the snapshot is left in
    /// <see cref="ReceiverStatus.Failed"/>.
    /// </summary>
    public async Task StartAsync(ReceiverConfig config)
    {
        await StopAsync().ConfigureAwait(false);

        if (!config.TryGetKey(out var key, out var keyError))
        {
            Fail(keyError!);
            throw new InvalidOperationException(keyError);
        }

        // A config that asks to come up muted does so on every start, not only the first:
        // «включать заглушённым» is a setting about launches, and a restart is one.
        if (config.StartMuted) Muted = true;

        Run? run = null;
        try
        {
            run = Run.Create(config, key, _features, this);
        }
        catch (Exception ex)
        {
            run?.Dispose();
            Fail(ex.Message);
            throw;
        }

        lock (_gate) _run = run;

        Emit(LogLevel.Info, config.Role == BridgeRole.Sender
            ? Loc.F(Strings.Log_Sending, run.Peer, run.Listen)
            : Loc.F(Strings.Log_Listening, run.Listen));
        if (run.Relay is not null) Emit(LogLevel.Info, Loc.F(Strings.Log_Relay, run.Relay));

        run.Begin();
        Publish();
    }

    /// <summary>Releases the port and every feature. Safe to call when already stopped.</summary>
    public async Task StopAsync()
    {
        Run? run;
        lock (_gate)
        {
            run = _run;
            _run = null;
        }
        if (run is null) return;

        await run.StopAsync().ConfigureAwait(false);
        Volatile.Write(ref _snapshot, new ReceiverSnapshot { Status = ReceiverStatus.Stopped });
        Updated?.Invoke(Snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var feature in _features) await feature.DisposeAsync().ConfigureAwait(false);
    }

    // MARK: - Internals

    private void Fail(string detail)
    {
        Volatile.Write(ref _snapshot, new ReceiverSnapshot { Status = ReceiverStatus.Failed, Detail = detail });
        Updated?.Invoke(Snapshot);
    }

    private void Emit(LogLevel level, string message) =>
        Log?.Invoke(new LogEntry(DateTime.UtcNow, level, message));

    /// <summary>Recomputes the snapshot from the live run and hands it to subscribers.</summary>
    private void Publish()
    {
        Run? run;
        lock (_gate) run = _run;
        if (run is null) return;

        var snapshot = run.Capture();
        Volatile.Write(ref _snapshot, snapshot);
        Updated?.Invoke(snapshot);
    }

    /// <summary>
    /// One activation of the transport. Everything mutable lives here, so restarting is a
    /// matter of dropping the old instance rather than resetting a dozen fields.
    /// </summary>
    private sealed class Run : IDisposable
    {
        private readonly ReceiverService _owner;
        private readonly AesGcm _aes;
        private readonly ulong _room;
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cancel = new();
        private readonly List<Task> _tasks = [];
        private readonly DateTime _startedAt = DateTime.UtcNow;

        private readonly BridgeRole _role;

        /// <summary>The direction we seal with; the peer opens with the other one.</summary>
        private readonly Direction _outgoing;
        private readonly Direction _incoming;

        /// <summary>
        /// Where HELLO goes once a second, or null when nothing needs announcing. The
        /// sending role always has one — that is how the far end learns we exist — and the
        /// receiving role only has one behind a relay.
        /// </summary>
        private readonly IPEndPoint? _helloTarget;

        private readonly Dictionary<PacketType, IFeature> _routes = [];
        private readonly List<IFeature> _started = [];

        /// <summary>Features that refused to start, kept so their page can say why.</summary>
        private readonly Dictionary<string, FeatureState> _startFaults = [];

        /// <summary>Features that are off in the config, kept so their page can say so.</summary>
        private readonly Dictionary<string, FeatureState> _disabled = [];

        // Sender-derived state, written by the receive loop and read by the tick timer.
        private ReplayWindow _replay = new();

        // Written only by the receive loop; 32-bit reads are atomic, so the tick timer can
        // read it without a lock.
        private uint _session;
        private IPEndPoint? _peer;

        /// <summary>
        /// Where the relay says the other end is, once it has said so.
        ///
        /// <para>
        /// Used for one thing: sending a keepalive straight at it, so this machine's own
        /// mapping is open when the other end tries the same. Never trusted as a source
        /// — replies still go wherever an authenticated packet last came from, which is
        /// the direct address only once one has actually arrived from there.
        /// </para>
        /// </summary>
        private IPEndPoint? _candidate;

        /// <summary>The relay, and the only address an introduction is accepted from.</summary>
        private readonly IPEndPoint? _relayEndpoint;
        private long _lastPacketTicks;
        private bool _muted;
        private string _senderName = "";
        private long _received;
        private long _rejected;
        private double _oneWayDelayMs = double.NaN;

        // Filled in from PONG, so only ever in the sending role.
        private double _measuredRttMs = double.NaN;
        private long _remoteReceived;
        private long _remoteLost;

        private readonly uint _ownSession = (uint)Random.Shared.Next(1, int.MaxValue);
        private int _ownSeq;

        private readonly RateMeter _rate = new();
        private Timer? _ticker;

        public IPEndPoint Listen { get; }
        public IPEndPoint? Relay { get; }

        /// <summary>The address the sending role dials. Null in the receiving role.</summary>
        public IPEndPoint? Peer { get; }

        private Run(
            ReceiverService owner,
            BridgeRole role,
            byte[] key,
            Socket socket,
            IPEndPoint listen,
            IPEndPoint? relay,
            IPEndPoint? peer)
        {
            _owner = owner;
            _role = role;
            _outgoing = role == BridgeRole.Sender ? Direction.SenderToReceiver : Direction.ReceiverToSender;
            _incoming = role == BridgeRole.Sender ? Direction.ReceiverToSender : Direction.SenderToReceiver;
            _aes = new AesGcm(key, Wire.TagSize);
            _room = Wire.RoomId(key);
            _socket = socket;
            Listen = listen;
            Relay = relay;
            Peer = peer;

            // The sending role knows where to send from the first frame, before anything has
            // been heard back. Without this a feature that has something to say at start-up —
            // the clipboard, most of all — would have to wait for a reply that only exists
            // because we sent something first.
            if (peer is not null) _peer = peer;
            _helloTarget = role == BridgeRole.Sender ? peer : relay;
            // Only the relay may introduce anybody. An introduction from anywhere else is
            // a stranger telling us where to send keepalives, which is not their business.
            _relayEndpoint = relay;
        }

        /// <summary>
        /// Binds the port, then starts the features. A feature that throws takes the whole
        /// start down with it unless it declared itself optional.
        /// </summary>
        public static Run Create(ReceiverConfig config, byte[] key, IFeature[] features, ReceiverService owner)
        {
            Socket? socket = null;
            Run? run = null;
            try
            {
                var relay = string.IsNullOrWhiteSpace(config.Relay)
                    ? null
                    : ReceiverConfig.ParseEndpoint(config.Relay, 47702);

                // The sending role resolves its peer before binding: an unusable address is
                // worth failing on before a port has been claimed, and it is the failure the
                // user is most likely to have caused.
                var peer = config.Role == BridgeRole.Sender ? config.ResolvePeer() : null;

                // Port 0 for the sending role. It dials rather than waits, so a fixed port
                // would buy nothing and cost the one thing that matters here: two roles being
                // able to run on one machine at the same time.
                var wanted = config.Role == BridgeRole.Sender
                    ? new IPEndPoint(IPAddress.Any, 0)
                    : ReceiverConfig.ParseEndpoint(config.Listen, 47702);

                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(wanted);
                var listen = (IPEndPoint)socket.LocalEndPoint!;

                run = new Run(owner, config.Role, key, socket, listen, relay, peer);
                run.StartFeatures(config, features);
                return run;
            }
            catch
            {
                // A required feature refusing to start must not leave the optional ones it
                // was registered after — or before — holding ports and devices.
                run?.StopStartedFeatures();
                run?.Dispose();
                socket?.Dispose();
                throw;
            }
        }

        private void StartFeatures(ReceiverConfig config, IFeature[] features)
        {
            var context = new FeatureContext(
                config,
                (level, message) => _owner.Emit(level, message),
                SendToPeer,
                () => _owner.Muted,
                muted => _owner.Muted = muted);

            foreach (var feature in features)
            {
                if (!feature.IsEnabled(config))
                {
                    // A feature that cannot work here at all says so in its own words. The
                    // generic line is only for a switch somebody turned off, and reporting a
                    // platform limit as one would send the user hunting for that switch.
                    var unavailable = feature.Unavailable(config);
                    _disabled[feature.Id] = new FeatureState
                    {
                        Id = feature.Id,
                        Title = feature.Title,
                        Status = FeatureStatus.Disabled,
                        Headline = unavailable is null ? Strings.Feature_Disabled : Strings.Feature_Unavailable,
                        Detail = unavailable,
                    };
                    continue;
                }

                try
                {
                    feature.Start(context);
                }
                catch (Exception ex) when (feature.IsOptional)
                {
                    _owner.Emit(LogLevel.Warning, Loc.F(Strings.Log_FeatureStartFailed, feature.Title, ex.Message));
                    _startFaults[feature.Id] = new FeatureState
                    {
                        Id = feature.Id,
                        Title = feature.Title,
                        Status = FeatureStatus.Failed,
                        Headline = Strings.Feature_NotStarted,
                        Detail = ex.Message,
                        Fault = ex.Message,
                    };
                    continue;
                }

                _started.Add(feature);
                foreach (var type in feature.HandledTypes) _routes[type] = feature;
            }
        }

        /// <summary>Stops whatever did start, best effort, on a failed start.</summary>
        private void StopStartedFeatures()
        {
            foreach (var feature in _started)
            {
                try
                {
                    feature.StopAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _owner.Emit(LogLevel.Warning, Loc.F(Strings.Log_FeatureRollback, feature.Title, ex.Message));
                }
            }
            _started.Clear();
            _routes.Clear();
        }

        public void Begin()
        {
            _tasks.Add(ReceiveLoop(_cancel.Token));
            if (_helloTarget is not null) _tasks.Add(HelloLoop(_helloTarget, _cancel.Token));
            _ticker = new Timer(_ => _owner.Publish(), null, TickInterval, TickInterval);
        }

        public async Task StopAsync()
        {
            _cancel.Cancel();
            if (_ticker is not null) await _ticker.DisposeAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(_tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            foreach (var feature in _started)
            {
                try
                {
                    await feature.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _owner.Emit(LogLevel.Warning, Loc.F(Strings.Log_FeatureStopFailed, feature.Title, ex.Message));
                }
            }
            _started.Clear();
            _routes.Clear();

            Dispose();
        }

        public void Dispose()
        {
            _socket.Dispose();
            _aes.Dispose();
            _cancel.Dispose();
        }

        // MARK: - Snapshot

        public ReceiverSnapshot Capture()
        {
            var now = DateTime.UtcNow;
            var received = Interlocked.Read(ref _received);

            var states = new Dictionary<string, FeatureState>(_disabled);
            foreach (var (id, state) in _startFaults) states[id] = state;

            string? fault = null;
            foreach (var feature in _started)
            {
                var state = feature.CaptureState() with { Id = feature.Id, Title = feature.Title };
                states[feature.Id] = state;
                if (state.Fault is not null && !feature.IsOptional) fault ??= state.Fault;
            }
            var lastTicks = Interlocked.Read(ref _lastPacketTicks);
            DateTime? lastPacketAt = lastTicks == 0 ? null : new DateTime(lastTicks, DateTimeKind.Utc);
            var delay = Volatile.Read(ref _oneWayDelayMs);
            var rtt = Volatile.Read(ref _measuredRttMs);

            // Mute is our own switch when we are the one holding the microphone, and the far
            // end's report when we are not. Same field on the wire, two different owners.
            var muted = _role == BridgeRole.Sender ? _owner.Muted : Volatile.Read(ref _muted);

            var status = fault is not null ? ReceiverStatus.Failed
                : lastPacketAt is null ? ReceiverStatus.WaitingForSender
                : now - lastPacketAt.Value > SenderTimeout ? ReceiverStatus.SenderLost
                : muted ? ReceiverStatus.Muted
                : ReceiverStatus.Live;

            return new ReceiverSnapshot
            {
                Status = status,
                Detail = fault,
                Role = _role,
                Listen = Listen.ToString(),
                Relay = Relay?.ToString(),
                PeerAddress = Volatile.Read(ref _peer)?.ToString(),
                SenderName = Volatile.Read(ref _senderName),
                Session = _session,
                Muted = muted,
                LastPacketAt = lastPacketAt,
                Uptime = now - _startedAt,
                PacketsPerSecond = _rate.Sample(received, now),
                Received = received,
                Rejected = Interlocked.Read(ref _rejected),
                OneWayDelayMs = double.IsNaN(delay) ? null : delay,
                MeasuredRttMs = double.IsNaN(rtt) ? null : rtt,
                RemoteReceived = Interlocked.Read(ref _remoteReceived),
                RemoteLost = Interlocked.Read(ref _remoteLost),
                Features = states,
            };
        }

        // MARK: - Loops

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[Wire.MaxPacket];
            var plaintext = new byte[Wire.MaxPacket];
            var any = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // ICMP "port unreachable" from a peer that went away surfaces here; keep going.
                    continue;
                }

                var datagram = buffer.AsSpan(0, result.ReceivedBytes);

                // The relay's introduction is the one packet that is not encrypted, and
                // could not be: the relay has no key. Handled before decryption, and
                // acted on in one small way only — see _candidate.
                if (Wire.PeerIntroduction(datagram, _room) is { } named)
                {
                    if (_relayEndpoint is not null && result.RemoteEndPoint.Equals(_relayEndpoint))
                    {
                        Volatile.Write(ref _candidate, named);
                    }
                    continue;
                }

                var length = Wire.Open(_aes, datagram, plaintext, _incoming, out var header);
                if (length < 0 || header.Room != _room)
                {
                    Interlocked.Increment(ref _rejected);
                    continue;
                }

                // A new session id means the sender restarted: start over. But stay locked
                // onto the current one while it is still talking, otherwise a second sender
                // (a stale process, say) would reset everything on every packet.
                if (_session != header.Session)
                {
                    var lastTicks = Interlocked.Read(ref _lastPacketTicks);
                    var currentIsLive = _session != 0
                        && lastTicks != 0
                        && DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc) < TimeSpan.FromSeconds(2);
                    if (currentIsLive)
                    {
                        Interlocked.Increment(ref _rejected);
                        continue;
                    }

                    _session = header.Session;
                    _replay = new ReplayWindow();
                    foreach (var feature in _started) feature.OnSessionReset();
                    _owner.Emit(LogLevel.Info, Loc.F(Strings.Log_Session, header.Session.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), result.RemoteEndPoint));
                }

                if (!_replay.Accept(header.Seq))
                {
                    Interlocked.Increment(ref _rejected);
                    continue;
                }

                // The receiving role learns where its peer is from whoever it hears; the
                // sending role already knows, and must not be talked into aiming somewhere
                // else by a packet that happened to arrive from another address.
                if (Peer is null) Volatile.Write(ref _peer, (IPEndPoint)result.RemoteEndPoint);
                Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref _muted, header.Flags.HasFlag(PacketFlags.Muted));
                Interlocked.Increment(ref _received);

                if (header.Type == PacketType.Hello)
                {
                    if (length < 10) continue;
                    var stamp = BinaryPrimitives.ReadUInt64LittleEndian(plaintext);
                    var nameLength = Math.Min(plaintext[9], length - 10);
                    if (nameLength > 0) Volatile.Write(ref _senderName, Encoding.UTF8.GetString(plaintext, 10, nameLength));
                    NoteDelay(stamp);
                    // PONG travels one way only, so only the receiving end answers. Both ends
                    // send HELLO, and a sender answering another sender's HELLO would put a
                    // packet type on the wire in a direction the contract does not have.
                    if (_role == BridgeRole.Receiver) SendPong(stamp);
                    continue;
                }

                if (header.Type == PacketType.Pong)
                {
                    if (_role == BridgeRole.Sender) NotePong(plaintext.AsSpan(0, length));
                    continue;
                }

                if (_routes.TryGetValue(header.Type, out var owner))
                {
                    try
                    {
                        owner.OnPacket(header, plaintext.AsSpan(0, length));
                    }
                    catch (Exception ex)
                    {
                        // A feature must never be able to stop the socket loop: the voice
                        // path shares it.
                        _owner.Emit(LogLevel.Warning, $"hexbridge: «{owner.Title}»: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Estimates one-way delay from the sender's wall-clock stamp. Only meaningful when
        /// both machines keep time; anything implausible is reported as unknown.
        /// </summary>
        private void NoteDelay(ulong senderStampMs)
        {
            var delta = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)senderStampMs;
            Volatile.Write(ref _oneWayDelayMs,
                delta is >= 0 and <= (long)MaxPlausibleDelayMs ? delta : double.NaN);
        }

        /// <summary>
        /// The other half of the round trip: our own HELLO stamp comes back in a PONG, so
        /// the number is a measurement rather than the clock-difference estimate
        /// <see cref="NoteDelay"/> has to settle for.
        /// </summary>
        private void NotePong(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 24) return;

            var echo = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            Interlocked.Exchange(ref _remoteReceived, (long)BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]));
            Interlocked.Exchange(ref _remoteLost, (long)BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]));

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Volatile.Write(ref _measuredRttMs, Math.Max(0, now - (double)echo));
        }

        private void SendPong(ulong echo)
        {
            var delivery = default(DeliveryStats);
            foreach (var feature in _started) delivery += feature.Delivery;

            var payload = new byte[24];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, echo);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), (ulong)delivery.Received);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), (ulong)delivery.Lost);

            SendToPeer(PacketType.Pong, payload, PacketFlags.None);
        }

        /// <summary>
        /// Seals a payload and sends it back to whoever we last heard from. Called by
        /// features from their own threads, so it takes the socket as it finds it.
        /// </summary>
        private void SendToPeer(PacketType type, ReadOnlyMemory<byte> payload, PacketFlags flags)
        {
            var peer = Volatile.Read(ref _peer);
            if (peer is null) return;

            var header = new Header(type, flags, _room, _ownSession, NextOwnSeq());

            byte[] datagram;
            lock (_aes)
            {
                // AesGcm is not thread safe and features send from their own threads.
                datagram = Wire.Seal(_aes, header, payload.Span, _outgoing);
            }

            try
            {
                _socket.SendTo(datagram, peer);
            }
            catch (SocketException)
            {
                // Nothing useful to do about a transient send failure on a status packet.
            }
            catch (ObjectDisposedException)
            {
                // Racing a stop.
            }
        }

        /// <summary>
        /// Once a second, as the contract asks. The sending role always runs it: it is how
        /// the far end learns we exist, how it measures the round trip, and how it tells
        /// «заглушен» from «пропал». The receiving role only runs it behind a relay, where
        /// nobody can reach us until the relay has seen a packet from us.
        /// </summary>
        private async Task HelloLoop(IPEndPoint target, CancellationToken token)
        {
            var name = Encoding.UTF8.GetBytes(Environment.MachineName);
            var role = _role == BridgeRole.Sender ? (byte)0 : (byte)1;

            while (!token.IsCancellationRequested)
            {
                var payload = new byte[10 + name.Length];
                BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                payload[8] = role;
                payload[9] = (byte)name.Length;
                name.CopyTo(payload, 10);

                // Mute rides the keepalive, not only the audio: during a mute there is no
                // audio to put it on, and that silence is exactly what has to be explained.
                var flags = _role == BridgeRole.Sender && _owner.Muted ? PacketFlags.Muted : PacketFlags.None;

                var header = new Header(PacketType.Hello, flags, _room, _ownSession, NextOwnSeq());
                try
                {
                    byte[] datagram;
                    lock (_aes) datagram = Wire.Seal(_aes, header, payload, _outgoing);
                    _socket.SendTo(datagram, target);

                    // And the same knock straight at the other end, when the relay has
                    // said where it is. This is the half of the hole punch that happens
                    // here: the Mac aims at us, we aim at the Mac, and both mappings open
                    // at once. Nothing depends on it working — if it does not, the relay
                    // carries on carrying everything.
                    if (Volatile.Read(ref _candidate) is { } direct && !direct.Equals(target))
                    {
                        var knock = new Header(PacketType.Hello, flags, _room, _ownSession, NextOwnSeq());
                        byte[] second;
                        lock (_aes) second = Wire.Seal(_aes, knock, payload, _outgoing);
                        _socket.SendTo(second, direct);
                    }
                }
                catch (SocketException)
                {
                    // The relay may be briefly unreachable; the next tick retries.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private uint NextOwnSeq() => (uint)Interlocked.Increment(ref _ownSeq);
    }
}
