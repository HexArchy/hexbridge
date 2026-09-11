using System.Numerics;
using System.Security.Cryptography;

using Microsoft.Win32.SafeHandles;

namespace HexBridge;

/// <summary>
/// Which chunks of an object are here, one bit each.
///
/// <para>
/// The obvious version of this is a dictionary of arrays, and it works right up until the
/// object is a file: four million chunks is four million entries and four gigabytes of them.
/// A bit each is 512 KB for the largest object the format can carry, and it is the whole
/// reason a file can cross at all.
/// </para>
///
/// <para>
/// Every question asked of it is answered in whole words rather than bit by bit, because
/// the ack asks them five times a second and a four-million-step loop five times a second
/// is a core spent counting. The bits past the end of the last word are set from the start
/// and never counted: a hole that does not exist would otherwise be named in every ack.
/// </para>
/// </summary>
internal sealed class ChunkBitmap
{
    private const int Bits = 64;

    private readonly ulong[] _words;
    private uint _have;

    /// <summary>
    /// Nothing below this is missing any more. It only ever moves forward, which is what
    /// keeps «which is the first hole» an amortised constant rather than a scan.
    /// </summary>
    private uint _cursor;

    public ChunkBitmap(uint count)
    {
        Count = count;
        _words = new ulong[(count + (Bits - 1)) / Bits];
        Pad();
    }

    public uint Count { get; }

    /// <summary>How many chunks have landed. Never counts the padding.</summary>
    public uint Have => _have;

    public bool IsFull => _have == Count;

    public bool Contains(uint index) =>
        index < Count && (_words[index / Bits] & (1UL << (int)(index % Bits))) != 0;

    /// <summary>True when this chunk was not here before.</summary>
    public bool Add(uint index)
    {
        if (index >= Count) return false;

        var word = index / Bits;
        var bit = 1UL << (int)(index % Bits);
        if ((_words[word] & bit) != 0) return false;

        _words[word] |= bit;
        _have++;
        return true;
    }

    /// <summary>Back to nothing, for an object whose hash did not match.</summary>
    public void Clear()
    {
        Array.Clear(_words);
        _have = 0;
        _cursor = 0;
        Pad();
    }

    /// <summary>The first chunk still missing, or <see cref="Count"/> when there is none.</summary>
    public uint FirstMissing() => _cursor = NextClear(_cursor);

    /// <summary>
    /// The first chunk at or after <paramref name="from"/> that is not here, or
    /// <see cref="Count"/> when there is none. Does not move the cursor: this one is asked
    /// from the middle of the object as well as from the front.
    /// </summary>
    public uint NextClear(uint from)
    {
        for (var word = from / Bits; word < _words.Length; word++)
        {
            var missing = ~_words[word];
            // The scan can start in the middle of a word, and the bits behind it are
            // answered for already.
            if (word == from / Bits) missing &= ulong.MaxValue << (int)(from % Bits);
            if (missing == 0) continue;

            return (word * (uint)Bits) + (uint)BitOperations.TrailingZeroCount(missing);
        }

        return Count;
    }

