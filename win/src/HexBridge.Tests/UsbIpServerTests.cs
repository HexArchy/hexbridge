using System.Net;
using HexBridge;
using HexBridge.DualSense;

namespace HexBridge.Tests;

/// <summary>
/// The server driven over a real loopback socket by a client written from the specification.
/// This is as close to Windows as it gets from macOS: every byte crosses TCP, in order, and
/// the replies are parsed the way vhci parses them.
/// </summary>
public class UsbIpServerTests : IAsyncLifetime
{
    private readonly List<byte[]> _outputReports = [];
    private VirtualDualSense _device = null!;
    private UsbIpServer _server = null!;

    public Task InitializeAsync()
    {
        _device = TestDevices.Device(_outputReports.Add);
        _server = new UsbIpServer(() => _device, (_, _) => { });
        _server.Start(new IPEndPoint(IPAddress.Loopback, 0));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _device.Dispose();
    }

    private IPEndPoint Endpoint => _server.LocalEndPoint!;

    private Task<UsbIpTestClient> ConnectAsync() => UsbIpTestClient.ConnectAsync(Endpoint);

    private async Task<UsbIpTestClient> ImportedAsync()
    {
        var client = await ConnectAsync();
        var (header, device) = await client.ImportAsync(_device.Info.BusId);
        Assert.Equal(UsbIpProtocol.StatusOk, header.Status);
        Assert.NotNull(device);
        return client;
    }

    // MARK: - Operational phase

    [Fact]
    public async Task DeviceListOffersTheControllerAndItsHidInterface()
    {
        await using var client = await ConnectAsync();
        var (header, count, device, interfaces) = await client.DeviceListAsync();

        Assert.Equal(UsbIpProtocol.Version, header.Version);
        Assert.Equal(UsbIpProtocol.OpRepDevList, header.Code);
        Assert.Equal(UsbIpProtocol.StatusOk, header.Status);
        Assert.Equal(1u, count);

        Assert.Equal("1-1", device!.BusId);
        Assert.Equal(TestDevices.Vendor, device.IdVendor);
        Assert.Equal(TestDevices.Product, device.IdProduct);
        Assert.Equal(UsbIpProtocol.SpeedHigh, device.Speed);

        // Only HID is forwarded; the audio function of a real DualSense is not ours yet.
        Assert.Equal(0x03, Assert.Single(interfaces).Class);
    }

    [Fact]
    public async Task DeviceListIsEmptyWhileNoControllerIsAttached()
    {
        await using var empty = new UsbIpServer(() => null, (_, _) => { });
        empty.Start(new IPEndPoint(IPAddress.Loopback, 0));

        await using var client = await UsbIpTestClient.ConnectAsync(empty.LocalEndPoint!);
        var (header, count, device, _) = await client.DeviceListAsync();

        Assert.Equal(UsbIpProtocol.StatusOk, header.Status);
        Assert.Equal(0u, count);
        Assert.Null(device);
    }

    [Fact]
    public async Task ImportingTheRightBusIdReturnsTheDeviceStruct()
    {
        await using var client = await ConnectAsync();
        var (header, device) = await client.ImportAsync("1-1");

        Assert.Equal(UsbIpProtocol.OpRepImport, header.Code);
        Assert.Equal(UsbIpProtocol.StatusOk, header.Status);
        Assert.Equal(_device.Info.DevId, device!.DevId);
        Assert.Equal(1, device.NumInterfaces);
    }

    [Fact]
    public async Task ImportingABusIdWeDoNotHaveIsRefusedWithoutAStruct()
    {
        await using var client = await ConnectAsync();
        var (header, device) = await client.ImportAsync("9-9");

        Assert.Equal(UsbIpProtocol.OpRepImport, header.Code);
        Assert.Equal(UsbIpProtocol.StatusNoDevice, header.Status);
        Assert.Null(device);
    }

