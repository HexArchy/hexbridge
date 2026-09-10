using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

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

    public const string InstallHint =
        "Установите драйвер usbip-win2 0.9.8.0: скачайте USBip-0.9.8.0-x64.exe со страницы " +
        "github.com/vadimgrn/usbip-win2/releases и запустите. Ни Secure Boot, ни тестовый " +
        "режим подписи отключать не нужно — бинарники подписаны Microsoft.";

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

    /// <summary>Re-checks for the client, e.g. after the user has installed it mid-session.</summary>
    public void Rescan() => ClientPath = Locate(explicitPath);

    /// <summary>
    /// Plugs the virtual device into the local vhci. <c>--receive-mode=low-latency</c> is the
    /// WSK event-callback path added in 0.9.8.0 for exactly this shape of traffic: small
    /// packets, hundreds a second.
    /// </summary>
    public async Task<bool> AttachAsync(string busId, string host, CancellationToken token)
    {
        var client = ClientPath;
        if (client is null) return false;
        if (_ports.ContainsKey(busId)) return true;

        var result = await RunAsync(client,
            ["attach", "--receive-mode=low-latency", "-r", host, "-b", busId], token).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            log(LogLevel.Warning, $"usbip: attach {busId} не удался ({result.ExitCode}): {Summarise(result)}");
            return false;
        }

        var port = ParsePort(result.Output) ?? ParsePort(result.Error) ?? -1;
        // A port we could not parse is still an attachment we made and must undo, so it is
        // remembered as -1 rather than dropped. `detach -p -1` is never run; the socket
        // closing takes the device down instead.
        _ports[busId] = port;
        log(LogLevel.Info, port >= 0
            ? $"usbip: {busId} подключён к порту {port}"
            : $"usbip: {busId} подключён");
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
            log(LogLevel.Warning, $"usbip: detach порта {port} ({busId}) не удался: {Summarise(result)}");
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
            ?? throw new InvalidOperationException($"не удалось запустить {path}");

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
            return new ProcessResult(-1, "", "usbip.exe не ответил за 15 секунд");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