    /// <summary>
    /// Missing chunk numbers after <paramref name="after"/>, up to as many as
    /// <paramref name="into"/> holds. Returns how many were written.
    /// </summary>
    public int ListMissing(uint after, Span<uint> into)
    {
        if (into.Length == 0) return 0;

        var found = 0;
        var from = after + 1;
        for (var word = from / Bits; word < _words.Length; word++)
        {
            var missing = ~_words[word];
            if (word == from / Bits) missing &= ulong.MaxValue << (int)(from % Bits);

            while (missing != 0)
            {
                into[found++] = (word * (uint)Bits) + (uint)BitOperations.TrailingZeroCount(missing);
                if (found == into.Length) return found;
                missing &= missing - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// Marks the bits past the last chunk as present. They belong to no chunk, and a hole
    /// that cannot be filled is one the ack would name for ever.
    /// </summary>
    private void Pad()
    {
        var spare = (uint)(_words.Length * Bits) - Count;
        if (spare == 0 || _words.Length == 0) return;
        _words[^1] |= ulong.MaxValue << (int)(Bits - spare);
    }
}

/// <summary>
/// Where the bytes of an object being sent are read from.
///
/// <para>
/// The contract has the side sending an object hold on to it until the other end confirms
/// it — there is nowhere else to retransmit from. For the clipboard that is the bytes in
/// memory; for a file it is the file itself, left open and read a chunk at a time,
/// including when a chunk has to be sent again. Sixty megabytes could be held either way;
/// four gigabytes can only be held the second.
/// </para>
/// </summary>
public abstract class BulkSource : IDisposable
{
    /// <summary>The whole object, in bytes.</summary>
    public abstract long Length { get; }

    /// <summary>SHA-256 of the whole object, worked out once when the source is opened.</summary>
    public abstract byte[] Hash { get; }

    /// <summary>
    /// Copies chunk <paramref name="index"/> into <paramref name="into"/> and answers how
    /// many bytes that was. Fewer than the chunk's length means the object changed
    /// underneath us, which the other end will see as a chunk of the wrong length.
    /// </summary>
    public abstract int Read(uint index, Span<byte> into);

    public abstract void Dispose();

    /// <summary>An object that is already in memory, which is the clipboard's whole story.</summary>
    public static BulkSource FromMemory(byte[] bytes) => new MemorySource(bytes);

    /// <summary>
    /// A file on this disk, opened for the life of the transfer and hashed on the way in.
    /// Reading it once to hash it is unavoidable — the hash is in the offer, which goes out
    /// before the first chunk — but it is read in gulps and never held.
    /// </summary>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    public static BulkSource FromFile(string path) => new FileSource(path);

    private sealed class MemorySource(byte[] bytes) : BulkSource
    {
        public override long Length => bytes.Length;

        public override byte[] Hash { get; } = SHA256.HashData(bytes);

        public override int Read(uint index, Span<byte> into)
        {
            var length = Bulk.ChunkLength(bytes.Length, index);
            bytes.AsSpan((int)index * Bulk.ChunkSize, length).CopyTo(into);
            return length;
        }

        public override void Dispose()
        {
        }
    }

    private sealed class FileSource : BulkSource
    {
        /// <summary>Big enough that hashing a gigabyte is a thousand reads rather than a million.</summary>
        private const int HashBuffer = 1 << 20;

        private readonly SafeFileHandle _handle;

        public FileSource(string path)
        {
            // Shared for reading, not for writing: a file that changes while it is being
            // sent would not match the hash that went out in the offer, and the other end
            // would spend the transfer asking for chunks that can never be right.
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(_handle);
            Hash = ComputeHash();
        }

        public override long Length { get; }

        public override byte[] Hash { get; }

        public override int Read(uint index, Span<byte> into)
        {
            var length = Bulk.ChunkLength(Length, index);
            if (length == 0) return 0;

            var wanted = into[..length];
            var offset = (long)index * Bulk.ChunkSize;
            var got = 0;
            while (got < wanted.Length)
            {
                var read = RandomAccess.Read(_handle, wanted[got..], offset + got);
                if (read <= 0) break;
                got += read;
            }
            return got;
        }

        public override void Dispose() => _handle.Dispose();

        private byte[] ComputeHash()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[HashBuffer];
            long at = 0;
            while (at < Length)
            {
                var read = RandomAccess.Read(_handle, buffer, at);
                if (read <= 0) break;
                hash.AppendData(buffer, 0, read);
                at += read;
            }
            return hash.GetHashAndReset();
        }
    }
}

/// <summary>
/// Where an object arriving is put together: which chunks have landed, and where their
/// bytes went.
///
/// <para>
/// Two of them, for the two ceilings. The clipboard's is an array, because the feature
/// waiting for it needs the bytes in hand. A file's is a temporary file written at each
/// chunk's own offset, because four gigabytes is not an array — and because a transfer that
/// is abandoned then costs a file to delete rather than a heap to collect.
/// </para>
/// </summary>
internal abstract class BulkAssembly : IDisposable
{
    protected BulkAssembly(uint size, uint chunkCount)
    {
        Size = size;
        Have = new ChunkBitmap(chunkCount);
    }

    public uint Size { get; }

    public ChunkBitmap Have { get; }

    public bool IsComplete => Have.IsFull;

    /// <summary>The object itself, for the kinds that are held in memory. Null otherwise.</summary>
    public virtual byte[]? Bytes => null;

    /// <summary>The file the object was streamed into, for the kinds that are. Null otherwise.</summary>
    public virtual string? TempPath => null;

    /// <summary>
    /// True when the chunk was taken. A chunk of the wrong length cannot be part of this
    /// object whatever else it is — taking it would corrupt the object in a way only the
    /// hash would catch — and one that is already here is simply the wire repeating itself.
    /// </summary>
    public bool Store(uint index, ReadOnlySpan<byte> data)
    {
        if (index >= Have.Count) return false;
        if (data.Length != Bulk.ChunkLength(Size, index)) return false;
        if (Have.Contains(index)) return false;

        Put(index, data);
        Have.Add(index);
        Hashed(index, data);
        return true;
    }

    /// <summary>Whether what has been assembled is what was offered.</summary>
    public abstract bool Verify(ReadOnlySpan<byte> expected);

    /// <summary>Everything back to nothing: the hash did not match, so it is all asked for again.</summary>
    public virtual void Forget() => Have.Clear();

    /// <summary>
    /// Hands the object over to whoever asked for it. After this the assembly no longer owns
    /// what it holds — most of all, it no longer deletes the temporary file.
    /// </summary>
    public virtual void Release()
    {
    }

    public abstract void Dispose();

    protected abstract void Put(uint index, ReadOnlySpan<byte> data);

    /// <summary>
    /// The chunk has landed. A store that hashes as it goes does it here rather than at the
    /// end, where the end of a four-gigabyte object would be seconds of a thread that is
    /// meant to be answering packets.
    /// </summary>
    protected virtual void Hashed(uint index, ReadOnlySpan<byte> data)
    {
    }

    public static BulkAssembly InMemory(uint size, uint chunkCount) => new MemoryAssembly(size, chunkCount);

    /// <summary>
    /// A temporary file of the object's full length, created up front so that a disk with no
    /// room for it says so now rather than at ninety-nine per cent.
    /// </summary>
    /// <exception cref="IOException">The file cannot be created, or there is no room for it.</exception>
    public static BulkAssembly InFile(string path, uint size, uint chunkCount) =>
        new FileAssembly(path, size, chunkCount);

    private sealed class MemoryAssembly(uint size, uint chunkCount) : BulkAssembly(size, chunkCount)
    {
        private readonly byte[] _buffer = new byte[size];

        public override byte[]? Bytes => _buffer;

        public override bool Verify(ReadOnlySpan<byte> expected) =>
            SHA256.HashData(_buffer).AsSpan().SequenceEqual(expected);

        public override void Dispose()
        {
        }

        protected override void Put(uint index, ReadOnlySpan<byte> data) =>
            data.CopyTo(_buffer.AsSpan((int)index * Bulk.ChunkSize));
    }

    /// <summary>
    /// One object streamed into a file of its own.
    ///
    /// <para>
    /// The hash is taken as the object fills rather than at the end. Chunks arrive in order
    /// nearly all of the time, so nearly all of the time the bytes being hashed are the ones
    /// in hand and nothing is read back at all; a chunk that fills an older hole makes the
    /// run behind it readable, and that run is read back from the page cache in gulps. The
    /// alternative is reading four gigabytes and hashing them in one go the moment the last
    /// hole fills, which is several seconds of a thread that has acks to send.
    /// </para>
    /// </summary>
    private sealed class FileAssembly : BulkAssembly
    {
        /// <summary>Read back in 64 KB gulps rather than chunk by chunk.</summary>
        private const int CatchUpBuffer = 64 * 1024;

        /// <summary>
        /// How much reading back one arriving chunk may pay for. A chunk that fills the
        /// first hole of a mostly complete object makes every chunk behind it hashable at
        /// once, and doing all of that inside one packet's handling is the stall this
        /// whole arrangement exists to avoid. The rest waits for the next chunk, and for
        /// the last one there is nothing left to wait for — which is why the transfer is
        /// finished off the socket's thread.
        /// </summary>
        private const int CatchUpChunks = 1024;

        private readonly string _path;
        private readonly SafeFileHandle _handle;

        private IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private byte[]? _readBuffer;

        /// <summary>How many chunks from the start have gone into the hash.</summary>
        private uint _hashed;

        private bool _released;
        private bool _closed;

        public FileAssembly(string path, uint size, uint chunkCount) : base(size, chunkCount)
        {
            _path = path;
            // Create rather than CreateNew: the name carries the transfer id, so anything
            // already under it is the remains of a transfer that is gone.
            _handle = File.OpenHandle(
                path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, size);
            try
            {
                RandomAccess.SetLength(_handle, size);
            }
            catch (Exception)
            {
                _handle.Dispose();
                Delete();
                throw;
            }
        }

        public override string? TempPath => _path;

        public override bool Verify(ReadOnlySpan<byte> expected)
        {
            // Whatever is left of the hash, which for an object that arrived in order is
            // nothing at all.
            CatchUp(int.MaxValue);
            return _hash.GetCurrentHash().AsSpan().SequenceEqual(expected);
        }

        public override void Forget()
        {
            base.Forget();
            _hash.Dispose();
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _hashed = 0;
        }

        public override void Release()
        {
            // The file has to be closed before it can be moved into place: it was opened
            // with no sharing, which is what stops anything else opening a half-written
            // object in the first place.
            Close();
            _released = true;
        }

        public override void Dispose()
        {
            Close();
            _hash.Dispose();
            if (!_released) Delete();
        }

        protected override void Put(uint index, ReadOnlySpan<byte> data) =>
            RandomAccess.Write(_handle, data, (long)index * Bulk.ChunkSize);

        protected override void Hashed(uint index, ReadOnlySpan<byte> data)
        {
            if (index != _hashed) return;

            // The common case, and the reason there is no read here: this chunk is the next
            // one the hash wants, and it is already in hand.
            _hash.AppendData(data);
            _hashed++;
            CatchUp(CatchUpChunks);
        }

        private void CatchUp(int budget)
        {
            while (budget > 0 && _hashed < Have.Count && Have.Contains(_hashed))
            {
                var buffer = _readBuffer ??= new byte[CatchUpBuffer];

                uint run = 0;
                var most = (uint)Math.Min(budget, buffer.Length / Bulk.ChunkSize);
                while (run < most && Have.Contains(_hashed + run)) run++;

                var offset = (long)_hashed * Bulk.ChunkSize;
                var length = (int)Math.Min((long)run * Bulk.ChunkSize, Size - offset);
                var got = 0;
                while (got < length)
                {
                    var read = RandomAccess.Read(_handle, buffer.AsSpan(got, length - got), offset + got);
                    if (read <= 0) break;
                    got += read;
                }

                _hash.AppendData(buffer, 0, got);
                _hashed += run;
                budget -= (int)run;
            }
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            _handle.Dispose();
        }

        private void Delete()
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception)
            {
                // A temporary file that outlives the transfer is swept at the next start.
                // Failing here would cost the transfer that is still running.
            }
        }
    }
}
