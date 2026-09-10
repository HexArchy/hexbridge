using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// The line between "forward a HID device" and "know what a DualSense is".
///
/// Everything on the transport side has to work for a device nobody has ever heard of; a
/// profile may add a name, a battery reading and a list of feature reports, and must add
/// nothing else. These tests are what keeps PS5 knowledge from leaking back down.
/// </summary>
public class DeviceProfileTests
{
    [Fact]
    public void TheDualSenseIsRecognisedAndTheEdgeSharesItsLayout()
    {
        Assert.Same(DeviceProfile.DualSense, DeviceProfile.Of(0x054C, 0x0CE6));
        Assert.Same(DeviceProfile.DualSense, DeviceProfile.Of(0x054C, 0x0DF2));
    }

    [Theory]
    [InlineData(TestDevices.UnknownVendor, TestDevices.UnknownProduct)]  // a wheel
    [InlineData(0x044F, 0xB10A)]                                         // a HOTAS
    [InlineData(0x054C, 0x05C4)]                                         // a DualShock 4: same vendor, other layout
    public void AnythingElseHasNoProfile(int vendor, int product) =>
        Assert.Null(DeviceProfile.Of((ushort)vendor, (ushort)product));

    [Fact]
    public void AnUnknownDeviceBuildsAndServesItsRealDescriptors()
    {
        using var device = TestDevices.Device(
            vendor: TestDevices.UnknownVendor, product: TestDevices.UnknownProduct);

        // No profile, and therefore no claims about the device beyond what it sent us.
        Assert.Null(device.ProfileName);
        Assert.False(device.CanVisualise);
        Assert.Empty(device.MissingFeatureReports);
        Assert.Equal("046D:C29B", device.Identity);
        Assert.Equal("USB 046D:C29B", device.ProductName);

        // The transport half is unchanged: real device descriptor, real report descriptor,
        // real endpoints, all read off the wire and none of it model-specific.
        var descriptor = device.Control(TestDevices.Setup(0x80, 0x06, 0x0100, 0, 18), [], 18);
        Assert.Equal(UsbIpProtocol.StatusSuccess, descriptor.Status);
        Assert.Equal(
            TestDevices.DeviceDescriptor(TestDevices.UnknownVendor, TestDevices.UnknownProduct),
            descriptor.Data);

        var report = device.Control(TestDevices.Setup(0x81, 0x06, 0x2200, 0, 512), [], 512);
        Assert.Equal(TestDevices.ReportDescriptor(), report.Data);
        Assert.Equal(0x84, device.InterruptInEndpoint);
        Assert.Equal(0x03, device.InterruptOutEndpoint);
    }

    [Fact]
    public void AnUnknownDeviceForwardsReportsWithoutDecodingThem()
    {
        using var device = TestDevices.Device(
            vendor: TestDevices.UnknownVendor, product: TestDevices.UnknownProduct);

        // Enough reports to pass the every-25th battery sample, which for this device must
        // never run: byte 53 of a wheel's report is not a battery.
        for (var i = 0; i < 60; i++) device.PushInputReport(TestDevices.InputReport((byte)i));

        Assert.Equal(60, device.InputReports);
        Assert.Null(device.Battery);
    }

    [Fact]
    public void AKnownDeviceStillDecodesTheOnePieceOfStateItOwns()
    {
        using var device = TestDevices.Device();

        for (var i = 0; i < 60; i++) device.PushInputReport(TestDevices.InputReport((byte)i, battery: 0x08));

        Assert.Equal("Wireless Controller", device.ProfileName);
        Assert.True(device.CanVisualise);
        Assert.Equal("85%", device.Battery);
    }

    [Fact]
    public void AnUnknownDeviceIsNotAskedForFeatureReportsItNeverPromised()
    {
        // The DualSense list — firmware, MAC, calibration — is DualSense knowledge. Applying
        // it to a wheel would produce a warning the user can do nothing about.
        var attach = TestDevices.Attach(
            withFeatures: false, vendor: TestDevices.UnknownVendor, product: TestDevices.UnknownProduct);
        using var unknown = new VirtualHidDevice(attach, _ => { });
        Assert.Empty(unknown.MissingFeatureReports);

        using var pad = new VirtualHidDevice(TestDevices.Attach(withFeatures: false), _ => { });
        Assert.Equal([0x05, 0x09, 0x20], pad.MissingFeatureReports);
    }
}
