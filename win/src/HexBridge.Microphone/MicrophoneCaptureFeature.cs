using System.Buffers.Binary;

namespace HexBridge.Microphone;

/// <summary>
/// The voice path in the other direction: capture → Opus → AUDIO packets on the shared
/// socket. The sending role's half of <see cref="MicrophoneFeature"/>.
///
/// <para>
/// It answers to the same feature id and publishes the same <see cref="MicrophoneState"/>,
/// so every page, tab and log line written for the receiving side works here unchanged.
/// Exactly one of the two is ever enabled — <see cref="IsEnabled"/> is the whole of that —
/// and neither claims a packet type, so registering both costs nothing.
/// </para>
///
/// <para>
/// Not optional, for the same reason the other half is not: a machine whose whole job is to
/// hand over a microphone, running with no microphone and saying nothing about it, is worse
/// than one that refuses to start and says why.
/// </para>
/// </summary>
public sealed class MicrophoneCaptureFeature : IFeature
{
    /// <summary>
    /// Nothing. AUDIO only leaves from here, PONG is the transport's own business, and a
    /// feature that claimed a type it never reads would only be in the way of one that does.
    /// </summary>
    private static readonly PacketType[] Types = [];

    private readonly object _gate = new();
    private readonly RateMeter _rate = new();

    private IAudioSource? _source;
    private VoiceEncoder? _encoder;
    private FeatureContext? _context;
    private string? _fault;

    private long _sent;
    private uint _frameIndex;
    private float _peak;
    private float _peakHold;
    private int _bitrate;

    public string Id => "microphone";
    public string Title => "Микрофон";
    public bool IsOptional => false;
    public IReadOnlyList<PacketType> HandledTypes => Types;

    /// <summary>On exactly when this machine is the one giving its microphone away.</summary>
    public bool IsEnabled(ReceiverConfig config) => config.Role == BridgeRole.Sender;

    public void Start(FeatureContext context)
    {
        var config = context.Config;
        var source = Open(config);

        VoiceEncoder encoder;
        try
        {
            encoder = new VoiceEncoder(
                config.Bitrate,
                fec: true,
                expectedLossPercent: config.ExpectedLossPercent);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        lock (_gate)
        {
            _source = source;
            _encoder = encoder;
            _context = context;
            _fault = null;
            _sent = 0;
            _frameIndex = 0;
            _peak = 0;
            _peakHold = 0;
            _bitrate = config.Bitrate;
        }
        _rate.Reset();

        try
        {
            source.Faulted += OnSourceFaulted;
            source.Start(OnFrame);
        }
        catch
        {
            source.Faulted -= OnSourceFaulted;
            source.Dispose();
            lock (_gate)
            {
                _source = null;
                _encoder = null;
            }
            throw;
        }

        context.Log(LogLevel.Info, $"hexbridge: захват звука — {source.Describe()}");
    }

    private static IAudioSource Open(ReceiverConfig config) => config.Input switch
    {
        "null" => new SilentSource(),
        "tone" => new ToneSource(),
        var input when input.StartsWith("wav:", StringComparison.Ordinal) =>
            new WaveFileSource(input[4..], config.InputGain),
        // WasapiSource is Windows-only by construction; say so here rather than letting a
        // native load failure surface somewhere less obvious.
        _ => OperatingSystem.IsWindows()
            ? OpenWasapi(config)
            : throw new PlatformNotSupportedException("захват через WASAPI доступен только на Windows"),
    };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IAudioSource OpenWasapi(ReceiverConfig config)
    {
        var device = DeviceCatalog.PickCapture(config.InputDevice)
            ?? throw new InvalidOperationException(
                config.InputDevice is null
                    ? "в системе нет ни одного микрофона. Подключите его и нажмите «Запустить»"
                    : $"устройство ввода не найдено: {config.InputDevice}");

        return new WasapiSource(device, config.InputGain);
    }

    public Task StopAsync()
    {
        IAudioSource? source;
        lock (_gate)
        {
            source = _source;
            _source = null;
            _encoder = null;
        }

        if (source is not null)
        {
            source.Faulted -= OnSourceFaulted;
            source.Dispose();
        }
        return Task.CompletedTask;
    }

    /// <summary>Nothing is routed here; AUDIO only ever leaves this machine.</summary>
    public void OnPacket(in Header header, ReadOnlySpan<byte> payload) { }

    /// <summary>
    /// One captured frame, on the capture thread. Encodes it and puts it on the wire, which
    /// is the entire hot path — fifty times a second, for hours.
    /// </summary>
    private void OnFrame(ReadOnlySpan<float> frame, float peak)
    {
        VoiceEncoder? encoder;
        FeatureContext? context;
        lock (_gate)
        {
            encoder = _encoder;
            context = _context;
            _peak = peak;
            if (peak > _peakHold) _peakHold = peak;
        }
        if (encoder is null || context is null) return;

        // Muted means nothing goes out — the same as the Mac. The far end is not left
        // guessing: the mute flag rides the keepalive, which keeps going through the silence.
        if (context.Muted) return;

        byte[] payload;
        try
        {
            var packet = encoder.Encode(frame);
            payload = new byte[4 + packet.Length];
            packet.CopyTo(payload.AsSpan(4));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, $"кодировщик Opus: {ex.Message}");
            return;
        }

        uint index;
        lock (_gate)
        {
            // The frame counter is separate from the transport's seq because seq also counts
            // HELLO: using it here would look like one lost frame a second in the jitter
            // buffer. It advances only for frames that actually left, so a mute is a gap in
            // time rather than a hole in the numbering.
            index = _frameIndex++;
            _sent++;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload, index);

        context.Send(PacketType.Audio, payload);
    }