    [Fact]
    public async Task ASecondImportIsRefusedWhileTheFirstHoldsTheDevice()
    {
        await using var first = await ImportedAsync();

        await using var second = await ConnectAsync();
        var (header, _) = await second.ImportAsync("1-1");

        Assert.Equal(UsbIpProtocol.StatusNoDevice, header.Status);
    }

    [Fact]
    public async Task ListingStillWorksWhileTheDeviceIsImported()
    {
        await using var attached = await ImportedAsync();

        await using var lister = await ConnectAsync();
        var (_, count, _, _) = await lister.DeviceListAsync();
        Assert.Equal(1u, count);
    }

    // MARK: - Control transfers

    [Fact]
    public async Task ControlInReturnsTheDeviceDescriptor()
    {
        await using var client = await ImportedAsync();
        await client.ControlInAsync(TestDevices.Setup(0x80, 0x06, 0x0100, 0, 18), 18);

        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(1u, reply.Seqnum);
        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(18, reply.ActualLength);
        Assert.Equal(TestDevices.DeviceDescriptor(), reply.Data);
    }

    [Fact]
    public async Task ControlInIsTruncatedToTheRequestedLength()
    {
        await using var client = await ImportedAsync();

        // 273 bytes of report descriptor into a 64-byte buffer. Answering with all 273
        // is usbip-win2 issue #187: vhci turns it into EOVERFLOW and the read dies.
        await client.ControlInAsync(TestDevices.Setup(0x81, 0x06, 0x2200, 0, 64), 64);
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(64, reply.ActualLength);
        Assert.Equal(64, reply.Data.Length);
        Assert.Equal(TestDevices.ReportDescriptor()[..64], reply.Data);
    }

    [Fact]
    public async Task SetConfigurationIsAnsweredWithNoData()
    {
        await using var client = await ImportedAsync();
        await client.ControlOutAsync(TestDevices.Setup(0x00, 0x09, 1, 0, 0), []);

        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(0, reply.ActualLength);
        Assert.Empty(reply.Data);
    }

    [Fact]
    public async Task GetReportAnswersFromTheFeatureSnapshot()
    {
        await using var client = await ImportedAsync();
        await client.ControlInAsync(TestDevices.Setup(0xA1, 0x01, 0x0305, 0, 41), 41);

        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(41, reply.ActualLength);
        Assert.Equal(0x05, reply.Data[0]);
    }

