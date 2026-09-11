using HexBridge.Localization;

namespace HexBridge;

/// <summary>
/// One feature's view of the shared channel: its own kind, in both directions, and nothing
/// else on the wire.
///
/// <para>
/// A lane is handed to a feature when it is built and outlives every start and stop, so the
/// composition root can wire the two together once and neither has to find the other later.
/// Until <see cref="BulkHost"/> is running the lane has nowhere to send, and says so.
/// </para>
/// </summary>
public sealed class BulkLane
{
    private readonly BulkHost _host;

    internal BulkLane(BulkHost host, BulkKind kind)
    {
        _host = host;
        Kind = kind;
    }

    public BulkKind Kind { get; }

    /// <summary>
    /// «Этот объект у меня уже есть», for this kind alone. Called on the socket thread.
    /// </summary>
    public Func<byte[], bool>? Owns { get; set; }

    /// <summary>
    /// Where this feature wants an arriving object staged while it is being assembled, or
    /// null to have it held in memory.
    ///
    /// <para>
    /// One answer to two questions, deliberately: a feature that names a folder is one
    /// whose objects are streamed to disk and never held whole, and a feature that names
    /// none is one whose objects fit in memory by construction. Files are the first, and
    /// the folder they stage in is the folder they land in — so the last step of a transfer
    /// is a rename rather than a copy from one volume to another. The clipboard is the
    /// second, and its 64 MiB ceiling is what makes that safe.
    /// </para>
    /// </summary>
    public Func<string?>? Staging { get; set; }

    /// <summary>An object of this kind arrived whole. Raised on the socket thread.</summary>
    public event Action<BulkDelivery>? Delivered;

    /// <summary>An outgoing transfer of this kind ended, whatever the reason.</summary>
    public event Action<BulkResult>? Finished;

    /// <summary>
    /// Starts offering an object of this kind. Returns its transfer id, or null with a
    /// reason the user can read.
    /// </summary>
    public uint? Offer(BulkFormat format, byte[] bytes, string description, DateTime now, out string? error) =>
        _host.Offer(Kind, format, BulkSource.FromMemory(bytes), description, now, out error);

    /// <summary>
    /// The same, for an object that is not in memory and must not be — a file, read a chunk
    /// at a time for as long as the transfer lasts. The source is taken over whatever
    /// happens next, including a refusal, so the caller never has to close it.
    /// </summary>
    public uint? Offer(BulkFormat format, BulkSource source, string description, DateTime now, out string? error) =>
        _host.Offer(Kind, format, source, description, now, out error);

    /// <summary>What this feature has in flight, for a progress bar. Never anybody else's.</summary>
    public BulkProgress[] Progress() => _host.Progress(Kind);

    /// <summary>Drops this feature's transfers and leaves the other kinds running.</summary>
    public void Reset() => _host.Reset(Kind);

    /// <summary>False while nothing is listening — the feature this kind belongs to is off.</summary>
    internal bool IsOpen => Delivered is not null;

    internal bool Has(byte[] hash) => Owns?.Invoke(hash) == true;

    internal string? StagingFolder() => Staging?.Invoke();

    internal void Deliver(BulkDelivery delivery) => Delivered?.Invoke(delivery);

    internal void Finish(BulkResult result) => Finished?.Invoke(result);
}

/// <summary>
/// The owner of the one <see cref="BulkChannel"/> on this machine, and the only feature that
/// claims the four bulk packet types.
///
/// <para>
/// There is one channel because there is one contract: the four types carry a transfer id and
/// a kind, and nothing in them says which feature an object belongs to. Two features each
/// with a channel of their own would answer each other's acks and hand out overlapping
/// transfer ids — which is why the host refuses to route one packet type to two features in
/// the first place. So the types are claimed here, once, and every object is dispatched to
/// the lane for its <see cref="BulkKind"/>.
/// </para>
///
/// <para>
/// It knows nothing about clipboards or files, exactly as the channel under it knows nothing
/// about either. Adding a third kind is a lane and a feature, and changes nothing here.
/// </para>
/// </summary>
public sealed class BulkHost : IFeature
{
    private static readonly PacketType[] Types =
        [PacketType.BulkOffer, PacketType.BulkChunk, PacketType.BulkAck, PacketType.BulkDone];

