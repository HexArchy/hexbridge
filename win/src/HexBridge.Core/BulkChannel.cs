using System.Buffers;

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
    /// <summary>The other end assembled the object and the hash matched.</summary>
    Delivered,

    /// <summary>
    /// The other side answered «не надо». Either it already holds these exact bytes, or
    /// nothing over there is listening for this kind — the contract has one field for both
    /// and the answer to each is the same: do not send it.
    /// </summary>
    AlreadyThere,

    /// <summary>Ten offers, no answer.</summary>
    NoAnswer,

    /// <summary>The acks stopped while chunks were still outstanding.</summary>
    Stalled,
}

/// <summary>
/// An object that arrived whole.
///
/// <para>
/// It comes one of two ways, and never both. A small object — the clipboard's — arrives as
/// <see cref="Bytes"/>, because the feature waiting for it has to hand those bytes to the
/// clipboard. A file arrives as <see cref="Path"/>: a temporary file, already verified,
/// which the feature taking it owns from that moment — it moves the file where it belongs
/// or deletes it, and if it does neither the file is left behind.
/// </para>
/// </summary>
public sealed record BulkDelivery(
    uint TransferId,
    BulkKind Kind,
    BulkFormat Format,
    uint Size,
    byte[] Hash,
    string Description,
    byte[]? Bytes,
    string? Path);

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
/// It knows nothing about clipboards or files. A feature hands it a source, a kind and a
/// format and gets told when they landed; a new kind is a new lane and changes nothing
/// here. Deliberately primitive, as the contract says: retransmission driven by the other
/// end's ack, no reordering, and no second TCP on top of UDP.
///
/// <para>
/// What it does have is pacing, because the contract asks for exactly that: «темп — местное
/// решение», and the missing lists are the signal that the local decision was wrong. The
/// rate climbs while chunks are landing and is halved when they are not.
/// </para>
///
/// <para>
/// Nothing the size of the object is ever held. An object being sent is read from its
/// <see cref="BulkSource"/> a chunk at a time, one arriving goes into a
/// <see cref="BulkAssembly"/> that is either an array or a file, and what has landed is a
/// bitmap. Four million chunks is the worst case, so nothing in the path of one packet may
/// walk them and nothing may allocate per chunk.
/// </para>
///
/// <para>
/// Time is a parameter rather than a field read from the clock, so every timeout in here is
/// reachable from a test without waiting for it.
/// </para>
/// </summary>
public sealed class BulkChannel
{
    /// <summary>
    /// How much of a second the token bucket may hold. A full second of credit goes out as
    /// one burst the moment a transfer starts, which is the flood the pacing is here to
    /// prevent; a tick's worth covers a timer that ran late without becoming a burst.
    /// </summary>
    private const double BurstSeconds = 0.05;

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
    /// What the pacing is aiming at, in chunks per second. Always inside the ceiling and
    /// the floor — see <see cref="Held"/>, which is the only thing that ever sets it.
    /// </summary>
    private double _rate = Bulk.StartChunksPerSecond;

    private double _ceiling = Bulk.MaxChunksPerSecond;

    /// <summary>
    /// Whether this path has ever lost a chunk. Before it has, the rate doubles; after it
    /// has, it climbs additively — there is nothing to learn from doubling past a limit
    /// that has already been found once.
    /// </summary>
    private bool _backedOff;

    /// <summary>No second cut before this: one hole is named in several acks running.</summary>
    private DateTime _quietUntil = DateTime.MinValue;

    /// <summary>Whether the last tick actually put chunks on the wire.</summary>
    private bool _sending;

    /// <summary>When a chunk last went out, so an idle channel can forget what it learned.</summary>
    private DateTime _lastChunkAt = DateTime.MinValue;

    /// <summary>
    /// «Этот объект у меня уже есть». The contract makes this the answer to a duplicate
    /// offer *and* the thing that stops two machines syncing each other in a circle.
    ///
    /// <para>
    /// It takes the kind because the channel is shared: the clipboard knows what is on the
    /// clipboard and nothing about the Downloads folder, and a hash is only meaningful to
    /// the feature the object belongs to. Answering «yes» for somebody else's kind would
    /// refuse a transfer nobody has.
    /// </para>
    /// </summary>
    public Func<BulkKind, byte[], bool>? Owns { get; set; }

    /// <summary>
    /// Whether anything on this machine is waiting for a kind at all.
    ///
    /// <para>
    /// Without it, an offer of a kind nobody subscribes to is accepted like any other: the
    /// whole object crosses the network, is acknowledged, and is then dropped because there
    /// is no one to hand it to. Somebody who switched file transfer off would go on paying
    /// for every file the other machine sends, and the other machine would show each one as
    /// delivered. Null means «take everything», which is what a channel with no features
    /// wired to it should do.
    /// </para>
    /// </summary>
    public Func<BulkKind, bool>? Wanted { get; set; }

