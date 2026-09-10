using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HexBridge.App;

/// <summary>
/// Starts the app at logon through the per-user Run key.
///
/// Deliberately not a scheduled task or a service: the receiver renders into a
/// per-session audio endpoint, so it has to run as the logged-in user anyway, and
/// the Run key needs no elevation — the user can toggle this from the UI without
/// a UAC prompt.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HexBridge";

    /// <summary>The registry is the source of truth, so a toggle survives reinstalls of ui.json.</summary>
    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            return ReadValue() is not null;
        }
    }

    public static bool TrySet(bool enabled, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "автозапуск доступен только в Windows";
            return false;
        }

        try
        {
            Apply(enabled);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("не удалось открыть ветку автозапуска");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        // `--tray` keeps the window closed on a logon start; the user asked for the
        // app, not for a window in their face every morning.
        key.SetValue(ValueName, $"\"{ExecutablePath()}\" --tray", RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>
    /// The real .exe, not the managed dll: under single-file publishing
    /// <c>Assembly.Location</c> is empty, which would register a broken command.
    /// </summary>
    private static string ExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)) return path;

        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName
            ?? throw new InvalidOperationException("не удалось определить путь к исполняемому файлу");
    }
}
