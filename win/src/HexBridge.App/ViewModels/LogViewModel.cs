using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HexBridge.App.ViewModels;

public sealed record LogLine(string Time, string Text, LogLevel Level)
{
    public bool IsError => Level == LogLevel.Error;
    public bool IsWarning => Level == LogLevel.Warning;
}

public sealed partial class LogViewModel : ObservableObject
{
    /// <summary>Beyond this the list is scrollback nobody reads, and it costs memory.</summary>
    private const int MaxLines = 500;

    // The receiver logs from its network thread; the UI drains this on its own tick so a
    // burst of messages cannot flood the dispatcher.
    private readonly ConcurrentQueue<LogEntry> _pending = new();

    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Bumped whenever lines were appended, so the view knows to scroll.</summary>
    [ObservableProperty]
    private long _revision;

    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Safe to call from any thread.</summary>
    public void Enqueue(LogEntry entry) => _pending.Enqueue(entry);

    public void Add(LogLevel level, string message) =>
        Enqueue(new LogEntry(DateTime.UtcNow, level, message));

    /// <summary>Called from the UI tick. Returns true when anything was appended.</summary>
    public bool Drain()
    {
        var added = false;
        while (_pending.TryDequeue(out var entry))
        {
            Lines.Add(new LogLine(entry.At.ToLocalTime().ToString("HH:mm:ss"), entry.Message, entry.Level));
            added = true;
        }

        while (Lines.Count > MaxLines) Lines.RemoveAt(0);

        if (added) Revision++;
        return added;
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = new StringBuilder();
        foreach (var line in Lines) text.AppendLine($"{line.Time}  {line.Text}");
        if (CopyToClipboard is not null) await CopyToClipboard(text.ToString());
    }

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        Revision++;
    }
}