    /// <summary>
    /// Where a kind wants an arriving object staged while it is being assembled.
    ///
    /// <para>
    /// A kind that answers with a folder is streamed into a file in it and is never held in
    /// memory, which is the whole reason a file can be four gigabytes. A kind that answers
    /// null — or a channel with nothing wired here — is assembled in memory, which is what
    /// the clipboard needs and what its 64 MiB ceiling pays for. Asked once per object,
    /// never per chunk.
    /// </para>
    /// </summary>
    public Func<BulkKind, string?>? Staging { get; set; }

    /// <summary>An object arrived whole and verified. Raised outside the channel's lock.</summary>
    public event Action<BulkDelivery>? Delivered;

    /// <summary>An outgoing transfer ended. Raised outside the channel's lock.</summary>
    public event Action<BulkResult>? Finished;

    /// <summary>Diagnostics, one line per interesting event. Raised outside the lock.</summary>
    public event Action<string>? Note;

    /// <summary>
    /// The highest rate the pacing may aim at, in chunks of 1024 bytes per second. Anything
    /// at or below zero means the only ceiling is <see cref="Bulk.MaxChunksPerSecond"/>.
    ///
    /// <para>
    /// This is the configured limit and the relay's together, worked out by whoever owns the
    /// channel. It is not what goes on the wire: the pacing under it climbs and falls with
    /// the link, and only ever aims lower than this.
    /// </para>
    /// </summary>
    public double ChunkRateCeiling
    {
        get
        {
            lock (_gate) return _ceiling;
        }
        set
        {
            lock (_gate)
            {
                _ceiling = value > 0 ? value : Bulk.MaxChunksPerSecond;
                _rate = Held(_rate);
            }
        }
    }

    /// <summary>What the pacing is allowing right now, the ceiling included.</summary>
    public double ChunksPerSecond
    {
        get
        {
            lock (_gate) return Rate();
        }
    }

    /// <summary>How many times an object may fail its hash before this end gives up on it.</summary>
    public int MaxHashFailures { get; set; } = 3;

    public BulkChannel(Action<PacketType, ReadOnlyMemory<byte>> send) => _send = send;

    /// <summary>
    /// «Объект больше, чем этот вид может нести», with the ceiling spelled from the constant
    /// for the kind in hand rather than typed into the string: the two had already drifted
    /// apart once, and now the two kinds are four gigabytes apart.
    /// </summary>
    public static string TooBig(BulkKind kind, long size) => Loc.F(
        Strings.Err_Bulk_TooBig,
        Loc.Size(size),
        kind == BulkKind.File
            ? Loc.F(Strings.Unit_Gibibytes, 4)
            : Loc.F(Strings.Unit_Mebibytes, Bulk.MaxClipboardSize / (1024 * 1024)));

    // MARK: - Sending

    /// <summary>
    /// Starts offering an object that is already in memory. Returns its transfer id, or null
    /// with a reason the user can read.
    /// </summary>
    public uint? Offer(BulkKind kind, BulkFormat format, byte[] bytes, string description, DateTime now, out string? error) =>
        Offer(kind, format, BulkSource.FromMemory(bytes), description, now, out error);

