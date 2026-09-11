using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace HexBridge.Files;

/// <summary>How an attempt at the fast path ended.</summary>
public enum FastPathOutcome
{
    /// <summary>
    /// Nothing answered on data port + 2, or the connection died before any of the file had
    /// gone out. Nothing was written on the other machine, so the file can still go the slow
    /// way — which is the whole reason the UDP channel is not going anywhere.
    /// </summary>
    Unreachable,

    /// <summary>Every record went out and the connection was closed from this end.</summary>
    Sent,

    /// <summary>
    /// The connection was made, part of the file crossed, and then it broke. Not retried on
    /// the other path: the other machine deletes what it had, so a retry would move the whole
    /// file a second time, and at the speeds that make this path worth having that is minutes
    /// somebody did not ask for.
    /// </summary>
    Broken,
}

/// <summary>
/// The half of the fast path that dials: one connection, one file, and no acknowledgement of
/// any kind — TCP already delivers every byte, in order, once.
/// </summary>
public static class FastPathSend
{
    /// <summary>
    /// Streams one file to <paramref name="where"/> and says how that went.
    ///
    /// <para>
    /// Blocking on purpose. The caller already blocks to hash a file before offering it on
    /// the other path, and is called from a thread that can afford to wait; an asynchronous
    /// version of this would buy nothing and would make «did it get across» something the
    /// caller had to come back for.
    /// </para>
    ///
    /// <para>
    /// The hash is taken before the connection is made rather than after. It has to travel in
    /// the opening record, which is the first thing said after connecting — and a machine
    /// that has accepted a connection and then hears nothing for as long as it takes to read
    /// four gigabytes off a disk is entitled to conclude the connection is dead. The cost is
    /// that a file which ends up going the slow way is hashed twice; that is a second on a
    /// transfer that is about to take minutes.
    /// </para>
    /// </summary>
    public static FastPathOutcome Send(
        IPEndPoint where,
        byte[] key,
        string path,
        string name,
        Action<FastPathFlight?>? moving,
        CancellationToken token)
    {
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FastPath.MaxRecordPlaintext, FileOptions.SequentialScan);

        var size = file.Length;
        if (size is <= 0 or > Bulk.MaxFileSize) return FastPathOutcome.Unreachable;

        var hash = HashOf(file);
        file.Position = 0;

        using var socket = new Socket(where.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (!Connect(socket, where, token)) return FastPathOutcome.Unreachable;

        var done = 0L;
        try
        {
            using var aes = new AesGcm(key, Wire.TagSize);
            using var stream = new NetworkStream(socket, ownsSocket: false);

            var frame = new byte[FastPath.LengthSize + FastPath.MaxRecordOnWire];
            var plain = new byte[FastPath.MaxRecordPlaintext];

            var prologue = new byte[FastPath.PrologueSize];
            FastPath.WritePrologue(prologue, Wire.RoomId(key));
            stream.Write(prologue);

            var opening = FastPath.Opening(name, (uint)size, hash);
            stream.Write(frame.AsSpan(0, FastPath.Seal(aes, FastPath.OpeningRecord, opening, frame)));
            stream.Flush();

            // Before a byte of the file. A connection that was accepted is not proof that
            // anything is reading it — the macOS firewall completes the handshake itself and
            // then keeps the connection from the application it has not been told to allow,
            // which looks from here exactly like a healthy peer for as long as it takes to
            // write the whole file into it. Unreachable rather than Broken: nothing of the
            // file has gone out, so the slow path can still deliver it.
            if (!HeardReady(socket, stream, aes)) return FastPathOutcome.Unreachable;

            var record = 1UL;
            while (done < size)
            {
                token.ThrowIfCancellationRequested();

                var wanted = (int)Math.Min(FastPath.MaxRecordPlaintext, size - done);
                file.ReadExactly(plain, 0, wanted);
                stream.Write(frame.AsSpan(0, FastPath.Seal(aes, record++, plain.AsSpan(0, wanted), frame)));

                done += wanted;
                moving?.Invoke(new FastPathFlight(name, done, size, BulkDirection.Outgoing));
            }

            // The end of the file is a record of its own, empty. Closing the socket would
            // say the same thing to a healthy peer and nothing at all to one that is
            // deciding whether what it has is whole.
            stream.Write(frame.AsSpan(0, FastPath.Seal(aes, record, ReadOnlySpan<byte>.Empty, frame)));
            stream.Flush();

            // Half-close, so the other end reads to a clean end of stream. Dropping the
            // socket outright is what Windows can turn into a reset, and a reset throws away
            // whatever is still in flight — including, here, the record that says the file
            // is complete.
            socket.Shutdown(SocketShutdown.Send);
            return FastPathOutcome.Sent;
        }
        catch (Exception)
        {
            // Nothing of the file on the wire yet means nothing was written over there
            // either, so the slow path can still have it. Past that point it cannot: see
            // <see cref="FastPathOutcome.Broken"/>.
            return done > 0 ? FastPathOutcome.Broken : FastPathOutcome.Unreachable;
        }
        finally
        {
            moving?.Invoke(null);
        }
    }

    /// <summary>
    /// Waits out the one record that travels backwards.
    ///
    /// <para>
    /// Two seconds, and any shortfall means the same as a refused connection: a short read,
    /// a close, a record that will not open, a byte that is not the one. The receive timeout
    /// is put back afterwards so that a slow disk on the other side cannot be mistaken for a
    /// dead peer during the transfer itself.
    /// </para>
    /// </summary>
    private static bool HeardReady(Socket socket, NetworkStream stream, AesGcm aes)
    {
        var was = socket.ReceiveTimeout;
        socket.ReceiveTimeout = (int)FastPath.ReadyTimeout.TotalMilliseconds;
        try
        {
            var answer = new byte[FastPath.ReadyFrameSize];
            stream.ReadExactly(answer);
            return FastPath.IsReady(aes, answer);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            socket.ReceiveTimeout = was;
        }
    }

    /// <summary>
    /// Three seconds and then the file goes the slow way. Synchronous over an asynchronous
    /// connect because that is the only way to put a deadline on one, and the thread this
    /// runs on is one that can afford to wait.
    /// </summary>
    private static bool Connect(Socket socket, IPEndPoint where, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(FastPath.ConnectTimeout);
            socket.ConnectAsync(where, deadline.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            // Refused, unreachable, filtered by a firewall, or simply slower than three
            // seconds. They all mean the same thing here, and the answer to all of them is
            // the same: send it the slow way rather than not at all.
            return false;
        }
    }

    /// <summary>
    /// SHA-256 of the whole file, read in gulps so that a gigabyte is a thousand reads rather
    /// than a million.
    /// </summary>
    private static byte[] HashOf(FileStream file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[FastPath.MaxRecordPlaintext];

        int read;
        while ((read = file.Read(buffer)) > 0) hash.AppendData(buffer, 0, read);
        return hash.GetHashAndReset();
    }
}