    /// <summary>The channel's own clock: its ack is every 200 ms and its chunks are paced.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);

    private readonly object _gate = new();
    private readonly Dictionary<BulkKind, BulkLane> _lanes = [];

    private FeatureContext? _context;
    private BulkChannel? _channel;
    private Thread? _worker;
    private CancellationTokenSource? _cancel;

    public string Id => "bulk";
    public string Title => Strings.Feature_Bulk_Title;
    public bool IsOptional => true;
    public IReadOnlyList<PacketType> HandledTypes => Types;

    /// <summary>
    /// On when anything needs it and off otherwise. A channel nobody is using would still
    /// accept an offer, pull gigabytes across the network and hand them to no one.
    /// </summary>
    public bool IsEnabled(ReceiverConfig config) => config.Clipboard || config.Files;

    /// <summary>
    /// The lane for one kind, created on first ask and the same object ever after. Called
    /// from the composition root while nothing is running.
    /// </summary>
    public BulkLane Lane(BulkKind kind)
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(kind, out var lane))
            {
                lane = new BulkLane(this, kind);
                _lanes[kind] = lane;
            }
            return lane;
        }
    }

    public void Start(FeatureContext context)
    {
        var channel = new BulkChannel(context.Send)
        {
            Owns = HasAlready,
            Wanted = IsWanted,
            Staging = StagingFor,
            ChunkRateCeiling = CeilingFor(context.Config),
        };
        channel.Delivered += OnDelivered;
        channel.Finished += OnFinished;
        channel.Note += note => context.Log(LogLevel.Info, note);

        var cancel = new CancellationTokenSource();
        var worker = new Thread(() => Run(channel, cancel.Token))
        {
            IsBackground = true,
            Name = "hexbridge.bulk",
        };

        lock (_gate)
        {
            _context = context;
            _channel = channel;
            _cancel = cancel;
            _worker = worker;
        }

        worker.Start();
    }

    public Task StopAsync()
    {
        CancellationTokenSource? cancel;
        Thread? worker;
        BulkChannel? channel;
        lock (_gate)
        {
            cancel = _cancel;
            worker = _worker;
            channel = _channel;
            _cancel = null;
            _worker = null;
            _channel = null;
            _context = null;
        }

        cancel?.Cancel();
        // Bounded: the thread only ever sleeps for a tick and does nothing that can block.
        worker?.Join(TimeSpan.FromSeconds(2));
        cancel?.Dispose();

        // After the thread has stopped, so nothing is being written while it is thrown
        // away. Half-received objects are what this is for: dropping the channel would
        // leave their temporary files on disk, and nobody would ever come back for them.
        channel?.Reset();
        return Task.CompletedTask;
    }

    public void OnPacket(in Header header, ReadOnlySpan<byte> payload) =>
        Volatile.Read(ref _channel)?.OnPacket(header.Type, payload, DateTime.UtcNow);

    /// <summary>
    /// The peer restarted. Half-received objects belong to a session that is gone; what we
    /// were pushing is still worth pushing, and the channel knows the difference.
    /// </summary>
    public void OnSessionReset() => Volatile.Read(ref _channel)?.PeerRestarted();

    /// <summary>
    /// Plumbing has no page and nothing to say on the one the features have. The headline is
    /// empty on purpose: every transfer is reported by the feature it belongs to, which is
    /// where somebody looking for it will be, and a second line saying the same thing in
    /// other words is a line to read twice.
    /// </summary>
    public FeatureState CaptureState()
    {
        var channel = Volatile.Read(ref _channel);
        return new FeatureState
        {
            Status = channel is null ? FeatureStatus.Stopped
                : channel.Progress().Length > 0 ? FeatureStatus.Live
                : FeatureStatus.Waiting,
            Headline = "",
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // MARK: - Lanes

    internal uint? Offer(BulkKind kind, BulkFormat format, BulkSource source, string description, DateTime now, out string? error)
    {
        var channel = Volatile.Read(ref _channel);
        if (channel is null)
        {
            // The source belongs to the channel from the moment it is handed over, and
            // there is no channel — so it is closed here rather than left open on a file
            // nobody is going to send.
            source.Dispose();
            error = Strings.Err_Bulk_NotRunning;
            return null;
        }
        return channel.Offer(kind, format, source, description, now, out error);
    }

    internal BulkProgress[] Progress(BulkKind kind) =>
        [.. Volatile.Read(ref _channel)?.Progress().Where(p => p.Kind == kind) ?? []];

    internal void Reset(BulkKind kind) => Volatile.Read(ref _channel)?.Reset(kind);

    private BulkLane? Find(BulkKind kind)
    {
        lock (_gate) return _lanes.GetValueOrDefault(kind);
    }

    private bool HasAlready(BulkKind kind, byte[] hash) => Find(kind)?.Has(hash) == true;

    private string? StagingFor(BulkKind kind) => Find(kind)?.StagingFolder();

    /// <summary>
    /// The fastest this machine should aim to send, worked out once when the channel is
    /// built.
    ///
    /// <para>
    /// Two things can hold it down. One is the setting, which is there for a link somebody
    /// knows something about that this program does not — a metered connection, a household
    /// that notices. The other is a relay: it drops whatever exceeds its per-address limit,
    /// and a dropped chunk comes back as a hole and is sent again, so aiming above what a
    /// relay carries is a way of making a transfer slower rather than faster. With neither,
    /// there is no artificial ceiling at all and the pacing finds the link's own.
    /// </para>
    /// </summary>
    internal static double CeilingFor(ReceiverConfig config)
    {
        double? relay = string.IsNullOrWhiteSpace(config.Relay)
            ? null
            : config.RelayPacketsPerSecond > 0 ? config.RelayPacketsPerSecond : Bulk.RelayPacketsPerSecond;

        return Bulk.CeilingFor(config.SendRate, relay);
    }

    /// <summary>
    /// A lane with a listener is a feature that is running. A kind whose feature is off is
    /// turned down at the offer, so nothing crosses the wire for it.
    /// </summary>
    private bool IsWanted(BulkKind kind) => Find(kind)?.IsOpen == true;

    private void OnDelivered(BulkDelivery delivery)
    {
        var lane = Find(delivery.Kind);
        if (lane is null || !lane.IsOpen)
        {
            // An offer for a kind nobody wants is turned down before any of it is pulled,
            // so getting here means the feature was switched off while its object was
            // already on the way. It is dropped, and it is said out loud: the silent
            // version is somebody watching a transfer finish on the Mac and then hunting
            // for a file that was never written.
            Volatile.Read(ref _context)?.Log(LogLevel.Warning,
                Loc.F(Strings.Log_Bulk_Unwanted, delivery.Description));

            // The object was handed over the moment it verified, and there is nobody to
            // hand it to. Whatever it was streamed into is ours to clear away.
            Discard(delivery);
            return;
        }

        lane.Deliver(delivery);
    }

    /// <summary>
    /// Throws away an object nobody took. Never throws itself: a temporary file that
    /// survives is swept at the next start, and a failure here would take the channel's
    /// thread with it.
    /// </summary>
    private static void Discard(BulkDelivery delivery)
    {
        if (delivery.Path is null) return;

        try
        {
            File.Delete(delivery.Path);
        }
        catch (Exception)
        {
        }
    }

    private void OnFinished(BulkResult result) => Find(result.Kind)?.Finish(result);

    // MARK: - Clock

    /// <summary>
    /// The channel's clock, on a thread of its own. It drives every timeout in the transfer
    /// layer, so it must not be a thread a feature can block: the clipboard used to own this
    /// loop, and a clipboard held open by another application stopped the acks with it.
    /// </summary>
    private void Run(BulkChannel channel, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                channel.Tick(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Volatile.Read(ref _context)?.Log(LogLevel.Error, Loc.F(Strings.Log_Bulk_Tick, ex.Message));
            }

            token.WaitHandle.WaitOne(TickInterval);
        }
    }
}