    /// <summary>
    /// Starts offering an object. The channel takes the source over: it reads every chunk
    /// from it, the ones it has to send a second time included, and closes it when the
    /// transfer ends however it ends. A source handed to an offer that is refused here is
    /// closed here.
    /// </summary>
    public uint? Offer(BulkKind kind, BulkFormat format, BulkSource source, string description, DateTime now, out string? error)
    {
        if (source.Length == 0)
        {
            source.Dispose();
            error = Strings.Err_Bulk_Empty;
            return null;
        }
        if (source.Length > Bulk.MaxSizeFor(kind))
        {
            var length = source.Length;
            source.Dispose();
            error = TooBig(kind, length);
            return null;
        }

        Outgoing transfer;
        lock (_gate)
        {
            var id = _nextId;
            _nextId = _nextId == uint.MaxValue ? 1 : _nextId + 1;

            transfer = new Outgoing(id, kind, format, source, description)
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

    /// <summary>
    /// Drops everything in flight. Used when the channel itself is going away — and the
    /// files half-written on disk go with it, rather than being left for somebody to find.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var transfer in _outgoing.Values) transfer.Dispose();
            foreach (var incoming in _incoming.Values) incoming.Dispose();
            _outgoing.Clear();
            _incoming.Clear();
            _finished.Clear();
            _credit = 0;
            _lastTick = null;
            _rate = Held(Bulk.StartChunksPerSecond);
            _backedOff = false;
            _quietUntil = DateTime.MinValue;
            _lastChunkAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Drops what one feature has in flight and leaves every other kind alone. Turning the
    /// clipboard off must not tear the file somebody is watching arrive off the wire.
    ///
    /// <para>
    /// The record of finished transfers is kept whatever the kind: it holds ids, not
    /// objects, and it is the only thing that can answer a lost BULK_DONE for the kinds
    /// that are still running.
    /// </para>
    /// </summary>
    public void Reset(BulkKind kind)
    {
        lock (_gate)
        {
            foreach (var (id, transfer) in _outgoing.ToArray())
            {
                if (transfer.Offer.Kind != kind) continue;
                _outgoing.Remove(id);
                transfer.Dispose();
            }
            foreach (var (id, incoming) in _incoming.ToArray())
            {
                if (incoming.Kind != kind) continue;
                _incoming.Remove(id);
                incoming.Dispose();
            }
        }
    }

    /// <summary>
    /// The peer is not the one we were talking to a moment ago — it restarted, or it has
    /// only just appeared.
    ///
    /// Anything half-received belongs to a session that is gone, so it goes. Anything we
    /// were pushing does *not*: the object is still worth delivering, and the new peer has
    /// simply never heard of it. Dropping it here is how an object offered before the peer's
    /// first packet arrives disappears without a trace — which is exactly what a machine
    /// with something already on its clipboard does at startup.
    /// </summary>
    public void PeerRestarted()
    {
        lock (_gate)
        {
            foreach (var incoming in _incoming.Values) incoming.Dispose();
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
        // again instead of letting the other end sit on the object until it times out.
        bool alreadyDone;
        lock (_gate) alreadyDone = _finished.ContainsKey(offer.TransferId);
        if (alreadyDone)
        {
            _send(PacketType.BulkDone, BulkCodec.WriteDone(offer.TransferId));
            return;
        }

        if (offer.Size == 0 || offer.Size > Bulk.MaxSizeFor(offer.Kind)) return;
        if (offer.ChunkCount != Bulk.ChunkCountFor(offer.Size)) return;

        // Turned down before a single chunk is asked for. «Не надо» is the only no the
        // contract has, and it is the right one: the other end stops, keeps its object, and
        // is not left waiting for an ack that is never coming.
        if (Wanted?.Invoke(offer.Kind) == false)
        {
            _send(PacketType.BulkAck, BulkCodec.WriteAck(new BulkAck(offer.TransferId, false, 0, [])));
            Note?.Invoke(Loc.F(Strings.Log_Bulk_Unwanted, Describe(offer)));
            return;
        }

        // The whole loop-breaker, and the reason the contract puts a hash in the offer at
        // all: if we already hold these exact bytes, nothing has to cross the wire.
        if (Owns?.Invoke(offer.Kind, offer.Hash) == true)
        {
            _send(PacketType.BulkAck, BulkCodec.WriteAck(new BulkAck(offer.TransferId, false, 0, [])));
            Note?.Invoke(Loc.F(Strings.Log_Bulk_Have, Describe(offer)));
            return;
        }

        // An offer is repeated once a second until it is accepted, so most of them are
        // about an object already being assembled: answer from that one rather than start
        // it over.
        Incoming? incoming;
        lock (_gate)
        {
            _incoming.TryGetValue(offer.TransferId, out incoming);
            if (incoming is not null && !incoming.Hash.AsSpan().SequenceEqual(offer.Hash))
            {
                // «Повторное использование идентификатора с другим хешем — это новая
                // передача»: the old one goes, temporary file and all, before the new one
                // claims the name that file is built from.
                _incoming.Remove(offer.TransferId);
                incoming.Dispose();
                incoming = null;
            }
        }

        if (incoming is null)
        {
            // Outside the lock: creating a file of four gigabytes is not work to hold a
            // channel for, and every ack on it would wait.
            var staging = Staging?.Invoke(offer.Kind);
            if (string.IsNullOrEmpty(staging) && offer.Size > Bulk.MaxClipboardSize)
            {
                // Too big to hold and nowhere to stream it. Silence is the only honest
                // answer: accepting would mean allocating gigabytes in order to throw them
                // away, and the other end stops after ten unanswered offers.
                Note?.Invoke(Loc.F(Strings.Log_Bulk_NoRoom, Describe(offer)));
                return;
            }

            BulkAssembly assembly;
            try
            {
                assembly = string.IsNullOrEmpty(staging)
                    ? BulkAssembly.InMemory(offer.Size, offer.ChunkCount)
                    : Stage(staging, offer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // No room for it, or nowhere to write it. Said out loud and not acked.
                Note?.Invoke(Loc.F(Strings.Log_Bulk_WriteFailed, ex.Message));
                return;
            }

            incoming = new Incoming(offer, assembly);
        }

        byte[] ack;
        lock (_gate)
        {
            if (_incoming.TryGetValue(offer.TransferId, out var present) && !ReferenceEquals(present, incoming))
            {
                // Another packet got here while the file was being created.
                incoming.Dispose();
                incoming = present;
            }
            else
            {
                _incoming[offer.TransferId] = incoming;
            }

            incoming.LastActivity = now;
            incoming.LastAckAt = now;
            ack = BulkCodec.WriteAck(incoming.BuildAck(accepted: true));
        }
        _send(PacketType.BulkAck, ack);
    }

    /// <summary>
    /// One chunk, on the socket thread. Everything here is a fixed amount of work: a bit in
    /// a bitmap and either a memcpy or one positional write. A transfer is up to four
    /// million of these, so anything that walked the chunks or allocated per chunk would
    /// show up as the microphone breaking up rather than as a slow file.
    /// </summary>
    private void HandleChunk(uint transferId, uint index, ReadOnlySpan<byte> data, DateTime now)
    {
        BulkDelivery? delivery = null;
        string? note = null;
        byte[]? done = null;
        byte[]? ack = null;

        lock (_gate)
        {
            if (_finished.ContainsKey(transferId))
            {
                done = BulkCodec.WriteDone(transferId);
            }
            else if (_incoming.TryGetValue(transferId, out var incoming))
            {
                incoming.LastActivity = now;
                try
                {
                    incoming.Store(index, data);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The disk filled up, or the folder went away underneath us. The
                    // transfer cannot finish, and letting it time out instead would leave
                    // the half-written file behind for thirty seconds longer.
                    _incoming.Remove(transferId);
                    incoming.Dispose();
                    note = Loc.F(Strings.Log_Bulk_WriteFailed, ex.Message);
                }

                // An object held in memory is finished here, where its last chunk landed,
                // exactly as it always was: hashing 64 MiB is milliseconds. A streamed one
                // is finished on the channel's own thread — see TickIncoming.
                if (note is null && incoming.IsComplete && !incoming.Streamed)
                {
                    var completion = Complete(incoming, now);
                    delivery = completion.Delivery;
                    done = completion.Done;
                    ack = completion.Ack;
                    note = completion.Note;
                }
            }
        }

        if (done is not null) _send(PacketType.BulkDone, done);
        if (ack is not null) _send(PacketType.BulkAck, ack);
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
                transfer.Dispose();
            }
            else
            {
                transfer.Accepted = true;
                NoteHoles(transfer, ack, now);
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
            if (_outgoing.Remove(transferId, out var transfer))
            {
                result = transfer.Result(BulkOutcome.Delivered);
                transfer.Dispose();
            }
        }
        if (result is not null) Finished?.Invoke(result);
    }

    // MARK: - Pacing

    /// <summary>
    /// The rate kept inside what it is allowed to be. It is applied every time the rate
    /// moves rather than only when it is read, and that is not tidiness: a rate left to
    /// climb past a ceiling it can never reach takes several halvings before a cut shows
    /// up on the wire at all, so the loop would go deaf exactly where a ceiling is set —
    /// which is to say, on a relay.
    ///
    /// <para>
    /// A configured ceiling below the floor wins over the floor. Somebody who asks for
    /// 50 KB/s is asking for something this program has no business overruling.
    /// </para>
    /// </summary>
    private double Held(double rate)
    {
        var top = Math.Min(_ceiling, Bulk.MaxChunksPerSecond);
        return Math.Min(Math.Max(rate, Math.Min(Bulk.MinChunksPerSecond, top)), top);
    }

    private double Rate() => _rate;

    /// <summary>
    /// The ack named holes. Whether that is loss or simply chunks that have not crossed the
    /// wire yet is the whole question, and the answer is how long they have had.
    ///
    /// <para>
    /// Every chunk that had been sent by the time the *previous* ack was answered has had a
    /// full ack interval and a round trip to arrive in. If this ack still does not have it,
    /// it did not arrive. Chunks sent since then may be in the air and prove nothing, and
    /// chunks never sent are not holes at all — they are simply the transfer not being
    /// finished.
    /// </para>
    ///
    /// <para>
    /// Distance was the other way to measure this — «so many chunks behind the furthest one
    /// sent» — and it does not work here: an ack with a full list sends this end back to the
    /// first hole, so the holes are always near the frontier however far the transfer has
    /// got. One ack of history costs a single number and says what distance cannot.
    /// </para>
    /// </summary>
    private void NoteHoles(Outgoing transfer, BulkAck ack, DateTime now)
    {
        var frontier = transfer.Frontier;
        transfer.Frontier = transfer.HighestSent;

        // One cut per pause, however many holes are named: a chunk that was lost is named
        // again in the next ack and the one after, because the copy that fills it has not
        // crossed yet. Counting it each time would halve the rate into the floor over one
        // lost packet.
        if (now < _quietUntil) return;
        if (!Lost(ack.FirstMissing) && !ack.Missing.Any(Lost)) return;

        _backedOff = true;
        _quietUntil = now + Bulk.BackoffPause;
        _rate = Held(_rate * Bulk.FallFactor);

        bool Lost(uint index) => index < frontier;
    }

    // MARK: - Clock

    /// <summary>
    /// Everything that happens on a timer: repeating an offer, putting chunks on the wire,
    /// the 200 ms ack, finishing a streamed object, and the timeouts. Call it about every
    /// 20 ms.
    /// </summary>
    public void Tick(DateTime now)
    {
        var outbox = new Outbox();
        var results = new List<BulkResult>();
        var notes = new List<string>();
        var deliveries = new List<BulkDelivery>();

        lock (_gate)
        {
            var elapsed = _lastTick is null ? TimeSpan.FromMilliseconds(20) : now - _lastTick.Value;
            _lastTick = now;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            // The climb, and only while chunks are actually going out: a channel that spent
            // the last ten minutes idle has learned nothing about the link, and starting the
            // next transfer at the rate the last one ended on is how its first second is
            // spent flooding a path that may not even be the same one.
            // Nothing has gone out for long enough that what the rate was measuring may no
            // longer be there. Back to the beginning, doubling and all.
            if (now - _lastChunkAt > Bulk.PacingIdle)
            {
                _rate = Held(Bulk.StartChunksPerSecond);
                _backedOff = false;
            }

            if (_sending)
            {
                _rate = Held(_backedOff
                    ? _rate + (elapsed.TotalSeconds * Bulk.RiseChunksPerSecond)
                    : _rate * Math.Pow(Bulk.RiseFactor, elapsed.TotalSeconds / Bulk.AckInterval.TotalSeconds));
            }
            _sending = false;

            // A token bucket rather than «n chunks per tick», so the rate on the wire does
            // not change when the caller's timer does.
            var rate = Rate();
            _credit = Math.Min(_credit + (elapsed.TotalSeconds * rate), Math.Max(1, rate * BurstSeconds));

            TickOutgoing(now, outbox, results, notes);
            TickIncoming(now, outbox, deliveries, notes);

            foreach (var (id, at) in _finished.ToArray())
            {
                if (now - at > Bulk.IncomingIdleTimeout) _finished.Remove(id);
            }
        }

        outbox.Flush(_send);
        foreach (var note in notes) Note?.Invoke(note);
        foreach (var delivery in deliveries) Delivered?.Invoke(delivery);
        foreach (var result in results) Finished?.Invoke(result);
    }

    private void TickOutgoing(DateTime now, Outbox outbox, List<BulkResult> results, List<string> notes)
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
                    transfer.Dispose();
                    continue;
                }

                transfer.OfferAttempts++;
                transfer.LastOfferAt = now;
                outbox.Add(PacketType.BulkOffer, BulkCodec.WriteOffer(transfer.Offer));
                continue;
            }

            if (transfer.HasQueued)
            {
                while (_credit >= 1 && transfer.TryDequeue(out var index))
                {
                    _credit -= 1;
                    _sending = true;
                    _lastChunkAt = now;
                    Chunk(outbox, transfer, index);
                    transfer.LastSendAt = now;
                }
                continue;
            }

            // Everything asked for is on the wire. Retransmission is driven by the other
            // end's ack, so if that ack never comes the transfer can only wait — poke it
            // with the last chunk rather than sit there until the timeout.
            if (now - transfer.LastAckAt > Bulk.SendingSilenceTimeout)
            {
                _outgoing.Remove(transfer.Id);
                results.Add(transfer.Result(BulkOutcome.Stalled));
                notes.Add(Loc.F(Strings.Log_Bulk_Stalled, Describe(transfer)));
                transfer.Dispose();
                continue;
            }

            if (now - transfer.LastSendAt > TimeSpan.FromMilliseconds(500) && _credit >= 1)
            {
                _credit -= 1;
                Chunk(outbox, transfer, transfer.Offer.ChunkCount - 1);
                transfer.LastSendAt = now;
            }
        }
    }

    /// <summary>
    /// One chunk packet, read off the source straight into the buffer it will be sent from.
    /// A chunk that cannot be read — the file was truncated while it was being sent — goes
    /// out short, and the other end drops it for its length, which is the same answer as
    /// losing it.
    /// </summary>
    private static void Chunk(Outbox outbox, Outgoing transfer, uint index)
    {
        var length = Bulk.ChunkLength(transfer.Offer.Size, index);
        var packet = outbox.Rent(Bulk.ChunkHeaderSize + length);
        BulkCodec.WriteChunkHeader(packet, transfer.Id, index);

        int read;
        try
        {
            read = transfer.Read(index, packet.AsSpan(Bulk.ChunkHeaderSize, length));
        }
        catch (IOException)
        {
            read = 0;
        }

        outbox.Built(PacketType.BulkChunk, packet, Bulk.ChunkHeaderSize + read);
    }

    private void TickIncoming(DateTime now, Outbox outbox, List<BulkDelivery> deliveries, List<string> notes)
    {
        foreach (var incoming in _incoming.Values.ToArray())
        {
            if (now - incoming.LastActivity > Bulk.IncomingIdleTimeout)
            {
                // Nothing for thirty seconds. The temporary file goes with it: a transfer
                // somebody walked away from must not leave four gigabytes in their
                // Downloads folder under a name they have never seen.
                _incoming.Remove(incoming.TransferId);
                incoming.Dispose();
                continue;
            }

            // A streamed object is finished here rather than where its last chunk landed.
            // Verifying it can mean hashing whatever the incremental hash has not caught up
            // with, and seconds of that on the socket thread is the microphone cutting out.
            if (incoming.IsComplete && incoming.Streamed)
            {
                var completion = Complete(incoming, now);
                if (completion.Delivery is not null) deliveries.Add(completion.Delivery);
                if (completion.Done is not null) outbox.Add(PacketType.BulkDone, completion.Done);
                if (completion.Ack is not null) outbox.Add(PacketType.BulkAck, completion.Ack);
                if (completion.Note is not null) notes.Add(completion.Note);
                if (completion.Ended) continue;
            }

            if (now - incoming.LastAckAt < Bulk.AckInterval) continue;
            incoming.LastAckAt = now;
            outbox.Add(PacketType.BulkAck, BulkCodec.WriteAck(incoming.BuildAck(accepted: true)));
        }
    }

    /// <summary>What finishing one object produced. A struct: none of it is worth a heap.</summary>
    private readonly record struct Completion(
        BulkDelivery? Delivery, byte[]? Done, byte[]? Ack, string? Note, bool Ended);

    /// <summary>
    /// The last hole has filled. Either the hash matches and the object is handed over, or
    /// it does not and every chunk is asked for again — «молчаливой порчи не бывает».
    /// Called with the lock held.
    /// </summary>
    private Completion Complete(Incoming incoming, DateTime now)
    {
        bool matched;
        try
        {
            matched = incoming.Verify();
        }
        catch (IOException ex)
        {
            _incoming.Remove(incoming.TransferId);
            incoming.Dispose();
            return new Completion(null, null, null, Loc.F(Strings.Log_Bulk_WriteFailed, ex.Message), Ended: true);
        }

        if (matched)
        {
            var delivery = incoming.ToDelivery();
            _incoming.Remove(incoming.TransferId);
            _finished[incoming.TransferId] = now;
            // After ToDelivery, so that disposing no longer deletes the file the object was
            // streamed into: it belongs to whoever is about to be handed it.
            incoming.Dispose();
            return new Completion(delivery, BulkCodec.WriteDone(incoming.TransferId), null, null, Ended: true);
        }

        incoming.HashFailures++;
        if (incoming.HashFailures >= MaxHashFailures)
        {
            // Against a peer that damages the object the same way every time, the loop
            // would otherwise never end.
            _incoming.Remove(incoming.TransferId);
            incoming.Dispose();
            return new Completion(null, null, null, Strings.Log_Bulk_GivingUp, Ended: true);
        }

        incoming.Forget();
        incoming.LastAckAt = now;
        return new Completion(
            null,
            null,
            BulkCodec.WriteAck(incoming.BuildAck(accepted: true)),
            Loc.F(Strings.Log_Bulk_HashFailed, incoming.HashFailures),
            Ended: false);
    }

    private static BulkAssembly Stage(string folder, BulkOffer offer)
    {
        Directory.CreateDirectory(folder);
        return BulkAssembly.InFile(
            System.IO.Path.Combine(folder, Bulk.PartialName(offer.TransferId)),
            offer.Size,
            offer.ChunkCount);
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

    /// <summary>
    /// Packets built while the channel is locked and put on the wire once it is not.
    ///
    /// <para>
    /// Chunk packets come out of a pool and go back into it the moment they are sent. A
    /// four-gigabyte object is four million of them, and an array each is four gigabytes
    /// through the collector for one file — on the thread that is also answering acks.
    /// Everything else here is rare enough to be an ordinary array.
    /// </para>
    /// </summary>
    private sealed class Outbox
    {
        private readonly List<Packet> _packets = [];

        public void Add(PacketType type, byte[] payload) =>
            _packets.Add(new Packet(type, payload, payload.Length, Pooled: false));

        /// <summary>A buffer to build a packet in. Handed back through <see cref="Built"/>.</summary>
        public byte[] Rent(int size) => ArrayPool<byte>.Shared.Rent(size);

        public void Built(PacketType type, byte[] buffer, int length) =>
            _packets.Add(new Packet(type, buffer, length, Pooled: true));

        public void Flush(Action<PacketType, ReadOnlyMemory<byte>> send)
        {
            foreach (var packet in _packets)
            {
                try
                {
                    send(packet.Type, packet.Buffer.AsMemory(0, packet.Length));
                }
                finally
                {
                    if (packet.Pooled) ArrayPool<byte>.Shared.Return(packet.Buffer);
                }
            }
            _packets.Clear();
        }

        private readonly record struct Packet(PacketType Type, byte[] Buffer, int Length, bool Pooled);
    }

    /// <summary>One object we are pushing.</summary>
    private sealed class Outgoing : IDisposable
    {
        private readonly BulkSource _source;

        /// <summary>
        /// The holes an ack named one by one. At most 257 of them, which is all one ack can
        /// carry.
        /// </summary>
        private readonly Queue<uint> _listed = new();

        private readonly HashSet<uint> _queued = [];

        /// <summary>
        /// Which chunks have ever been put on the wire. A bit each, for the same reason the
        /// other end keeps one: four million of them is the worst case, and the alternative
        /// is a full sweep of the object whenever an ack runs out of room.
        /// </summary>
        private readonly ChunkBitmap _everSent;

        /// <summary>
        /// Where the sweep past the end of an ack's list has got to. A cursor, not a queue:
        /// for a four-million-chunk object the queue would be four million entries.
        /// </summary>
        private uint _sweep;

        private uint _sweepEnd;

        public Outgoing(uint id, BulkKind kind, BulkFormat format, BulkSource source, string description)
        {
            _source = source;
            Id = id;
            Offer = new BulkOffer(
                id, kind, format, (uint)source.Length, Bulk.ChunkCountFor(source.Length), source.Hash, description);
            _everSent = new ChunkBitmap(Offer.ChunkCount);
        }

        public uint Id { get; }

        public BulkOffer Offer { get; }

        public bool Accepted { get; set; }
        public int OfferAttempts { get; set; }
        public DateTime LastOfferAt { get; set; }
        public DateTime LastAckAt { get; set; }
        public DateTime LastSendAt { get; set; }

        /// <summary>Chunks handed to the socket so far, for the progress bar only.</summary>
        public uint ChunksSent { get; set; }

        /// <summary>
        /// The furthest chunk ever put on the wire. Everything past it is not a hole, only
        /// a part of the object whose turn has not come.
        /// </summary>
        public uint HighestSent { get; private set; }

        /// <summary>
        /// What <see cref="HighestSent"/> was when the last ack came in. A hole below it
        /// has had an ack interval and a round trip to turn up in, and did not.
        /// </summary>
        public uint Frontier { get; set; }

        public bool HasQueued => _listed.Count > 0 || _sweep < _sweepEnd;

        /// <summary>Back to square one: offer it again and wait to be told what to send.</summary>
        public void Renegotiate()
        {
            Accepted = false;
            OfferAttempts = 0;
            LastOfferAt = DateTime.MinValue;
            ChunksSent = 0;
            HighestSent = 0;
            Frontier = 0;
            // The peer that had these chunks is gone, so as far as the new one is
            // concerned none of them has ever been sent.
            _everSent.Clear();
            Clear();
        }

        /// <summary>
        /// Turns an ack into a send list: every hole it names, and then — when it ran out of
        /// room to name them — the chunks past the last one it named that have never been
        /// sent at all.
        ///
        /// <para>
        /// A full list is «there may be more». Taking that literally and starting the whole
        /// object again from the first hole is what the contract used to spell out, and it
        /// is expensive: the macOS side measured it at 1.75 times the chunks on the wire at
        /// one per cent loss, and on a four-gigabyte object one early loss would mean
        /// re-sending four gigabytes every 200 ms.
        /// </para>
        ///
        /// <para>
        /// Answering the named stretch exactly and otherwise only moving forward is safe
        /// because the other end keeps reporting: a hole it had no room to name this time is
        /// named in one of the acks that follow, as the holes in front of it are filled. So
        /// every ack either repairs what it named or puts chunks on the wire that have never
        /// been sent, and the transfer cannot sit still. It is a local decision either way —
        /// nothing in the format says which one a machine picks — and both ends now pick
        /// this one.
        /// </para>
        /// </summary>
        public void Rebuild(BulkAck ack)
        {
            Clear();
            if (ack.FirstMissing >= Offer.ChunkCount) return;

            Enqueue(ack.FirstMissing);
            var named = ack.FirstMissing;
            foreach (var index in ack.Missing)
            {
                if (index >= Offer.ChunkCount) continue;
                Enqueue(index);
                if (index > named) named = index;
            }

            if (!ack.Truncated) return;

            _sweep = named + 1;
            _sweepEnd = Offer.ChunkCount;
        }

        public bool TryDequeue(out uint index)
        {
            if (_listed.TryDequeue(out index))
            {
                _queued.Remove(index);
                Noted(index);
                return true;
            }

            if (_sweep < _sweepEnd)
            {
                // Past what the ack could name, only what has never been sent. Anything
                // already sent and still missing out there is a hole the other end will
                // name once the ones in front of it are filled, and sending it again on a
                // guess is the cost this avoids.
                _sweep = _everSent.NextClear(_sweep);
                if (_sweep < _sweepEnd)
                {
                    index = _sweep++;
                    Noted(index);
                    return true;
                }

                _sweep = _sweepEnd;
            }

            index = 0;
            return false;
        }

        public int Read(uint index, Span<byte> into) => _source.Read(index, into);

        public BulkResult Result(BulkOutcome outcome) => new(
            Id, Offer.Kind, Offer.Format, Offer.Size, Offer.Hash, Offer.Description, outcome);

        public void Dispose() => _source.Dispose();

        private void Clear()
        {
            _listed.Clear();
            _queued.Clear();
            _sweep = 0;
            _sweepEnd = 0;
        }

        private void Enqueue(uint index)
        {
            if (_queued.Add(index)) _listed.Enqueue(index);
        }

        private void Noted(uint index)
        {
            _everSent.Add(index);
            if (index > HighestSent) HighestSent = index;
            if (ChunksSent < Offer.ChunkCount) ChunksSent++;
        }
    }

    /// <summary>One object being assembled, and the protocol state around it.</summary>
    private sealed class Incoming(BulkOffer offer, BulkAssembly assembly) : IDisposable
    {
        public uint TransferId { get; } = offer.TransferId;
        public BulkKind Kind { get; } = offer.Kind;
        public BulkFormat Format { get; } = offer.Format;
        public uint Size { get; } = offer.Size;
        public uint ChunkCount { get; } = offer.ChunkCount;
        public byte[] Hash { get; } = offer.Hash;
        public string Description { get; } = offer.Description;

        public uint HaveCount => assembly.Have.Have;
        public int HashFailures { get; set; }
        public DateTime LastActivity { get; set; }
        public DateTime LastAckAt { get; set; }

        public bool IsComplete => assembly.IsComplete;

        /// <summary>True when it is going into a file rather than into memory.</summary>
        public bool Streamed => assembly.TempPath is not null;

        public void Store(uint index, ReadOnlySpan<byte> data) => assembly.Store(index, data);

        public bool Verify() => assembly.Verify(Hash);

        public void Forget() => assembly.Forget();

        public void Dispose() => assembly.Dispose();

        /// <summary>
        /// The first hole, then up to 256 more. When nothing is missing the first-missing
        /// field points one past the last chunk — the contract has no other way to say
        /// «ничего не нужно», and the other end reads anything at or past the count as
        /// «жду BULK_DONE».
        /// </summary>
        public BulkAck BuildAck(bool accepted)
        {
            var first = assembly.Have.FirstMissing();
            if (first >= ChunkCount) return new BulkAck(TransferId, accepted, ChunkCount, []);

            Span<uint> listed = stackalloc uint[Bulk.MaxMissingListed];
            var count = assembly.Have.ListMissing(first, listed);
            return new BulkAck(TransferId, accepted, first, listed[..count].ToArray());
        }

        /// <summary>
        /// Hands the object over. Whoever takes it owns what it holds from here — for a
        /// streamed object that is the temporary file, which is nobody else's to delete
        /// once this has been called.
        /// </summary>
        public BulkDelivery ToDelivery()
        {
            assembly.Release();
            return new BulkDelivery(
                TransferId, Kind, Format, Size, Hash, Description, assembly.Bytes, assembly.TempPath);
        }
    }
}
