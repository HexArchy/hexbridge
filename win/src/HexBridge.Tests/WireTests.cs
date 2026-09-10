using System.Security.Cryptography;
using HexBridge;
using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// The shared wire format. The device channel rides the same socket, key and session as the
/// voice, so the only thing that had to change for it was which types the codec lets past.
/// </summary>
public class WireTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(PacketType.Audio)]
    [InlineData(PacketType.Hello)]
    [InlineData(PacketType.Pong)]
    [InlineData(PacketType.DeviceAttach)]
    [InlineData(PacketType.DeviceDetach)]
    [InlineData(PacketType.DeviceInput)]
    [InlineData(PacketType.DeviceOutput)]
    [InlineData(PacketType.DeviceAck)]
    public void EveryTypeTheProtocolDefinesIsAccepted(PacketType type)
    {
        var bytes = new byte[Wire.HeaderSize];
        Wire.WriteHeader(bytes, new Header(type, PacketFlags.None, 1, 2, 3));

        Assert.True(Wire.TryReadHeader(bytes, out var header));
        Assert.Equal(type, header.Type);
    }

    [Fact]
    public void ATypeTheProtocolDoesNotDefineIsRejected()
    {
        var bytes = new byte[Wire.HeaderSize];
        Wire.WriteHeader(bytes, new Header(PacketType.Audio, PacketFlags.None, 1, 2, 3));
        bytes[5] = 99;

        Assert.False(Wire.TryReadHeader(bytes, out _));
    }

    [Fact]
    public void TypeNumbersMatchTheContract()
    {
        Assert.Equal(4, (byte)PacketType.DeviceAttach);
        Assert.Equal(5, (byte)PacketType.DeviceDetach);
        Assert.Equal(6, (byte)PacketType.DeviceInput);
        Assert.Equal(7, (byte)PacketType.DeviceOutput);
        Assert.Equal(8, (byte)PacketType.DeviceAck);
    }

    [Fact]
    public void AnAckSealedTowardsTheSenderOpensAgain()
    {
        using var aes = new AesGcm(Key, Wire.TagSize);
        var header = new Header(PacketType.DeviceAck, PacketFlags.None, Wire.RoomId(Key), 0x1234, 5);
        var payload = DeviceChannel.WriteAck(1);

        var datagram = Wire.Seal(aes, header, payload, Direction.ReceiverToSender);
        var plaintext = new byte[Wire.MaxPacket];

        var length = Wire.Open(aes, datagram, plaintext, Direction.ReceiverToSender, out var read);
        Assert.Equal(1, length);
        Assert.Equal(PacketType.DeviceAck, read.Type);
        Assert.True(DeviceChannel.TryReadAck(plaintext.AsSpan(0, length), out var device));
        Assert.Equal(1, device);

        // The direction is part of the nonce, so the same bytes read the other way fail.
        Assert.Equal(-1, Wire.Open(aes, datagram, plaintext, Direction.SenderToReceiver, out _));
    }

    [Fact]
    public void ADeviceInputPacketSurvivesTheRoundTrip()
    {
        using var aes = new AesGcm(Key, Wire.TagSize);
        var room = Wire.RoomId(Key);
        var payload = DeviceChannel.WriteInput(new DeviceInput(0, 12345, TestDevices.InputReport(counter: 3)));

        var datagram = Wire.Seal(aes, new Header(PacketType.DeviceInput, PacketFlags.None, room, 1, 1),
            payload, Direction.SenderToReceiver);

        var plaintext = new byte[Wire.MaxPacket];
        var length = Wire.Open(aes, datagram, plaintext, Direction.SenderToReceiver, out var header);

        Assert.Equal(PacketType.DeviceInput, header.Type);
        Assert.True(DeviceChannel.TryReadInput(plaintext.AsSpan(0, length), out var input));
        Assert.Equal(12345u, input.Index);
        Assert.Equal(3, input.Report[7]);
    }

    [Fact]
    public void AGamepadStreamStaysInsideTheReplayWindow()
    {
        var window = new ReplayWindow();

        // 250 input reports a second plus voice: the window has to hold a busy second
        // without ever refusing a fresh sequence number.
        for (var seq = 0u; seq < 3000; seq++) Assert.True(window.Accept(seq));

        Assert.False(window.Accept(2999));
        Assert.False(window.Accept(100));
    }
}
