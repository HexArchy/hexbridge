using System.Collections.Concurrent;

using HexBridge.Localization;

namespace HexBridge.Clipboard;

/// <summary>
/// The shared clipboard: copy on the Mac, paste on this PC, and the other way round.
///
/// It owns no transport of its own. Everything that has to arrive intact goes through
/// <see cref="BulkChannel"/>, which is the contract's reliable layer and knows nothing about
/// clipboards; this class only decides what to hand it and what to do with what comes back.
///
/// Off unless asked for, and it stays that way. The clipboard is where passwords live for the
/// few seconds between a manager and a login form, and a feature that ships enabled would send
/// them to another machine before anybody read a settings page.
///
/// Optional: a clipboard that cannot be opened is a reason to grey out one page, not to take
/// the voice path down with it.
/// </summary>
public sealed class ClipboardFeature : IFeature
{
    /// <summary>
    /// All four bulk types, because both roles live here: we offer objects and we accept
    /// them. The day a second feature wants reliable delivery, the channel moves up into the
    /// host and the routing goes with it — nothing in this file would have to change.
    /// </summary>
    private static readonly PacketType[] Types =
        [PacketType.BulkOffer, PacketType.BulkChunk, PacketType.BulkAck, PacketType.BulkDone];

    /// <summary>The clipboard is polled this often. Under a change it costs one counter read.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The bulk layer's own clock: its ack is every 200 ms and its chunks are paced.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);

    private readonly Func<Action<LogLevel, string>, IClipboardSurface> _surfaceFactory;
    private readonly ConcurrentQueue<BulkDelivery> _arrivals = new();
    private readonly object _gate = new();

    private FeatureContext? _context;
    private BulkChannel? _channel;
    private ClipboardSync? _sync;
    private IClipboardSurface? _surface;
    private Thread? _worker;
    private CancellationTokenSource? _cancel;
    private string? _fault;

    private long _sent;
    private long _received;
    private string? _lastDescription;
    private BulkDirection? _lastDirection;
    private DateTime? _lastAt;

    public ClipboardFeature() : this(DefaultSurface) { }

    /// <summary>Test seam: the surface is the only part of this feature that touches Windows.</summary>
    public ClipboardFeature(Func<Action<LogLevel, string>, IClipboardSurface> surfaceFactory) =>
        _surfaceFactory = surfaceFactory;

    public string Id => "clipboard";
    public string Title => Strings.Feature_Clipboard_Title;
    public bool IsOptional => true;
    public IReadOnlyList<PacketType> HandledTypes => Types;

    /// <summary>Off in a fresh config, and only on because somebody said so.</summary>
    public bool IsEnabled(ReceiverConfig config) => config.Clipboard;

    public void Start(FeatureContext context)
    {
        var surface = _surfaceFactory(context.Log);
        var sync = new ClipboardSync(surface);
        var channel = new BulkChannel(context.Send)
        {
            Owns = sync.Owns,
        };
        channel.Delivered += _arrivals.Enqueue;
        channel.Finished += OnFinished;
        channel.Note += note => context.Log(LogLevel.Info, note);

        var cancel = new CancellationTokenSource();
        var worker = new Thread(() => Run(sync, channel, context, cancel.Token))
        {
            IsBackground = true,
            Name = "hexbridge.clipboard",
        };
        // The clipboard is documented as apartment-bound, and the OLE path other applications
        // put on it expects an STA caller. Nothing here costs anything if it is not honoured,
        // and a great deal goes subtly wrong if it is not.
        if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);

        lock (_gate)
        {
            _context = context;
            _surface = surface;
            _sync = sync;
            _channel = channel;
            _cancel = cancel;
            _worker = worker;
            _fault = null;
            _sent = 0;
            _received = 0;
            _lastDescription = null;
            _lastDirection = null;
            _lastAt = null;
        }

