using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HexBridge.App.ViewModels;

namespace HexBridge.App.Views;

public partial class LogView : UserControl
{
    private ScrollViewer? _scroller;

    public LogView()
    {
        AvaloniaXamlLoader.Load(this);
        _scroller = this.FindControl<ScrollViewer>("Scroller");
        DataContextChanged += OnDataContextChanged;
    }

    private LogViewModel? _model;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as LogViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
    }

    /// <summary>
    /// Following the tail is done here rather than in the view model: the model has no
    /// business knowing about scroll offsets, and Revision changing is the only signal needed.
    /// </summary>
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogViewModel.Revision) || _model?.AutoScroll != true) return;
        _scroller ??= this.FindControl<ScrollViewer>("Scroller");
        _scroller?.ScrollToEnd();
    }
}
