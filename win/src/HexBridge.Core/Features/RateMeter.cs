namespace HexBridge;

/// <summary>
/// Packets per second over a sliding one-second window, so the number reads steadily
/// instead of swinging with whichever tick a burst happened to land in.
/// </summary>
public sealed class RateMeter
{
    private readonly Queue<(DateTime At, long Count)> _window = new();

    public double Sample(long total, DateTime now)
    {
        _window.Enqueue((now, total));
        while (_window.Count > 1 && now - _window.Peek().At > TimeSpan.FromSeconds(1))
        {
            _window.Dequeue();
        }

        var oldest = _window.Peek();
        var span = (now - oldest.At).TotalSeconds;
        return span > 0.05 ? (total - oldest.Count) / span : 0;
    }

    public void Reset() => _window.Clear();
}
