using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HexBridge;
using HexBridge.Clipboard;
using HexBridge.Files;
using HexBridge.Microphone;

namespace HexBridge.Tests;

/// <summary>
/// File transfer as the receiver actually hosts it: the files feature and the clipboard on
/// one shared channel, routed by kind, carrying a real object over a real UDP socket.
///
/// <para>
/// The two halves that only this level can check are here rather than in the unit tests
/// below them: that a file written on one side turns up whole on the other, and that the two
/// features sharing the channel never see each other's traffic.
/// </para>
/// </summary>
public class FilesFeatureTests
{
    /// <summary>
    /// On in a fresh config, unlike the clipboard, and the same on the Mac — the two
    /// defaults were decided separately and came out opposite, which would have meant a
    /// file dropped on one machine going nowhere while the other screen said nothing.
    /// Claiming no packet types is what lets it share the channel with the clipboard at
    /// all; the host claims them.
    /// </summary>
    [Fact]
    public void TheFilesFeatureIsOptionalOnInAFreshConfigAndClaimsNoPacketTypes()
    {
        var feature = new FilesFeature(new BulkHost());

        Assert.True(feature.IsOptional);
        Assert.Empty(feature.HandledTypes);
        Assert.True(feature.IsEnabled(new ReceiverConfig()));
        Assert.False(feature.IsEnabled(new ReceiverConfig { Files = false }));
    }

    [Fact]
    public async Task AFileCrossesTheSocketAndLandsInTheDownloadsFolder()
    {
        await using var link = await Link.OpenAsync(clipboard: true, files: true);

        var contents = new byte[120_000];
        new Random(7).NextBytes(contents);
        link.Offer(BulkKind.File, BulkFormat.Opaque, contents, "отчёт.bin");

        var arrived = await link.WaitForFile("отчёт.bin");
        Assert.Equal(contents, File.ReadAllBytes(arrived));

        var state = link.Files;
        Assert.Equal(1, state.Received);
        Assert.Equal("отчёт.bin", state.Arrived[0].Name);
        Assert.Equal(contents.Length, state.Arrived[0].Bytes);
    }

    [Fact]
    public async Task TheClipboardAndTheFilesFeatureEachSeeOnlyTheirOwnDeliveries()
    {
        var surface = new FakeClipboardSurface();
        await using var link = await Link.OpenAsync(clipboard: true, files: true, surface: surface);

        var text = "буфер, а не файл";
        link.Offer(BulkKind.Clipboard, BulkFormat.Utf8Text, Encoding.UTF8.GetBytes(text), "текст");
        link.Offer(BulkKind.File, BulkFormat.Opaque, Encoding.UTF8.GetBytes("файл, а не буфер"), "notes.txt");

        var arrived = await link.WaitForFile("notes.txt");
        await link.WaitForClipboard(1);

        // One object each, and neither feature touched the other's.
        Assert.Equal("файл, а не буфер", File.ReadAllText(arrived));
        Assert.Equal(text, Encoding.UTF8.GetString(Assert.Single(surface.Applied).Bytes));
        Assert.Single(Directory.GetFiles(link.Downloads));
        Assert.Equal(1, link.Files.Received);
    }

    [Fact]
    public async Task AFileOfferedWhileFileTransferIsOffIsTurnedDownBeforeAnyOfItIsPulled()
    {
        await using var link = await Link.OpenAsync(clipboard: true, files: false);

        link.Offer(BulkKind.File, BulkFormat.Opaque, new byte[40_000], "notes.txt");
        var result = await link.WaitForResult();

        // «Не надо» is the only refusal the contract has, and it is the right one: the
        // bytes stay here instead of crossing the network to be thrown away there.
        Assert.Equal(BulkOutcome.AlreadyThere, result.Outcome);
        Assert.Equal(0, link.ChunksSent);
        Assert.Empty(Directory.GetFiles(link.Downloads));
    }