        worker.Start();
        context.Log(LogLevel.Warning,
            Strings.Log_Clip_On);
    }

    public Task StopAsync()
    {
        CancellationTokenSource? cancel;
        Thread? worker;
        IClipboardSurface? surface;
        lock (_gate)
        {
            cancel = _cancel;
            worker = _worker;
            surface = _surface;
            _cancel = null;
            _worker = null;
            _surface = null;
            _channel = null;
            _sync = null;
        }

        cancel?.Cancel();
        // Bounded: the worker only ever sleeps for a tick, and a clipboard held open by
        // another application is already capped by the retry budget inside the surface.
        worker?.Join(TimeSpan.FromSeconds(2));
        cancel?.Dispose();
        // The Windows surface owns a message-only window; anything else owns nothing.
        (surface as IDisposable)?.Dispose();
        while (_arrivals.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }

    public void OnPacket(in Header header, ReadOnlySpan<byte> payload)
    {
        var channel = Volatile.Read(ref _channel);
        channel?.OnPacket(header.Type, payload, DateTime.UtcNow);
    }

    /// <summary>
    /// The sender restarted. Half-transferred objects belong to a session that is gone, and
    /// what the peer holds is anybody's guess again — but our own clipboard is still ours.
    /// </summary>
    public void OnSessionReset()
    {
        Volatile.Read(ref _channel)?.PeerRestarted();
        Volatile.Read(ref _sync)?.ForgetPeer();
    }

    public FeatureState CaptureState()
    {
        BulkChannel? channel;
        string? fault;
        long sent, received;
        string? lastDescription;
        BulkDirection? lastDirection;
        DateTime? lastAt;

        lock (_gate)
        {
            channel = _channel;
            fault = _fault;
            sent = _sent;
            received = _received;
            lastDescription = _lastDescription;
            lastDirection = _lastDirection;
            lastAt = _lastAt;
        }

        if (channel is null)
        {
            return new ClipboardState
            {
                Status = fault is null ? FeatureStatus.Stopped : FeatureStatus.Failed,
                Headline = fault is null ? Strings.Feature_Clip_Stopped : Strings.Feature_Clip_Failed,
                Detail = fault,
                Fault = fault,
            };
        }

        var flight = channel.Progress().FirstOrDefault();
        var moved = sent + received;

        return new ClipboardState
        {
            Status = fault is not null ? FeatureStatus.Failed
                : flight is not null ? FeatureStatus.Live
                : moved == 0 ? FeatureStatus.Waiting
                : FeatureStatus.Live,
            Headline = fault is not null ? Strings.Feature_Clip_ClipboardFailed
                : flight is not null ? Strings.Feature_Clip_Transferring
                : moved == 0 ? Strings.Feature_Clip_Waiting
                : Strings.Feature_Clip_Live,
            Detail = fault ?? Describe(lastDescription, lastDirection, lastAt),
            Fault = fault,
            Ready = fault is null,
            Sent = sent,
            Received = received,
            LastDescription = lastDescription,
            LastDirection = lastDirection,
            LastAt = lastAt,
            TransferDescription = flight?.Description,
            TransferDirection = flight?.Direction,
            Progress = flight?.Fraction ?? 0,
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // MARK: - Worker

    /// <summary>
    /// The one thread that touches the clipboard, so nothing else has to think about
    /// apartments. It also drives the bulk clock, which keeps every timeout in the transfer
    /// layer on a thread that cannot be blocked by the socket.
    /// </summary>
    private void Run(ClipboardSync sync, BulkChannel channel, FeatureContext context, CancellationToken token)
    {
        var nextPoll = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            try
            {
                while (_arrivals.TryDequeue(out var delivery)) Accept(sync, delivery, context);

                if (now >= nextPoll)
                {
                    nextPoll = now + PollInterval;
                    Publish(sync.Poll(), channel, context, now);
                }

                channel.Tick(now);
            }
            catch (Exception ex)
            {
                // A clipboard that misbehaves must not take the thread — and with it every
                // timeout in the transfer layer — down with it.
                lock (_gate) _fault = ex.Message;
                context.Log(LogLevel.Error, Loc.F(Strings.Log_Clip, ex.Message));
            }

            token.WaitHandle.WaitOne(TickInterval);
        }
    }

    private void Accept(ClipboardSync sync, BulkDelivery delivery, FeatureContext context)
    {
        if (delivery.Kind != BulkKind.Clipboard) return;

        var item = new ClipboardItem(delivery.Format, delivery.Bytes);
        sync.Apply(item);

        lock (_gate)
        {
            _received++;
            _lastDescription = item.Describe();
            _lastDirection = BulkDirection.Incoming;
            _lastAt = DateTime.UtcNow;
        }
        context.Log(LogLevel.Info, Loc.F(Strings.Log_Clip_Received, item.Describe()));
    }

    private void Publish(ClipboardItem? item, BulkChannel channel, FeatureContext context, DateTime now)
    {
        if (item is null) return;

        channel.Offer(BulkKind.Clipboard, item.Format, item.Bytes, item.Describe(), now, out var error);
        if (error is not null) context.Log(LogLevel.Warning, Loc.F(Strings.Log_Clip, error));
    }

    private void OnFinished(BulkResult result)
    {
        if (result.Outcome is BulkOutcome.Delivered or BulkOutcome.AlreadyThere)
        {
            Volatile.Read(ref _sync)?.NotePeerHas(result.Hash);
        }
        if (result.Outcome != BulkOutcome.Delivered) return;

        lock (_gate)
        {
            _sent++;
            _lastDescription = result.Description;
            _lastDirection = BulkDirection.Outgoing;
            _lastAt = DateTime.UtcNow;
        }
    }

    private static string Describe(string? description, BulkDirection? direction, DateTime? at)
    {
        if (description is null || at is null) return Strings.Feature_Clip_Detail_Empty;

        var arrow = direction == BulkDirection.Outgoing ? Strings.Feature_Clip_Sent : Strings.Feature_Clip_Received;
        var ago = DateTime.UtcNow - at.Value;
        var when = ago < TimeSpan.FromMinutes(1) ? Strings.Clipboard_When_JustNow
            : ago < TimeSpan.FromHours(1) ? Loc.F(Strings.Clipboard_When_Minutes, (int)ago.TotalMinutes)
            : Loc.F(Strings.Clipboard_When_Hours, (int)ago.TotalHours);
        return Loc.F(Strings.Feature_Clip_Last, description, arrow, when);
    }

    private static IClipboardSurface DefaultSurface(Action<LogLevel, string> log) =>
        OperatingSystem.IsWindows()
            ? new WindowsClipboard(log)
            : throw new PlatformNotSupportedException(Strings.Feature_Clip_WindowsOnly);
}
