using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using HexBridge.Localization;

namespace HexBridge.Devices;

/// <summary>
/// Drives the usbip-win2 client so the user never has to open a console.
///
/// Everything here degrades to "not installed" rather than to an error: a machine without
/// the driver is not a broken machine, it is a machine that needs one 25 MB installer, and
/// the UI can say so much better than an exception can.
/// </summary>
public sealed partial class UsbIpAttacher(string? explicitPath, Action<LogLevel, string> log)
{
    /// <summary>Where the 0.9.8.0 installer puts it, then the older layout, then PATH.</summary>
    private static readonly string[] KnownPaths =
    [
        @"C:\Program Files\USBip\usbip.exe",
        @"C:\Program Files\usbip-win2\usbip.exe",
        @"C:\Program Files (x86)\USBip\usbip.exe",
    ];

    /// <summary>
    /// What to do about a missing driver. A property rather than a constant now that it is
    /// a translated string: the language can change while the app is running, and a constant
    /// would have been baked into every call site at compile time.
    /// </summary>
    public static string InstallHint => Strings.Devices_Driver_InstallHint;

    /// <summary>
    /// The installer to hand the user, for the architecture this machine actually runs.
    ///
    /// usbip-win2 ships x64 and ARM64 binaries under one tag, and the wrong one produces a
    /// driver that simply never loads. The question is about the operating system rather
    /// than about us: on ARM64 Windows our own x64 build runs under emulation perfectly
    /// well, and it still needs the ARM64 driver.
    /// </summary>
    public static string InstallerUrl =>
        "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.0/USBip-0.9.8.0-"
        + (RuntimeInformation.OSArchitecture is Architecture.Arm64 ? "arm64" : "x64")
        + ".exe";


    /// <summary>vhci port per busid. Each forwarded device is its own attachment.</summary>
    private readonly ConcurrentDictionary<string, int> _ports = new();

    /// <summary>Full path to usbip.exe, or null when the driver is not installed.</summary>
    public string? ClientPath { get; private set; } = Locate(explicitPath);

    public bool IsInstalled => ClientPath is not null;

    /// <summary>The vhci port a busid is plugged into, or null when it is not attached.</summary>
    public int? Port(string busId) => _ports.TryGetValue(busId, out var port) ? port : null;

    /// <summary>Every attachment we made, for the UI.</summary>
    public IReadOnlyDictionary<string, int> Ports => _ports;

    public static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        // usbip.exe is a Windows binary and the vhci driver it talks to is a Windows driver.
        // Looking for it anywhere else would only produce a confusing "not found".
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var candidate in KnownPaths)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "usbip.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }

    private long _lastLookup;

    /// <summary>
    /// Re-checks for the client, so an install done while the app is open is noticed
    /// without a restart — the whole difference between "install this driver" as a dead
    /// end and as something the screen watches for you.
    ///
    /// Once found it never looks again, and while missing it looks at most once a second,
    /// so the ten-times-a-second UI poll can call this on every frame.
    /// </summary>
    /// <returns>Whether the client is there now.</returns>
    public bool Rescan()
    {
        if (ClientPath is not null) return true;

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastLookup) < 1000) return false;
        Interlocked.Exchange(ref _lastLookup, now);

        ClientPath = Locate(explicitPath);
        return ClientPath is not null;
    }

    /// <summary>
    /// Plugs the virtual device into the local vhci. <c>--receive-mode=low-latency</c> is the
    /// WSK event-callback path added in 0.9.8.0 for exactly this shape of traffic: small
    /// packets, hundreds a second.
    /// </summary>
    /// <param name="serverPort">
    /// Where the server actually ended up. Passed with the client's global
    /// <c>--tcp-port</c>, which has to come before the subcommand: the standard 3240 is
    /// often taken by usbip-win2's own <c>usbipd</c> service, and this end steps aside
    /// rather than fighting it for a number neither of us needs.
    /// </param>
    public async Task<bool> AttachAsync(string busId, string host, int serverPort, CancellationToken token)
    {
        var client = ClientPath;
        if (client is null) return false;
        if (_ports.ContainsKey(busId)) return true;

        var result = await RunAsync(client,
            ["--tcp-port", serverPort.ToString(CultureInfo.InvariantCulture),
             "attach", "--receive-mode=low-latency", "-r", host, "-b", busId], token).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            log(LogLevel.Warning, Loc.F(Strings.Log_UsbIp_AttachFailed, busId, result.ExitCode, Summarise(result)));
            return false;
        }

        var port = ParsePort(result.Output) ?? ParsePort(result.Error) ?? -1;
        // A port we could not parse is still an attachment we made and must undo, so it is
        // remembered as -1 rather than dropped. `detach -p -1` is never run; the socket
        // closing takes the device down instead.
        _ports[busId] = port;
        log(LogLevel.Info, port >= 0
            ? Loc.F(Strings.Log_UsbIp_AttachedPort, busId, port)
            : Loc.F(Strings.Log_UsbIp_Attached, busId));
        return true;
    }

    /// <summary>
    /// Unplugs one device. Only ever by port: <c>detach --all</c> would also unplug whatever
    /// else the user has forwarded — including the other three devices we forwarded
    /// ourselves — which is exactly the bug four devices make possible.
    /// </summary>
    public async Task DetachAsync(string busId, CancellationToken token)
    {
        var client = ClientPath;
        if (!_ports.TryRemove(busId, out var port)) return;
        if (client is null || port < 0) return;

        var result = await RunAsync(client, ["detach", "-p", port.ToString()], token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            log(LogLevel.Warning, Loc.F(Strings.Log_UsbIp_DetachFailed, port, busId, Summarise(result)));
        }
    }

    /// <summary>Unplugs everything we attached, one port at a time. Used on the way out.</summary>
    public async Task DetachAllAsync(CancellationToken token)
    {
        foreach (var busId in _ports.Keys.ToArray())
        {
            await DetachAsync(busId, token).ConfigureAwait(false);
        }
    }

    /// <summary>Forgets an attachment without running anything — used when the driver dropped it.</summary>
    public void Forget(string busId) => _ports.TryRemove(busId, out _);

    internal static int? ParsePort(string output)
    {
        var match = PortPattern().Match(output);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    [GeneratedRegex(@"port\D{0,12}(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex PortPattern();

    private static string Summarise(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 200 ? text[..200] : text;
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Error);

    private static async Task<ProcessResult> RunAsync(string path, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException(Loc.F(Strings.Log_UsbIp_LaunchFailed, path));

        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);

        // usbip.exe returns promptly; a hang means the driver is wedged, and waiting
        // forever would take the whole feature down with it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }
            return new ProcessResult(-1, "", Strings.Log_UsbIp_NoAnswer);
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
