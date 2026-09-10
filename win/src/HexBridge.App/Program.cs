using System.Globalization;
using Avalonia;

namespace HexBridge.App;

internal static class Program
{
    /// <summary>
    /// Mirrors the console receiver's <c>--config</c> flag so a single install directory can
    /// be driven by either front end.
    /// </summary>
    public static string? ConfigPath { get; private set; }

    /// <summary>Set by <c>--tray</c>, which the autostart entry passes at logon.</summary>
    public static bool StartHidden { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // The interface is Russian throughout, so numbers get Russian separators no matter
        // which Windows locale the machine happens to run.
        var russian = new CultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = russian;
        CultureInfo.DefaultThreadCurrentUICulture = russian;

        var index = Array.IndexOf(args, "--config");
        if (index >= 0 && index + 1 < args.Length) ConfigPath = args[index + 1];

        StartHidden = args.Contains("--tray");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Referenced by name by the Avalonia design-time tooling; do not rename.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
