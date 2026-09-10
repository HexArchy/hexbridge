using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using HexBridge.App.ViewModels;
using HexBridge.App.Views;

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
        ApplyTheme(_model.Theme);

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
            _model.Log.Add(LogLevel.Warning, $"hexbridge: значок в трее недоступен: {ex.Message}");
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
                    new NativeMenuItem("Показать окно") { Command = _model!.ShowWindowCommand },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("Пауза") { Command = _model.TogglePauseCommand },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("Выход") { Command = _model.ExitCommand },
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

        // Index 2 is the pause entry; its wording flips with the receiver state.
        if (_tray.Menu?.Items.ElementAtOrDefault(2) is NativeMenuItem pause) pause.Header = _model.PauseLabel;
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
