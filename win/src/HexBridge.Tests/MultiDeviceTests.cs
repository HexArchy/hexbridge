using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HexBridge;
using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// Four devices at once: the numbers the protocol hands out, the busids they turn into, and
/// the promise that unplugging one leaves the other three alone.
///
/// The whole design rests on the device number being the only identity — most pads report no
/// serial number — so every test here is really a test of one question: does device
/// <c>n</c> end up on busid <c>1-(n+1)</c> and nowhere else.
/// </summary>
public class MultiDeviceTests
{

    private static ReceiverConfig Config() => new()
    {
        Listen = $"127.0.0.1:{Ports.Free()}",
        Psk = ReceiverConfig.GenerateKey(),
        Output = "null",
        Gamepad = true,
        UsbIpListen = $"127.0.0.1:{Ports.Free()}",
        UsbIpAutoAttach = false,
    };

    // MARK: - The USB/IP server on its own

    [Fact]
    public async Task FourDevicesAreListedWithFourDistinctBusIds()
    {
        var devices = Enumerable.Range(0, 4)
            .Select(n => TestDevices.Device(number: (byte)n))
            .ToArray();
        try
        {
            await using var server = new UsbIpServer(() => devices, (_, _) => { });
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));

            await using var client = await UsbIpTestClient.ConnectAsync(server.LocalEndPoint!);
            var (header, count, listed) = await client.DeviceListAllAsync();

            Assert.Equal(UsbIpProtocol.StatusOk, header.Status);
            Assert.Equal(4u, count);
            Assert.Equal(4, listed.Length);

            Assert.Equal(["1-1", "1-2", "1-3", "1-4"], listed.Select(l => l.Device.BusId));

            // devid is what vhci stamps on every URB. Two devices sharing one would send
            // half of somebody's steering input to the pedals.
            Assert.Equal(4, listed.Select(l => l.Device.DevId).Distinct().Count());

