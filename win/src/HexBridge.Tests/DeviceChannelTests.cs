using HexBridge.DualSense;

namespace HexBridge.Tests;

/// <summary>Round trips for the HID channel payloads in docs/PROTOCOL.md.</summary>
public class DeviceChannelTests
{
    [Fact]
    public void AttachRoundTripsWithEveryBlockKind()
    {
        var original = TestDevices.Attach(device: 2);
        var bytes = DeviceChannel.WriteAttach(original);

        Assert.True(DeviceChannel.TryReadAttach(bytes, out var parsed));
        Assert.Equal(2, parsed.Device);
        Assert.Equal(original.Blocks.Count, parsed.Blocks.Count);

        Assert.Equal(TestDevices.DeviceDescriptor(), parsed.First(DescriptorKind.Device));
        Assert.Equal(TestDevices.ConfigurationDescriptor(), parsed.First(DescriptorKind.Configuration));
        Assert.Equal(TestDevices.ReportDescriptor(), parsed.First(DescriptorKind.HidReport));
        Assert.Equal(3, parsed.All(DescriptorKind.FeatureReport).Count());
    }

    [Fact]
    public void AWholeDualSenseAttachFitsInOneDatagram()
    {
        var bytes = DeviceChannel.WriteAttach(TestDevices.Attach());
        Assert.True(Wire.HeaderSize + bytes.Length + Wire.TagSize <= Wire.MaxPacket,
            $"DEV_ATTACH занял {bytes.Length} байт полезной нагрузки");
    }

    [Fact]
    public void BlockLengthsAreLittleEndian()
    {
        var attach = new DeviceAttach(0, [new DescriptorBlock(DescriptorKind.HidReport, new byte[300])]);
        var bytes = DeviceChannel.WriteAttach(attach);

        Assert.Equal((byte)DescriptorKind.HidReport, bytes[2]);
        Assert.Equal(0x2C, bytes[3]);   // 300 = 0x012C, low byte first
        Assert.Equal(0x01, bytes[4]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(40)]
    public void ATruncatedAttachIsRejectedRatherThanGuessed(int keep)
    {
        var bytes = DeviceChannel.WriteAttach(TestDevices.Attach());
        Assert.False(DeviceChannel.TryReadAttach(bytes.AsSpan(0, keep), out _));
    }

    [Fact]
    public void InputRoundTrips()
    {
        var report = TestDevices.InputReport(counter: 7);
        var bytes = DeviceChannel.WriteInput(new DeviceInput(1, 0x01020304, report));

        Assert.Equal(5 + report.Length, bytes.Length);
        Assert.True(DeviceChannel.TryReadInput(bytes, out var parsed));
        Assert.Equal(1, parsed.Device);
        Assert.Equal(0x01020304u, parsed.Index);
        Assert.Equal(report, parsed.Report);

        // The counter is little-endian, as everything in this protocol's payloads is.
        Assert.Equal(0x04, bytes[1]);
        Assert.Equal(0x01, bytes[4]);
    }

    [Fact]
    public void OutputRoundTripsAndKeepsTheReportId()
    {
        var report = TestDevices.OutputReport();
        var bytes = DeviceChannel.WriteOutput(0, report);

        Assert.Equal(1 + TestDevices.OutputReportLength, bytes.Length);
        Assert.True(DeviceChannel.TryReadOutput(bytes, out var device, out var parsed));
        Assert.Equal(0, device);
        Assert.Equal(0x02, parsed[0]);
        Assert.Equal(report, parsed);
    }

    [Fact]
    public void AckIsOneByte()
    {
        var bytes = DeviceChannel.WriteAck(3);

        Assert.Single(bytes);
        Assert.True(DeviceChannel.TryReadAck(bytes, out var device));
        Assert.Equal(3, device);
        Assert.False(DeviceChannel.TryReadAck(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void DetachIsOneByte()
    {
        Assert.True(DeviceChannel.TryReadDetach([2], out var device));
        Assert.Equal(2, device);
        Assert.False(DeviceChannel.TryReadDetach(ReadOnlySpan<byte>.Empty, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ATruncatedInputIsRejected(int length) =>
        Assert.False(DeviceChannel.TryReadInput(new byte[length], out _));
}
