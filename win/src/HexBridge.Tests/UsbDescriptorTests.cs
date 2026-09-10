using System.Buffers.Binary;
using HexBridge.DualSense;

namespace HexBridge.Tests;

public class UsbDescriptorTests
{
    [Fact]
    public void DeviceDescriptorReadsLittleEndianIds()
    {
        var device = UsbDeviceDescriptor.Parse(TestDevices.DeviceDescriptor());

        Assert.Equal(TestDevices.Vendor, device.IdVendor);
        Assert.Equal(TestDevices.Product, device.IdProduct);
        Assert.Equal(TestDevices.BcdDevice, device.BcdDevice);
        Assert.Equal(1, device.ManufacturerString);
        Assert.Equal(2, device.ProductString);
        Assert.Equal(0, device.SerialString);   // a DualSense has no serial
        Assert.Equal(1, device.NumConfigurations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(17)]
    public void ATruncatedDeviceDescriptorIsRejected(int length) =>
        Assert.Throws<FormatException>(() =>
            UsbDeviceDescriptor.Parse(TestDevices.DeviceDescriptor().AsSpan(0, length)));

    [Fact]
    public void OnlyTheHidInterfaceSurvives()
    {
        var config = UsbConfigurationDescriptor.FromFullDescriptor(TestDevices.ConfigurationDescriptor());

        Assert.Equal(1, config.Bytes[4]);                      // bNumInterfaces
        Assert.Equal(config.Bytes.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(config.Bytes.AsSpan(2)));  // wTotalLength agrees
        Assert.Equal(1, config.ConfigurationValue);
        Assert.Equal(new UsbIpInterfaceInfo(0x03, 0x00, 0x00), config.Interface);

        // Exactly one interface descriptor, and it is the HID one renumbered to zero.
        var interfaces = Descriptors(config.Bytes).Where(d => d[1] == UsbDescriptorType.Interface).ToList();
        var single = Assert.Single(interfaces);
        Assert.Equal(0, single[2]);   // bInterfaceNumber
        Assert.Equal(0, single[3]);   // bAlternateSetting
        Assert.Equal(0x03, single[5]);
    }

    [Fact]
    public void TheAudioFunctionAndItsIsochronousEndpointAreGone()
    {
        var config = UsbConfigurationDescriptor.FromFullDescriptor(TestDevices.ConfigurationDescriptor());
        var descriptors = Descriptors(config.Bytes);

        Assert.DoesNotContain(descriptors, d => d[1] == UsbDescriptorType.InterfaceAssociation);
        Assert.DoesNotContain(descriptors, d => d[1] == UsbDescriptorType.Endpoint && (d[3] & 0x03) == 0x01);
        Assert.All(descriptors.Where(d => d[1] == UsbDescriptorType.Endpoint),
            d => Assert.Equal(0x03, d[3] & 0x03));
    }

    [Fact]
    public void TheInterruptEndpointsAreFound()
    {
        var config = UsbConfigurationDescriptor.FromFullDescriptor(TestDevices.ConfigurationDescriptor());

        Assert.Equal(0x84, config.InterruptInEndpoint);
        Assert.Equal(0x03, config.InterruptOutEndpoint);
        Assert.Equal(64, config.InterruptInMaxPacket);
    }

    [Fact]
    public void TheHidClassDescriptorIsKeptForGetDescriptor()
    {
        var config = UsbConfigurationDescriptor.FromFullDescriptor(TestDevices.ConfigurationDescriptor());

        Assert.NotNull(config.HidDescriptor);
        Assert.Equal(UsbDescriptorType.Hid, config.HidDescriptor![1]);
        Assert.Equal(0x0111, BinaryPrimitives.ReadUInt16LittleEndian(config.HidDescriptor.AsSpan(2)));
        Assert.Equal(UsbDescriptorType.HidReport, config.HidDescriptor[5 + 1]);
    }

    [Fact]
    public void ARewrittenConfigurationIsStillWalkable()
    {
        var config = UsbConfigurationDescriptor.FromFullDescriptor(TestDevices.ConfigurationDescriptor());
        var walked = Descriptors(config.Bytes).Sum(d => d.Length) + 9;

        // Every byte after the header belongs to exactly one descriptor: no padding, no gap.
        Assert.Equal(config.Bytes.Length, walked);
    }

    [Fact]
    public void AConfigurationWithNoHidInterfaceIsRefused()
    {
        byte[] audioOnly =
        [
            0x09, 0x02, 0x12, 0x00, 0x01, 0x01, 0x00, 0xC0, 0xFA,
            0x09, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
        ];

        Assert.Throws<FormatException>(() => UsbConfigurationDescriptor.FromFullDescriptor(audioOnly));
    }

    [Fact]
    public void ATruncatedConfigurationDoesNotWalkOffTheEnd()
    {
        var full = TestDevices.ConfigurationDescriptor();
        // Claim more than we hand over: the parser must trust the shorter of the two.
        BinaryPrimitives.WriteUInt16LittleEndian(full.AsSpan(2), (ushort)(full.Length + 200));

        var config = UsbConfigurationDescriptor.FromFullDescriptor(full);
        Assert.Equal(0x84, config.InterruptInEndpoint);
    }

    private static List<byte[]> Descriptors(byte[] configuration)
    {
        var result = new List<byte[]>();
        var offset = (int)configuration[0];
        while (offset + 2 <= configuration.Length)
        {
            int length = configuration[offset];
            if (length < 2 || offset + length > configuration.Length) break;
            result.Add(configuration[offset..(offset + length)]);
            offset += length;
        }
        return result;
    }
}
