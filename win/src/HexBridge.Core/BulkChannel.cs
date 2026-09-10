using System.Security.Cryptography;

using HexBridge.Localization;

namespace HexBridge;

public enum BulkDirection
{
    Outgoing,
    Incoming,
}

/// <summary>How an outgoing transfer ended.</summary>
public enum BulkOutcome
{
    /// <summary>The receiver assembled the object and the hash matched.</summary>
    Delivered,

    /// <summary>The receiver already had this exact object and asked us not to send it.</summary>
    AlreadyThere,

    /// <summary>Ten offers, no answer.</summary>
    NoAnswer,

    /// <summary>The acks stopped while chunks were still outstanding.</summary>
    Stalled,
}

/// <summary>An object that arrived whole.</summary>
public sealed record BulkDelivery(
    uint TransferId,
    BulkKind Kind,
    BulkFormat Format,
    byte[] Bytes,
    byte[] Hash,
    string Description);

/// <summary>The end of an outgoing transfer, whatever the reason.</summary>
public sealed record BulkResult(
    uint TransferId,
    BulkKind Kind,
    BulkFormat Format,
    uint Size,
    byte[] Hash,
    string Description,
    BulkOutcome Outcome);

/// <summary>One line of «что сейчас едет», for a progress bar and nothing else.</summary>
public sealed record BulkProgress(
    uint TransferId,
    BulkDirection Direction,
    BulkKind Kind,
    BulkFormat Format,
    uint Size,
    uint ChunksDone,
    uint ChunkCount,
    string Description)
{
    public double Fraction => ChunkCount == 0 ? 0 : (double)ChunksDone / ChunkCount;
}

/// <summary>
/// The reliable-delivery layer from docs/PROTOCOL.md, both roles in one object: it offers
/// objects, it accepts them, and it retransmits what the other side says it is missing.
///
/// It knows nothing about clipboards. A feature hands it bytes, a kind and a format and gets
/// told when they landed; the day file transfer arrives it adds a kind and changes nothing
/// here. Deliberately primitive, as the contract says: a fixed send budget, repeat on the
/// receiver's ack, no congestion control and no reordering.
///
/// Time is a parameter rather than a field read from the clock, so every timeout in here is
/// reachable from a test without waiting for it.
/// </summary>
public sealed class BulkChannel
{
    private readonly object _gate = new();
    private readonly Action<PacketType, ReadOnlyMemory<byte>> _send;
    private readonly Dictionary<uint, Outgoing> _outgoing = [];
    private readonly Dictionary<uint, Incoming> _incoming = [];

    /// <summary>Ids of transfers already assembled, kept so a lost BULK_DONE can be re-sent.</summary>
    private readonly Dictionary<uint, DateTime> _finished = [];

    private uint _nextId = (uint)Random.Shared.Next(1, int.MaxValue);
    private double _credit;
    private DateTime? _lastTick;

    /// <summary>
    /// «Этот объект у меня уже есть». The contract makes this the answer to a duplicate
    /// offer *and* the thing that stops two machines syncing each other in a circle.
    /// </summary>
    public Func<byte[], bool>? Owns { get; set; }

    /// <summary>An object arrived whole and verified. Raised outside the channel's lock.</summary>
    public event Action<BulkDelivery>? Delivered;

    /// <summary>An outgoing transfer ended. Raised outside the channel's lock.</summary>
    public event Action<BulkResult>? Finished;

    /// <summary>Diagnostics, one line per interesting event. Raised outside the lock.</summary>
    public event Action<string>? Note;

    /// <summary>
    /// Chunks per second the sender is allowed to put on the wire. The relay caps a source
    /// at 2000 packets/s and voice plus a gamepad already use 300 of them, so the default
    /// leaves headroom rather than filling the pipe.
    /// </summary>
    public double ChunksPerSecond { get; set; } = 1500;

    /// <summary>How many times an object may fail its hash before the receiver gives up on it.</summary>
    public int MaxHashFailures { get; set; } = 3;

    public BulkChannel(Action<PacketType, ReadOnlyMemory<byte>> send) => _send = send;

    // MARK: - Sending

