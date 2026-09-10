using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// The control-transfer emulation. Every request here is one a real Windows host, Steam or
/// a game makes during enumeration, and answering any of them wrongly means a controller
/// that appears in Device Manager and works nowhere.
/// </summary>
public class VirtualHidDeviceTests
{
    [Fact]
    public void IdentityComesFromTheDeviceDescriptorAndNowhereElse()
    {
        using var device = TestDevices.Device();

        Assert.Equal(TestDevices.Vendor, device.Info.IdVendor);
        Assert.Equal(TestDevices.Product, device.Info.IdProduct);
        Assert.Equal(TestDevices.BcdDevice, device.Info.BcdDevice);
        Assert.Equal(UsbIpProtocol.SpeedHigh, device.Info.Speed);
        Assert.Equal("1-1", device.Info.BusId);
        Assert.Equal(1, device.Info.NumInterfaces);
        Assert.Equal(0x03, Assert.Single(device.Info.Interfaces).Class);
    }

    [Fact]
    public void BusIdsAreOnePerDeviceNumber()
    {
        Assert.Equal("1-1", VirtualHidDevice.BusIdFor(0));
        Assert.Equal("1-2", VirtualHidDevice.BusIdFor(1));
        Assert.Equal(2u, TestDevices.Device(number: 1).Info.DevNum);
    }

    [Fact]
    public void GetDescriptorReturnsTheRealDeviceDescriptor()
    {
        using var device = TestDevices.Device();
        var result = device.Control(TestDevices.Setup(0x80, 0x06, 0x0100, 0, 18), [], 18);

        Assert.Equal(0, result.Status);
        Assert.Equal(TestDevices.DeviceDescriptor(), result.Data);
    }

    [Fact]
    public void TheShortConfigurationReadWindowsMakesFirstIsAnswered()
    {
        using var device = TestDevices.Device();

        // Windows asks for the 9-byte header, reads wTotalLength, then asks for the rest.
        var header = device.Control(TestDevices.Setup(0x80, 0x06, 0x0200, 0, 9), [], 9);
        Assert.Equal(9, header.Data.Length);

        var total = header.Data[2] | (header.Data[3] << 8);
        var full = device.Control(TestDevices.Setup(0x80, 0x06, 0x0200, 0, (ushort)total), [], total);

        Assert.Equal(total, full.Data.Length);
        Assert.Equal(1, full.Data[4]);  // one interface, the HID one
    }

    [Fact]
    public void GetDescriptorReturnsTheHidReportDescriptorVerbatim()
    {
        using var device = TestDevices.Device();
        var result = device.Control(
            TestDevices.Setup(0x81, 0x06, 0x2200, 0, TestDevices.ReportDescriptorLength),
            [], TestDevices.ReportDescriptorLength);

        Assert.Equal(0, result.Status);
        Assert.Equal(TestDevices.ReportDescriptor(), result.Data);
    }

    [Fact]
    public void GetDescriptorReturnsTheHidClassDescriptor()
    {
        using var device = TestDevices.Device();
        var result = device.Control(TestDevices.Setup(0x81, 0x06, 0x2100, 0, 9), [], 9);

        Assert.Equal(0, result.Status);
        Assert.Equal(9, result.Data.Length);
        Assert.Equal(UsbDescriptorType.Hid, result.Data[1]);
    }

    [Fact]
    public void StringDescriptorsAnswerTheLanguageListAndTheNames()
    {
        using var device = TestDevices.Device();

        var languages = device.Control(TestDevices.Setup(0x80, 0x06, 0x0300, 0, 255), [], 255);
        Assert.Equal([0x04, 0x03, 0x09, 0x04], languages.Data);

        // The device descriptor points iProduct at index 2.
        var product = device.Control(TestDevices.Setup(0x80, 0x06, 0x0302, 0x0409, 255), [], 255);
        Assert.Equal(0, product.Status);
        Assert.Equal(UsbDescriptorType.String, product.Data[1]);
        Assert.Equal(device.ProductName, VirtualHidDevice.DecodeString(product.Data));
    }

    [Fact]
    public void AStringIndexNobodyDeclaredStalls()
    {
        using var device = TestDevices.Device();
        var result = device.Control(TestDevices.Setup(0x80, 0x06, 0x0309, 0x0409, 255), [], 255);

        Assert.Equal(UsbIpProtocol.StatusStall, result.Status);
    }

    [Fact]
    public void AStringDescriptorSentByTheMacWinsOverTheFallback()
    {
        var attach = TestDevices.Attach();
        var blocks = attach.Blocks.ToList();
        blocks.Add(new DescriptorBlock(DescriptorKind.StringDescriptor,
            [0x02, .. VirtualHidDevice.EncodeString("DualSense Wireless Controller")]));

        using var device = new VirtualHidDevice(new DeviceAttach(0, blocks), _ => { });
        Assert.Equal("DualSense Wireless Controller", device.ProductName);
    }

    [Fact]
    public void SetConfigurationIsAcceptedAndReadBack()
    {
        using var device = TestDevices.Device();

        Assert.Equal(0, device.Control(TestDevices.Setup(0x00, 0x09, 1, 0, 0), [], 0).Status);
        var read = device.Control(TestDevices.Setup(0x80, 0x08, 0, 0, 1), [], 1);
        Assert.Equal([1], read.Data);

        // A configuration we never advertised is refused.
        Assert.Equal(UsbIpProtocol.StatusStall,
            device.Control(TestDevices.Setup(0x00, 0x09, 7, 0, 0), [], 0).Status);
    }

