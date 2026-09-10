using HexBridge.Clipboard;

namespace HexBridge.Tests;

/// <summary>
/// A clipboard with no Windows behind it, driven by the test.
///
/// Shared between the suites rather than nested in one of them: the clipboard has to behave
/// identically whichever end of the link it is on, and two copies of the fake would be two
/// chances for that claim to stop being checked.
/// </summary>
internal sealed class FakeClipboardSurface : IClipboardSurface
{
    private readonly object _gate = new();
    private ClipboardItem? _item;
    private long _count;
    private readonly List<ClipboardItem> _applied = [];

    /// <summary>Only what the feature pasted, never what the test copied.</summary>
    public IReadOnlyList<ClipboardItem> Applied
    {
        get { lock (_gate) return [.. _applied]; }
    }

    public long ChangeCount
    {
        get { lock (_gate) return _count; }
    }

    public ClipboardItem? Read()
    {
        lock (_gate) return _item;
    }

    public void Write(ClipboardItem item)
    {
        lock (_gate)
        {
            _item = item;
            _count++;
            _applied.Add(item);
        }
    }

    /// <summary>Somebody pressed Ctrl-C on this machine.</summary>
    public void UserCopies(ClipboardItem item)
    {
        lock (_gate)
        {
            _item = item;
            _count++;
        }
    }
}