    /// <summary>
    /// Starts offering an object. Returns its transfer id, or null with a reason the user
    /// can read. The bytes are held until the receiver confirms them, as the contract
    /// requires — there is nowhere else to retransmit from.
    /// </summary>
    public uint? Offer(BulkKind kind, BulkFormat format, byte[] bytes, string description, DateTime now, out string? error)
    {
        if (bytes.Length == 0)
        {
            error = Strings.Err_Bulk_Empty;
            return null;
        }
        if (bytes.Length > Bulk.MaxObjectSize)
        {
            error = Loc.F(Strings.Err_Bulk_TooBig, Loc.F(Strings.Unit_Mebibytes, bytes.Length / (1024 * 1024)));
            return null;
        }

        var hash = SHA256.HashData(bytes);
        Outgoing transfer;
        lock (_gate)
        {
            var id = _nextId;
            _nextId = _nextId == uint.MaxValue ? 1 : _nextId + 1;

            transfer = new Outgoing(id, kind, format, bytes, hash, description)
            {
                OfferAttempts = 1,
                LastOfferAt = now,
                LastAckAt = now,
                LastSendAt = now,
            };
            _outgoing[id] = transfer;
        }

        _send(PacketType.BulkOffer, BulkCodec.WriteOffer(transfer.Offer));
        error = null;
        return transfer.Id;
    }