    [Fact]
    public async Task TheSameFileIsAcceptedOnceFileTransferIsOn()
    {
        await using var link = await Link.OpenAsync(clipboard: true, files: true);

        link.Offer(BulkKind.File, BulkFormat.Opaque, new byte[40_000], "notes.txt");
        var result = await link.WaitForResult();

        Assert.Equal(BulkOutcome.Delivered, result.Outcome);
        Assert.True(link.ChunksSent > 0);
    }

    [Fact]
    public async Task AFileSentFromThisMachineArrivesWithItsOwnNameAsTheDescription()
    {
        await using var link = await Link.OpenAsync(clipboard: false, files: true);

        var contents = new byte[90_000];
        new Random(11).NextBytes(contents);
        var path = Path.Combine(link.Downloads, "снимок.png");
        File.WriteAllBytes(path, contents);

        Assert.Null(link.Feature.Send(path));

        var delivery = await link.WaitForDelivery();
        Assert.Equal(BulkKind.File, delivery.Kind);
        // Not the text or image format, however the bytes look: a file is a file.
        Assert.Equal(BulkFormat.Opaque, delivery.Format);
        Assert.Equal("снимок.png", delivery.Description);
        Assert.Equal(contents, delivery.Bytes);
    }

    [Fact]
    public void AFileOverTheCeilingIsRefusedWithTheLimitInTheMessage()
    {
        using var folder = new TempDownloads();
        var feature = new FilesFeature(new BulkHost(), () => folder.Path);

        var path = Path.Combine(folder.Path, "huge.bin");
        using (var file = new FileStream(path, FileMode.CreateNew))
        {
            // Length without content: the point is that the refusal comes off the
            // directory entry, before anything is read into memory to be refused.
            file.SetLength(Bulk.MaxObjectSize + 1L);
        }

        var error = feature.Send(path);

        Assert.NotNull(error);
        Assert.Contains("64", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNoLongerThereIsSaidSoInWords()
    {
        using var folder = new TempDownloads();
        var feature = new FilesFeature(new BulkHost(), () => folder.Path);

        var error = feature.Send(Path.Combine(folder.Path, "gone.txt"));

        Assert.NotNull(error);
        Assert.Contains("gone.txt", error, StringComparison.Ordinal);
    }

    // MARK: - Harness

    /// <summary>A Downloads folder of this test's own, emptied when it ends.</summary>
    private sealed class TempDownloads : IDisposable
    {
        public TempDownloads()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "hexbridge-files", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A temporary directory that outlives the run is not a failed test.
            }
        }
    }

    /// <summary>
    /// A live receiver on loopback and a bulk channel talking to it over a real socket, so
    /// the sealing, the routing, the shared channel and both features are the real ones.
    /// </summary>
    private sealed class Link : IAsyncDisposable
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        private readonly ReceiverService _receiver;
        private readonly TempDownloads _downloads;
        private readonly AesGcm _aes;
        private readonly UdpClient _socket;
        private readonly IPEndPoint _target;
        private readonly ulong _room;
        private readonly CancellationTokenSource _cancel = new();
        private readonly List<Task> _pumps = [];
        private readonly List<BulkDelivery> _deliveries = [];
        private readonly List<BulkResult> _results = [];
        private readonly List<string> _log = [];

        private uint _seq = 1;
        private int _chunksSent;

        private Link(ReceiverService receiver, FilesFeature feature, TempDownloads downloads, byte[] key, IPEndPoint target)
        {
            _receiver = receiver;
            _downloads = downloads;
            Feature = feature;
            _aes = new AesGcm(key, Wire.TagSize);
            _room = Wire.RoomId(key);
            _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _target = target;

            Channel = new BulkChannel(Send);
            Channel.Delivered += delivery => { lock (_deliveries) _deliveries.Add(delivery); };
            Channel.Finished += result => { lock (_results) _results.Add(result); };
        }

