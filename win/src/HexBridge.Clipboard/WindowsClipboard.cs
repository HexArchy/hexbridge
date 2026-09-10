using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using HexBridge.Localization;

namespace HexBridge.Clipboard;

/// <summary>
/// The Windows clipboard through the plain Win32 API.
///
/// Two things about it shape everything here. It is a shared, singly-owned resource: any
/// application can hold it open, and while one does, <c>OpenClipboard</c> fails — so every
/// access retries instead of giving up, and a failed read is reported as «нечего читать»
/// rather than as a fault. And it is apartment-bound: the documented requirement is an STA
/// thread, which the clipboard feature's worker provides and this type assumes.
///
/// <c>GetClipboardSequenceNumber</c> is the piece that makes polling cheap: it needs no
/// window, no open handle and no permission, and it is the exact analogue of the Mac's
/// <c>NSPasteboard.changeCount</c>.
///
/// The message-only window is not decoration. <c>EmptyClipboard</c> sets the clipboard owner
/// to the window passed to <c>OpenClipboard</c>, and if that was NULL the owner becomes NULL
/// — which makes every subsequent <c>SetClipboardData</c> fail. Writing needs a real HWND,
/// so there is one, parented to HWND_MESSAGE so it never appears anywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboard : IClipboardSurface, IDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    /// <summary>How many times to wait out another application holding the clipboard.</summary>
    private const int OpenAttempts = 10;
    private const int OpenRetryMs = 20;

    private const int HwndMessage = -3;

    private readonly uint _pngFormat = RegisterClipboardFormatW("PNG");
    private readonly Action<LogLevel, string> _log;

    /// <summary>
    /// Created lazily, on whichever thread first touches the clipboard — which is the STA
    /// worker, and the thread the window should belong to.
    /// </summary>
    private IntPtr _owner;

    public WindowsClipboard(Action<LogLevel, string> log) => _log = log;

    public void Dispose()
    {
        if (_owner == IntPtr.Zero) return;
        DestroyWindow(_owner);
        _owner = IntPtr.Zero;
    }

    public long ChangeCount => GetClipboardSequenceNumber();

    public ClipboardItem? Read()
    {
        if (!Open(Owner())) return null;
        try
        {
            // An image wins over text: applications that put a picture on the clipboard
            // usually add a text label next to it, and the picture is what was copied.
            if (_pngFormat != 0 && IsClipboardFormatAvailable(_pngFormat))
            {
                var png = ReadBytes(_pngFormat);
                if (png is { Length: > 0 }) return new ClipboardItem(BulkFormat.Png, png);
            }

            if (IsClipboardFormatAvailable(CfUnicodeText))
            {
                var text = ReadText();
                if (!string.IsNullOrEmpty(text)) return new ClipboardItem(BulkFormat.Utf8Text, Encoding.UTF8.GetBytes(text));
            }

            return null;
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, Loc.F(Strings.Log_Clip_ReadFailed, ex.Message));
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Write(ClipboardItem item)
    {
        if (item.Format == BulkFormat.Png && _pngFormat == 0)
        {
            _log(LogLevel.Warning, Strings.Log_Clip_NoPng);
            return;
        }

        var owner = Owner();
        if (owner == IntPtr.Zero)
        {
            _log(LogLevel.Warning, Strings.Log_Clip_NoOwnerWindow);
            return;
        }

        if (!Open(owner))
        {
            _log(LogLevel.Warning, Strings.Log_Clip_Busy);
            return;
        }

        try
        {
            EmptyClipboard();
            var bytes = item.Format == BulkFormat.Utf8Text
                ? Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(item.Bytes) + '\0')
                : item.Bytes;
            var format = item.Format == BulkFormat.Utf8Text ? CfUnicodeText : _pngFormat;

            var handle = Allocate(bytes);
            if (handle == IntPtr.Zero) return;

            // Ownership passes to the clipboard on success and stays with us on failure —
            // getting this backwards is a leak on one path and a double free on the other.
            if (SetClipboardData(format, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                _log(LogLevel.Warning, Strings.Log_Clip_Refused);
            }
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, Loc.F(Strings.Log_Clip_WriteFailed, ex.Message));
        }
        finally
        {
            CloseClipboard();
        }
    }

    // MARK: - Private

    /// <summary>
    /// The clipboard is a shared, singly-owned resource: while any other application holds it
    /// open, this fails. Waiting it out is the only thing to do, and a bounded wait is the
    /// only thing that is safe to do.
    /// </summary>
    private static bool Open(IntPtr owner)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(OpenRetryMs);
        }
        return false;
    }

    private IntPtr Owner()
    {
        if (_owner != IntPtr.Zero) return _owner;

        // "STATIC" is a predefined class, so no window class has to be registered — and a
        // HWND_MESSAGE child is invisible, gets no input and never reaches the taskbar.
        _owner = CreateWindowExW(
            0, "STATIC", null, 0, 0, 0, 0, 0,
            new IntPtr(HwndMessage), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_owner == IntPtr.Zero)
        {
            _log(LogLevel.Warning, Strings.Log_Clip_OwnerWindowFailed);
        }
        return _owner;
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;

        var size = (long)GlobalSize(handle);
        if (size <= 0) return null;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadText()
    {
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero) return null;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static IntPtr Allocate(byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }
        return handle;
    }

    // MARK: - Win32

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
