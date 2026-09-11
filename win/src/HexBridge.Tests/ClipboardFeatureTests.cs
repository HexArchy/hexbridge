using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HexBridge;
using HexBridge.Clipboard;
using HexBridge.Microphone;

namespace HexBridge.Tests;

/// <summary>
/// The clipboard as the receiver actually hosts it: registered on the feature host, routed
/// the four bulk types, and — the test that matters — carrying a real object over a real UDP
/// socket on loopback, with a quarter of the chunks thrown away on the way.
/// </summary>
public class ClipboardFeatureTests
{

    private static ReceiverConfig Config(bool clipboard = true) => new()
    {
        Listen = $"127.0.0.1:{Ports.Free()}",
        Psk = ReceiverConfig.GenerateKey(),
        Output = "null",
        Gamepad = false,
        Clipboard = clipboard,
    };

    [Fact]
    public void TheClipboardClaimsTheFourBulkTypesAndNothingElse()
    {
        var feature = new ClipboardFeature();

        Assert.Equal(
            [PacketType.BulkOffer, PacketType.BulkChunk, PacketType.BulkAck, PacketType.BulkDone],
            feature.HandledTypes);
        Assert.Empty(feature.HandledTypes.Intersect(new MicrophoneFeature().HandledTypes));
    }

    [Fact]
    public void TheClipboardIsOptionalAndOffInAFreshConfig()
    {
        var feature = new ClipboardFeature();

        Assert.True(feature.IsOptional);
        // The whole privacy position in one assertion: a config nobody has edited does not
        // send the clipboard anywhere.
        Assert.False(feature.IsEnabled(new ReceiverConfig()));
        Assert.True(feature.IsEnabled(new ReceiverConfig { Clipboard = true }));
    }

    [Fact]
    public async Task ADisabledClipboardStillHasAPageToShow()
    {
        await using var receiver = new ReceiverService(new ClipboardFeature(_ => new FakeClipboardSurface()));
        await receiver.StartAsync(Config(clipboard: false));
        await Task.Delay(300);

        Assert.Equal(FeatureStatus.Disabled, receiver.Snapshot.Features["clipboard"].Status);
    }

    [Fact]
    public async Task ATextObjectCrossesTheSocketAndLandsOnTheClipboard()
    {
        var surface = new FakeClipboardSurface();
        await using var link = await Link.OpenAsync(surface);

        var text = "буфер обмена, проверка «ёж»";
        link.Offer(BulkFormat.Utf8Text, Encoding.UTF8.GetBytes(text));

        var applied = await link.WaitForApplied(1);
        Assert.Equal(BulkFormat.Utf8Text, applied[0].Format);
        Assert.Equal(text, Encoding.UTF8.GetString(applied[0].Bytes));
    }

    [Fact]
    public async Task AnImageCrossesTheSocketIntactWithAQuarterOfTheChunksLost()
    {
        var surface = new FakeClipboardSurface();
        await using var link = await Link.OpenAsync(surface);

        var random = new Random(31);
        link.DropChunks = () => random.Next(4) == 0;

        var image = new byte[200_000];
        new Random(32).NextBytes(image);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(image, 0);

        link.Offer(BulkFormat.Png, image);

        var applied = await link.WaitForApplied(1);
        Assert.Equal(BulkFormat.Png, applied[0].Format);
        Assert.Equal(image, applied[0].Bytes);
        Assert.True(link.ChunksDropped > 0, "loss was not actually injected");
    }

    [Fact]
    public async Task WhatIsCopiedOnThisMachineIsOfferedToThePeerAndNotBounced()
    {
        var surface = new FakeClipboardSurface();
        await using var link = await Link.OpenAsync(surface);

        var image = new byte[80_000];
        new Random(33).NextBytes(image);
        surface.UserCopies(new ClipboardItem(BulkFormat.Png, image));

        var delivery = await link.WaitForDelivery();
        Assert.Equal(BulkFormat.Png, delivery.Format);
        Assert.Equal(image, delivery.Bytes);

        // The receiver put nothing back on its own clipboard, so there is nothing for its
        // watcher to see and nothing to send us a second time.
        await Task.Delay(1000);
        Assert.Empty(surface.Applied);
        Assert.Equal(1, link.DeliveriesSeen);
    }

