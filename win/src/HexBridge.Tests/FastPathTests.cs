using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using HexBridge.Files;
using HexBridge.Localization;

namespace HexBridge.Tests;

/// <summary>
/// The fast path for files: a TCP stream on data port + 2, which exists because the chunked
/// UDP channel moved a file between a Mac and a Windows VM at 2.8 MB/s and a plain stream
/// between the same two machines moved one at 370 MB/s.
///
/// <para>
/// Four of these are about what happens when it goes wrong, and that is deliberate. The
/// happy case is one connection and one file; every other case ends with somebody's disk
/// holding either a file they did not ask for or half of one they did, and both of those are
/// worse than the transfer simply not happening.
/// </para>
/// </summary>
public class FastPathTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // MARK: - The path itself

    [Fact]
    public async Task AFileCrossesTheStreamWholeAndIsStagedWhereTheSweepWouldFindIt()
    {
        using var downloads = new TempDownloads();
        using var mine = new TempDownloads();
        var key = RandomNumberGenerator.GetBytes(32);

        var landed = new TaskCompletionSource<BulkDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new FastPathListener(IPAddress.Loopback, Ports.Free(tcp: true, udp: false), key, () => downloads.Path);
        listener.Landed += delivery => landed.TrySetResult(delivery);
        listener.Start();

        // Several full records and a short one at the end, which is the only place the
        // framing can be wrong without every case being wrong.
        var contents = new byte[(3 * FastPath.MaxRecordPlaintext) + 1234];
        new Random(19).NextBytes(contents);
        var source = Path.Combine(mine.Path, "отчёт.bin");
        File.WriteAllBytes(source, contents);

        var outcome = FastPathSend.Send(
            new IPEndPoint(IPAddress.Loopback, listener.Port), key, source, "отчёт.bin", null, CancellationToken.None);

        Assert.Equal(FastPathOutcome.Sent, outcome);

        var delivery = await landed.Task.WaitAsync(Patience);
        Assert.Equal("отчёт.bin", delivery.Description);
        Assert.Equal(BulkKind.File, delivery.Kind);
        // A file is a file however its bytes look, on this path as on the other one.
        Assert.Equal(BulkFormat.Opaque, delivery.Format);
        Assert.Equal((uint)contents.Length, delivery.Size);
        Assert.Equal(SHA256.HashData(contents), delivery.Hash);

        Assert.NotNull(delivery.Path);
        Assert.Equal(contents, File.ReadAllBytes(delivery.Path));
        // Under the name a machine that lost power mid-transfer would leave behind, so the
        // sweep at the next start takes it away — the same as on the other path.
        Assert.EndsWith(Bulk.PartialExtension, delivery.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// The room in the prologue is derived from the key, so a stranger with a key of their
    /// own never gets this far. This is the case the prologue cannot catch: the right room
    /// and the wrong sealing key, which is what somebody who watched the network and copied
    /// thirteen plaintext bytes would have.
    /// </summary>
    [Fact]
    public async Task AConnectionWhoseOpeningRecordDoesNotOpenIsDroppedWithNothingWritten()
    {
        using var downloads = new TempDownloads();
        var theirs = RandomNumberGenerator.GetBytes(32);

        var complaint = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new FastPathListener(IPAddress.Loopback, Ports.Free(tcp: true, udp: false), theirs, () => downloads.Path);
        listener.Landed += _ => Assert.Fail("something was handed over on a connection that never opened");
        listener.Note += (_, message) => complaint.TrySetResult(message);
        listener.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, listener.Port);
            var stream = client.GetStream();

            var prologue = new byte[FastPath.PrologueSize];
            FastPath.WritePrologue(prologue, Wire.RoomId(theirs));
            stream.Write(prologue);

            using var wrong = new AesGcm(RandomNumberGenerator.GetBytes(32), Wire.TagSize);
            var frame = new byte[FastPath.LengthSize + FastPath.MaxRecordOnWire];
            var opening = FastPath.Opening("вирус.exe", 4096, new byte[32]);
            stream.Write(frame.AsSpan(0, FastPath.Seal(wrong, FastPath.OpeningRecord, opening, frame)));
            stream.Flush();
        }

        Assert.Equal(Strings.Log_Files_Fast_Unsealed, await complaint.Task.WaitAsync(Patience));
        Assert.Empty(Directory.GetFiles(downloads.Path));
    }

    /// <summary>
    /// A stream that stops in the middle leaves nothing at all — not the file, which was
    /// never whole, and not the temporary one it was being written into.
    /// </summary>
    [Fact]
    public async Task AConnectionCutOffPartWayLeavesNoFileAndNoTemporaryOne()
    {
        using var downloads = new TempDownloads();
        var key = RandomNumberGenerator.GetBytes(32);

        var complaint = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new FastPathListener(IPAddress.Loopback, Ports.Free(tcp: true, udp: false), key, () => downloads.Path);
        listener.Landed += _ => Assert.Fail("half a file was handed over as if it were whole");
        listener.Note += (_, message) => complaint.TrySetResult(message);
        listener.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, listener.Port);
            var stream = client.GetStream();

            var prologue = new byte[FastPath.PrologueSize];
            FastPath.WritePrologue(prologue, Wire.RoomId(key));
            stream.Write(prologue);

            using var aes = new AesGcm(key, Wire.TagSize);
            var frame = new byte[FastPath.LengthSize + FastPath.MaxRecordOnWire];

            // Announced as a megabyte, and then one record and a closed socket.
            var body = new byte[1024];
            new Random(23).NextBytes(body);
            var opening = FastPath.Opening("половина.bin", 1024 * 1024, SHA256.HashData(body));
            stream.Write(frame.AsSpan(0, FastPath.Seal(aes, FastPath.OpeningRecord, opening, frame)));
            stream.Write(frame.AsSpan(0, FastPath.Seal(aes, 1, body, frame)));
            stream.Flush();
        }

        await complaint.Task.WaitAsync(Patience);
        Assert.Empty(Directory.GetFiles(downloads.Path));
    }

    // MARK: - The feature that owns it

    /// <summary>
    /// Nothing answering on data port + 2 is not a failure: the file goes the slow way, which
    /// is why the UDP channel is not going anywhere. The port is held here by a socket that
    /// is bound and never listening, which is what makes the connection refused outright
    /// rather than accepted and then ignored — and, incidentally, what keeps the feature's
    /// own listener from claiming it.
    /// </summary>
    [Fact]
    public async Task AFileFallsBackToTheSlowPathWhenNothingAnswersOnTheFastOne()
    {
        using var downloads = new TempDownloads();
        using var mine = new TempDownloads();

        var fastPort = Ports.Free(tcp: true, udp: false);
        using var bound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        bound.Bind(new IPEndPoint(IPAddress.Loopback, fastPort));

        var log = new List<string>();
        var feature = new FilesFeature(new BulkHost(), () => downloads.Path);
        feature.Start(Context(DataPortBehind(fastPort), log));

        try
        {
            var source = Path.Combine(mine.Path, "notes.txt");
            File.WriteAllText(source, "не так уж и много букв");

            // The bulk channel is not running in this test, so its own refusal is the proof
            // that the offer got as far as it: the fast path gave up and handed the file on
            // rather than swallowing it.
            Assert.Equal(Strings.Err_Bulk_NotRunning, feature.Send(source));
        }
        finally
        {
            await feature.StopAsync();
        }

        Assert.Contains(Loc.F(Strings.Log_Files_Fast_Fallback, "notes.txt"), log);
        Assert.Empty(Directory.GetFiles(downloads.Path));
    }

    [Fact]
    public async Task TheListenerGivesThePortBackWhenTheFeatureStops()
    {
        using var downloads = new TempDownloads();

        var fastPort = Ports.Free(tcp: true, udp: false);
        var log = new List<string>();
        var feature = new FilesFeature(new BulkHost(), () => downloads.Path);
        feature.Start(Context(DataPortBehind(fastPort), log));

        Assert.Contains(Loc.F(Strings.Log_Files_Fast_On, fastPort), log);
        Assert.False(CanBind(fastPort), "the feature is running and something else took its port");

        await feature.StopAsync();

        // The one that matters: a feature switched off and on again in the settings must not
        // find its own socket still holding the port it is about to ask for.
        Assert.True(CanBind(fastPort), "the port was still held after the feature stopped");
    }

    // MARK: - Harness

    /// <summary>
    /// A config whose data port puts the fast path on <paramref name="fastPort"/>, since that
    /// is the number a test can pick and the other one is derived from it.
    /// </summary>
    private static ReceiverConfig DataPortBehind(int fastPort) => new()
    {
        Listen = $"127.0.0.1:{fastPort - 2}",
        Psk = ReceiverConfig.GenerateKey(),
    };

    /// <summary>
    /// Enough of a context for a feature that touches a folder and one socket. The peer is
    /// loopback because that is the only machine a test has.
    /// </summary>
    private static FeatureContext Context(ReceiverConfig config, List<string> log) => new(
        config,
        (_, message) => { lock (log) log.Add(message); },
        (_, _, _) => { },
        () => false,
        _ => { },
        () => new IPEndPoint(IPAddress.Loopback, PairingPayload.DefaultPort));

    private static bool CanBind(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
