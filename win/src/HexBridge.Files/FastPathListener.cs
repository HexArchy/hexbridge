using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using HexBridge.Localization;

namespace HexBridge.Files;

/// <summary>
/// What the fast path has on the wire right now, for a progress bar and nothing else. Null
/// where one of these is expected means nothing is moving.
/// </summary>
public sealed record FastPathFlight(string Name, long Done, long Total, BulkDirection Direction)
{
    public double Fraction => Total == 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>
/// The half of the fast path that waits: a TCP listener on data port + 2 that takes one file
/// per connection and hands it over exactly as the UDP channel hands one over.
///
/// <para>
/// Nothing about a file that came this way may be visible once it has landed. It is streamed
/// into the same kind of temporary file, under the same name the sweep at start-up looks
/// for; it is hashed as it is written and deleted rather than renamed when the hash does not
/// match; and it reaches <see cref="FilesFeature"/> as an ordinary
/// <see cref="BulkDelivery"/>, so the sanitised name, the never-overwrite numbering and the
/// counters are one implementation rather than two that can drift apart.
/// </para>
///
/// <para>
/// A connection whose opening record does not open is dropped without a byte being written.
/// That is the whole defence on this port: the prologue is in the clear and proves nothing,
/// and everything after it is sealed under the pairing key.
/// </para>
/// </summary>
public sealed class FastPathListener : IDisposable
{
    /// <summary>
    /// How long a connection may take to produce a prologue and an opening record. A socket
    /// that connects and then says nothing costs a thread and a file handle; five seconds is
    /// what the short-code exchange allows, for the same reason.
    /// </summary>
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(5);

    /// <summary>How many ids to try before giving up on staging this file at all.</summary>
    private const int NamingAttempts = 8;

    private readonly TcpListener _listener;
    private readonly byte[] _key;
    private readonly ulong _room;
    private readonly Func<string> _folder;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<TcpClient> _live = [];

    private Task? _accepting;
    private bool _disposed;

    public FastPathListener(IPAddress bind, int port, byte[] key, Func<string> folder)
    {
        _listener = new TcpListener(bind, port);
        _key = key;
        _room = Wire.RoomId(key);
        _folder = folder;
    }

    /// <summary>A file arrived whole and verified. Raised on the connection's own thread.</summary>
    public event Action<BulkDelivery>? Landed;

    /// <summary>What is arriving, or null once nothing is. Raised as each record lands.</summary>
    public event Action<FastPathFlight?>? Moving;

    /// <summary>One line for the log, raised on whichever thread noticed.</summary>
    public event Action<LogLevel, string>? Note;

    /// <summary>
    /// The port actually bound. Only meaningful once <see cref="Start"/> has returned, which
    /// is what makes port 0 — «let the operating system choose» — usable from a test.
    /// </summary>
    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;

    /// <summary>Claims the port. Throws when it is taken, which the caller is expected to survive.</summary>
    public void Start()
    {
        _listener.Start();
        _accepting = Task.Run(() => AcceptAsync(_stop.Token));
    }

    /// <summary>
    /// Gives the port back. Connections in flight are dropped rather than waited on: a file
    /// still arriving belongs to a feature that is being switched off, and the temporary
    /// file behind it goes with the connection's own clean-up.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _stop.Cancel();
        _listener.Stop();

        TcpClient[] live;
        lock (_gate)
        {
            live = [.. _live];
            _live.Clear();
        }

        // Closing the socket is what ends a read sitting in the middle of a four-gigabyte
        // file; cancelling alone would leave it blocked until the idle timeout.
        foreach (var client in live) client.Dispose();