    [Fact]
    public async Task AnUnimplementedRequestStallsInsteadOfHangingTheEnumeration()
    {
        await using var client = await ImportedAsync();
        await client.ControlInAsync(TestDevices.Setup(0xC0, 0x77, 0, 0, 8), 8);

        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusStall, reply.Status);
        Assert.Equal(0, reply.ActualLength);
    }

    // MARK: - Interrupt transfers

    [Fact]
    public async Task AnInterruptInUrbWaitsUntilThereIsAReport()
    {
        await using var client = await ImportedAsync();
        await client.InterruptInAsync(_device.InterruptInEndpoint, 64);

        // No report yet: a real interrupt endpoint keeps the URB, it does not complete empty.
        Assert.True(await client.IsQuietAsync(TimeSpan.FromMilliseconds(250)));

        _device.PushInputReport(TestDevices.InputReport(counter: 5));
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(64, reply.ActualLength);
        Assert.Equal(5, reply.Data[7]);
    }

    [Fact]
    public async Task AReportThatArrivedFirstCompletesTheNextUrbAtOnce()
    {
        await using var client = await ImportedAsync();
        _device.PushInputReport(TestDevices.InputReport(counter: 9));

        await client.InterruptInAsync(_device.InterruptInEndpoint, 64);
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(9, reply.Data[7]);
    }

    [Fact]
    public async Task InterruptInIsTruncatedToTheRequestedLength()
    {
        await using var client = await ImportedAsync();
        await client.InterruptInAsync(_device.InterruptInEndpoint, 32);
        _device.PushInputReport(TestDevices.InputReport());

        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(32, reply.ActualLength);
        Assert.Equal(32, reply.Data.Length);
    }

    [Fact]
    public async Task AStreamOfReportsComesBackInOrder()
    {
        await using var client = await ImportedAsync();

        for (var i = 0; i < 8; i++) await client.InterruptInAsync(_device.InterruptInEndpoint, 64);
        for (var i = 0; i < 8; i++) _device.PushInputReport(TestDevices.InputReport(counter: (byte)i));

        for (var i = 0; i < 8; i++)
        {
            var reply = await client.ReadSubmitReplyAsync();
            Assert.Equal((uint)(i + 1), reply.Seqnum);
            Assert.Equal(i, reply.Data[7]);
        }
    }

    [Fact]
    public async Task InterruptOutReachesTheMac()
    {
        await using var client = await ImportedAsync();
        var report = TestDevices.OutputReport(rumble: 0x7F);

        await client.InterruptOutAsync(_device.InterruptOutEndpoint, report);
        var reply = await client.ReadSubmitReplyAsync();

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
        Assert.Equal(TestDevices.OutputReportLength, reply.ActualLength);
        Assert.Empty(reply.Data);
        Assert.Equal(report, Assert.Single(_outputReports));
    }

    [Fact]
    public async Task AnEndpointWeNeverAdvertisedStalls()
    {
        await using var client = await ImportedAsync();
        await client.InterruptInAsync(0x87, 64);

        var reply = await client.ReadSubmitReplyAsync();
        Assert.Equal(UsbIpProtocol.StatusStall, reply.Status);
    }

    [Fact]
    public async Task UnlinkingAPendingUrbAnswersOnceAndOnlyOnce()
    {
        await using var client = await ImportedAsync();
        var victim = await client.InterruptInAsync(_device.InterruptInEndpoint, 64);
        var unlink = await client.UnlinkAsync(victim);

        var reply = Assert.IsType<UsbIpUnlinkReply>(await client.ReadReplyAsync());
        Assert.Equal(unlink, reply.Seqnum);
        Assert.Equal(UsbIpProtocol.StatusUnlinked, reply.Status);

        // The unlinked URB must not also produce a RET_SUBMIT when a report turns up.
        _device.PushInputReport(TestDevices.InputReport());
        Assert.True(await client.IsQuietAsync(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public async Task UnlinkingSomethingAlreadyFinishedReportsSuccessRatherThanReset()
    {
        await using var client = await ImportedAsync();
        var done = await client.ControlInAsync(TestDevices.Setup(0x80, 0x06, 0x0100, 0, 18), 18);
        await client.ReadSubmitReplyAsync();

        await client.UnlinkAsync(done);
        var reply = Assert.IsType<UsbIpUnlinkReply>(await client.ReadReplyAsync());

        Assert.Equal(UsbIpProtocol.StatusSuccess, reply.Status);
    }

    // MARK: - The invariant

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(512)]
    public async Task NoReplyEverExceedsTheRequestedLength(int requested)
    {
        await using var client = await ImportedAsync();

        // A descriptor longer than the buffer, one exactly its size, one shorter, and a
        // 64-byte input report — the four shapes vhci can hand us.
        await client.ControlInAsync(TestDevices.Setup(0x80, 0x06, 0x0100, 0, (ushort)requested), requested);
        await client.ControlInAsync(TestDevices.Setup(0x81, 0x06, 0x2200, 0, (ushort)requested), requested);
        await client.ControlInAsync(TestDevices.Setup(0xA1, 0x01, 0x0320, 0, (ushort)requested), requested);
        await client.InterruptInAsync(_device.InterruptInEndpoint, requested);
        _device.PushInputReport(TestDevices.InputReport());

        for (var i = 0; i < 4; i++)
        {
            var reply = await client.ReadSubmitReplyAsync();
            Assert.True(reply.ActualLength <= requested,
                $"actual_length {reply.ActualLength} > transfer_buffer_length {requested}");
            Assert.Equal(reply.ActualLength, reply.Data.Length);
        }
    }
}
