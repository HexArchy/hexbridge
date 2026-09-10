namespace HexBridge.Devices;

/// <summary>
/// Hands HID input reports from the network thread to whoever is waiting on an interrupt
/// IN URB, in order, without allocating a task per report at 250 Hz.
///
/// Waiters are served first-in-first-out so two outstanding URBs cannot complete out of
/// order. Reports pile up only when nobody is asking; past a handful the oldest go, because
/// a gamepad report is absolute state and a stale one is worth nothing.
/// </summary>
public sealed class ReportPipe
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _reports = new();
    private readonly Queue<TaskCompletionSource<byte[]>> _waiters = new();
    private readonly int _capacity;

    private bool _closed;

    public ReportPipe(int capacity = 8) => _capacity = Math.Max(1, capacity);

    /// <summary>Reports dropped because nothing was reading fast enough.</summary>
    public long Dropped { get; private set; }

    public int Depth
    {
        get { lock (_gate) return _reports.Count; }
    }

    public void Push(byte[] report)
    {
        TaskCompletionSource<byte[]>? waiter = null;

        lock (_gate)
        {
            if (_closed) return;

            while (_waiters.Count > 0 && waiter is null)
            {
                var candidate = _waiters.Dequeue();
                // A cancelled waiter is already finished; skip past it.
                if (!candidate.Task.IsCompleted) waiter = candidate;
            }

            if (waiter is null)
            {
                _reports.Enqueue(report);
                while (_reports.Count > _capacity)
                {
                    _reports.Dequeue();
                    Dropped++;
                }
            }
        }

        // Completing outside the lock: the continuation is the URB reply path.
        waiter?.TrySetResult(report);
    }

    public ValueTask<byte[]> ReadAsync(CancellationToken token)
    {
        TaskCompletionSource<byte[]> waiter;

        lock (_gate)
        {
            if (_reports.Count > 0) return ValueTask.FromResult(_reports.Dequeue());
            if (_closed) return ValueTask.FromException<byte[]>(new ObjectDisposedException(nameof(ReportPipe)));

            waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        return new ValueTask<byte[]>(WaitAsync(waiter, token));
    }

    private static async Task<byte[]> WaitAsync(TaskCompletionSource<byte[]> waiter, CancellationToken token)
    {
        using var registration = token.Register(static state =>
            ((TaskCompletionSource<byte[]>)state!).TrySetCanceled(), waiter);
        return await waiter.Task.ConfigureAwait(false);
    }

    /// <summary>Wakes every waiter with a cancellation; used when the device goes away.</summary>
    public void Close()
    {
        TaskCompletionSource<byte[]>[] waiters;
        lock (_gate)
        {
            _closed = true;
            _reports.Clear();
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiters) waiter.TrySetCanceled();
    }
}
