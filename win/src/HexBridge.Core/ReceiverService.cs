using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HexBridge;

/// <summary>
/// The shared transport and the host for everything built on it: one socket, one key, one
/// session, and a list of features that each claim a few packet types.
///
/// The host knows nothing about audio or gamepads. Both front ends (console and desktop
/// app) drive this and differ only in how they render <see cref="Updated"/> and
/// <see cref="Log"/>.
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

    /// <summary>Fires from background threads; front ends marshal as they need.</summary>
    public event Action<LogEntry>? Log;

    /// <summary>Fires on the tick timer while running, at most ten times a second.</summary>
    public event Action<ReceiverSnapshot>? Updated;

    /// <summary>Every feature this host was built with, enabled or not, in registration order.</summary>
    public IReadOnlyList<IFeature> Features => _features;

    public ReceiverSnapshot Snapshot => Volatile.Read(ref _snapshot);

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

        Emit(LogLevel.Info, $"hexbridge: слушаю {run.Listen}");
        if (run.Relay is not null) Emit(LogLevel.Info, $"hexbridge: регистрируюсь на релее {run.Relay}");

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
        private long _lastPacketTicks;
        private bool _muted;
        private string _senderName = "";
        private long _received;
        private long _rejected;
        private double _oneWayDelayMs = double.NaN;

        private readonly uint _ownSession = (uint)Random.Shared.Next(1, int.MaxValue);
        private int _ownSeq;

        private readonly RateMeter _rate = new();
        private Timer? _ticker;

        public IPEndPoint Listen { get; }
        public IPEndPoint? Relay { get; }

        private Run(ReceiverService owner, byte[] key, Socket socket, IPEndPoint listen, IPEndPoint? relay)
        {
            _owner = owner;
            _aes = new AesGcm(key, Wire.TagSize);
            _room = Wire.RoomId(key);
            _socket = socket;
            Listen = listen;
            Relay = relay;
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
                var listen = ReceiverConfig.ParseEndpoint(config.Listen, 47702);
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(listen);

                var relay = string.IsNullOrWhiteSpace(config.Relay)
                    ? null
                    : ReceiverConfig.ParseEndpoint(config.Relay, 47702);

                run = new Run(owner, key, socket, listen, relay);
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
                SendToPeer);

            foreach (var feature in features)
            {
                if (!feature.IsEnabled(config))
                {
                    _disabled[feature.Id] = new FeatureState
                    {
                        Id = feature.Id,
                        Title = feature.Title,
                        Status = FeatureStatus.Disabled,
                        Headline = "Выключено в настройках",
                    };
                    continue;
                }

                try
                {
                    feature.Start(context);
                }
                catch (Exception ex) when (feature.IsOptional)
                {
                    _owner.Emit(LogLevel.Warning, $"hexbridge: «{feature.Title}» не запущено: {ex.Message}");
                    _startFaults[feature.Id] = new FeatureState
                    {
                        Id = feature.Id,
                        Title = feature.Title,
                        Status = FeatureStatus.Failed,
                        Headline = "Не запущено",
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
                    _owner.Emit(LogLevel.Warning, $"hexbridge: «{feature.Title}» при откате: {ex.Message}");
                }
            }
            _started.Clear();
            _routes.Clear();
        }

        public void Begin()
        {
            _tasks.Add(ReceiveLoop(_cancel.Token));
            if (Relay is not null) _tasks.Add(HelloLoop(Relay, _cancel.Token));
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
                    _owner.Emit(LogLevel.Warning, $"hexbridge: «{feature.Title}» при остановке: {ex.Message}");
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

            var status = fault is not null ? ReceiverStatus.Failed
                : lastPacketAt is null ? ReceiverStatus.WaitingForSender
                : now - lastPacketAt.Value > SenderTimeout ? ReceiverStatus.SenderLost
                : Volatile.Read(ref _muted) ? ReceiverStatus.Muted
                : ReceiverStatus.Live;

            return new ReceiverSnapshot
            {
                Status = status,
                Detail = fault,
                Listen = Listen.ToString(),
                Relay = Relay?.ToString(),
                PeerAddress = Volatile.Read(ref _peer)?.ToString(),
                SenderName = Volatile.Read(ref _senderName),
                Session = _session,
                Muted = Volatile.Read(ref _muted),
                LastPacketAt = lastPacketAt,
                Uptime = now - _startedAt,
                PacketsPerSecond = _rate.Sample(received, now),
                Received = received,
                Rejected = Interlocked.Read(ref _rejected),
                OneWayDelayMs = double.IsNaN(delay) ? null : delay,
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
                var length = Wire.Open(_aes, datagram, plaintext, Direction.SenderToReceiver, out var header);
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
                    _owner.Emit(LogLevel.Info, $"hexbridge: сессия {header.Session:x8} от {result.RemoteEndPoint}");
                }

                if (!_replay.Accept(header.Seq))
                {
                    Interlocked.Increment(ref _rejected);
                    continue;
                }

                Volatile.Write(ref _peer, (IPEndPoint)result.RemoteEndPoint);
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
                    SendPong(stamp);
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

        private void SendPong(ulong echo)
        {
            var delivery = default(DeliveryStats);
            foreach (var feature in _started) delivery += feature.Delivery;

            var payload = new byte[24];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, echo);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), (ulong)delivery.Received);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), (ulong)delivery.Lost);

            SendToPeer(PacketType.Pong, payload);
        }

        /// <summary>
        /// Seals a payload and sends it back to whoever we last heard from. Called by
        /// features from their own threads, so it takes the socket as it finds it.
        /// </summary>
        private void SendToPeer(PacketType type, ReadOnlyMemory<byte> payload)
        {
            var peer = Volatile.Read(ref _peer);
            if (peer is null) return;

            var header = new Header(type, PacketFlags.None, _room, _ownSession, NextOwnSeq());

            byte[] datagram;
            lock (_aes)
            {
                // AesGcm is not thread safe and features send from their own threads.
                datagram = Wire.Seal(_aes, header, payload.Span, Direction.ReceiverToSender);
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

        // In relay mode nobody can reach us directly, so we announce ourselves and the
        // relay learns our address from the source of these packets.
        private async Task HelloLoop(IPEndPoint relay, CancellationToken token)
        {
            var name = Encoding.UTF8.GetBytes(Environment.MachineName);

            while (!token.IsCancellationRequested)
            {
                var payload = new byte[10 + name.Length];
                BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                payload[8] = 1;  // role: receiver
                payload[9] = (byte)name.Length;
                name.CopyTo(payload, 10);

                var header = new Header(PacketType.Hello, PacketFlags.None, _room, _ownSession, NextOwnSeq());
                try
                {
                    byte[] datagram;
                    lock (_aes) datagram = Wire.Seal(_aes, header, payload, Direction.ReceiverToSender);
                    _socket.SendTo(datagram, relay);
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
