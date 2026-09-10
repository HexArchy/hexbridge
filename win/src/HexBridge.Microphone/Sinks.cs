using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HexBridge.Microphone;

public interface IAudioSink : IDisposable
{
    string Describe();
    void Start();

    /// <summary>Render endpoint name, when there is a real device behind the sink.</summary>
    string? DeviceName => null;

    /// <summary>The capture endpoint the user should pick in games, when we can name it.</summary>
    string? PairedCaptureName => null;

    /// <summary>
    /// Raised when playback dies on its own — the cable was uninstalled, Steam restarted.
    /// The sink cannot know whether anyone is listening on a console or in a window, so it
    /// reports instead of printing.
    /// </summary>
    event Action<string>? Faulted;
}

/// <summary>
/// Finds the render endpoint whose paired capture endpoint games will see as a microphone.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DeviceCatalog
{
    /// <summary>
    /// Tried in order. Steam's driver ships with every Steam install; the VB-Audio and
    /// VoiceMeeter cables are the usual alternatives.
    /// </summary>
    public static readonly string[] PreferredPatterns =
    [
        "Steam Streaming Microphone",
        "CABLE Input",
        "VoiceMeeter Aux Input",
        "VoiceMeeter Input",
        "VB-Audio",
    ];

    /// <summary>Steam also exposes a 16-channel variant that cannot carry plain stereo.</summary>
    private const string UnsupportedVariant = "16ch";

    public static List<MMDevice> RenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public static List<MMDevice> CaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
    }

    /// <summary>
    /// The microphone the sending role reads. An explicit selector matches an endpoint id or
    /// part of a name; nothing at all means the system's communications default, which is
    /// the endpoint Windows itself hands to a voice application.
    ///
    /// <para>
    /// Deliberately without the preference list <see cref="Pick"/> carries. On the receiving
    /// side there is one right answer — the virtual cable games read from — and picking it
    /// saves the user a decision. On this side the right answer is a physical microphone
    /// nothing here can rank, and guessing is how somebody ends up broadcasting the wrong
    /// room.
    /// </para>
    /// </summary>
    public static MMDevice? PickCapture(string? selector)
    {
        var devices = CaptureDevices();

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return devices.FirstOrDefault(d =>
                d.ID.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase));
        }

        using var enumerator = new MMDeviceEnumerator();
        return enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var preferred)
            ? preferred
            : devices.FirstOrDefault();
    }

    /// <summary>
    /// Resolves an explicit selector (substring or endpoint id), or falls back to the
    /// first preferred virtual cable that is present.
    /// </summary>
    public static MMDevice? Pick(string? selector)
    {
        var devices = RenderDevices();

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return devices.FirstOrDefault(d =>
                d.ID.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var pattern in PreferredPatterns)
        {
            var match = devices.FirstOrDefault(d =>
                d.FriendlyName.Contains(pattern, StringComparison.OrdinalIgnoreCase) &&
                !d.FriendlyName.Contains(UnsupportedVariant, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>The capture endpoint the user should select in games, if we can name it.</summary>
    public static string? PairedCaptureName(MMDevice render)
    {
        // Virtual cables name the two halves consistently, e.g. "Speakers (Steam Streaming
        // Microphone)" / "Microphone (Steam Streaming Microphone)".
        var open = render.FriendlyName.IndexOf('(');
        var close = render.FriendlyName.LastIndexOf(')');
        if (open < 0 || close <= open) return null;

        var inner = render.FriendlyName.Substring(open + 1, close - open - 1);
        return CaptureDevices().FirstOrDefault(d => d.FriendlyName.Contains(inner, StringComparison.OrdinalIgnoreCase))?.FriendlyName;
    }
}

/// <summary>Renders into a virtual cable through WASAPI shared mode.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiSink : IAudioSink
{
    private readonly WasapiPlayer _player;
    private readonly string _description;

    public event Action<string>? Faulted;

    public string? DeviceName { get; }
    public string? PairedCaptureName { get; }

    public WasapiSink(IWaveProvider provider, string? selector, int latencyMs)
    {
        var device = DeviceCatalog.Pick(selector)
            ?? throw new InvalidOperationException(
                selector is null
                    ? "не найдено виртуальное устройство. Установите Steam (Steam Streaming Microphone) " +
                      "или VB-Audio Virtual Cable, либо укажите --device"
                    : $"устройство вывода не найдено: {selector}");

        _player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(latencyMs)
            .Build();

        // Shared mode resamples for us, but a cable running at something other than
        // 48 kHz stereo is worth surfacing: it usually means the endpoint is misconfigured.
        var mix = _player.DeviceMixFormat;
        var paired = DeviceCatalog.PairedCaptureName(device);
        DeviceName = device.FriendlyName;
        PairedCaptureName = paired;
        _description = $"{device.FriendlyName} [{mix.SampleRate} Гц, {mix.Channels} ch]" +
                       (paired is null ? "" : $" → в играх выбирайте «{paired}»");

        try
        {
            _player.Init(provider);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"«{device.FriendlyName}» не принимает 48000 Гц / 2 канала / 32-bit float " +
                $"(текущий формат устройства: {mix.SampleRate} Гц, {mix.Channels} ch). " +
                "Откройте Параметры звука → свойства этого устройства и выберите формат " +
                $"«2 канала, 32 бит, 48000 Гц». Исходная ошибка: {ex.Message}", ex);
        }

        // If Steam restarts, or the cable is uninstalled mid-session, playback just
        // stops; say so instead of going quiet with no explanation.
        _player.PlaybackStopped += (_, e) =>
        {
            Faulted?.Invoke(e.Exception is null
                ? "hexbridge: вывод остановлен — устройство пропало"
                : $"hexbridge: вывод остановлен: {e.Exception.Message}");
        };
    }

    public string Describe() => _description;

    public void Start() => _player.Play();

    public void Dispose()
    {
        _player.Stop();
        _player.Dispose();
    }
}

/// <summary>
/// Pulls from the provider on a paced thread without a real device. Used for
/// end-to-end testing off Windows, and for <c>--output wav:</c> captures.
/// </summary>
public sealed class PumpSink : IAudioSink
{
    private readonly IWaveProvider _provider;
    private readonly WaveFileWriter? _writer;
    private readonly string _description;
    private readonly CancellationTokenSource _cancel = new();
    private Thread? _thread;

    // Nothing can pull a paced thread out from under us, so this never fires here.
#pragma warning disable CS0067
    public event Action<string>? Faulted;
#pragma warning restore CS0067

    public PumpSink(IWaveProvider provider, string? wavPath)
    {
        _provider = provider;
        if (wavPath is not null)
        {
            _writer = new WaveFileWriter(wavPath, provider.WaveFormat);
            _description = $"файл {wavPath}";
        }
        else
        {
            _description = "null (звук никуда не выводится)";
        }
    }

    public string Describe() => _description;

    public void Start()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "hexbridge-pump" };
        _thread.Start();
    }

    private void Pump()
    {
        var bytesPerFrame = MicWaveProvider.FrameSamples * _provider.WaveFormat.Channels * sizeof(float);
        var buffer = new byte[bytesPerFrame];
        var clock = Stopwatch.StartNew();
        long frames = 0;

        while (!_cancel.IsCancellationRequested)
        {
            var read = _provider.Read(buffer);
            _writer?.Write(buffer, 0, read);
            frames++;

            // Stay on a 20 ms grid rather than accumulating sleep drift.
            var due = TimeSpan.FromMilliseconds(frames * 20);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                _cancel.Token.WaitHandle.WaitOne(wait);
            }
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _writer?.Dispose();
        _cancel.Dispose();
    }
}
