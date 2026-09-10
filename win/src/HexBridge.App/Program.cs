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

        // The language, before anything that could produce a string. «System» — the default
        // — leaves the machine's own culture in place, so a Russian Windows comes up in
        // Russian and everything else in English; an explicit choice brings its own number
        // conventions with it, because somebody who asked for English asked for 12.5 too.
        //
        // Reading ui.json here rather than waiting for the view model is what keeps the very
        // first log line and the tray tooltip in the right language.
        HexBridge.Localization.Language.Apply(AppSettings.Load().Language);

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
