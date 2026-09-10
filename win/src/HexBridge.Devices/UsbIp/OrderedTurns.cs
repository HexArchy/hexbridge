namespace HexBridge.Devices;

/// <summary>
/// Hands out tickets and lets holders act strictly in the order the tickets were
/// taken.
///
/// Interrupt-IN URBs are answered by one task each, and each of those tasks has to
/// write its reply to the shared socket. Guarding the socket with a semaphore is
/// enough to keep the bytes from interleaving, but <c>SemaphoreSlim</c> makes no
/// ordering promise — whoever the scheduler happens to wake writes first. The
/// reports themselves are handed out in order, so the effect is that the right
/// report reaches the wrong place in the stream: the driver sees input reordered.
/// That surfaced as a test passing on macOS and failing on Windows, which is only
/// luck of the scheduler either way.
/// </summary>
internal sealed class OrderedTurns
{
    private readonly object _gate = new();
    private readonly Dictionary<long, TaskCompletionSource> _waiting = [];
    private long _issued;
    private long _serving;

    /// <summary>Takes the next ticket. Must be called in the order to be preserved.</summary>
    public long Take()
    {
        lock (_gate) return _issued++;
    }

    public Task WaitAsync(long ticket)
    {
        lock (_gate)
        {
            if (ticket == _serving) return Task.CompletedTask;

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[ticket] = waiter;
            return waiter.Task;
        }
    }

    /// <summary>
    /// Gives up the turn. Must be called for every ticket taken, including ones
    /// whose URB was cancelled or failed — otherwise everything behind it stalls.
    /// </summary>
    public void Done(long ticket)
    {
        lock (_gate)
        {
            if (ticket != _serving)
            {
                // Out of order: remember it finished so the queue can skip it later.
                _waiting.Remove(ticket);
                _finished.Add(ticket);
                return;
            }

            _serving++;
            while (_finished.Remove(_serving)) _serving++;

            if (_waiting.Remove(_serving, out var next)) next.TrySetResult();
        }
    }

    private readonly HashSet<long> _finished = [];
}
