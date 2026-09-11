using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using HexBridge.Localization;

namespace HexBridge.Files;

/// <summary>
/// Files, in both directions: one dropped on this window goes to the other machine, and one
/// sent from there lands in this user's Downloads folder.
///
/// <para>
/// Two paths carry a file, and this owns one of them. The normal one is the fast path — a
/// TCP stream on data port + 2, which moved a file at 370 MB/s between the two machines the
/// chunked channel managed 2.8 MB/s on. The other is the file lane of
/// <see cref="BulkHost"/>, the same reliable channel the clipboard uses, and it is not
/// going anywhere: it is what a file falls back to when no connection can be made, and it is
/// all a pair who can only reach each other through a relay ever has.
/// </para>
///
/// <para>
/// Either way this class does the two things no transport may know about: it decides what a
/// received name is allowed to become on this disk, and it reads the file being sent. Both
/// paths hand a finished file to the same queue, so which one carried it stops mattering the
/// moment it has landed.
/// </para>
///
/// <para>
/// Off unless asked for. A file that arrives is written without anybody being asked, and
/// «what appeared in my Downloads folder» is not a question somebody should have to answer
/// about a program they installed for a microphone.
/// </para>
/// </summary>
public sealed class FilesFeature : IFeature
{
    /// <summary>How many arrivals the page remembers. A log, not a file manager.</summary>
    private const int Remembered = 20;

    private readonly BulkLane _lane;
    private readonly Func<string> _folder;
    private readonly object _gate = new();
    private readonly List<ArrivedFile> _arrived = [];

    private BlockingCollection<BulkDelivery>? _arrivals;
    private Thread? _worker;
    private FeatureContext? _context;
    private FastPathListener? _fast;
    private FastPathFlight? _flight;

    /// <summary>
    /// Cancelled when the feature stops, so a file already streaming out on the fast path
    /// goes with it. Never disposed: the send it is cancelling holds the token, and taking
    /// the source away underneath it would turn an ordinary shutdown into a transfer the
    /// user is told broke.
    /// </summary>
    private CancellationTokenSource? _sending;
    private string? _fault;
    private long _sent;
    private long _received;

    /// <summary>
    /// Where the fast path dials and waits, worked out once when the feature starts.
    ///
    /// <para>
    /// One number for both, and it comes from this machine's own data port. Both ends take
    /// the data port from the same pairing payload, so both arrive at the same number — and
    /// in the receiving role there is nothing else to go on anyway: the config does not name
    /// the other machine, and the port a datagram arrived from is an ephemeral one that says
    /// nothing about where that machine listens.
    /// </para>
    /// </summary>
    private int _fastPort = PairingPayload.FastPathPort(PairingPayload.DefaultPort);

    /// <summary>
    /// The pairing key, taken once at start. The fast path seals with it directly rather than
    /// through the shared socket, which is the one thing on this path the transport cannot do
    /// on a feature's behalf.
    /// </summary>
    private byte[] _key = [];

    public FilesFeature(BulkHost bulk) : this(bulk, FileNames.Downloads) { }

    /// <summary>Test seam: where files land is the only thing here that touches the machine.</summary>
    public FilesFeature(BulkHost bulk, Func<string> folder)
    {
        _lane = bulk.Lane(BulkKind.File);
        _folder = folder;

        // A file is put together on disk rather than in memory, and in the folder it is
        // going to end up in. The last step of a transfer is then a rename, which costs the
        // same whatever the size, instead of a copy from one volume to another — which for
        // four gigabytes means writing four gigabytes a second time.
        _lane.Staging = Folder;
    }

    public string Id => "files";
    public string Title => Strings.Feature_Files_Title;
    public bool IsOptional => true;

    /// <summary>None: the bulk types are claimed by <see cref="BulkHost"/>.</summary>
    public IReadOnlyList<PacketType> HandledTypes => [];

    public bool IsEnabled(ReceiverConfig config) => config.Files;

