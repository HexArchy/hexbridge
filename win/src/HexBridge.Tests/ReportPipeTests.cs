using HexBridge.Devices;

namespace HexBridge.Tests;

public class ReportPipeTests
{
    [Fact]
    public async Task AWaiterIsHandedTheNextReport()
    {
        var pipe = new ReportPipe();
        var pending = pipe.ReadAsync(CancellationToken.None);

        Assert.False(pending.IsCompleted);
        pipe.Push([1, 2, 3]);

        Assert.Equal(new byte[] { 1, 2, 3 }, await pending);
    }

    [Fact]
    public async Task AReportThatArrivedFirstIsReturnedImmediately()
    {
        var pipe = new ReportPipe();
        pipe.Push([7]);

        Assert.Equal(new byte[] { 7 }, await pipe.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwoWaitersAreServedInOrder()
    {
        var pipe = new ReportPipe();
        var first = pipe.ReadAsync(CancellationToken.None);
        var second = pipe.ReadAsync(CancellationToken.None);

        pipe.Push([1]);
        pipe.Push([2]);

        // Out-of-order completion would hand Windows an older controller state than the
        // one it already had.
        Assert.Equal(new byte[] { 1 }, await first);
        Assert.Equal(new byte[] { 2 }, await second);
    }

    [Fact]
    public void TheOldestReportsGoWhenNobodyIsReading()
    {
        var pipe = new ReportPipe(capacity: 4);
        for (var i = 0; i < 10; i++) pipe.Push([(byte)i]);

        Assert.Equal(4, pipe.Depth);
        Assert.Equal(6, pipe.Dropped);
    }

    [Fact]
    public async Task ACancelledWaiterDoesNotEatTheNextReport()
    {
        var pipe = new ReportPipe();
        using var cancel = new CancellationTokenSource();

        var abandoned = pipe.ReadAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);

        pipe.Push([42]);
        Assert.Equal(new byte[] { 42 }, await pipe.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClosingWakesEveryWaiter()
    {
        var pipe = new ReportPipe();
        var pending = pipe.ReadAsync(CancellationToken.None);

        pipe.Close();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