    [Fact]
    public async Task AnObjectThePeerAlreadyHoldsIsRefusedRatherThanPulled()
    {
        var surface = new FakeClipboardSurface();
        var item = new ClipboardItem(BulkFormat.Utf8Text, Encoding.UTF8.GetBytes("одно и то же"));
        surface.UserCopies(item);

        await using var link = await Link.OpenAsync(surface);
        // Let the feature notice what is already on its clipboard, so it owns that hash.
        await link.WaitForDelivery();

        link.Offer(item.Format, item.Bytes);
        var result = await link.WaitForResult();

        Assert.Equal(BulkOutcome.AlreadyThere, result.Outcome);
        Assert.Empty(surface.Applied);
    }

    // MARK: - Harness

    /// <summary>
    /// A live receiver on loopback and a bulk channel talking to it over a real socket, so
    /// the sealing, the routing and the feature are all the real ones.
    /// </summary>
    private sealed class Link : IAsyncDisposable
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        private readonly ReceiverService _receiver;
        private readonly AesGcm _aes;
        private readonly UdpClient _socket;
        private readonly IPEndPoint _target;
        private readonly ulong _room;
        private readonly CancellationTokenSource _cancel = new();
        private readonly List<Task> _pumps = [];
        private readonly List<BulkDelivery> _deliveries = [];
        private readonly List<BulkResult> _results = [];

        private uint _seq = 1;
        private int _rejected;
        private readonly Dictionary<PacketType, int> _seen = [];

        public BulkChannel Channel { get; }
        public FakeClipboardSurface Surface { get; }

        public List<string> Log { get; init; } = [];
        public Func<bool> DropChunks { get; set; } = () => false;
        public int ChunksDropped { get; private set; }

        public int DeliveriesSeen
        {
            get { lock (_deliveries) return _deliveries.Count; }
        }

        private Link(ReceiverService receiver, FakeClipboardSurface surface, byte[] key, IPEndPoint target)
        {
            _receiver = receiver;
            Surface = surface;
            _aes = new AesGcm(key, Wire.TagSize);
            _room = Wire.RoomId(key);
            _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _target = target;

            Channel = new BulkChannel(Send);
            Channel.Delivered += delivery => { lock (_deliveries) _deliveries.Add(delivery); };
            Channel.Finished += result => { lock (_results) _results.Add(result); };
        }

        public static async Task<Link> OpenAsync(FakeClipboardSurface surface)
        {
            var config = Config();
            Assert.True(config.TryGetKey(out var key, out _));

            var receiver = new ReceiverService(new ClipboardFeature(_ => surface));
            var log = new List<string>();
            receiver.Log += entry => { lock (log) log.Add(entry.Message); };
            await receiver.StartAsync(config);

            var link = new Link(receiver, surface, key, ReceiverConfig.ParseEndpoint(config.Listen, 47702)) { Log = log };
            link.Begin();
            return link;
        }

        private void Begin()
        {
            _pumps.Add(Task.Run(ReceiveLoop));
            _pumps.Add(Task.Run(TickLoop));
            _pumps.Add(Task.Run(HelloLoop));
        }

        public void Offer(BulkFormat format, byte[] bytes) =>
            Channel.Offer(BulkKind.Clipboard, format, bytes, "проверка", DateTime.UtcNow, out _);

        public async Task<IReadOnlyList<ClipboardItem>> WaitForApplied(int count)
        {
            await Until(() => Surface.Applied.Count >= count, () => $"на буфер обмена так и не легло {count} объектов; {Describe()}");
            return Surface.Applied;
        }

