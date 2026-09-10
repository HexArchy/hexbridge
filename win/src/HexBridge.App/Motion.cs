using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HexBridge.App;

/// <summary>
/// «Показывать анимацию в Windows» — Параметры → Специальные возможности → Дисплей.
///
/// Avalonia has no API for this: <c>IPlatformSettings</c> stops at tap and hotkey
/// timings, and the request for a reduced-motion signal (issue #19405) is open and
/// unmoving. So the flag is read straight from the system.
///
/// The value is cached for a second rather than forever: DESIGN.md §5.6 requires the
/// setting to be read when an animation starts, not once at launch, because the user
/// can flip it without restarting the app. A WM_SETTINGCHANGE hook would be tidier,
/// but <c>Avalonia.Win32.Win32Properties</c> is not referenced on non-Windows build
/// hosts, and this project has to compile on macOS too. A one-second read of a cached
/// system parameter costs nothing measurable.
/// </summary>
public static class ReducedMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(1);

    private static bool _cached;
    private static DateTime _readAt = DateTime.MinValue;

    /// <summary>
    /// Overrides the system value. Used by tests and by the design harness that takes
    /// the screenshots; null means «ask the system».
    /// </summary>
    public static bool? Override { get; set; }

    /// <summary>True when the user asked Windows to cut animation down.</summary>
    public static bool IsEnabled
    {
        get
        {
            if (Override is { } forced) return forced;
            if (!OperatingSystem.IsWindows()) return false;

            var now = DateTime.UtcNow;
            if (now - _readAt < Ttl) return _cached;

            _readAt = now;
            _cached = ReadSystemFlag();
            return _cached;
        }
    }

    /// <summary>Drops the cache so the next read hits the system again.</summary>
    public static void Invalidate() => _readAt = DateTime.MinValue;

    /// <summary>
    /// Picks between the full duration and the reduced one. Reduced motion never means
    /// «no feedback at all» — a transition that carries meaning collapses to a 120 ms
    /// crossfade rather than disappearing (§5.6).
    /// </summary>
    public static TimeSpan Pick(TimeSpan full) =>
        IsEnabled ? TimeSpan.FromMilliseconds(120) : full;

    [SupportedOSPlatform("windows")]
    private static bool ReadSystemFlag()
    {
        try
        {
            var enabled = true;
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) && !enabled;
        }
        catch (Exception)
        {
            // A missing export or a locked-down container: assume animation is wanted.
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool SystemParametersInfo(
        uint action, uint param, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint winIni);
}
