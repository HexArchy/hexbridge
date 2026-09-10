using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HexBridge;
using HexBridge.DualSense;
using HexBridge.Microphone;

namespace HexBridge.Tests;

/// <summary>
/// The feature host and the device feature riding on it, including the acknowledgement the
/// contract requires for every DEV_ATTACH.
/// </summary>
public class FeatureHostTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ReceiverConfig Config() => new()
    {
        Listen = $"127.0.0.1:{FreePort()}",
        Psk = ReceiverConfig.GenerateKey(),
        Output = "null",
        Gamepad = true,
        UsbIpListen = $"127.0.0.1:{FreePort()}",
        UsbIpAutoAttach = false,
    };

    [Fact]
    public void TwoFeaturesCannotClaimTheSamePacketType()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ReceiverService(new MicrophoneFeature(), new MicrophoneFeature()));

        Assert.Contains("Audio", error.Message);
    }

    [Fact]
    public void TheMicrophoneAndTheGamepadClaimDisjointTypes()
    {
        var microphone = new MicrophoneFeature();
        var gamepad = new DualSenseFeature();

        Assert.Empty(microphone.HandledTypes.Intersect(gamepad.HandledTypes));
        Assert.Contains(PacketType.Audio, microphone.HandledTypes);
        Assert.Contains(PacketType.DeviceAttach, gamepad.HandledTypes);
        Assert.Contains(PacketType.DeviceInput, gamepad.HandledTypes);

        // The two answers the receiver sends are not routed to anyone: they only leave.
        Assert.DoesNotContain(PacketType.DeviceAck, gamepad.HandledTypes);
        Assert.DoesNotContain(PacketType.DeviceOutput, gamepad.HandledTypes);
    }

    [Fact]
    public void TheGamepadIsOptionalAndTheMicrophoneIsNot()
    {
        Assert.False(new MicrophoneFeature().IsOptional);
        Assert.True(new DualSenseFeature().IsOptional);
    }

    [Fact]
    public async Task ADisabledFeatureIsNotStartedButStillHasAPageToShow()
    {
        var config = Config();
        config.Gamepad = false;

        await using var receiver = new ReceiverService(new MicrophoneFeature(), new DualSenseFeature());
        await receiver.StartAsync(config);
        await Task.Delay(300);

        var state = receiver.Snapshot.Features["dualsense"];
        Assert.Equal(FeatureStatus.Disabled, state.Status);
    }

    [Fact]
    public async Task AttachIsAcknowledgedEveryTimeItArrives()
    {
        var config = Config();
        Assert.True(config.TryGetKey(out var key, out _));

        await using var receiver = new ReceiverService(new MicrophoneFeature(), new DualSenseFeature());
        await receiver.StartAsync(config);

        using var aes = new AesGcm(key, Wire.TagSize);
        var room = Wire.RoomId(key);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = ReceiverConfig.ParseEndpoint(config.Listen, 47702);

        var attach = DeviceChannel.WriteAttach(TestDevices.Attach());
        var plaintext = new byte[Wire.MaxPacket];

        for (uint seq = 1; seq <= 2; seq++)
        {
            var datagram = Wire.Seal(aes,
                new Header(PacketType.DeviceAttach, PacketFlags.None, room, 0x1234, seq),
                attach, Direction.SenderToReceiver);
            await socket.SendAsync(datagram, target);

            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reply = await socket.ReceiveAsync(cancel.Token);

            var length = Wire.Open(aes, reply.Buffer, plaintext, Direction.ReceiverToSender, out var header);

            // Every DEV_ATTACH is acknowledged, not only the first: a lost ack has to be
            // curable by the sender simply repeating itself.
            Assert.Equal(PacketType.DeviceAck, header.Type);
            Assert.Equal(1, length);
            Assert.True(DeviceChannel.TryReadAck(plaintext.AsSpan(0, length), out var device));
            Assert.Equal(0, device);
        }

        // The snapshot is republished on the host's own tick, so it lags the ack slightly.
        DualSenseState? state = null;
        for (var attempt = 0; attempt < 50 && state?.Attached != true; attempt++)
        {
            await Task.Delay(50);
            state = receiver.Snapshot.Feature<DualSenseState>("dualsense");
        }

        Assert.NotNull(state);
        Assert.True(state!.Attached);
        Assert.Equal(TestDevices.Vendor, state.VendorId);
        Assert.Equal("1-1", state.BusId);
    }

    [Fact]
    public async Task InputReportsReachTheVirtualDeviceAndLossIsCounted()
    {
        var config = Config();
        Assert.True(config.TryGetKey(out var key, out _));

        await using var receiver = new ReceiverService(new DualSenseFeature());
        await receiver.StartAsync(config);

        using var aes = new AesGcm(key, Wire.TagSize);
        var room = Wire.RoomId(key);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = ReceiverConfig.ParseEndpoint(config.Listen, 47702);

        uint seq = 1;
        async Task Send(PacketType type, byte[] payload)
        {
            var datagram = Wire.Seal(aes, new Header(type, PacketFlags.None, room, 7, seq++),
                payload, Direction.SenderToReceiver);
            await socket.SendAsync(datagram, target);
        }

        await Send(PacketType.DeviceAttach, DeviceChannel.WriteAttach(TestDevices.Attach()));
        await socket.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Indices 0, 1 and 4: two reports are missing between the second and the third.
        foreach (var index in new uint[] { 0, 1, 4 })
        {
            await Send(PacketType.DeviceInput,
                DeviceChannel.WriteInput(new DeviceInput(0, index, TestDevices.InputReport((byte)index))));
        }

        DualSenseState? state = null;
        for (var attempt = 0; attempt < 50 && (state?.ReportsReceived ?? 0) < 3; attempt++)
        {
            await Task.Delay(50);
            state = receiver.Snapshot.Feature<DualSenseState>("dualsense");
        }

        Assert.NotNull(state);
        Assert.Equal(3, state!.ReportsReceived);
        Assert.Equal(2, state.ReportsLost);
    }

    [Fact]
    public async Task DetachTakesTheDeviceOutOfTheDeviceList()
    {
        var config = Config();
        Assert.True(config.TryGetKey(out var key, out _));

        await using var receiver = new ReceiverService(new DualSenseFeature());
        await receiver.StartAsync(config);

        using var aes = new AesGcm(key, Wire.TagSize);
        var room = Wire.RoomId(key);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = ReceiverConfig.ParseEndpoint(config.Listen, 47702);

        uint seq = 1;
        async Task Send(PacketType type, byte[] payload)
        {
            var datagram = Wire.Seal(aes, new Header(type, PacketFlags.None, room, 9, seq++),
                payload, Direction.SenderToReceiver);
            await socket.SendAsync(datagram, target);
        }

        await Send(PacketType.DeviceAttach, DeviceChannel.WriteAttach(TestDevices.Attach()));
        await socket.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        var usbip = ReceiverConfig.ParseEndpoint(config.UsbIpListen, 3240);
        await using (var client = await UsbIpTestClient.ConnectAsync(usbip))
        {
            var (_, count, _, _) = await client.DeviceListAsync();
            Assert.Equal(1u, count);
        }

        await Send(PacketType.DeviceDetach, [0]);

        var gone = false;
        for (var attempt = 0; attempt < 50 && !gone; attempt++)
        {
            await Task.Delay(50);
            await using var client = await UsbIpTestClient.ConnectAsync(usbip);
            var (_, count, _, _) = await client.DeviceListAsync();
            gone = count == 0;
        }

        Assert.True(gone, "устройство осталось в списке после DEV_DETACH");
    }
}
