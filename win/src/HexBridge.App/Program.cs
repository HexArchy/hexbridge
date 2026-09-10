using System.Globalization;
using Avalonia;
using Velopack;

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
        // First line, before anything else, and that is not a style preference. Velopack
        // re-runs this executable with hook arguments during install, update and uninstall
        // (`--veloapp-install` and friends); `Run` recognises them, does its work and exits
        // the process. Anything above it — a culture assignment, a window, a socket — would
        // run during every one of those invisible launches, and a UI put on screen there is
        // the classic «инсталлятор мигнул окном» bug.
        VelopackApp.Build().Run();

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
