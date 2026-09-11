using System.Net;
using System.Net.Sockets;
using System.Text;
using HexBridge.Clipboard;
using HexBridge.Devices;
using HexBridge.Microphone;

namespace HexBridge.Tests;

/// <summary>
/// Two whole instances of the product on one machine, one giving its microphone away and one
/// taking it, talking over a real UDP socket with real encryption and real Opus.
///
/// <para>
/// This is the test the sending role exists to pass. Everything else here checks a piece;
/// this checks that the pieces are the same product from both ends — that the direction in
/// the nonce, the role byte in HELLO, the frame counter, the mute flag, the PONG counters
/// and the reliable channel all line up when the machine at each end is this one.
/// </para>
/// </summary>
public class BridgeLoopbackTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AToneCrossesTheLinkAndComesOutAsAudio()
    {
        await using var link = await Link.OpenAsync();

        // The loudest frame seen while waiting, not after. PeakHold is drained by the read
        // — that is what makes it a hold — so every poll of the snapshot takes it away,
        // and asking again afterwards asks about whatever sliver of time has passed since
        // the last question. That sliver can easily be silence while the jitter buffer is
        // filling, and then this test fails having heard the tone perfectly well.
        var loudest = 0f;

        await link.Until(
            () =>
            {
                if (link.Playing is { } sample) loudest = Math.Max(loudest, sample.PeakHold);
                return link.Playing?.Decoded > 25;
            },
            () => $"звук не дошёл; {link.Describe()}");

        var playing = link.Playing!;
        Assert.True(playing.Received > 0);

        // The tone is sent at half scale. Opus rings a little either side of that, so the
        // assertion is «слышно и не искажено», not an exact number.
        Assert.InRange(Math.Max(loudest, playing.PeakHold), 0.2f, 1.0f);
        Assert.Equal(0, playing.DroppedLate);

        // And the sending end knows it arrived, which is a different fact: it means PONG
        // came back through the other direction of the same socket.
        await link.Until(
            () => link.Sender.Snapshot.RemoteReceived > 0,
            () => $"PONG не вернулся; {link.Describe()}");

        Assert.NotNull(link.Sender.Snapshot.RttMs);
        Assert.Equal(0, link.Sender.Snapshot.RemoteLost);
        Assert.Equal(ReceiverStatus.Live, link.Sender.Snapshot.Status);
        Assert.Equal(ReceiverStatus.Live, link.Receiver.Snapshot.Status);
    }

    [Fact]
    public async Task EachEndLearnsTheOtherIsThere()
    {
        await using var link = await Link.OpenAsync();

        await link.Until(
            () => link.Sender.Snapshot.LastPacketAt is not null
                && link.Receiver.Snapshot.LastPacketAt is not null,
            () => $"одна из сторон никого не услышала; {link.Describe()}");

        // Both HELLOs carry the machine name, so both ends can name the other.
        Assert.Equal(Environment.MachineName, link.Receiver.Snapshot.SenderName);
        Assert.Equal(BridgeRole.Sender, link.Sender.Snapshot.Role);
        Assert.Equal(BridgeRole.Receiver, link.Receiver.Snapshot.Role);

        // The giving end binds a port the OS chose, which is the whole reason two roles can
        // share one machine — and one loopback test.
        Assert.NotEqual(link.Receiver.Snapshot.Listen, link.Sender.Snapshot.Listen);
    }

    [Fact]
    public async Task WhatIsCopiedOnTheGivingMachineArrivesOnTheOtherOne()
    {
        await using var link = await Link.OpenAsync();

        var text = "буфер обмена в обратную сторону, «ёж»";
        link.SenderClipboard.UserCopies(new ClipboardItem(BulkFormat.Utf8Text, Encoding.UTF8.GetBytes(text)));

        await link.Until(
            () => link.ReceiverClipboard.Applied.Count > 0,
            () => $"буфер не доехал; {link.Describe()}");

        var applied = link.ReceiverClipboard.Applied[0];
        Assert.Equal(BulkFormat.Utf8Text, applied.Format);
        Assert.Equal(text, Encoding.UTF8.GetString(applied.Bytes));
    }

    [Fact]
    public async Task AnImageCopiedOnTheTakingMachineArrivesOnTheGivingOne()
    {
        await using var link = await Link.OpenAsync();

        // A PNG rather than a line of text: it needs hundreds of BULK_CHUNKs, which is the
        // part of the reliable layer that has never run in this direction before.
        var image = new byte[120_000];
        new Random(41).NextBytes(image);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(image, 0);
        link.ReceiverClipboard.UserCopies(new ClipboardItem(BulkFormat.Png, image));

        await link.Until(
            () => link.SenderClipboard.Applied.Count > 0,
            () => $"картинка не доехала; {link.Describe()}");

        var applied = link.SenderClipboard.Applied[0];
        Assert.Equal(BulkFormat.Png, applied.Format);
        Assert.Equal(image, applied.Bytes);
    }

    [Fact]
    public async Task MuteStopsTheAudioAndSaysSoRatherThanGoingQuiet()
    {
        await using var link = await Link.OpenAsync(muted: true);

        // The keepalive keeps going, so the far end hears us even with nothing to play.
        await link.Until(
            () => link.Receiver.Snapshot.LastPacketAt is not null,
            () => $"приёмник не услышал ни одного пакета; {link.Describe()}");

        await Task.Delay(600);

        Assert.Equal(ReceiverStatus.Muted, link.Receiver.Snapshot.Status);
        Assert.True(link.Receiver.Snapshot.Muted);
        Assert.Equal(0, link.Playing?.Received ?? 0);
        Assert.Equal(0, link.Capturing?.Sent ?? -1);

        // Unmuting is live: no restart, no reconnect, and the frame counter picks up from
        // where it was rather than from zero.
        link.Sender.Muted = false;
        await link.Until(
            () => link.Playing?.Decoded > 5,
            () => $"после снятия мьюта звук не пошёл; {link.Describe()}");
        Assert.Equal(ReceiverStatus.Live, link.Receiver.Snapshot.Status);
    }

    [Fact]
    public async Task TheGivingMachineSaysWhyItCannotForwardAGamepad()
    {
        await using var link = await Link.OpenAsync(gamepad: true);

        var devices = link.Sender.Snapshot.Features["devices"];
        Assert.Equal(FeatureStatus.Disabled, devices.Status);

        // Not «выключено в настройках»: the switch is on, and the reason is a fact about
        // Windows that no switch can change. Saying the wrong one sends the user hunting.
        Assert.NotEqual("Выключено в настройках", devices.Headline);
        Assert.NotNull(devices.Detail);
        Assert.Contains("дескрипторы", devices.Detail);

        // The taking machine, with the same switch, really does run it.
        Assert.NotEqual(FeatureStatus.Disabled, link.Receiver.Snapshot.Features["devices"].Status);
    }

    [Fact]
    public async Task OneInstanceCanBeSwitchedFromOneRoleToTheOtherAndBack()
    {
        // What the settings do when somebody presses «Сменить»: the same host, the same
        // features, a different config. Nothing here is a second process, because the
        // product is one executable and this is the thing that has to survive.
        var psk = ReceiverConfig.GenerateKey();
        var port = FreePort();

        await using var host = new ReceiverService(
            new MicrophoneFeature(),
            new MicrophoneCaptureFeature(),
            new DevicesFeature(),
            new ClipboardFeature(_ => new FakeClipboardSurface()));

        var taking = new ReceiverConfig
        {
            Role = BridgeRole.Receiver,
            Listen = $"127.0.0.1:{port}",
            Psk = psk,
            Output = "null",
            Gamepad = false,
        };
        var giving = new ReceiverConfig
        {
            Role = BridgeRole.Sender,
            Target = $"127.0.0.1:{port}",
            Psk = psk,
            Input = "tone",
            Gamepad = false,
        };

        await host.StartAsync(taking);
        Assert.Equal(BridgeRole.Receiver, host.Snapshot.Role);
        Assert.Equal(FeatureStatus.Waiting, host.Snapshot.Features["microphone"].Status);

        await host.StopAsync();
        await host.StartAsync(giving);
        Assert.Equal(BridgeRole.Sender, host.Snapshot.Role);

        // The other half of the microphone is running now, and it is producing frames — the
        // switch is not just a label.
        await Wait(() => host.Snapshot.Feature<MicrophoneState>("microphone") is { IsCapture: true, Sent: > 5 },
            () => $"после смены роли звук не пошёл: {host.Snapshot.Features["microphone"].Headline}");

        // And the port it used to hold is free, which is what makes switching on one machine
        // possible at all.
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)))
        {
            Assert.NotNull(probe);
        }

        await host.StopAsync();
        await host.StartAsync(taking);
        Assert.Equal(BridgeRole.Receiver, host.Snapshot.Role);
        Assert.False(host.Snapshot.Feature<MicrophoneState>("microphone")?.IsCapture);
    }

    /// <summary>Polls until a condition holds, then fails with something worth reading.</summary>
    private static async Task Wait(Func<bool> condition, Func<string> complaint)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail(complaint());
    }

    // MARK: - Harness

    /// <summary>
    /// Both halves of the product, wired to each other over loopback: two feature hosts, two
    /// clipboards, one tone and no mocks between them.
    /// </summary>
    private sealed class Link : IAsyncDisposable
    {
        public required ReceiverService Receiver { get; init; }
        public required ReceiverService Sender { get; init; }
        public required FakeClipboardSurface ReceiverClipboard { get; init; }
        public required FakeClipboardSurface SenderClipboard { get; init; }
        public required List<string> Log { get; init; }

        /// <summary>The microphone as the taking machine sees it.</summary>
        public MicrophoneState? Playing => Receiver.Snapshot.Feature<MicrophoneState>("microphone");

        /// <summary>The microphone as the giving machine sees it.</summary>
        public MicrophoneState? Capturing => Sender.Snapshot.Feature<MicrophoneState>("microphone");

        public static async Task<Link> OpenAsync(bool muted = false, bool gamepad = false)
        {
            var psk = ReceiverConfig.GenerateKey();
            var port = FreePort();
            var log = new List<string>();

            void Note(string side, LogEntry entry)
            {
                lock (log) log.Add($"{side}: {entry.Message}");
            }

            var receiverConfig = new ReceiverConfig
            {
                Role = BridgeRole.Receiver,
                Listen = $"127.0.0.1:{port}",
                Psk = psk,
                Output = "null",
                Clipboard = true,
                Gamepad = gamepad,
                UsbIpListen = $"127.0.0.1:{FreePort()}",
                UsbIpAutoAttach = false,
                // A short buffer so the test does not spend a quarter of a second priming one.
                JitterMs = 40,
            };

            var senderConfig = new ReceiverConfig
            {
                Role = BridgeRole.Sender,
                Target = $"127.0.0.1:{port}",
                Psk = psk,
                Input = "tone",
                Clipboard = true,
                Gamepad = gamepad,
                StartMuted = muted,
            };

            var receiverClipboard = new FakeClipboardSurface();
            var senderClipboard = new FakeClipboardSurface();

            var receiver = new ReceiverService(
                new MicrophoneFeature(),
                new MicrophoneCaptureFeature(),
                new DevicesFeature(),
                new ClipboardFeature(_ => receiverClipboard));
            receiver.Log += entry => Note("принимает", entry);

            var sender = new ReceiverService(
                new MicrophoneFeature(),
                new MicrophoneCaptureFeature(),
                new DevicesFeature(),
                new ClipboardFeature(_ => senderClipboard));
            sender.Log += entry => Note("отдаёт", entry);

            await receiver.StartAsync(receiverConfig);
            await sender.StartAsync(senderConfig);

            return new Link
            {
                Receiver = receiver,
                Sender = sender,
                ReceiverClipboard = receiverClipboard,
                SenderClipboard = senderClipboard,
                Log = log,
            };
        }

        public async Task Until(Func<bool> condition, Func<string> complaint)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(20);
            }
            Assert.Fail(complaint());
        }

        /// <summary>Both sides in one line, so a timeout says why rather than that.</summary>
        public string Describe()
        {
            string tail;
            lock (Log) tail = string.Join(" | ", Log.TakeLast(8));

            var send = Sender.Snapshot;
            var recv = Receiver.Snapshot;
            return $"отдаёт: status={send.Status} sent={Capturing?.Sent} peer={send.PeerAddress} "
                + $"remote={send.RemoteReceived}/{send.RemoteLost} fault=«{Capturing?.Fault}» ‖ "
                + $"принимает: status={recv.Status} received={Playing?.Received} decoded={Playing?.Decoded} "
                + $"fault=«{Playing?.Fault}» rejected={recv.Rejected} ‖ log=[{tail}]";
        }

        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync();
            await Receiver.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