    /// <summary>
    /// The peer restarted. Nothing buffered here belongs to it — we hold no state about the
    /// far end — but the frame numbering has to start over, or the receiver's freshly reset
    /// jitter buffer would prime on a number thousands of frames into the future and then sit
    /// there discarding everything that follows as late.
    /// </summary>
    public void OnSessionReset()
    {
        lock (_gate) _frameIndex = 0;
    }

    public FeatureState CaptureState()
    {
        IAudioSource? source;
        VoiceEncoder? encoder;
        long sent;
        float peak, peakHold;
        int bitrate;
        bool muted;

        lock (_gate)
        {
            source = _source;
            encoder = _encoder;
            sent = _sent;
            peak = _peak;
            peakHold = _peakHold;
            _peakHold = 0;
            bitrate = _bitrate;
            muted = _context?.Muted ?? false;
        }

        var fault = Volatile.Read(ref _fault);
        if (source is null)
        {
            return new MicrophoneState
            {
                IsCapture = true,
                Status = fault is null ? FeatureStatus.Stopped : FeatureStatus.Failed,
                Headline = fault is null ? "Остановлено" : "Ошибка",
                Detail = fault,
                Fault = fault,
            };
        }

        return new MicrophoneState
        {
            IsCapture = true,
            Status = fault is not null ? FeatureStatus.Failed
                : muted ? FeatureStatus.Warning
                : sent == 0 ? FeatureStatus.Waiting
                : FeatureStatus.Live,
            Headline = fault is not null ? "Ошибка захвата"
                : muted ? "Микрофон заглушен"
                : "Звук уходит",
            Detail = fault ?? source.Describe(),
            Fault = fault,
            OutputDescription = source.Describe(),
            DeviceName = source.DeviceName,
            PacketsPerSecond = _rate.Sample(sent, DateTime.UtcNow),
            Sent = sent,
            IsMuted = muted,
            Bitrate = bitrate,
            Peak = peak,
            PeakHold = peakHold,
            LastPacketBytes = encoder?.LastPacketBytes ?? 0,
        };
    }

    private void OnSourceFaulted(string message)
    {
        Volatile.Write(ref _fault, message);
        FeatureContext? context;
        lock (_gate) context = _context;
        context?.Log(LogLevel.Error, message);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