        public async Task<BulkDelivery> WaitForDelivery()
        {
            await Until(() => DeliveriesSeen > 0, () => $"приёмник ничего не предложил; {Describe()}");
            lock (_deliveries) return _deliveries[0];
        }

        /// <summary>What the feature thinks of itself, so a timeout says why rather than that.</summary>
        private string Describe()
        {
            var state = _receiver.Snapshot.Feature<ClipboardState>("clipboard");
            if (state is null) return "фича не опубликовала состояние";

            string seen, tail;
            lock (_seen) seen = string.Join(",", _seen.Select(p => $"{p.Key}:{p.Value}"));
            lock (Log) tail = string.Join(" | ", Log.TakeLast(6));

            return $"status={state.Status} headline=«{state.Headline}» detail=«{state.Detail}» "
                + $"fault=«{state.Fault}» sent={state.Sent} received={state.Received} "
                + $"rejected={Volatile.Read(ref _rejected)} seen=[{seen}] log=[{tail}]";
        }

        public async Task<BulkResult> WaitForResult()
        {
            await Until(() => { lock (_results) return _results.Count > 0; }, () => $"передача не завершилась; {Describe()}");
            lock (_results) return _results[0];
        }

        private static async Task Until(Func<bool> condition, Func<string> complaint)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(20);
            }
            Assert.Fail(complaint());
        }

        private void Send(PacketType type, ReadOnlyMemory<byte> payload)
        {
            if (type == PacketType.BulkChunk && DropChunks())
            {
                ChunksDropped++;
                return;
            }

            byte[] datagram;
            lock (_aes)
            {
                var header = new Header(type, PacketFlags.None, _room, 0xABCD1234, _seq++);
                datagram = Wire.Seal(_aes, header, payload.Span, Direction.SenderToReceiver);
            }
            try
            {
                _socket.Send(datagram, datagram.Length, _target);
            }
            catch (SocketException)
            {
                // Racing a shutdown; the test's own timeout is the backstop.
            }
        }

        private async Task ReceiveLoop()
        {
            var plaintext = new byte[Wire.MaxPacket];
            while (!_cancel.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(_cancel.Token);
                }
                catch (Exception)
                {
                    return;
                }

                int length;
                Header header;
                lock (_aes)
                {
                    length = Wire.Open(_aes, result.Buffer, plaintext, Direction.ReceiverToSender, out header);
                }
                if (length < 0 || header.Room != _room)
                {
                    Interlocked.Increment(ref _rejected);
                    continue;
                }
                lock (_seen) _seen[header.Type] = _seen.GetValueOrDefault(header.Type) + 1;
                Channel.OnPacket(header.Type, plaintext.AsSpan(0, length), DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Exactly what the Mac does once a second. Without it the receiver has no idea where
        /// to send anything, so nothing it wants to offer could ever leave — which is a fact
        /// about the transport, not about the clipboard.
        /// </summary>
        private async Task HelloLoop()
        {
            var name = Encoding.UTF8.GetBytes("loopback");
            while (!_cancel.IsCancellationRequested)
            {
                var payload = new byte[10 + name.Length];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                    payload, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                payload[8] = 0;  // role: sender
                payload[9] = (byte)name.Length;
                name.CopyTo(payload, 10);
                Send(PacketType.Hello, payload);

                try
                {
                    await Task.Delay(200, _cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task TickLoop()
        {
            while (!_cancel.IsCancellationRequested)
            {
                Channel.Tick(DateTime.UtcNow);
                try
                {
                    await Task.Delay(20, _cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();
            _socket.Dispose();
            try
            {
                await Task.WhenAll(_pumps);
            }
            catch (Exception)
            {
                // Shutdown noise.
            }
            await _receiver.DisposeAsync();
            _aes.Dispose();
            _cancel.Dispose();
        }
    }
}
