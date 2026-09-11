using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using HexBridge.App.ViewModels;
using HexBridge.Localization;

namespace HexBridge.App.Views;

public partial class FilesView : UserControl
{
    public FilesView()
    {
        AvaloniaXamlLoader.Load(this);

        // Drag and drop is not a binding: the events arrive here and nowhere else, so the
        // page turns them into the one call the view model exposes and keeps the view model
        // free of Avalonia's storage types.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = Dropped(e) is null ? DragDropEffects.None : DragDropEffects.Copy;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is FilesViewModel model && Dropped(e) is { } path) model.Send(path);
    }

    private async void OnPick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FilesViewModel model) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Files_Picker_Title,
            AllowMultiple = false,
        });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) model.Send(path);
    }

    /// <summary>
    /// The one local file being dragged, or null. Anything without a path on this disk —
    /// text, a drag out of a browser, a file inside an archive — is not something that can
    /// be read and sent, and saying no while the pointer is still moving is how the user
    /// finds that out without dropping it first.
    /// </summary>
    private static string? Dropped(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        return files is { Length: 1 } ? files[0].TryGetLocalPath() : null;
    }
}
