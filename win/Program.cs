using System.Runtime.InteropServices;
using HexBridge;
using HexBridge.Clipboard;
using HexBridge.Devices;
using HexBridge.Files;
using HexBridge.Localization;
using HexBridge.Microphone;

// The language, before the first line of output: «System» leaves the machine's own
// culture alone, which is what keeps a Russian console exactly as it was. The console has
// no preferences file of its own — the desktop app's ui.json belongs to the window — so
// there is no choice to offer here, only the system's.
Language.Apply(AppLanguage.System);


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
        Console.WriteLine(Strings.Cli_Usage);
        return 0;

    case "keygen":
        Console.WriteLine(ReceiverConfig.GenerateKey());
        return 0;

    case "list-devices":
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(Strings.Cli_DevicesWindowsOnly);
            return 1;
        }
        ListDevices();
        return 0;

    case "list-inputs":
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(Strings.Cli_DevicesWindowsOnly);
            return 1;
        }
        ListInputs();
        return 0;
}

// ── Configuration ─────────────────────────────────────────────────────────────

var configPath = Flag("config") ?? ReceiverConfig.ResolvedPath();
var config = ReceiverConfig.Load(configPath);

if (Flag("role") is { } roleFlag)
{
    if (!Enum.TryParse<BridgeRole>(roleFlag, ignoreCase: true, out var role))
    {
        Console.Error.WriteLine(Loc.F(Strings.Cli_UnknownRole, roleFlag));
        return 1;
    }
    config.Role = role;
}

if (Flag("listen") is { } listenFlag) config.Listen = listenFlag;
if (Flag("target") is { } targetFlag) config.Target = targetFlag;
if (Flag("input") is { } inputFlag) config.Input = inputFlag;
if (Flag("input-device") is { } inputDeviceFlag) config.InputDevice = inputDeviceFlag;
if (Flag("input-gain") is { } ig && float.TryParse(ig, out var inputGain)) config.InputGain = inputGain;
if (Flag("bitrate") is { } br && int.TryParse(br, out var bitrate)) config.Bitrate = bitrate;
if (BoolFlag("muted")) config.StartMuted = true;
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
    Console.Error.WriteLine(Strings.Cli_NoKey);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Strings.Cli_Usage);
    return 1;
}

// ── Run ───────────────────────────────────────────────────────────────────────

// The composition root: the one place that names the features. Everything below this line
// — and everything inside ReceiverService — works off the IFeature contract, so a third
// feature is a new class and one more entry here.
//
// Both halves of the microphone are registered and the role decides which one starts. They
// share an id and a state record, so everything downstream — the stats line included — is
// written once.

// The one channel every feature that needs delivery guarantees shares. It claims the four
// bulk packet types on their behalf, because the host routes a type to exactly one feature
// and both of the two below would ask for the same four.
var bulk = new BulkHost();

await using var receiver = new ReceiverService(
    new MicrophoneFeature(),
    new MicrophoneCaptureFeature(),
    new DevicesFeature(),
    bulk,
    new ClipboardFeature(bulk),
    new FilesFeature(bulk));
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
    Console.Error.WriteLine(Loc.F(Strings.Log_Prefix, ex.Message));
    return 1;
}

// Autodiscovery (PROTOCOL.md, «Автопоиск хоста»). The advertisement carries a tag derived
// from the key, never the key or the name, so only the machine holding this same key treats
// this one as its own. It runs here and not only in the desktop wizard because the case it
// exists for happens long after pairing: the router reboots, DHCP hands out a different
// address, and the other side has to find this machine again with nobody at either keyboard.
//
// Only the listening role advertises: what is being published is an address to dial, and a
// machine that dials has none worth publishing.
using var discovery = new DiscoveryPublisher((level, message) =>
{
    if (level == LogLevel.Error) Console.Error.WriteLine(message);
    else Console.WriteLine(message);
});
if (config.Role == BridgeRole.Receiver) discovery.Publish(config, Environment.MachineName);

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
        if (pairedName is not null) Console.WriteLine(Loc.F(Strings.Cli_PairedWith, pairedName));
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void ListInputs()
{
    var preferred = DeviceCatalog.PickCapture(null)?.ID;
    foreach (var d in DeviceCatalog.CaptureDevices())
    {
        Console.WriteLine($"{(d.ID == preferred ? " *" : "  ")} {d.FriendlyName}");
    }
}