    [Fact]
    public void TheStandardRequestsWindowsMakesDuringEnumerationAllSucceed()
    {
        using var device = TestDevices.Device();

        Assert.Equal([0, 0], device.Control(TestDevices.Setup(0x80, 0x00, 0, 0, 2), [], 2).Data);
        Assert.Equal(0, device.Control(TestDevices.Setup(0x02, 0x01, 0, 0x84, 0), [], 0).Status);
        Assert.Equal(0, device.Control(TestDevices.Setup(0x01, 0x0B, 0, 0, 0), [], 0).Status);
        Assert.Equal([0], device.Control(TestDevices.Setup(0x81, 0x0A, 0, 0, 1), [], 1).Data);
    }

    [Theory]
    [InlineData((byte)0x05)]
    [InlineData((byte)0x09)]
    [InlineData((byte)0x20)]
    public void GetReportAnswersTheFeatureSnapshotsSteamAsksFor(byte reportId)
    {
        using var device = TestDevices.Device();
        var setup = TestDevices.Setup(0xA1, 0x01, (ushort)(0x0300 | reportId), 0, 255);

        var result = device.Control(setup, [], 255);

        Assert.Equal(0, result.Status);
        Assert.Equal(reportId, result.Data[0]);
        // The snapshot is returned as the controller gave it, report id included.
        Assert.True(result.Data.Length > 1);
    }

    [Fact]
    public void AMissingFeatureSnapshotStallsRatherThanReturningZeros()
    {
        using var device = new VirtualHidDevice(TestDevices.Attach(withFeatures: false), _ => { });

        var result = device.Control(TestDevices.Setup(0xA1, 0x01, 0x0305, 0, 41), [], 41);

        // Zeros in report 0x05 are a divide by zero inside games. A stall is the honest answer.
        Assert.Equal(UsbIpProtocol.StatusStall, result.Status);
        Assert.Equal([0x05, 0x09, 0x20], device.MissingFeatureReports);
    }

    [Fact]
    public void EveryRequiredFeatureSnapshotIsPresentInAHealthyAttach()
    {
        using var device = TestDevices.Device();
        Assert.Empty(device.MissingFeatureReports);
    }

    [Fact]
    public void GetReportOnTheInputReportReturnsTheLatestOne()
    {
        using var device = TestDevices.Device();
        var setup = TestDevices.Setup(0xA1, 0x01, 0x0101, 0, 64);

        Assert.Equal(UsbIpProtocol.StatusStall, device.Control(setup, [], 64).Status);

        device.PushInputReport(TestDevices.InputReport(counter: 9));
        var result = device.Control(setup, [], 64);

        Assert.Equal(0, result.Status);
        Assert.Equal(9, result.Data[7]);
    }

    [Fact]
    public void SetReportForwardsTheOutputReportToTheMac()
    {
        var sent = new List<byte[]>();
        using var device = TestDevices.Device(sent.Add);

        var report = TestDevices.OutputReport();
        var result = device.Control(TestDevices.Setup(0x21, 0x09, 0x0202, 0, 48), report, 48);

        Assert.Equal(0, result.Status);
        Assert.Equal(report, Assert.Single(sent));
    }

    [Fact]
    public void AnIdenticalOutputReportIsNotSentTwice()
    {
        var sent = new List<byte[]>();
        using var device = TestDevices.Device(sent.Add);

        device.WriteInterrupt(TestDevices.OutputReport(rumble: 0x40));
        device.WriteInterrupt(TestDevices.OutputReport(rumble: 0x40));
        device.WriteInterrupt(TestDevices.OutputReport(rumble: 0x10));

        // The report is absolute state, so a repeat carries no information at all.
        Assert.Equal(2, sent.Count);
        Assert.Equal(2, device.OutputReports);
    }

    [Fact]
    public void SetIdleAndProtocolAreAnsweredRatherThanStalled()
    {
        using var device = TestDevices.Device();

        Assert.Equal(0, device.Control(TestDevices.Setup(0x21, 0x0A, 0x0000, 0, 0), [], 0).Status);
        Assert.Equal(0, device.Control(TestDevices.Setup(0x21, 0x0B, 0x0001, 0, 0), [], 0).Status);
        Assert.Equal([1], device.Control(TestDevices.Setup(0xA1, 0x03, 0, 0, 1), [], 1).Data);
    }

    [Fact]
    public void AVendorRequestStalls()
    {
        using var device = TestDevices.Device();
        Assert.Equal(UsbIpProtocol.StatusStall,
            device.Control(TestDevices.Setup(0xC0, 0x01, 0, 0, 8), [], 8).Status);
    }

    [Fact]
    public void ATruncatedSetupPacketStalls()
    {
        using var device = TestDevices.Device();
        Assert.Equal(UsbIpProtocol.StatusStall, device.Control(new byte[4], [], 8).Status);
    }

    [Fact]
    public void AnAttachMissingTheDeviceDescriptorIsRefused()
    {
        var blocks = TestDevices.Attach().Blocks.Where(b => b.Kind != DescriptorKind.Device).ToList();
        Assert.Throws<FormatException>(() => new VirtualHidDevice(new DeviceAttach(0, blocks), _ => { }));
    }

    [Theory]
    [InlineData((byte)0x00, "5%")]
    [InlineData((byte)0x08, "85%")]
    [InlineData((byte)0x18, "85%, заряжается")]
    [InlineData((byte)0x20, "заряжен")]
    public void BatteryComesOutOfByteFiftyThree(byte raw, string expected)
    {
        var report = TestDevices.InputReport(battery: raw);
        Assert.Equal(expected, DualSenseInput.DescribeBattery(report));
    }

    [Fact]
    public void ATooShortReportHasNoBattery() =>
        Assert.Null(DualSenseInput.DescribeBattery(new byte[10]));
}
