using System.Buffers.Binary;

namespace HexBridge.Microphone;

/// <summary>
/// The voice path as a feature: jitter buffer, Opus decoder and an audio sink, fed by
/// AUDIO packets off the shared socket.
///
/// Not optional. Without a working output there is nothing to listen to, and a receiver
/// that silently starts anyway is worse than one that says why it did not.
/// </summary>
public sealed class MicrophoneFeature : IFeature
{
    private static readonly PacketType[] Types = [PacketType.Audio];

    private readonly object _gate = new();
    private readonly RateMeter _rate = new();

    private MicWaveProvider? _provider;
    private IAudioSink? _sink;
    private FeatureContext? _context;
    private string? _fault;

    public string Id => "microphone";
    public string Title => "Микрофон";
    public bool IsOptional => false;
    public IReadOnlyList<PacketType> HandledTypes => Types;

    /// <summary>Always on: the receiver exists to carry a microphone.</summary>
    public bool IsEnabled(ReceiverConfig config) => true;

    public void Start(FeatureContext context)
    {
        var config = context.Config;
        var provider = new MicWaveProvider(config.JitterMs, config.MaxJitterMs, config.Gain);

        IAudioSink sink = config.Output switch
        {
            "null" => new PumpSink(provider, null),
            var o when o.StartsWith("wav:", StringComparison.Ordinal) => new PumpSink(provider, o[4..]),
            // WasapiSink is Windows-only by construction; say so here rather than letting a
            // native load failure surface somewhere less obvious.
            _ => OperatingSystem.IsWindows()
                ? new WasapiSink(provider, config.Device, config.LatencyMs)
                : throw new PlatformNotSupportedException("вывод в WASAPI доступен только на Windows"),
        };

        try
        {
            sink.Faulted += OnSinkFaulted;
            sink.Start();
        }
        catch
        {
            sink.Faulted -= OnSinkFaulted;
            sink.Dispose();
            throw;
        }

        lock (_gate)
        {
            _provider = provider;
            _sink = sink;
            _context = context;
            _fault = null;
        }
        _rate.Reset();

        context.Log(LogLevel.Info, $"hexbridge: вывод звука — {sink.Describe()}");
    }

    public Task StopAsync()
    {
        IAudioSink? sink;
        lock (_gate)
        {
            sink = _sink;
            _sink = null;
            _provider = null;
        }

        if (sink is not null)
        {
            sink.Faulted -= OnSinkFaulted;
            sink.Dispose();
        }
        return Task.CompletedTask;
    }

    public void OnPacket(in Header header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 4) return;
        var provider = Volatile.Read(ref _provider);
        if (provider is null) return;

        var frameIndex = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        provider.Push(frameIndex, payload[4..].ToArray());
    }

    public void OnSessionReset() => Volatile.Read(ref _provider)?.Reset();

    public DeliveryStats Delivery
    {
        get
        {
            var provider = Volatile.Read(ref _provider);
            if (provider is null) return default;
            return new DeliveryStats(
                Interlocked.Read(ref provider.Received),
                Interlocked.Read(ref provider.Concealed) + Interlocked.Read(ref provider.DroppedLate));
        }
    }

    public FeatureState CaptureState()
    {
        MicWaveProvider? provider;
        IAudioSink? sink;
        lock (_gate)
        {
            provider = _provider;
            sink = _sink;
        }

        var fault = Volatile.Read(ref _fault);
        if (provider is null || sink is null)
        {
            return new MicrophoneState
            {
                Status = fault is null ? FeatureStatus.Stopped : FeatureStatus.Failed,
                Headline = fault is null ? "Остановлено" : "Ошибка",
                Detail = fault,
                Fault = fault,
            };
        }

        var received = Interlocked.Read(ref provider.Received);
        return new MicrophoneState
        {
            Status = fault is not null ? FeatureStatus.Failed
                : received == 0 ? FeatureStatus.Waiting
                : FeatureStatus.Live,
            Headline = fault is not null ? "Ошибка вывода" : "Звук идёт",
            Detail = fault ?? sink.Describe(),
            Fault = fault,
            OutputDescription = sink.Describe(),
            DeviceName = sink.DeviceName,
            PairedCaptureName = sink.PairedCaptureName,
            PacketsPerSecond = _rate.Sample(received, DateTime.UtcNow),
            Received = received,
            Decoded = Interlocked.Read(ref provider.Decoded),
            Concealed = Interlocked.Read(ref provider.Concealed),
            DroppedLate = Interlocked.Read(ref provider.DroppedLate),
            Underruns = Interlocked.Read(ref provider.Underruns),
            Peak = provider.LastPeak,
            PeakHold = provider.TakePeakHold(),
            Depth = provider.Depth,
            TargetDepth = provider.TargetDepth,
            MaxDepth = provider.MaxDepth,
        };
    }

    private void OnSinkFaulted(string message)
    {
        Volatile.Write(ref _fault, message);
        Volatile.Read(ref _context)?.Log(LogLevel.Error, message);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
