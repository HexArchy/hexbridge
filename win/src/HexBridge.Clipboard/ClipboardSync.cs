using System.Security.Cryptography;

namespace HexBridge.Clipboard;

/// <summary>
/// One thing the clipboard can hold, in the only two shapes this feature carries: UTF-8 text
/// and a PNG. Everything else on the clipboard is left alone.
/// </summary>
public sealed class ClipboardItem
{
    public ClipboardItem(BulkFormat format, byte[] bytes)
    {
        Format = format;
        Bytes = bytes;
        Hash = SHA256.HashData(bytes);
    }

    public BulkFormat Format { get; }
    public byte[] Bytes { get; }
    public byte[] Hash { get; }

    /// <summary>
    /// What the UI is told about a transfer. Deliberately the shape and the size and nothing
    /// else: the description travels over the wire and lands in a log, and the clipboard is
    /// where passwords live.
    /// </summary>
    public string Describe() => Format switch
    {
        BulkFormat.Utf8Text => $"текст, {Size(Bytes.Length)}",
        BulkFormat.Png => $"изображение, {Size(Bytes.Length)}",
        _ => $"данные, {Size(Bytes.Length)}",
    };

    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):F1} МБ"
        : bytes >= 1024
            ? $"{bytes / 1024.0:F1} КБ"
            : $"{bytes} Б";
}

/// <summary>
/// The system clipboard, reduced to what synchronising it needs. A change counter rather
/// than an event because neither macOS nor Win32 offers a dependable notification without a
/// window, and because a counter is trivial to fake in a test.
/// </summary>
public interface IClipboardSurface
{
    /// <summary>Bumped by the system on every change, by anyone, including us.</summary>
    long ChangeCount { get; }

    /// <summary>Null when the clipboard holds nothing we carry, or could not be read.</summary>
    ClipboardItem? Read();

    void Write(ClipboardItem item);
}

/// <summary>
/// The loop breaker.
///
/// Two machines that mirror each other's clipboard will, done naively, hand the same object
/// back and forth forever: we paste what arrived, our own watcher sees the change, and off it
/// goes again. The contract's hash is enough to stop that, but only if this side knows which
/// hashes describe *its own current clipboard* — which is not the same thing as remembering
/// the last few objects seen. Copying A, then B, then A again has to send A a second time,
/// and a suppression list would eat it.
///
/// So there are exactly two facts here: what our clipboard currently holds (one or two
/// hashes, because writing a PNG and reading it back does not always give the same bytes),
/// and what the peer is known to hold. Nothing else is remembered.
/// </summary>
public sealed class ClipboardSync(IClipboardSurface surface)
{
    private readonly List<byte[]> _owned = [];
    private byte[]? _peerHash;
    private long _lastChangeCount = long.MinValue;

    /// <summary>
    /// The answer to a BULK_OFFER: we already hold exactly these bytes, so nothing needs to
    /// cross the wire. Safe to call from the socket thread.
    /// </summary>
    public bool Owns(byte[] hash)
    {
        lock (_owned)
        {
            foreach (var known in _owned)
            {
                if (known.AsSpan().SequenceEqual(hash)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Reads the clipboard if it has changed and returns what should be offered to the peer,
    /// or null when there is nothing new to say. Call it from the clipboard's own thread.
    /// </summary>
    public ClipboardItem? Poll()
    {
        var change = surface.ChangeCount;
        if (change == _lastChangeCount) return null;
        _lastChangeCount = change;

        var item = surface.Read();
        if (item is null) return null;

        // Still the content we already know about — the counter moved for some other format,
        // or this is the echo of our own paste.
        if (Owns(item.Hash)) return null;

        lock (_owned)
        {
            _owned.Clear();
            _owned.Add(item.Hash);
        }

        // The peer sent us this in the first place, or already has it: offering it back is
        // the second half of the loop, and this is where it stops.
        if (_peerHash is not null && _peerHash.AsSpan().SequenceEqual(item.Hash)) return null;

        return item;
    }

    /// <summary>
    /// Puts a received object on the clipboard without letting our own watcher bounce it
    /// back. The read-back matters: Windows and macOS both re-encode some formats, so the
    /// bytes that come out are not always the bytes that went in, and it is the ones that
    /// come out that our watcher will see.
    /// </summary>
    public void Apply(ClipboardItem item)
    {
        lock (_owned)
        {
            _owned.Clear();
            _owned.Add(item.Hash);
        }
        _peerHash = item.Hash;

        surface.Write(item);

        _lastChangeCount = surface.ChangeCount;
        var readBack = surface.Read();
        if (readBack is null) return;

        lock (_owned)
        {
            if (!Owns(readBack.Hash)) _owned.Add(readBack.Hash);
        }
    }

    /// <summary>The peer confirmed it holds this object, so we must not offer it again.</summary>
    public void NotePeerHas(byte[] hash) => _peerHash = hash;

    /// <summary>
    /// The link came back up and nothing is known about the other side any more. What our own
    /// clipboard holds is still true, so that is kept.
    /// </summary>
    public void ForgetPeer() => _peerHash = null;
}