            // Each entry still carries its own interface list, which is the part a
            // fixed-stride reader would get wrong.
            Assert.All(listed, l => Assert.Equal(0x03, Assert.Single(l.Interfaces).Class));
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
    }

    [Fact]
    public async Task EachBusIdImportsItsOwnDeviceAndLeavesTheRestAvailable()
    {
        var devices = Enumerable.Range(0, 4)
            .Select(n => TestDevices.Device(number: (byte)n))
            .ToArray();
        try
        {
            await using var server = new UsbIpServer(() => devices, (_, _) => { });
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = server.LocalEndPoint!;

            await using var third = await UsbIpTestClient.ConnectAsync(endpoint);
            var (header, info) = await third.ImportAsync("1-3");

            Assert.Equal(UsbIpProtocol.StatusOk, header.Status);
            Assert.Equal(devices[2].Info.DevId, info!.DevId);
            Assert.True(server.IsImported("1-3"));
            Assert.False(server.IsImported("1-1"));

            // A second client takes a different busid on the same server, which is exactly
            // what `usbip attach` does once per device.
            await using var first = await UsbIpTestClient.ConnectAsync(endpoint);
            var (firstHeader, firstInfo) = await first.ImportAsync("1-1");
            Assert.Equal(UsbIpProtocol.StatusOk, firstHeader.Status);
            Assert.Equal(devices[0].Info.DevId, firstInfo!.DevId);

            // And the one already taken stays taken.
            await using var again = await UsbIpTestClient.ConnectAsync(endpoint);
            var (busy, _) = await again.ImportAsync("1-3");
            Assert.Equal(UsbIpProtocol.StatusNoDevice, busy.Status);
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
    }

    [Fact]
    public async Task ImportedDevicesReceiveOnlyTheirOwnInterruptReports()
    {
        var devices = new[]
        {
            TestDevices.Device(number: 0),
            TestDevices.Device(number: 1, vendor: TestDevices.UnknownVendor, product: TestDevices.UnknownProduct),
        };
        try
        {
            await using var server = new UsbIpServer(() => devices, (_, _) => { });
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = server.LocalEndPoint!;

            await using var first = await UsbIpTestClient.ConnectAsync(endpoint);
            Assert.Equal(UsbIpProtocol.StatusOk, (await first.ImportAsync("1-1")).Header.Status);
            await using var second = await UsbIpTestClient.ConnectAsync(endpoint);
            Assert.Equal(UsbIpProtocol.StatusOk, (await second.ImportAsync("1-2")).Header.Status);

            // A report pushed into one device must never surface on the other's endpoint.
            devices[0].PushInputReport(TestDevices.InputReport(counter: 11));
            devices[1].PushInputReport(TestDevices.InputReport(counter: 22));

            await first.InterruptInAsync(0x04, TestDevices.InputReportLength);
            var firstReply = await first.ReadSubmitReplyAsync();
            await second.InterruptInAsync(0x04, TestDevices.InputReportLength);
            var secondReply = await second.ReadSubmitReplyAsync();

            Assert.Equal(11, firstReply.Data[7]);
            Assert.Equal(22, secondReply.Data[7]);
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
    }

    // MARK: - The whole feature, over a real socket

    /// <summary>A sender that speaks the wire protocol at a running receiver.</summary>
    private sealed class Peer : IDisposable
    {
        private readonly AesGcm _aes;
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly IPEndPoint _target;
        private readonly ulong _room;
        private uint _seq = 1;

        /// <summary>
        /// One report counter per device, the way the real sender keeps them. A single
        /// shared counter would make every switch between two forwarded devices look like
        /// a lost report on both of them.
        /// </summary>
        private readonly uint[] _reportIndex = new uint[4];

        public Peer(ReceiverConfig config)
        {
            Assert.True(config.TryGetKey(out var key, out _));
            _aes = new AesGcm(key, Wire.TagSize);
            _room = Wire.RoomId(key);
            _target = ReceiverConfig.ParseEndpoint(config.Listen, 47702);
        }

        public async Task Send(PacketType type, byte[] payload)
        {
            var datagram = Wire.Seal(_aes, new Header(type, PacketFlags.None, _room, 42, _seq++),
                payload, Direction.SenderToReceiver);
            await _socket.SendAsync(datagram, _target);
        }

        public Task Attach(byte device, ushort vendor = TestDevices.Vendor, ushort product = TestDevices.Product) =>
            Send(PacketType.DeviceAttach,
                DeviceChannel.WriteAttach(TestDevices.Attach(device, vendor: vendor, product: product)));

        /// <summary>
        /// Announces devices and keeps announcing until each one is acknowledged — which is
        /// exactly what the real sender does once a second, and for the same reason. UDP on
        /// loopback does drop packets under load, and a test that assumed otherwise would
        /// fail for a case the product already handles.
        /// </summary>
        public async Task AttachUntilAcked(params (byte Number, ushort Vendor, ushort Product)[] devices)
        {
            var pending = devices.ToDictionary(d => d.Number);
            for (var round = 0; round < 10 && pending.Count > 0; round++)
            {
                foreach (var device in pending.Values.ToArray())
                {
                    await Attach(device.Number, device.Vendor, device.Product);
                }
                foreach (var acked in await CollectAcks(pending.Count, TimeSpan.FromMilliseconds(500)))
                {
                    pending.Remove(acked);
                }
            }
            Assert.Empty(pending.Keys);
        }

        public Task AttachUntilAcked(params byte[] numbers) =>
            AttachUntilAcked([.. numbers.Select(n => (n, TestDevices.Vendor, TestDevices.Product))]);

        public Task Detach(byte device) => Send(PacketType.DeviceDetach, [device]);

        /// <summary>One input report, with this device's own next index.</summary>
        public Task Input(byte device, byte counter) =>
            Send(PacketType.DeviceInput,
                DeviceChannel.WriteInput(
                    new DeviceInput(device, _reportIndex[device]++, TestDevices.InputReport(counter))));

        /// <summary>Pretends a report for this device never made it onto the wire.</summary>
        public void Drop(byte device) => _reportIndex[device]++;

        /// <summary>Drains the acks the receiver sends back, up to a deadline.</summary>
        public async Task<List<byte>> CollectAcks(int expected, TimeSpan timeout)
        {
            var acks = new List<byte>();
            using var deadline = new CancellationTokenSource(timeout);
            try
            {
                while (acks.Count < expected)
                {
                    var received = await _socket.ReceiveAsync(deadline.Token);
                    var plaintext = new byte[Wire.MaxPacket];
                    var length = Wire.Open(_aes, received.Buffer, plaintext, Direction.ReceiverToSender, out var header);
                    if (length < 0 || header.Type != PacketType.DeviceAck) continue;
                    if (DeviceChannel.TryReadAck(plaintext.AsSpan(0, length), out var device)) acks.Add(device);
                }
            }
            catch (OperationCanceledException)
            {
                // Fewer than asked for; the assertion says what that means.
            }
            return acks;
        }

        public void Dispose()
        {
            _socket.Dispose();
            _aes.Dispose();
        }
    }

    /// <summary>
    /// Polls the published snapshot until it says what the test expects. <paramref name="poke"/>
    /// is re-sent every so often, so a packet lost on loopback costs a retry rather than a
    /// red build — the same repetition the sender does on the wire.
    /// </summary>
    private static async Task<DevicesState> Settle(
        ReceiverService receiver, Func<DevicesState, bool> until, string what, Func<Task>? poke = null)
    {
        DevicesState? state = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            state = receiver.Snapshot.Feature<DevicesState>("devices");
            if (state is not null && until(state)) return state;
            if (poke is not null && attempt % 8 == 7) await poke();
            await Task.Delay(50);
        }
        Assert.Fail($"не дождались: {what} (сейчас {state?.Devices.Count ?? -1} устройств)");
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task FourDevicesForwardAtOnceAndAFifthNumberIsRefused()
    {
        var config = Config();
        var feature = new DevicesFeature();
        await using var receiver = new ReceiverService(feature);
        await receiver.StartAsync(config);

        using var peer = new Peer(config);
        await peer.AttachUntilAcked(0, 1, 2, 3);

        var state = await Settle(receiver, s => s.Devices.Count == 4, "четыре устройства");
        Assert.Equal(["1-1", "1-2", "1-3", "1-4"], state.Devices.Select(d => d.BusId));
        Assert.Equal([0, 1, 2, 3], state.Devices.Select(d => d.Number));

        // The device list the vhci driver sees agrees with the snapshot.
        var usbip = ReceiverConfig.ParseEndpoint(config.UsbIpListen, 3240);
        await using (var client = await UsbIpTestClient.ConnectAsync(usbip))
        {
            var (_, count, listed) = await client.DeviceListAllAsync();
            Assert.Equal(4u, count);
            Assert.Equal(["1-1", "1-2", "1-3", "1-4"], listed.Select(l => l.Device.BusId));
        }

        // Number 4 has no virtual port to live on. Refusing it is the only safe answer:
        // folding it onto an occupied number would unplug a controller somebody is holding.
        for (var attempt = 0; attempt < 40 && feature.RejectedAttaches == 0; attempt++)
        {
            await peer.Attach(4);
            await Task.Delay(50);
        }

        Assert.True(feature.RejectedAttaches > 0, "пятое устройство приняли");
        var after = receiver.Snapshot.Feature<DevicesState>("devices")!;
        Assert.Equal(4, after.Devices.Count);
        Assert.Equal(4, after.MaxDevices);
    }

    [Fact]
    public async Task ReportsNeverCrossBetweenDevices()
    {
        var config = Config();
        await using var receiver = new ReceiverService(new DevicesFeature());
        await receiver.StartAsync(config);

        using var peer = new Peer(config);
        await peer.AttachUntilAcked(
            (0, TestDevices.Vendor, TestDevices.Product),
            (1, TestDevices.UnknownVendor, TestDevices.UnknownProduct));
        await Settle(receiver, s => s.Devices.Count == 2, "два устройства");

        // Five reports for device 0, two for device 1, interleaved on one socket, with one
        // of device 1's dropped on the way.
        foreach (var step in new Func<Task>[]
                 {
                     () => peer.Input(0, 1),
                     () => peer.Input(1, 1),
                     () => peer.Input(0, 2),
                     () => peer.Input(0, 3),
                     () => { peer.Drop(1); return peer.Input(1, 2); },
                     () => peer.Input(0, 4),
                     () => peer.Input(0, 5),
                 })
        {
            await step();
            // Spaced out on purpose: a burst on loopback can overrun the receive buffer,
            // and a report the socket never saw would look like a report the receiver
            // misrouted.
            await Task.Delay(5);
        }

        var state = await Settle(receiver,
            s => s.Devices.Count == 2 && s.Devices.Sum(d => d.ReportsReceived) == 7,
            "семь репортов");

        Assert.Equal(5, state.Devices.Single(d => d.Number == 0).ReportsReceived);
        Assert.Equal(2, state.Devices.Single(d => d.Number == 1).ReportsReceived);

        // Gap accounting is per device: the loss belongs to the device that lost it, and
        // interleaving with a healthy stream does not smear it across both.
        Assert.Equal(0, state.Devices.Single(d => d.Number == 0).ReportsLost);
        Assert.Equal(1, state.Devices.Single(d => d.Number == 1).ReportsLost);

        // And the profile is per device: only the model we recognise gets decoded.
        Assert.True(state.Devices.Single(d => d.Number == 0).CanVisualise);
        Assert.False(state.Devices.Single(d => d.Number == 1).CanVisualise);
    }

    [Fact]
    public async Task UnpluggingOneDeviceLeavesTheOthersAndFreesItsNumber()
    {
        var config = Config();
        await using var receiver = new ReceiverService(new DevicesFeature());
        await receiver.StartAsync(config);

        using var peer = new Peer(config);
        await peer.AttachUntilAcked(0, 1, 2);
        await Settle(receiver, s => s.Devices.Count == 3, "три устройства");

        await peer.Detach(1);

        var state = await Settle(receiver, s => s.Devices.Count == 2, "два устройства после отключения",
            () => peer.Detach(1));
        Assert.Equal(["1-1", "1-3"], state.Devices.Select(d => d.BusId));
        Assert.Equal([0, 2], state.Devices.Select(d => d.Number));

        // The survivors keep working: a report addressed to 2 still lands.
        await peer.Input(2, 9);
        var reporting = await Settle(receiver,
            s => s.Devices.Any(d => d.Number == 2 && d.ReportsReceived > 0),
            "репорт на уцелевшее устройство");
        Assert.True(reporting.Devices.Single(d => d.Number == 2).ReportsReceived >= 1);
        Assert.Equal(0, reporting.Devices.Single(d => d.Number == 0).ReportsReceived);

        // The number is free again and the Mac may hand it to a different device.
        await peer.AttachUntilAcked((1, TestDevices.UnknownVendor, TestDevices.UnknownProduct));
        var reused = await Settle(receiver, s => s.Devices.Count == 3, "номер 1 выдан заново");
        var replacement = reused.Devices.Single(d => d.Number == 1);
        Assert.Equal("1-2", replacement.BusId);
        Assert.Equal(TestDevices.UnknownVendor, replacement.VendorId);
        Assert.Null(replacement.ProfileName);
    }

    [Fact]
    public async Task ARepeatedAttachIsAcknowledgedWithoutRebuildingTheDevice()
    {
        var config = Config();
        await using var receiver = new ReceiverService(new DevicesFeature());
        await receiver.StartAsync(config);

        using var peer = new Peer(config);
        await peer.AttachUntilAcked(0);
        await Settle(receiver, s => s.Devices.Count == 1, "одно устройство");

        await Settle(receiver, s => s.Devices[0].ReportsReceived == 1, "первый репорт",
            () => peer.Input(0, 1));

        // The sender repeats DEV_ATTACH once a second until it is acknowledged. A repeat
        // that rebuilt the device would reset the counters and drop the usbip session.
        await peer.Attach(0);
        Assert.Equal([0], await peer.CollectAcks(1, TimeSpan.FromSeconds(5)));

        await Task.Delay(200);
        var state = receiver.Snapshot.Feature<DevicesState>("devices")!;
        Assert.Equal(1, Assert.Single(state.Devices).ReportsReceived);
    }

    [Fact]
    public async Task AttachingADifferentDeviceOnTheSameNumberReplacesIt()
    {
        var config = Config();
        await using var receiver = new ReceiverService(new DevicesFeature());
        await receiver.StartAsync(config);

        using var peer = new Peer(config);
        await peer.AttachUntilAcked(0);
        await Settle(receiver, s => s.Devices.Count == 1, "одно устройство");

        // Unplug a pad, plug another into the same port, and lose the DEV_DETACH in
        // between: the number comes back with different hardware behind it, and treating
        // that as "nothing changed" would leave Windows driving the wrong descriptors.
        var state = await Settle(receiver,
            s => s.Devices.Count == 1 && s.Devices[0].VendorId == TestDevices.UnknownVendor,
            "устройство заменено",
            () => peer.Attach(0, TestDevices.UnknownVendor, TestDevices.UnknownProduct));
        Assert.Equal("1-1", state.Devices[0].BusId);
    }
}