// ── Stats line ────────────────────────────────────────────────────────────────

static async Task StatsLoop(ReceiverService receiver, CancellationToken token)
{
    long lastCount = 0;

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
        // Peak-hold over the whole five-second window, not the last 20 ms frame. The Mac
        // prints the same statistic, and the point of these two numbers is that a person
        // can hold them side by side and see whether the level survived the trip. Sampling
        // one arbitrary frame here read ~20 dB quieter than the Mac on identical audio.
        var peakValue = mic is null ? 0f : Math.Max(mic.PeakHold, mic.Peak);
        var peak = Loc.F(Strings.Cli_Stats_Peak,
            Loc.Dbfs1(peakValue > 0 ? 20 * Math.Log10(peakValue) : -99).PadLeft(11));

        // The two roles count different things, and printing «декодировано» on a machine
        // that only encodes would be four zeroes pretending to be telemetry.
        string line;
        if (mic is { IsCapture: true })
        {
            line = Loc.F(Strings.Cli_Stats_Sent,
                Loc.Rate((mic.Sent - lastCount) / 5.0).PadLeft(8),
                peak,
                Loc.Count(mic.Sent),
                Loc.F(Strings.Unit_Bytes, mic.LastPacketBytes).PadLeft(6),
                Loc.Count(s.RemoteReceived),
                Loc.Count(s.RemoteLost),
                s.RttMs is { } rtt ? Loc.Ms(rtt) : Strings.Common_Empty);
            lastCount = mic.Sent;
        }
        else
        {
            var received = mic?.Received ?? 0;
            line = Loc.F(Strings.Cli_Stats_Received,
                Loc.Rate((received - lastCount) / 5.0).PadLeft(8),
                peak,
                Loc.Count(mic?.Decoded ?? 0),
                Loc.Count(mic?.Rebuilt ?? 0),
                Loc.Count(mic?.Concealed ?? 0),
                Loc.Count(mic?.Depth ?? 0),
                Loc.Count(mic?.DroppedLate ?? 0),
                Loc.Count(mic?.Underruns ?? 0));
            lastCount = received;
        }

        // Anything other than the microphone gets one word, whatever it turns out to be:
        // the stats line must not need editing when a feature is added.
        foreach (var feature in s.Features.Values)
        {
            if (feature.Id is "microphone" or "" ) continue;
            if (feature.Status is FeatureStatus.Disabled or FeatureStatus.Stopped) continue;
            // A feature with nothing to say says nothing. The shared transfer channel is
            // the one that never has: what it carries is reported by whichever feature the
            // object belongs to, and twice is once too many on a line this narrow.
            if (feature.Headline.Length == 0) continue;
            line += Loc.F(Strings.Cli_Stats_Feature, feature.Title, feature.Headline);
        }

        if (s.Muted) line += Strings.Cli_Stats_Muted;
        if (s.LastPacketAt is { } at && DateTime.UtcNow - at > TimeSpan.FromSeconds(3))
        {
            line += Loc.F(Strings.Cli_Stats_Silent, Loc.Duration(DateTime.UtcNow - at));
        }
        else if (s.LastPacketAt is null)
        {
            line += Strings.Cli_Stats_NoAnswer;
        }
        if (s.Rejected > 0) line += Loc.F(Strings.Cli_Stats_Discarded, Loc.Count(s.Rejected));

        Console.WriteLine(line);
    }
}