    public void Start(FeatureContext context)
    {
        var arrivals = new BlockingCollection<BulkDelivery>();
        var worker = new Thread(() => Run(arrivals, context))
        {
            IsBackground = true,
            Name = "hexbridge.files",
        };

        context.Config.TryGetKey(out var key, out _);

        lock (_gate)
        {
            _context = context;
            _arrivals = arrivals;
            _worker = worker;
            _fault = null;
            _flight = null;
            _sent = 0;
            _received = 0;
            _key = key;
            _fastPort = FastPortFor(context.Config);
            _sending = new CancellationTokenSource();
            _arrived.Clear();
        }

        // Before the lane is listening, which is the moment nothing can be in flight: a
        // file offered to a feature with nobody on it is turned down, so anything
        // half-written in there belongs to a run that ended without being able to clear up
        // after itself — a machine that lost power, or was switched off mid-transfer.
        FileNames.SweepPartials(Folder());

        _lane.Delivered += OnDelivered;
        _lane.Finished += OnFinished;

        worker.Start();
        context.Log(LogLevel.Info, Loc.F(Strings.Log_Files_On, _folder()));

        // After the queue exists, because this is the second thing that fills it.
        OpenFastPath(context, key);
    }

    public Task StopAsync()
    {
        // First, and outside the lock: nothing new may arrive once the queue is being
        // closed, and the port has to be back before anything tries to claim it again — a
        // feature switched off and on again in the settings would otherwise find its own
        // socket still holding it.
        CloseFastPath();

        _lane.Delivered -= OnDelivered;
        _lane.Finished -= OnFinished;
        _lane.Reset();

        BlockingCollection<BulkDelivery>? arrivals;
        Thread? worker;
        lock (_gate)
        {
            arrivals = _arrivals;
            worker = _worker;
            _arrivals = null;
            _worker = null;
            _context = null;
            _flight = null;
        }

        // Completing the queue is what ends the worker's wait; whatever is still in it is
        // a file that arrived whole and is worth finishing, so it is written on the way out.
        arrivals?.CompleteAdding();
        worker?.Join(TimeSpan.FromSeconds(2));
        arrivals?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Never called: the bulk types belong to <see cref="BulkHost"/>.</summary>
    public void OnPacket(in Header header, ReadOnlySpan<byte> payload)
    {
    }

    public FeatureState CaptureState()
    {
        string? fault;
        bool running;
        long sent, received;
        ArrivedFile[] arrived;
        FastPathFlight? fast;

        lock (_gate)
        {
            fault = _fault;
            running = _arrivals is not null;
            sent = _sent;
            received = _received;
            arrived = [.. _arrived];
            fast = _flight;
        }

        var folder = Folder();
        if (!running)
        {
            return new FilesState
            {
                Status = fault is null ? FeatureStatus.Stopped : FeatureStatus.Failed,
                Headline = fault is null ? Strings.Feature_Files_Stopped : Strings.Feature_Files_Failed,
                Detail = fault,
                Fault = fault,
                Folder = folder,
            };
        }

        // Two paths, one progress bar. The page does not say which one is carrying the file
        // — that belongs in the log — but it has to say that one of them is: a four-gigabyte
        // file crossing in silence looks exactly like nothing happening.
        var slow = _lane.Progress().FirstOrDefault();
        var moving = fast is not null || slow is not null;
        var moved = sent + received;

        return new FilesState
        {
            Status = fault is not null ? FeatureStatus.Failed
                : moving ? FeatureStatus.Live
                : moved == 0 ? FeatureStatus.Waiting
                : FeatureStatus.Live,
            Headline = fault is not null ? Strings.Feature_Files_Failed
                : moving ? Strings.Feature_Files_Transferring
                : moved == 0 ? Strings.Feature_Files_Waiting
                : Strings.Feature_Files_Live,
            Detail = fault ?? (arrived.Length == 0
                ? Strings.Feature_Files_Detail_Empty
                : Loc.F(Strings.Feature_Files_Detail_Last, arrived[0].Name)),
            Fault = fault,
            Folder = folder,
            Sent = sent,
            Received = received,
            Arrived = arrived,
            TransferDescription = fast?.Name ?? slow?.Description,
            TransferDirection = fast?.Direction ?? slow?.Direction,
            Progress = fast?.Fraction ?? slow?.Fraction ?? 0,
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // MARK: - Sending

    /// <summary>
    /// Offers a file to the other machine. Returns null when it is on its way, or a sentence
    /// the user can read when it is not.
    ///
    /// <para>
    /// The file is never read into memory — it is opened and left open, and the channel
    /// reads each chunk out of it as it goes, the ones it has to send a second time
    /// included. What this does do before any of that is read it once to hash it, because
    /// the hash travels in the offer and the offer goes first; so it is called from a
    /// thread that can afford to wait, which for four gigabytes is seconds.
    /// </para>
    ///
    /// <para>
    /// The size is checked from the directory entry rather than from the file: a refusal
    /// that had to open and hash the file first would be the slowest way there is of saying
    /// no.
    /// </para>
    /// </summary>
    public string? Send(string path)
    {
        FeatureContext? context;
        lock (_gate) context = _context;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return Loc.F(Strings.Err_Files_Gone, Path.GetFileName(path));
            if (file.Length > Bulk.MaxSizeFor(BulkKind.File)) return BulkChannel.TooBig(BulkKind.File, file.Length);

            // The contract's own rule for kind 2: the description is the name and nothing
            // else, and it is trimmed before it is sent rather than by whoever receives it.
            var name = FileNames.Sanitise(Path.GetFileName(path));

            // Said before either path is tried rather than after one succeeded: the fast
            // path can hold this thread for as long as a gigabyte takes, and a log that says
            // nothing until it is over is a log somebody reads while wondering whether their
            // drop registered at all.
            context?.Log(LogLevel.Info, Loc.F(Strings.Log_Files_Sending, name, Loc.Size(file.Length)));

            switch (TryFastPath(context, path, name, file.Length))
            {
                case FastPathOutcome.Sent:
                    return null;

                case FastPathOutcome.Broken:
                    // Not retried on the slow path: the other machine deleted what it had,
                    // so this would move the whole file again — minutes, at the speeds that
                    // make the fast path worth having in the first place.
                    var broken = Loc.F(Strings.Err_Files_Fast_Broken, name);
                    lock (_gate) _fault = broken;
                    return broken;
            }

            _lane.Offer(BulkFormat.Opaque, BulkSource.FromFile(path), name, DateTime.UtcNow, out var error);
            return error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.Message;
        }
    }

    /// <summary>Where arriving files land. Never throws; a broken answer is not worth a page.</summary>
    public string Folder()
    {
        try
        {
            return _folder();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void OnFinished(BulkResult result)
    {
        // A refusal used to end here in silence: the file was dropped, nothing crossed,
        // and nothing on either screen said why. For a file the refusal can only mean one
        // thing — unlike the clipboard, nothing on this channel ever already holds a copy
        // of a file — so it can be named instead of guessed at.
        if (result.Outcome == BulkOutcome.AlreadyThere)
        {
            FeatureContext? refused;
            lock (_gate)
            {
                _fault = Loc.F(Strings.Feature_Files_Refused, result.Description);
                refused = _context;
            }
            refused?.Log(LogLevel.Warning, Loc.F(Strings.Feature_Files_Refused, result.Description));
            return;
        }

        if (result.Outcome != BulkOutcome.Delivered) return;

        FeatureContext? context;
        lock (_gate)
        {
            _sent++;
            _fault = null;
            context = _context;
        }
        context?.Log(LogLevel.Info, Loc.F(Strings.Log_Files_Sent, result.Description));
    }

    // MARK: - The fast path

    /// <summary>
    /// Claims data port + 2, which is where a file arrives when it does not have to be cut
    /// into datagrams.
    ///
    /// <para>
    /// A port somebody else holds is a warning and nothing more. Files still cross on the UDP
    /// channel, more slowly, and that is a great deal better than a feature that refuses to
    /// start; the likeliest holder of the port is this machine's other half, since both roles
    /// are meant to be able to run here at once.
    /// </para>
    /// </summary>
    private void OpenFastPath(FeatureContext context, byte[] key)
    {
        // No usable pairing key means no transport either, so there is nothing to listen for.
        if (key.Length == 0) return;

        int port;
        lock (_gate) port = _fastPort;

        var listener = new FastPathListener(BindFor(context.Config), port, key, Folder);
        listener.Landed += OnDelivered;
        listener.Moving += OnMoving;
        listener.Note += context.Log;

        try
        {
            listener.Start();
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            listener.Dispose();
            context.Log(LogLevel.Warning, Loc.F(Strings.Log_Files_Fast_Off, port, ex.Message));
            return;
        }

        lock (_gate) _fast = listener;
        context.Log(LogLevel.Info, Loc.F(Strings.Log_Files_Fast_On, listener.Port));
    }

    /// <summary>Gives the port back and stops whatever is still going out on it.</summary>
    private void CloseFastPath()
    {
        FastPathListener? listener;
        CancellationTokenSource? sending;
        lock (_gate)
        {
            listener = _fast;
            sending = _sending;
            _fast = null;
            _sending = null;
        }

        sending?.Cancel();
        listener?.Dispose();
    }

    /// <summary>
    /// Tries the stream, and answers what happened so the caller can decide between «done»,
    /// «go the slow way» and «tell the user».
    /// </summary>
    private FastPathOutcome TryFastPath(FeatureContext? context, string path, string name, long size)
    {
        if (context is null) return FastPathOutcome.Unreachable;

        // An empty file is refused, and it is refused in one place: the channel's own check,
        // which both kinds go through. Streaming it here would make «empty» mean one thing
        // for a file and another for everything else.
        if (size <= 0) return FastPathOutcome.Unreachable;

        byte[] key;
        int port;
        FastPathListener? ours;
        CancellationToken token;
        lock (_gate)
        {
            key = _key;
            port = _fastPort;
            ours = _fast;
            token = _sending?.Token ?? new CancellationToken(canceled: true);
        }

        if (key.Length == 0) return FastPathOutcome.Unreachable;
        if (context.Peer is not { } peer) return FastPathOutcome.Unreachable;

        var where = new IPEndPoint(peer.Address, port);

        // Never this machine's own listener. Both roles can run here at once — that is what
        // the sending role's ephemeral data port is for — and on loopback the address the
        // dialling half would aim at is the address the waiting half is bound to. Without
        // this, a file dropped on one of them would land in this machine's own Downloads
        // folder while the other machine got nothing at all.
        if (ours is not null && ours.Port == where.Port && IPAddress.IsLoopback(where.Address))
        {
            return FastPathOutcome.Unreachable;
        }

        var started = DateTime.UtcNow;
        var outcome = FastPathSend.Send(where, key, path, name, OnMoving, token);

        switch (outcome)
        {
            case FastPathOutcome.Sent:
                lock (_gate)
                {
                    _sent++;
                    _fault = null;
                }
                // Somebody who wonders why a gigabyte took three seconds gets an answer
                // rather than a mystery: which path carried it, and how fast it went.
                context.Log(LogLevel.Info, Loc.F(
                    Strings.Log_Files_Fast_Sent,
                    name,
                    Loc.Size(size),
                    Loc.Throughput(size, DateTime.UtcNow - started)));
                break;

            case FastPathOutcome.Unreachable:
                context.Log(LogLevel.Info, Loc.F(Strings.Log_Files_Fast_Fallback, name));
                break;

            case FastPathOutcome.Broken:
                // The sentence the user reads is the caller's to write; it is the caller
                // that knows this was a file somebody dropped rather than a line in a log.
                break;
        }

        return outcome;
    }

    /// <summary>What the fast path has on the wire, in either direction. Null means nothing.</summary>
    private void OnMoving(FastPathFlight? flight)
    {
        lock (_gate) _flight = flight;
    }

    /// <summary>
    /// Which address the listener binds, which is the one the data channel was told to wait
    /// on. Binding everything when the config named one interface would be this feature
    /// quietly reaching further than the rest of the program does.
    /// </summary>
    private static IPAddress BindFor(ReceiverConfig config)
    {
        try
        {
            return ReceiverConfig.ParseEndpoint(config.Listen, PairingPayload.DefaultPort).Address;
        }
        catch (Exception)
        {
            return IPAddress.Any;
        }
    }

    private static int FastPortFor(ReceiverConfig config)
    {
        try
        {
            return PairingPayload.FastPathPort(
                ReceiverConfig.ParseEndpoint(config.Listen, PairingPayload.DefaultPort).Port);
        }
        catch (Exception)
        {
            return PairingPayload.FastPathPort(PairingPayload.DefaultPort);
        }
    }

    // MARK: - Receiving

    /// <summary>
    /// Called on the channel's thread, so it does nothing but queue: giving a file its real
    /// name can mean waiting on a disk, and that thread has acks to send.
    ///
    /// <para>
    /// A file arrives as a temporary file that is already whole and already verified, and
    /// from this moment it belongs to this feature — if the queue will not take it, it is
    /// this feature that has to clear it away.
    /// </para>
    /// </summary>
    private void OnDelivered(BulkDelivery delivery)
    {
        BlockingCollection<BulkDelivery>? arrivals;
        lock (_gate) arrivals = _arrivals;

        try
        {
            if (arrivals is null)
            {
                Discard(delivery);
                return;
            }
            arrivals.Add(delivery);
        }
        catch (InvalidOperationException)
        {
            // The feature was switched off between the delivery and this line.
            Discard(delivery);
        }
    }

    /// <summary>
    /// Throws away an object that never made it to a name. Never throws: what is left
    /// behind is swept the next time the feature starts, and a failure here would cost the
    /// thread it is on.
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

    /// <summary>
    /// One thread, so two files that land together cannot both decide they are «notes (2)».
    /// </summary>
    private void Run(BlockingCollection<BulkDelivery> arrivals, FeatureContext context)
    {
        foreach (var delivery in arrivals.GetConsumingEnumerable())
        {
            try
            {
                // Two ways in, and which one it is says how the object crossed. A file is
                // staged on disk and gets its real name by being renamed; anything that
                // came the other way was small enough to be held, and is written out here.
                var path = delivery.Path is { } staged
                    ? FileNames.Adopt(Folder(), delivery.Description, staged)
                    : FileNames.Save(Folder(), delivery.Description, delivery.Bytes ?? []);
                var arrival = new ArrivedFile(
                    Path.GetFileName(path), path, delivery.Size, DateTime.UtcNow);

                lock (_gate)
                {
                    _received++;
                    _fault = null;
                    _arrived.Insert(0, arrival);
                    if (_arrived.Count > Remembered) _arrived.RemoveAt(_arrived.Count - 1);
                }

                context.Log(LogLevel.Info,
                    Loc.F(Strings.Log_Files_Received, arrival.Name, Loc.Size(arrival.Bytes)));
            }
            catch (Exception ex)
            {
                // A full disk or a folder somebody took the rights to must not cost the
                // thread: the next file may well land, and the page says what happened.
                // What was staged goes with it — a file that could not be given its name
                // is not one to leave lying under the name it was streamed into.
                Discard(delivery);
                lock (_gate) _fault = ex.Message;
                context.Log(LogLevel.Error, Loc.F(Strings.Log_Files_WriteFailed, ex.Message));
            }
        }
    }
}
