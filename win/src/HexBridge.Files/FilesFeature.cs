using System.Collections.Concurrent;

using HexBridge.Localization;

namespace HexBridge.Files;

/// <summary>
/// Files, in both directions: one dropped on this window goes to the other machine, and one
/// sent from there lands in this user's Downloads folder.
///
/// <para>
/// It owns no transport. Everything rides the file lane of <see cref="BulkHost"/> — the same
/// reliable channel the clipboard uses, which is the whole reason the kind exists in the
/// contract — so this class does two things the channel must not know about: it decides what
/// a received name is allowed to become on this disk, and it reads the file being sent.
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
    private string? _fault;
    private long _sent;
    private long _received;

    public FilesFeature(BulkHost bulk) : this(bulk, FileNames.Downloads) { }

    /// <summary>Test seam: where files land is the only thing here that touches the machine.</summary>
    public FilesFeature(BulkHost bulk, Func<string> folder)
    {
        _lane = bulk.Lane(BulkKind.File);
        _folder = folder;
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

        lock (_gate)
        {
            _context = context;
            _arrivals = arrivals;
            _worker = worker;
            _fault = null;
            _sent = 0;
            _received = 0;
            _arrived.Clear();
        }

        _lane.Delivered += OnDelivered;
        _lane.Finished += OnFinished;

        worker.Start();
        context.Log(LogLevel.Info, Loc.F(Strings.Log_Files_On, _folder()));
    }

    public Task StopAsync()
    {
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

        lock (_gate)
        {
            fault = _fault;
            running = _arrivals is not null;
            sent = _sent;
            received = _received;
            arrived = [.. _arrived];
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

        var flight = _lane.Progress().FirstOrDefault();
        var moved = sent + received;

        return new FilesState
        {
            Status = fault is not null ? FeatureStatus.Failed
                : flight is not null ? FeatureStatus.Live
                : moved == 0 ? FeatureStatus.Waiting
                : FeatureStatus.Live,
            Headline = fault is not null ? Strings.Feature_Files_Failed
                : flight is not null ? Strings.Feature_Files_Transferring
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
            TransferDescription = flight?.Description,
            TransferDirection = flight?.Direction,
            Progress = flight?.Fraction ?? 0,
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // MARK: - Sending

    /// <summary>
    /// Offers a file to the other machine. Returns null when it is on its way, or a sentence
    /// the user can read when it is not.
    ///
    /// <para>
    /// Reads the whole file, so it is called from a thread that can afford to wait. The size
    /// is checked from the directory entry first: the ceiling exists because both ends hold
    /// the object in memory, and reading a four-gigabyte file in order to refuse it would be
    /// the one way to run out of memory on the way to saying no.
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
            if (file.Length > Bulk.MaxObjectSize)
            {
                return Loc.F(Strings.Err_Bulk_TooBig,
                    Loc.Size(file.Length),
                    Bulk.MaxObjectSize / (1024 * 1024));
            }

            var bytes = File.ReadAllBytes(path);

            // The contract's own rule for kind 2: the description is the name and nothing
            // else, and it is trimmed before it is sent rather than by whoever receives it.
            var name = FileNames.Sanitise(Path.GetFileName(path));
            _lane.Offer(BulkFormat.Opaque, bytes, name, DateTime.UtcNow, out var error);
            if (error is not null) return error;

            context?.Log(LogLevel.Info, Loc.F(Strings.Log_Files_Sending, name, Loc.Size(bytes.Length)));
            return null;
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

    // MARK: - Receiving

    /// <summary>
    /// Called on the socket thread, so it does nothing but queue: writing sixty megabytes to
    /// disk there would stop every packet on the link, voice included, for as long as it took.
    /// </summary>
    private void OnDelivered(BulkDelivery delivery)
    {
        BlockingCollection<BulkDelivery>? arrivals;
        lock (_gate) arrivals = _arrivals;

        try
        {
            arrivals?.Add(delivery);
        }
        catch (InvalidOperationException)
        {
            // The feature was switched off between the delivery and this line.
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
                var path = FileNames.Save(Folder(), delivery.Description, delivery.Bytes);
                var arrival = new ArrivedFile(
                    Path.GetFileName(path), path, delivery.Bytes.Length, DateTime.UtcNow);

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
                lock (_gate) _fault = ex.Message;
                context.Log(LogLevel.Error, Loc.F(Strings.Log_Files_WriteFailed, ex.Message));
            }
        }
    }
}
