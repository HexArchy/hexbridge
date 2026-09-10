using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// A plain TCP client that speaks USB/IP the way usbip-win2 does. Written against the
/// specification rather than against the server, so the two agreeing means something.
/// </summary>
public sealed class UsbIpTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private readonly Dictionary<uint, bool> _directions = [];
    private NetworkStream _stream = null!;
    private uint _seqnum;

    public static async Task<UsbIpTestClient> ConnectAsync(IPEndPoint endpoint)
    {
        var client = new UsbIpTestClient();
        await client._tcp.ConnectAsync(endpoint);
        client._stream = client._tcp.GetStream();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        await Task.CompletedTask;
    }

    // MARK: - Operational phase

    public sealed record ListedDevice(UsbIpDeviceInfo Device, UsbIpInterfaceInfo[] Interfaces);

    /// <summary>
    /// Reads a whole OP_REP_DEVLIST. Entries are variable length — a device struct followed
    /// by one four-byte record per interface — so the only way to find the second entry is
    /// to have parsed the first, which is exactly what a multi-device reply has to survive.
    /// </summary>
    public async Task<(OpHeader Header, uint Count, ListedDevice[] Devices)> DeviceListAllAsync()
    {
        await Write(new OpHeader(UsbIpProtocol.Version, UsbIpProtocol.OpReqDevList, 0).ToArray());

        var header = OpHeader.Read(await Read(UsbIpProtocol.OpHeaderSize));
        var count = BinaryPrimitives.ReadUInt32BigEndian(await Read(4));

        var listed = new List<ListedDevice>((int)count);
        for (var entry = 0u; entry < count; entry++)
        {
            var device = UsbIpDeviceInfo.Read(await Read(UsbIpProtocol.UsbDeviceSize));
            var interfaces = new UsbIpInterfaceInfo[device.NumInterfaces];
            for (var i = 0; i < interfaces.Length; i++)
            {
                interfaces[i] = UsbIpInterfaceInfo.Read(await Read(UsbIpProtocol.UsbInterfaceSize));
            }
            listed.Add(new ListedDevice(device, interfaces));
        }
        return (header, count, [.. listed]);
    }

    public async Task<(OpHeader Header, uint Count, UsbIpDeviceInfo? Device, UsbIpInterfaceInfo[] Interfaces)>
        DeviceListAsync()
    {
        var (header, count, devices) = await DeviceListAllAsync();
        return devices.Length == 0
            ? (header, count, null, [])
            : (header, count, devices[0].Device, devices[0].Interfaces);
    }

    public async Task<(OpHeader Header, UsbIpDeviceInfo? Device)> ImportAsync(string busId)
    {
        var request = new byte[UsbIpProtocol.OpHeaderSize + UsbIpProtocol.BusIdSize];
        new OpHeader(UsbIpProtocol.Version, UsbIpProtocol.OpReqImport, 0).Write(request);
        UsbIpProtocol.WriteString(request.AsSpan(UsbIpProtocol.OpHeaderSize, UsbIpProtocol.BusIdSize), busId);
        await Write(request);

        var header = OpHeader.Read(await Read(UsbIpProtocol.OpHeaderSize));
        if (header.Status != UsbIpProtocol.StatusOk) return (header, null);

        return (header, UsbIpDeviceInfo.Read(await Read(UsbIpProtocol.UsbDeviceSize)));
    }

    // MARK: - URB phase

    public Task<uint> ControlInAsync(byte[] setup, int transferBufferLength) =>
        SubmitAsync(0, UsbIpProtocol.DirectionIn, transferBufferLength, setup);

    public Task<uint> ControlOutAsync(byte[] setup, byte[] data) =>
        SubmitAsync(0, UsbIpProtocol.DirectionOut, data.Length, setup, data);

    public Task<uint> InterruptInAsync(int endpoint, int transferBufferLength) =>
        SubmitAsync((uint)(endpoint & 0x0F), UsbIpProtocol.DirectionIn, transferBufferLength, new byte[8]);

    public Task<uint> InterruptOutAsync(int endpoint, byte[] data) =>
        SubmitAsync((uint)(endpoint & 0x0F), UsbIpProtocol.DirectionOut, data.Length, new byte[8], data);

    public async Task<uint> SubmitAsync(
        uint endpoint, uint direction, int transferBufferLength, byte[] setup, byte[]? data = null)
    {
        var seqnum = ++_seqnum;
        _directions[seqnum] = direction == UsbIpProtocol.DirectionIn;

        var submit = new UsbIpSubmit
        {
            Header = new UrbHeader(UsbIpProtocol.CmdSubmit, seqnum, 0x00010001, direction, endpoint),
            TransferBufferLength = transferBufferLength,
            NumberOfPackets = -1,
            Setup = setup,
            TransferBuffer = data ?? [],
        };

        await Write(submit.ToArray());
        return seqnum;
    }

    /// <summary>
    /// An isochronous URB with one descriptor per microframe, laid out the way the driver
    /// lays them out: contiguous packets of <paramref name="packetLength"/> bytes.
    /// </summary>
    public async Task<uint> IsochronousAsync(
        int endpoint, uint direction, int packets, int packetLength, byte[]? data = null)
    {
        var seqnum = ++_seqnum;
        _directions[seqnum] = direction == UsbIpProtocol.DirectionIn;

        var descriptors = new UsbIpIsoPacket[packets];
        for (var i = 0; i < packets; i++)
        {
            descriptors[i] = new UsbIpIsoPacket(i * packetLength, packetLength, 0, 0);
        }

        var submit = new UsbIpSubmit
        {
            Header = new UrbHeader(
                UsbIpProtocol.CmdSubmit, seqnum, 0x00010001, direction, (uint)(endpoint & 0x0F)),
            TransferBufferLength = packets * packetLength,
            NumberOfPackets = packets,
            Interval = 1,
            Setup = new byte[UsbIpProtocol.SetupSize],
            TransferBuffer = data ?? (direction == UsbIpProtocol.DirectionOut
                ? new byte[packets * packetLength]
                : []),
            IsoPackets = descriptors,
        };

        await Write(submit.ToArray());
        return seqnum;
    }

    public async Task<uint> UnlinkAsync(uint victim)
    {
        var seqnum = ++_seqnum;
        await Write(new UsbIpUnlink
        {
            Header = new UrbHeader(UsbIpProtocol.CmdUnlink, seqnum, 0x00010001, 0, 0),
            UnlinkSeqnum = victim,
        }.ToArray());
        return seqnum;
    }

    /// <summary>Reads the next reply of either kind, following the same rules the driver does.</summary>
    public async Task<object> ReadReplyAsync()
    {
        var header = await Read(UsbIpProtocol.UrbHeaderSize);
        var command = BinaryPrimitives.ReadUInt32BigEndian(header);

        if (command == UsbIpProtocol.RetUnlink) return UsbIpUnlinkReply.Read(header);
        if (command != UsbIpProtocol.RetSubmit) throw new InvalidDataException($"команда {command}");

        var seqnum = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var actual = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
        var packets = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32));
        var isIn = _directions.GetValueOrDefault(seqnum, true);

        // Direction is not in the reply — vhci knows it from the URB it still holds, and so
        // does this. An IN transfer sends its payload back, an OUT one does not, and an
        // isochronous transfer appends a descriptor per packet either way.
        var count = packets is UsbIpProtocol.NonIsochronous or 0 ? 0 : (int)packets;
        var following = (isIn && actual > 0 ? actual : 0) + count * UsbIpProtocol.IsoPacketSize;

        var data = following > 0 ? await Read(following) : [];
        return UsbIpSubmitReply.Read(header, data);
    }

    public async Task<UsbIpSubmitReply> ReadSubmitReplyAsync() =>
        Assert.IsType<UsbIpSubmitReply>(await ReadReplyAsync());

    /// <summary>
    /// True when nothing has arrived within the window — how a pending URB is proven.
    /// Peeks at the receive buffer rather than reading, so a failure does not also eat a byte
    /// and turn one bad assertion into a stream that never resynchronises.
    /// </summary>
    public async Task<bool> IsQuietAsync(TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            if (_tcp.Available > 0) return false;
            await Task.Delay(20);
        }
        return _tcp.Available == 0;
    }

    /// <summary>Puts bytes on the socket unchanged, for the malformed-header cases.</summary>
    public Task WriteRawAsync(byte[] bytes) => Write(bytes);

    private async Task Write(byte[] bytes) => await _stream.WriteAsync(bytes);

    private async Task<byte[]> Read(int count)
    {
        var bytes = new byte[count];
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _stream.ReadExactlyAsync(bytes, cancel.Token);
        return bytes;
    }
}