    /// <summary>Drops everything in flight. Used when the channel itself is going away.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _outgoing.Clear();
            _incoming.Clear();
            _finished.Clear();
            _credit = 0;
            _lastTick = null;
        }
    }

    /// <summary>
    /// The peer is not the one we were talking to a moment ago — it restarted, or it has
    /// only just appeared.
    ///
    /// Anything half-received belongs to a session that is gone, so it goes. Anything we
    /// were pushing does *not*: the object is still worth delivering, and the new peer has
    /// simply never heard of it. Dropping it here is how an object offered before the peer's
    /// first packet arrives disappears without a trace — which is exactly what a receiver
    /// with something already on its clipboard does at startup.
    /// </summary>
    public void PeerRestarted()
    {
        lock (_gate)
        {
            _incoming.Clear();
            _finished.Clear();
            foreach (var transfer in _outgoing.Values) transfer.Renegotiate();
        }
    }

    // MARK: - Packets

    /// <summary>
    /// One decrypted bulk packet. Types outside 9..12 are not this layer's business and are
    /// ignored rather than treated as an error.
    /// </summary>
    public void OnPacket(PacketType type, ReadOnlySpan<byte> payload, DateTime now)
    {
        switch (type)
        {
            case PacketType.BulkOffer:
                if (BulkCodec.TryReadOffer(payload, out var offer)) HandleOffer(offer, now);
                break;
            case PacketType.BulkChunk:
                if (BulkCodec.TryReadChunk(payload, out var id, out var index, out var data))
                {
                    HandleChunk(id, index, data, now);
                }
                break;
            case PacketType.BulkAck:
                if (BulkCodec.TryReadAck(payload, out var ack)) HandleAck(ack, now);
                break;
            case PacketType.BulkDone:
                if (BulkCodec.TryReadDone(payload, out var done)) HandleDone(done);
                break;
            default:
                break;
        }
    }

    private void HandleOffer(BulkOffer offer, DateTime now)
    {
        // A repeat of an offer we have already satisfied: the BULK_DONE was lost, so say it
        // again instead of letting the sender sit on the bytes until it times out.
        bool alreadyDone;
        lock (_gate) alreadyDone = _finished.ContainsKey(offer.TransferId);
        if (alreadyDone)
        {
            _send(PacketType.BulkDone, BulkCodec.WriteDone(offer.TransferId));
            return;
        }

        if (offer.Size == 0 || offer.Size > Bulk.MaxObjectSize) return;
        if (offer.ChunkCount != Bulk.ChunkCountFor(offer.Size)) return;

        // The whole loop-breaker, and the reason the contract puts a hash in the offer at
        // all: if we already hold these exact bytes, nothing has to cross the wire.
        if (Owns?.Invoke(offer.Hash) == true)
        {
            _send(PacketType.BulkAck, BulkCodec.WriteAck(new BulkAck(offer.TransferId, false, 0, [])));
            Note?.Invoke(Loc.F(Strings.Log_Bulk_Have, Describe(offer)));
            return;
        }

        byte[] ack;
        lock (_gate)
        {
            if (!_incoming.TryGetValue(offer.TransferId, out var incoming)
                || !incoming.Hash.AsSpan().SequenceEqual(offer.Hash))
            {
                incoming = new Incoming(offer);
                _incoming[offer.TransferId] = incoming;
            }
            incoming.LastActivity = now;
            incoming.LastAckAt = now;
            ack = BulkCodec.WriteAck(incoming.BuildAck(accepted: true));
        }
        _send(PacketType.BulkAck, ack);
    }

    private void HandleChunk(uint transferId, uint index, byte[] data, DateTime now)
    {
        BulkDelivery? delivery = null;
        string? note = null;
        var packets = new List<(PacketType, byte[])>();

        lock (_gate)
        {
            if (_finished.ContainsKey(transferId))
            {
                packets.Add((PacketType.BulkDone, BulkCodec.WriteDone(transferId)));
            }
            else if (_incoming.TryGetValue(transferId, out var incoming))
            {
                incoming.LastActivity = now;
                incoming.Store(index, data);

                if (incoming.IsComplete)
                {
                    if (incoming.Verify())
                    {
                        delivery = incoming.ToDelivery();
                        _incoming.Remove(transferId);
                        _finished[transferId] = now;
                        packets.Add((PacketType.BulkDone, BulkCodec.WriteDone(transferId)));
                    }
                    else
                    {
                        // «Молчаливой порчи не бывает»: every chunk is asked for again.
                        incoming.HashFailures++;
                        note = Loc.F(Strings.Log_Bulk_HashFailed, incoming.HashFailures);
                        if (incoming.HashFailures >= MaxHashFailures)
                        {
                            _incoming.Remove(transferId);
                            note = Strings.Log_Bulk_GivingUp;
                        }
                        else
                        {
                            incoming.Forget();
                            incoming.LastAckAt = now;
                            packets.Add((PacketType.BulkAck, BulkCodec.WriteAck(incoming.BuildAck(accepted: true))));
                        }
                    }
                }
            }
        }

        foreach (var (type, payload) in packets) _send(type, payload);
        if (note is not null) Note?.Invoke(note);
        if (delivery is not null) Delivered?.Invoke(delivery);
    }

    private void HandleAck(BulkAck ack, DateTime now)
    {
        BulkResult? result = null;
        lock (_gate)
        {
            if (!_outgoing.TryGetValue(ack.TransferId, out var transfer)) return;

            transfer.LastAckAt = now;
            if (!ack.Accepted)
            {
                _outgoing.Remove(ack.TransferId);
                result = transfer.Result(BulkOutcome.AlreadyThere);
            }
            else
            {
                transfer.Accepted = true;
                transfer.Rebuild(ack);
            }
        }
        if (result is not null) Finished?.Invoke(result);
    }

    private void HandleDone(uint transferId)
    {
        BulkResult? result = null;
        lock (_gate)
        {
            if (_outgoing.Remove(transferId, out var transfer)) result = transfer.Result(BulkOutcome.Delivered);
        }
        if (result is not null) Finished?.Invoke(result);
    }

    // MARK: - Clock

    /// <summary>
    /// Everything that happens on a timer: repeating an offer, putting chunks on the wire,
    /// the receiver's 200 ms ack, and the timeouts. Call it about every 20 ms.
    /// </summary>
    public void Tick(DateTime now)
    {
        var packets = new List<(PacketType, byte[])>();
        var results = new List<BulkResult>();
        var notes = new List<string>();

        lock (_gate)
        {
            var elapsed = _lastTick is null ? TimeSpan.FromMilliseconds(20) : now - _lastTick.Value;
            _lastTick = now;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            // A token bucket rather than «n chunks per tick», so the rate on the wire does
            // not change when the caller's timer does.
            _credit = Math.Min(_credit + elapsed.TotalSeconds * ChunksPerSecond, ChunksPerSecond);

            TickOutgoing(now, packets, results, notes);
            TickIncoming(now, packets);

            foreach (var (id, at) in _finished.ToArray())
            {
                if (now - at > Bulk.IncomingIdleTimeout) _finished.Remove(id);
            }
        }

        foreach (var (type, payload) in packets) _send(type, payload);
        foreach (var note in notes) Note?.Invoke(note);
        foreach (var result in results) Finished?.Invoke(result);
    }

    private void TickOutgoing(DateTime now, List<(PacketType, byte[])> packets, List<BulkResult> results, List<string> notes)
    {
        foreach (var transfer in _outgoing.Values.ToArray())
        {
            if (!transfer.Accepted)
            {
                if (now - transfer.LastOfferAt < Bulk.OfferInterval) continue;

                if (transfer.OfferAttempts >= Bulk.MaxOfferAttempts)
                {
                    _outgoing.Remove(transfer.Id);
                    results.Add(transfer.Result(BulkOutcome.NoAnswer));
                    notes.Add(Loc.F(Strings.Log_Bulk_NoAnswer, Describe(transfer)));
                    continue;
                }

                transfer.OfferAttempts++;
                transfer.LastOfferAt = now;
                packets.Add((PacketType.BulkOffer, BulkCodec.WriteOffer(transfer.Offer)));
                continue;
            }

            if (transfer.HasQueued)
            {
                while (_credit >= 1 && transfer.TryDequeue(out var index))
                {
                    _credit -= 1;
                    packets.Add((PacketType.BulkChunk,
                        BulkCodec.WriteChunk(transfer.Id, index, transfer.Chunk(index))));
                    transfer.LastSendAt = now;
                }
                continue;
            }

            // Everything asked for is on the wire. Retransmission is driven by the
            // receiver's ack, so if that ack never comes the transfer can only wait — poke
            // it with the last chunk rather than sit there until the timeout.
            if (now - transfer.LastAckAt > Bulk.SendingSilenceTimeout)
            {
                _outgoing.Remove(transfer.Id);
                results.Add(transfer.Result(BulkOutcome.Stalled));
                notes.Add(Loc.F(Strings.Log_Bulk_Stalled, Describe(transfer)));
                continue;
            }

            if (now - transfer.LastSendAt > TimeSpan.FromMilliseconds(500) && _credit >= 1)
            {
                _credit -= 1;
                var last = transfer.Offer.ChunkCount - 1;
                packets.Add((PacketType.BulkChunk, BulkCodec.WriteChunk(transfer.Id, last, transfer.Chunk(last))));
                transfer.LastSendAt = now;
            }
        }
    }

    private void TickIncoming(DateTime now, List<(PacketType, byte[])> packets)
    {
        foreach (var incoming in _incoming.Values.ToArray())
        {
            if (now - incoming.LastActivity > Bulk.IncomingIdleTimeout)
            {
                _incoming.Remove(incoming.TransferId);
                continue;
            }

            if (now - incoming.LastAckAt < Bulk.AckInterval) continue;
            incoming.LastAckAt = now;
            packets.Add((PacketType.BulkAck, BulkCodec.WriteAck(incoming.BuildAck(accepted: true))));
        }
    }

    // MARK: - Telemetry

    /// <summary>Everything in flight, for a progress bar. Cheap; safe on a UI tick.</summary>
    public BulkProgress[] Progress()
    {
        lock (_gate)
        {
            return
            [
                .. _outgoing.Values.Select(t => new BulkProgress(
                    t.Id, BulkDirection.Outgoing, t.Offer.Kind, t.Offer.Format, t.Offer.Size,
                    t.ChunksSent, t.Offer.ChunkCount, t.Offer.Description)),
                .. _incoming.Values.Select(i => new BulkProgress(
                    i.TransferId, BulkDirection.Incoming, i.Kind, i.Format, i.Size,
                    i.HaveCount, i.ChunkCount, i.Description)),
            ];
        }
    }

    private static string Describe(BulkOffer offer) => Loc.F(Strings.Log_Bulk_Describe, offer.Kind, Loc.F(Strings.Unit_Bytes, offer.Size));

    private static string Describe(Outgoing transfer) => Describe(transfer.Offer);

    // MARK: - Roles

    /// <summary>One object we are pushing.</summary>
    private sealed class Outgoing(uint id, BulkKind kind, BulkFormat format, byte[] bytes, byte[] hash, string description)
    {
        private readonly byte[] _bytes = bytes;
        private readonly Queue<uint> _queue = new();
        private readonly HashSet<uint> _queued = [];

        public uint Id { get; } = id;

        public BulkOffer Offer { get; } = new(
            id, kind, format, (uint)bytes.Length, Bulk.ChunkCountFor(bytes.Length), hash, description);

        public bool Accepted { get; set; }
        public int OfferAttempts { get; set; }
        public DateTime LastOfferAt { get; set; }
        public DateTime LastAckAt { get; set; }
        public DateTime LastSendAt { get; set; }

        /// <summary>Chunks handed to the socket so far, for the progress bar only.</summary>
        public uint ChunksSent { get; set; }

        public bool HasQueued => _queue.Count > 0;

        /// <summary>Back to square one: offer it again and wait to be told what to send.</summary>
        public void Renegotiate()
        {
            Accepted = false;
            OfferAttempts = 0;
            LastOfferAt = DateTime.MinValue;
            ChunksSent = 0;
            _queue.Clear();
            _queued.Clear();
        }

        /// <summary>
        /// Turns an ack into a send list. A full list means the receiver had more holes than
        /// it could name, and the contract's answer to that is to start over from the first
        /// one rather than to guess at the rest.
        /// </summary>
        public void Rebuild(BulkAck ack)
        {
            _queue.Clear();
            _queued.Clear();
            if (ack.FirstMissing >= Offer.ChunkCount) return;

            if (ack.Truncated)
            {
                for (var i = ack.FirstMissing; i < Offer.ChunkCount; i++) Enqueue(i);
                return;
            }

            Enqueue(ack.FirstMissing);
            foreach (var index in ack.Missing)
            {
                if (index < Offer.ChunkCount) Enqueue(index);
            }
        }

        private void Enqueue(uint index)
        {
            if (_queued.Add(index)) _queue.Enqueue(index);
        }

        public bool TryDequeue(out uint index)
        {
            if (_queue.TryDequeue(out index))
            {
                _queued.Remove(index);
                if (ChunksSent < Offer.ChunkCount) ChunksSent++;
                return true;
            }
            return false;
        }

        public ReadOnlySpan<byte> Chunk(uint index) =>
            _bytes.AsSpan((int)index * Bulk.ChunkSize, Bulk.ChunkLength(_bytes.Length, index));

        public BulkResult Result(BulkOutcome outcome) => new(
            Id, Offer.Kind, Offer.Format, Offer.Size, Offer.Hash, Offer.Description, outcome);
    }

    /// <summary>One object being assembled.</summary>
    private sealed class Incoming
    {
        private readonly byte[] _buffer;
        private readonly bool[] _have;

        public Incoming(BulkOffer offer)
        {
            TransferId = offer.TransferId;
            Kind = offer.Kind;
            Format = offer.Format;
            Size = offer.Size;
            ChunkCount = offer.ChunkCount;
            Hash = offer.Hash;
            Description = offer.Description;
            _buffer = new byte[offer.Size];
            _have = new bool[offer.ChunkCount];
        }

        public uint TransferId { get; }
        public BulkKind Kind { get; }
        public BulkFormat Format { get; }
        public uint Size { get; }
        public uint ChunkCount { get; }
        public byte[] Hash { get; }
        public string Description { get; }

        public uint HaveCount { get; private set; }
        public int HashFailures { get; set; }
        public DateTime LastActivity { get; set; }
        public DateTime LastAckAt { get; set; }

        public bool IsComplete => HaveCount == ChunkCount;

        public void Store(uint index, byte[] data)
        {
            if (index >= ChunkCount) return;
            // A chunk of the wrong length cannot be part of this object, whatever else it
            // is. Taking it would corrupt the buffer in a way only the hash would catch.
            if (data.Length != Bulk.ChunkLength(Size, index)) return;
            if (_have[index]) return;

            data.CopyTo(_buffer, (int)index * Bulk.ChunkSize);
            _have[index] = true;
            HaveCount++;
        }

        public bool Verify() => SHA256.HashData(_buffer).AsSpan().SequenceEqual(Hash);

        public void Forget()
        {
            Array.Clear(_have);
            HaveCount = 0;
        }

        /// <summary>
        /// The first hole, then up to 256 more. When nothing is missing the first-missing
        /// field points one past the last chunk — the contract has no other way to say
        /// «ничего не нужно», and the sender reads anything at or past the count as «жду
        /// BULK_DONE».
        /// </summary>
        public BulkAck BuildAck(bool accepted)
        {
            uint first = ChunkCount;
            var listed = new List<uint>(Bulk.MaxMissingListed);

            for (uint i = 0; i < ChunkCount; i++)
            {
                if (_have[i]) continue;
                if (first == ChunkCount)
                {
                    first = i;
                    continue;
                }
                if (listed.Count == Bulk.MaxMissingListed) break;
                listed.Add(i);
            }

            return new BulkAck(TransferId, accepted, first, [.. listed]);
        }

        public BulkDelivery ToDelivery() => new(TransferId, Kind, Format, _buffer, Hash, Description);
    }
}
