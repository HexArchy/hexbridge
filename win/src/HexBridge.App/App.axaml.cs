using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using HexBridge.Localization;
using HexBridge.App.ViewModels;
using HexBridge.App.Views;
using Semi.Avalonia;

namespace HexBridge.App;

public partial class App : Application
{
    private MainViewModel? _model;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    private readonly Dictionary<TrayState, WindowIcon> _trayIcons = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Closing the window hides it; only the tray's Exit really quits.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _model = new MainViewModel(Program.ConfigPath);
        _model.RequestShowWindow = ShowWindow;
        _model.RequestExit = () => Shutdown(desktop);
        _model.ApplyTheme = ApplyTheme;
        _model.ApplySemiLocale = ApplySemiLocale;
        ApplyTheme(_model.Theme);
        ApplySemiLocale();

        _window = new MainWindow { DataContext = _model };
        _window.Closing += (_, e) =>
        {
            if (_exiting) return;
            // Hiding rather than closing keeps the receiver running with the window gone,
            // which is the whole point of the tray.
            e.Cancel = true;
            _window.Hide();
        };
        desktop.MainWindow = _window;

        var clipboard = _window.Clipboard;
        if (clipboard is not null) _model.AttachClipboard(text => clipboard.SetTextAsync(text));

        // A tray failure must not cost the user their window; the app is still usable without it.
        try
        {
            BuildTray();
        }
        catch (Exception ex)
        {
            _model.Log.Add(LogLevel.Warning, Loc.F(Strings.Log_TrayUnavailable, ex.Message));
        }

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Tray) or nameof(MainViewModel.TrayTooltip)) UpdateTray();
        };

        if (!_model.StartMinimised && !Program.StartHidden) _window.Show();
        if (_model.StartOnLaunch) _model.StartCommand.Execute(null);

        desktop.ShutdownRequested += (_, _) => _exiting = true;
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Semi keeps its own table for the strings inside the controls it themes — the text box
    /// context menu, mostly — and picks it by culture. It is set here rather than in App.axaml
    /// because the language is a preference now, and pushed again on every change so the two
    /// tables cannot disagree.
    /// </summary>
    private void ApplySemiLocale()
    {
        foreach (var theme in Styles.OfType<SemiTheme>()) theme.Locale = Localizer.Instance.Culture;
    }

    private void ApplyTheme(ThemePreference preference) => RequestedThemeVariant = preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private void BuildTray()
    {
        foreach (var (state, file) in new[]
                 {
                     (TrayState.Idle, "tray-idle.png"),
                     (TrayState.Live, "tray-live.png"),
                     (TrayState.Warn, "tray-warn.png"),
                     (TrayState.Error, "tray-error.png"),
                 })
        {
            using var stream = AssetLoader.Open(new Uri($"avares://HexBridge/Assets/{file}"));
            _trayIcons[state] = new WindowIcon(new Bitmap(stream));
        }

        _tray = new TrayIcon
        {
            Icon = _trayIcons[TrayState.Idle],
            ToolTipText = "HexBridge",
            IsVisible = true,
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem(Strings.Tray_Show) { Command = _model!.ShowWindowCommand },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem(Strings.App_Button_Pause) { Command = _model.TogglePauseCommand },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem(Strings.Tray_Quit) { Command = _model.ExitCommand },
                },
            },
        };
        _tray.Clicked += (_, _) => ShowWindow();

        TrayIcon.SetIcons(this, [_tray]);
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _model is null) return;
        _tray.Icon = _trayIcons[_model.Tray];
        _tray.ToolTipText = _model.TrayTooltip;

        // Index 0 and 4 are fixed labels and index 2 flips with the receiver state; all
        // three are rewritten on every tick, which is also what carries a language change
        // into a menu the platform built once.
        if (_tray.Menu?.Items.ElementAtOrDefault(0) is NativeMenuItem show) show.Header = Strings.Tray_Show;
        if (_tray.Menu?.Items.ElementAtOrDefault(2) is NativeMenuItem pause) pause.Header = _model.PauseLabel;
        if (_tray.Menu?.Items.ElementAtOrDefault(4) is NativeMenuItem quit) quit.Header = Strings.Tray_Quit;
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _exiting = true;
        if (_tray is not null) _tray.IsVisible = false;
        if (_model is not null) await _model.DisposeAsync();
        desktop.Shutdown();
    }
}