        // Bounded: the accept loop is already cancelled and has nothing else to wait on.
        _accepting?.Wait(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (Exception)
            {
                // Cancelled, or the listener was closed under us. Either way there is
                // nothing left to accept.
                return;
            }

            lock (_gate) _live.Add(client);

            // One task per connection rather than one at a time: two files dropped together
            // is an ordinary thing to do, and the second must not wait out the first.
            // Nothing is awaited here — a connection that hangs must not stop the next one
            // being accepted.
            _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            await ReceiveAsync(client, token);
        }
        catch (Exception ex)
        {
            // A peer that went away, a disk that filled, a feature switched off underneath
            // us. None of it is worth more than a line, and whatever was being written has
            // already been cleaned up below.
            Note?.Invoke(LogLevel.Warning, Loc.F(Strings.Log_Files_Fast_Cut, Reason(ex)));
        }
        finally
        {
            Moving?.Invoke(null);
            lock (_gate) _live.Remove(client);
            client.Dispose();
        }
    }

    private async Task ReceiveAsync(TcpClient client, CancellationToken token)
    {
        using var aes = new AesGcm(_key, Wire.TagSize);
        var stream = client.GetStream();

        using var clock = CancellationTokenSource.CreateLinkedTokenSource(token);
        clock.CancelAfter(Handshake);

        var prologue = new byte[FastPath.PrologueSize];
        await stream.ReadExactlyAsync(prologue, clock.Token);
        if (!FastPath.PrologueMatches(prologue, _room))
        {
            // Not ours: a port scanner, or somebody else's HexBridge on the same network.
            // Nothing to say back, and nothing to write.
            return;
        }

        var frame = new byte[FastPath.LengthSize + FastPath.MaxRecordOnWire];
        var plain = new byte[FastPath.MaxRecordPlaintext];

        var opening = await ReadRecordAsync(stream, aes, FastPath.OpeningRecord, frame, plain, clock.Token);
        if (opening < 0 || !FastPath.TryReadOpening(plain.AsSpan(0, opening), out var name, out var size, out var hash))
        {
            // The one check that matters on this port. Everything past the prologue is
            // sealed under the pairing key, so a record that does not open was written by
            // somebody who does not hold it — and at this point not one byte of a file has
            // been written, which is exactly the property the contract asks for.
            Note?.Invoke(LogLevel.Warning, Strings.Log_Files_Fast_Unsealed);
            return;
        }

        // The moment the opening record opens, and before the file is staged: the other
        // machine is holding the whole transfer back until it hears this, because a socket
        // that was accepted proves only that a kernel — or a firewall standing in for one —
        // took the connection. Nothing waits on the write; if it never leaves, the sender's
        // own deadline says so and the file arrives the slow way instead.
        await stream.WriteAsync(FastPath.ReadyRecord(aes), clock.Token);
        await stream.FlushAsync(clock.Token);

        var started = DateTime.UtcNow;
        var staged = Stage(size, out var temp, out var id);
        var written = 0L;
        var handed = false;

        try
        {
            using (staged)
            {
                using var running = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                // The same 30 seconds the UDP path gives an idle incoming transfer: a
                // stream that has stopped moving is a transfer that will not finish, and
                // the temporary file behind it is not worth holding open.
                clock.CancelAfter(Bulk.IncomingIdleTimeout);

                for (var record = 1UL; ; record++)
                {
                    var length = await ReadRecordAsync(stream, aes, record, frame, plain, clock.Token);
                    if (length < 0) throw new InvalidDataException(Strings.Log_Files_Fast_Why_Unsealed);
                    if (length == 0) break;

                    written += length;
                    // A file that grew past what was offered is not the file that was
                    // offered, and there is no reason to write the rest of it to find out.
                    if (written > size) throw new InvalidDataException(Strings.Log_Files_Fast_Why_Long);

                    await staged.WriteAsync(plain.AsMemory(0, length), clock.Token);
                    running.AppendData(plain.AsSpan(0, length));

                    Moving?.Invoke(new FastPathFlight(name, written, size, BulkDirection.Incoming));
                    // Pushed forward by every record that lands, so the timeout measures
                    // silence rather than how long a large file takes.
                    clock.CancelAfter(Bulk.IncomingIdleTimeout);
                }

                if (written != size || !running.GetCurrentHash().AsSpan().SequenceEqual(hash))
                {
                    // Deleted rather than renamed, the same as on the other path: a file
                    // that does not hash to what was offered is not worth having under a
                    // name somebody will open.
                    Note?.Invoke(LogLevel.Warning, Loc.F(Strings.Log_Files_Fast_Damaged, name));
                    return;
                }
            }

            Note?.Invoke(LogLevel.Info, Loc.F(
                Strings.Log_Files_Fast_Received,
                name,
                Loc.Size(written),
                Loc.Throughput(written, DateTime.UtcNow - started)));

            // Whole and verified, handed over exactly as the UDP channel hands one over —
            // which is what makes a file that came this way indistinguishable from one that
            // came the slow way the moment it has landed.
            handed = true;
            Landed?.Invoke(new BulkDelivery(
                id, BulkKind.File, BulkFormat.Opaque, size, hash, name, null, temp));
        }
        finally
        {
            if (!handed) Discard(temp);
        }
    }