        public BulkChannel Channel { get; }
        public FilesFeature Feature { get; }
        public string Downloads => _downloads.Path;
        public int ChunksSent => Volatile.Read(ref _chunksSent);

        public FilesState Files =>
            _receiver.Snapshot.Feature<FilesState>("files") ?? throw new InvalidOperationException(Describe());

        public static async Task<Link> OpenAsync(bool clipboard, bool files, FakeClipboardSurface? surface = null)
        {
            var downloads = new TempDownloads();
            var config = new ReceiverConfig
            {
                Listen = $"127.0.0.1:{Ports.Free()}",
                Psk = ReceiverConfig.GenerateKey(),
                Output = "null",
                Gamepad = false,
                Clipboard = clipboard,
                Files = files,
            };
            Assert.True(config.TryGetKey(out var key, out _));

            var bulk = new BulkHost();
            var feature = new FilesFeature(bulk, () => downloads.Path);
            var receiver = new ReceiverService(
                bulk,
                new ClipboardFeature(bulk, _ => surface ?? new FakeClipboardSurface()),
                feature);

            var link = new Link(
                receiver, feature, downloads, key,
                ReceiverConfig.ParseEndpoint(config.Listen, 47702));
            receiver.Log += entry => { lock (link._log) link._log.Add(entry.Message); };

            await receiver.StartAsync(config);
            link.Begin();
            return link;
        }

        private void Begin()
        {
            _pumps.Add(Task.Run(ReceiveLoop));
            _pumps.Add(Task.Run(TickLoop));
            _pumps.Add(Task.Run(HelloLoop));
        }

        public void Offer(BulkKind kind, BulkFormat format, byte[] bytes, string description) =>
            Channel.Offer(kind, format, bytes, description, DateTime.UtcNow, out _);

        /// <summary>Waits for a named file to be there and to have stopped growing.</summary>
        public async Task<string> WaitForFile(string name)
        {
            var path = Path.Combine(Downloads, name);
            await Until(() => File.Exists(path) && Files.Received > 0, () => $"файл «{name}» так и не появился; {Describe()}");
            return path;
        }

        public async Task WaitForClipboard(int count)
        {
            var state = _receiver.Snapshot.Feature<ClipboardState>("clipboard");
            Assert.NotNull(state);
            await Until(
                () => (_receiver.Snapshot.Feature<ClipboardState>("clipboard")?.Received ?? 0) >= count,
                () => $"на буфер обмена так и не легло {count} объектов; {Describe()}");
        }

        public async Task<BulkDelivery> WaitForDelivery()
        {
            await Until(() => { lock (_deliveries) return _deliveries.Count > 0; }, () => $"ничего не приехало; {Describe()}");
            lock (_deliveries) return _deliveries[0];
        }

        public async Task<BulkResult> WaitForResult()
        {
            await Until(() => { lock (_results) return _results.Count > 0; }, () => $"передача не завершилась; {Describe()}");
            lock (_results) return _results[0];
        }

        /// <summary>What the feature thinks of itself, so a timeout says why rather than that.</summary>
        private string Describe()
        {
            var state = _receiver.Snapshot.Feature<FilesState>("files");
            string tail;
            lock (_log) tail = string.Join(" | ", _log.TakeLast(6));

            return state is null
                ? $"фича не опубликовала состояние; log=[{tail}]"
                : $"status={state.Status} headline=«{state.Headline}» detail=«{state.Detail}» "
                    + $"fault=«{state.Fault}» sent={state.Sent} received={state.Received} "
                    + $"chunks={ChunksSent} log=[{tail}]";
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
            if (type == PacketType.BulkChunk) Interlocked.Increment(ref _chunksSent);

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
                if (length < 0 || header.Room != _room) continue;
                Channel.OnPacket(header.Type, plaintext.AsSpan(0, length), DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Exactly what the Mac does once a second. Without it the receiver has no idea
        /// where to send anything, so nothing it wants to offer could ever leave.
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
            _downloads.Dispose();
        }
    }
}
