using System.Runtime.InteropServices;
using HexBridge;
using HexBridge.Clipboard;
using HexBridge.Devices;
using HexBridge.Microphone;

const string Usage = """
hexbridge-receiver — принимает микрофон с Mac и отдаёт его как микрофон Windows.

Использование:
  hexbridge-receiver [флаги]         запустить приём
  hexbridge-receiver list-devices    показать устройства вывода и их пары-микрофоны
  hexbridge-receiver keygen          сгенерировать общий ключ (PSK)

Флаги:
  --config PATH     путь к конфигу (по умолчанию config.json рядом с exe)
  --listen [A:]P    что слушать (по умолчанию 0.0.0.0:47702)
  --psk BASE64      общий ключ, 32 байта в base64; должен совпадать с Mac
  --device SEL      часть имени устройства вывода; по умолчанию автоопределение
  --jitter MS       целевая задержка буфера, мс (по умолчанию 60)
  --max-jitter MS   при превышении буфер подрезается (по умолчанию 240)
  --gain F          усиление на выходе, 1.0 — без изменений
  --latency MS      запрошенная задержка WASAPI (по умолчанию 50)
  --relay H:P       регистрироваться на релее вместо прямого приёма
  --output MODE     wasapi (по умолчанию), null или wav:путь — для диагностики
  --no-gamepad      не пробрасывать USB-устройства, только звук
  --usbip PATH      путь к usbip.exe, если он не в C:\Program Files\USBip
  --quiet           не печатать строку статистики раз в 5 секунд
""";

var argv = args.ToList();
string? subcommand = argv.Count > 0 && !argv[0].StartsWith('-') ? argv[0] : null;
if (subcommand is not null) argv.RemoveAt(0);

string? Flag(string name)
{
    var i = argv.IndexOf($"--{name}");
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
}

bool BoolFlag(string name) => argv.Contains($"--{name}");

switch (subcommand)
{
    case "help" or "-h" or "--help":
        Console.WriteLine(Usage);
        return 0;

    case "keygen":
        Console.WriteLine(ReceiverConfig.GenerateKey());
        return 0;

    case "list-devices":
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("список устройств доступен только на Windows");
            return 1;
        }
        ListDevices();
        return 0;
}

// ── Configuration ─────────────────────────────────────────────────────────────

var configPath = Flag("config") ?? ReceiverConfig.DefaultPath;
var config = ReceiverConfig.Load(configPath);

if (Flag("listen") is { } listenFlag) config.Listen = listenFlag;
if (Flag("psk") is { } pskFlag) config.Psk = pskFlag;
if (Flag("device") is { } deviceFlag) config.Device = deviceFlag;
if (Flag("relay") is { } relayFlag) config.Relay = relayFlag;
if (Flag("output") is { } outputFlag) config.Output = outputFlag;
if (Flag("jitter") is { } j && int.TryParse(j, out var jitterMs)) config.JitterMs = jitterMs;
if (Flag("max-jitter") is { } mj && int.TryParse(mj, out var maxJitterMs)) config.MaxJitterMs = maxJitterMs;
if (Flag("gain") is { } g && float.TryParse(g, out var gainValue)) config.Gain = gainValue;
if (Flag("latency") is { } l && int.TryParse(l, out var latencyMs)) config.LatencyMs = latencyMs;
if (Flag("usbip") is { } usbipFlag) config.UsbIpPath = usbipFlag;
if (BoolFlag("no-gamepad")) config.Gamepad = false;

if (!config.TryGetKey(out _, out _))
{
    Console.Error.WriteLine("hexbridge: psk должен быть 32 байта в base64 — сгенерируйте через `hexbridge-receiver keygen`\n");
    Console.Error.WriteLine(Usage);
    return 1;
}

// ── Run ───────────────────────────────────────────────────────────────────────

// The composition root: the one place that names the features. Everything below this line
// — and everything inside ReceiverService — works off the IFeature contract, so a third
// feature is a new class and one more entry here.
await using var receiver = new ReceiverService(
    new MicrophoneFeature(),
    new DevicesFeature(),
    new ClipboardFeature());
receiver.Log += entry =>
{
    if (entry.Level == LogLevel.Error) Console.Error.WriteLine(entry.Message);
    else Console.WriteLine(entry.Message);
};

try
{
    await receiver.StartAsync(config);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"hexbridge: {ex.Message}");
    return 1;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
// Service managers stop us with SIGTERM; shut down cleanly so a wav capture
// gets its header finalised.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cancel.Cancel();
});

var stats = BoolFlag("quiet") ? Task.CompletedTask : StatsLoop(receiver, cancel.Token);

try
{
    await Task.Delay(Timeout.Infinite, cancel.Token);
}
catch (OperationCanceledException)
{
    // Ctrl-C or SIGTERM.
}

await stats;
await receiver.StopAsync();
return 0;

// ── Subcommands ───────────────────────────────────────────────────────────────

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void ListDevices()
{
    foreach (var d in DeviceCatalog.RenderDevices())
    {
        var pairedName = DeviceCatalog.PairedCaptureName(d);
        // Read the name out first: a lambda body does not inherit the platform guard.
        var name = d.FriendlyName;
        var preferred = DeviceCatalog.PreferredPatterns.Any(p =>
            name.Contains(p, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{(preferred ? " *" : "  ")} {name}");
        if (pairedName is not null) Console.WriteLine($"      пара для игр: {pairedName}");
    }
}

// ── Stats line ────────────────────────────────────────────────────────────────

static async Task StatsLoop(ReceiverService receiver, CancellationToken token)
{
    long lastReceived = 0;

    while (!token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var s = receiver.Snapshot;
        var mic = s.Feature<MicrophoneState>("microphone");
        var received = mic?.Received ?? 0;
        var line =
            $"принято {(received - lastReceived) / 5,3} пак/с  " +
            $"пик {(mic is { Peak: > 0 } ? 20 * Math.Log10(mic.Peak) : -99),5:F1} dBFS  " +
            $"декодировано {mic?.Decoded ?? 0}  " +
            $"скрыто {mic?.Concealed ?? 0}  " +
            $"буфер {mic?.Depth ?? 0}  " +
            $"поздних {mic?.DroppedLate ?? 0}  " +
            $"недоборов {mic?.Underruns ?? 0}";
        lastReceived = received;

        // Anything other than the microphone gets one word, whatever it turns out to be:
        // the stats line must not need editing when a feature is added.
        foreach (var feature in s.Features.Values)
        {
            if (feature.Id is "microphone" or "" ) continue;
            if (feature.Status is FeatureStatus.Disabled or FeatureStatus.Stopped) continue;
            line += $"  [{feature.Title}: {feature.Headline}]";
        }

        if (s.Muted) line += "  [MUTED]";
        if (s.LastPacketAt is { } at && DateTime.UtcNow - at > TimeSpan.FromSeconds(3))
        {
            line += $"  отправитель молчит {(DateTime.UtcNow - at).TotalSeconds:F0} с";
        }
        if (s.Rejected > 0) line += $"  отброшено {s.Rejected}";

        Console.WriteLine(line);
    }
}