    /// <summary>
    /// Reads one framed record and opens it, answering the plaintext length or -1 when it
    /// does not open.
    /// </summary>
    private static async Task<int> ReadRecordAsync(
        NetworkStream stream, AesGcm aes, ulong record, byte[] frame, byte[] plain, CancellationToken token)
    {
        await stream.ReadExactlyAsync(frame.AsMemory(0, FastPath.LengthSize), token);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(frame);

        // Believed only as far as the buffer the contract's own ceiling sizes. A length
        // somebody made up would otherwise be an allocation somebody else chose.
        if (length < Wire.TagSize || length > FastPath.MaxRecordOnWire) return -1;

        await stream.ReadExactlyAsync(frame.AsMemory(FastPath.LengthSize, (int)length), token);
        return FastPath.Open(aes, record, frame.AsSpan(FastPath.LengthSize, (int)length), plain);
    }

    /// <summary>
    /// Opens the temporary file an arriving file is streamed into, at its full length so that
    /// a disk with no room for it says so now rather than at ninety-nine per cent.
    ///
    /// <para>
    /// The name carries a transfer id of its own and the extension the start-up sweep looks
    /// for, so a machine that loses power in the middle of one of these leaves no more behind
    /// than one on the other path does. <see cref="FileMode.CreateNew"/> rather than
    /// <see cref="FileMode.Create"/>: the id is picked at random here, and the one file it
    /// must never land on top of is a transfer that is still running.
    /// </para>
    /// </summary>
    private FileStream Stage(uint size, out string path, out uint id)
    {
        var folder = _folder();
        Directory.CreateDirectory(folder);

        for (var attempt = 1; ; attempt++)
        {
            var chosen = (uint)Random.Shared.Next(1, int.MaxValue);
            var candidate = Path.Combine(folder, Bulk.PartialName(chosen));
            try
            {
                var file = new FileStream(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, FastPath.MaxRecordPlaintext);
                try
                {
                    file.SetLength(size);
                }
                catch (Exception)
                {
                    file.Dispose();
                    Discard(candidate);
                    throw;
                }

                path = candidate;
                id = chosen;
                return file;
            }
            catch (IOException) when (attempt < NamingAttempts && File.Exists(candidate))
            {
                // That id is another transfer's. Anything else — no room, no permission — is
                // a real failure and belongs to the caller.
            }
        }
    }

    /// <summary>
    /// Throws away a file that never got a name. Never throws itself: what survives is swept
    /// at the next start, and failing here would cost the connection's thread.
    /// </summary>
    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// What to put in the log for a connection that broke. Not the exception's type and not a
    /// code — the glossary is explicit that neither means anything to the person reading it.
    /// </summary>
    private static string Reason(Exception ex) => ex switch
    {
        OperationCanceledException => Strings.Log_Files_Fast_Why_Silent,
        EndOfStreamException => Strings.Log_Files_Fast_Why_Short,
        InvalidDataException reason => reason.Message,
        _ => ex.Message,
    };
}
