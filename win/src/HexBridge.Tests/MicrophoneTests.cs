using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Concentus;
using Concentus.Enums;
using HexBridge;
using HexBridge.DualSense;
using HexBridge.Microphone;

namespace HexBridge.Tests;

/// <summary>
/// The voice path, which was working in production before any of this and must still be.
/// Nothing here is about the gamepad; it is here to fail loudly if the split into features
/// broke the thing the product exists for.
/// </summary>
public class MicrophoneTests
{
    private const int SampleRate = MicWaveProvider.SampleRate;
    private const int FrameSamples = MicWaveProvider.FrameSamples;

    /// <summary>A 20 ms frame of a 440 Hz tone, Opus-encoded exactly as the Mac sends it.</summary>
    private static byte[] EncodeTone(IOpusEncoder encoder, int frameIndex)
    {
        var pcm = new short[FrameSamples];
        for (var i = 0; i < pcm.Length; i++)
        {
            var t = (frameIndex * FrameSamples + i) / (double)SampleRate;
            pcm[i] = (short)(short.MaxValue * 0.5 * Math.Sin(2 * Math.PI * 440 * t));
        }

        var packet = new byte[400];
        var length = encoder.Encode(pcm, FrameSamples, packet, packet.Length);
        return packet[..length];
    }

    private static IOpusEncoder Encoder()
    {
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32000;
        return encoder;
    }

    [Fact]
    public void TheJitterBufferDecodesAToneIntoStereoFloats()
    {
        var provider = new MicWaveProvider(jitterMs: 60, maxJitterMs: 240, gain: 1.0f);
        var encoder = Encoder();

        for (var i = 0; i < 10; i++) provider.Push((uint)i, EncodeTone(encoder, i));
        Assert.Equal(10, provider.Received);

        var buffer = new byte[FrameSamples * 2 * sizeof(float)];
        var loudest = 0f;
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(buffer.Length, provider.Read(buffer));
            loudest = Math.Max(loudest, provider.LastPeak);
        }

        Assert.True(provider.Decoded > 0, "ни один кадр не декодировался");
        Assert.True(loudest > 0.1f, $"пик {loudest} — звук не дошёл до выхода");
        Assert.Equal(0, provider.Underruns);
    }

    [Fact]
    public void AMissingFrameIsConcealedRatherThanSkipped()
    {
        var provider = new MicWaveProvider(jitterMs: 60, maxJitterMs: 240, gain: 1.0f);
        var encoder = Encoder();

        // Frame 3 never arrives.
        for (var i = 0; i < 8; i++)
        {
            if (i == 3) continue;
            provider.Push((uint)i, EncodeTone(encoder, i));
        }

        var buffer = new byte[FrameSamples * 2 * sizeof(float)];
        for (var i = 0; i < 8; i++) provider.Read(buffer);

        Assert.True(provider.Concealed > 0, "пропуск не был скрыт");
    }

    [Fact]
    public async Task AudioStillArrivesWithTheGamepadFeatureAlongsideIt()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var usbIpPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        udp.Dispose();

        var config = new ReceiverConfig
        {
            Listen = $"127.0.0.1:{listenPort}",
            Psk = ReceiverConfig.GenerateKey(),
            Output = "null",
            JitterMs = 40,
            Gamepad = true,
            UsbIpListen = $"127.0.0.1:{usbIpPort}",
            UsbIpAutoAttach = false,
        };
        Assert.True(config.TryGetKey(out var key, out _));

        await using var receiver = new ReceiverService(new MicrophoneFeature(), new DualSenseFeature());
        await receiver.StartAsync(config);

        using var aes = new AesGcm(key, Wire.TagSize);
        var room = Wire.RoomId(key);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = ReceiverConfig.ParseEndpoint(config.Listen, 47702);

        var encoder = Encoder();
        uint seq = 1;

        // A gamepad attach in the middle of the audio: the two share one socket, and the
        // point of the split is that neither can disturb the other.
        for (uint frame = 0; frame < 25; frame++)
        {
            if (frame == 10)
            {
                var attachPacket = Wire.Seal(aes,
                    new Header(PacketType.DeviceAttach, PacketFlags.None, room, 55, seq++),
                    DeviceChannel.WriteAttach(TestDevices.Attach()), Direction.SenderToReceiver);
                await socket.SendAsync(attachPacket, target);
            }

            var opus = EncodeTone(encoder, (int)frame);
            var payload = new byte[4 + opus.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, frame);
            opus.CopyTo(payload, 4);

            var datagram = Wire.Seal(aes,
                new Header(PacketType.Audio, PacketFlags.None, room, 55, seq++),
                payload, Direction.SenderToReceiver);
            await socket.SendAsync(datagram, target);
            await Task.Delay(5);
        }

        MicrophoneState? microphone = null;
        for (var attempt = 0; attempt < 60 && (microphone?.Decoded ?? 0) < 5; attempt++)
        {
            await Task.Delay(50);
            microphone = receiver.Snapshot.Feature<MicrophoneState>("microphone");
        }

        Assert.NotNull(microphone);
        Assert.Equal(25, microphone!.Received);
        Assert.True(microphone.Decoded >= 5, $"декодировано только {microphone.Decoded} кадров");
        Assert.Null(microphone.Fault);
        Assert.Equal("null (звук никуда не выводится)", microphone.OutputDescription);

        var gamepad = receiver.Snapshot.Feature<DualSenseState>("dualsense");
        Assert.True(gamepad!.Attached);
        Assert.Equal(ReceiverStatus.Live, receiver.Snapshot.Status);
    }

    [Fact]
    public async Task AHelloIsAnsweredWithAPongCarryingTheMicrophoneCounters()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        udp.Dispose();

        var config = new ReceiverConfig
        {
            Listen = $"127.0.0.1:{listenPort}",
            Psk = ReceiverConfig.GenerateKey(),
            Output = "null",
            Gamepad = false,
        };
        Assert.True(config.TryGetKey(out var key, out _));

        await using var receiver = new ReceiverService(new MicrophoneFeature(), new DualSenseFeature());
        await receiver.StartAsync(config);

        using var aes = new AesGcm(key, Wire.TagSize);
        var room = Wire.RoomId(key);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = ReceiverConfig.ParseEndpoint(config.Listen, 47702);

        var encoder = Encoder();
        var opus = EncodeTone(encoder, 0);
        var audio = new byte[4 + opus.Length];
        opus.CopyTo(audio, 4);
        await socket.SendAsync(
            Wire.Seal(aes, new Header(PacketType.Audio, PacketFlags.None, room, 3, 1), audio,
                Direction.SenderToReceiver), target);

        var name = "тест"u8.ToArray();
        var hello = new byte[10 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(hello, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        hello[8] = 0;
        hello[9] = (byte)name.Length;
        name.CopyTo(hello, 10);

        await socket.SendAsync(
            Wire.Seal(aes, new Header(PacketType.Hello, PacketFlags.None, room, 3, 2), hello,
                Direction.SenderToReceiver), target);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await socket.ReceiveAsync(cancel.Token);

        var plaintext = new byte[Wire.MaxPacket];
        var length = Wire.Open(aes, reply.Buffer, plaintext, Direction.ReceiverToSender, out var header);

        Assert.Equal(PacketType.Pong, header.Type);
        Assert.Equal(24, length);
        Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(plaintext.AsSpan(8)));
    }
}
