import Foundation

/// How fast chunks are put on the wire, and how that speed is arrived at.
///
/// docs/PROTOCOL.md leaves pacing to each side and says only what happens to a
/// side that sends too fast: the missing lists come back longer, and every
/// chunk it loses it sends twice. That is the whole signal here. A loop that
/// simply spins is not «unlimited» — it is a loop that overruns the socket, the
/// other machine's buffers and its disk, and then spends the time it saved
/// sending the same chunks again.
///
/// So the speed is found rather than declared: it grows while acknowledgements
/// come back clean and is cut when they do not. Additive increase and
/// multiplicative decrease, the oldest arrangement there is, and the right one
/// here — the cost of overshooting is paid in retransmission, so the climb has
/// to be patient and the retreat immediate.
///
/// A struct with no clock of its own: time arrives as a parameter, so every
/// constant below is reachable from a test without waiting for it.
public struct BulkPacer {

    // MARK: - The constants, and what each one is for

    /// Where a transfer starts, in chunks a second. A chunk is 1024 bytes, so
    /// this is about a megabyte a second: slow enough that no path anybody
    /// pairs two machines over notices it, fast enough that a small object is
    /// gone before the climb matters at all.
    public static let openingRate: Double = 1024

    /// The slowest it will ever go. Below about a quarter of a megabyte a
    /// second a large file stops being a transfer and becomes a leak, and a
    /// link bad enough to deserve less than this will not be rescued by
    /// sending less.
    public static let floorRate: Double = 256

    /// The fastest it will go with nothing else in the way.
    ///
    /// Not a policy — a guard. On a loopback or a quiet gigabit link nothing is
    /// ever lost, so the climb has no opposing force and would go on forever;
    /// one tick would then hand the socket a million chunks and the timer that
    /// drives it would never return. 262 144 chunks a second is 256 MB/s, well
    /// past the point where the socket and the disk, rather than the pacing,
    /// are what the transfer is waiting for.
    public static let hardCeiling: Double = 262_144

    /// What the rate is held to when the other machine is reached through a
    /// relay.
    ///
    /// A relay drops what exceeds its per-endpoint limit, and dropped chunks come
    /// back as holes — aiming above it is a way of making the transfer slower,
    /// not faster. The relay's own default is 20 000 packets a second, raised
    /// from 2000 once a file started riding the same socket; voice is 50 a
    /// second and a forwarded gamepad up to 250, and they share it, so this
    /// leaves them and the acknowledgements their room.
    ///
    /// Assuming the higher number is the safe direction to be wrong in. Against
    /// an older relay that still carries 2000, the loss that causes is exactly
    /// what the backoff reads, and the rate settles where the path really is.
    /// Assuming the lower one has no such feedback: it is simply slow for ever,
    /// and nothing says why.
    public static let relayCeiling: Double = 19_000

    /// Added to the rate for each acknowledgement that showed no loss.
    ///
    /// One acknowledgement is 200 ms, so a clean link gains about 5 MB/s for
    /// every second it stays clean: quick enough to fill a local network in a
    /// few seconds, gradual enough that it is the loss signal and not the climb
    /// that decides where the ceiling is.
    public static let increasePerCleanRound: Double = 1024

    /// What the rate is multiplied by on an acknowledgement that showed loss.
    ///
    /// 0.7 rather than the textbook half. The evidence here is one 200 ms
    /// window of a link that is also carrying voice and a gamepad, which is a
    /// noisier measurement than a congestion window; halving on it would leave
    /// the transfer crawling after a single burst of interference, and the
    /// climb back takes seconds.
    public static let decreaseFactor: Double = 0.7

    /// How much of a round may be missing before the round counts as lossy.
    ///
    /// Not zero, and this is the number that decides whether «unlimited» is
    /// worth having. One chunk missing out of twenty thousand is a link doing
    /// its job; treating it as congestion would pin every real network at the
    /// opening rate forever, because no real network is perfect. One in a
    /// hundred is where the resends start costing more than the speed is
    /// worth.
    public static let lossThreshold: Double = 0.01

    /// How long a burst the token bucket may accumulate, in seconds of sending.
    ///
    /// The bucket exists so the rate on the wire does not change when the
    /// caller's timer wobbles. It is shallow on purpose: a full second of
    /// credit at a high rate would arrive as one enormous burst the moment the
    /// timer was late, which is exactly the overrun the pacing is here to
    /// avoid.
    public static let burstWindow: TimeInterval = 0.05

    /// The smallest burst worth allowing, whatever the rate. At the floor rate
    /// a 50 ms window is thirteen chunks, and a bucket that shallow would let
    /// rounding rather than the rate decide how much goes out.
    public static let minimumBurst: Double = 16

    // MARK: - State

    /// Chunks a second, as it stands. Between ``floorRate`` and the effective
    /// ceiling at all times.
    public private(set) var chunksPerSecond: Double

    /// A ceiling asked for from outside — the setting, or the knowledge that a
    /// relay is in the path. Nil means the only ceiling is ``hardCeiling``.
    public var ceiling: Double? {
        didSet { clamp() }
    }

    /// Chunks that may go out now. Fractional, because the tick is 20 ms and
    /// most rates are not a whole number of chunks per tick.
    private var credit: Double = 0

    public init(ceiling: Double? = nil) {
        self.chunksPerSecond = Self.openingRate
        self.ceiling = ceiling
        clamp()
    }

    /// The ceiling actually in force.
    public var effectiveCeiling: Double {
        min(ceiling ?? Self.hardCeiling, Self.hardCeiling)
    }

    // MARK: - The bucket

    /// Time passed. Credit accrues at the current rate and stops at one burst.
    public mutating func advance(by elapsed: TimeInterval) {
        guard elapsed > 0 else { return }
        let burst = max(Self.minimumBurst, chunksPerSecond * Self.burstWindow)
        credit = min(credit + elapsed * chunksPerSecond, burst)
    }

    /// Spends one chunk's worth of credit, or says there is none.
    public mutating func take() -> Bool {
        guard credit >= 1 else { return false }
        credit -= 1
        return true
    }

    /// Throws away accumulated credit. Used when the socket is rebuilt: credit
    /// earned against a link that no longer exists is not credit.
    public mutating func rest() {
        credit = 0
    }

    // MARK: - The loop

    /// One acknowledgement's worth of evidence.
    ///
    /// `sent` is how many chunks went out since the previous acknowledgement,
    /// and `lost` how many of those the other machine is still asking for after
    /// having had a full round to receive them. A round with nothing to judge
    /// on — nothing was sent — changes nothing: silence is not evidence that
    /// the link is clean.
    public mutating func note(sent: Int, lost: Int) {
        guard sent > 0 else { return }
        if Double(lost) / Double(sent) > Self.lossThreshold {
            chunksPerSecond *= Self.decreaseFactor
        } else {
            chunksPerSecond += Self.increasePerCleanRound
        }
        clamp()
    }

    /// Back to the opening rate. A new transfer to a machine that has just
    /// appeared knows nothing about the path, and inheriting a speed that was
    /// right for a different one is how a transfer starts by overrunning it.
    public mutating func restart() {
        chunksPerSecond = Self.openingRate
        credit = 0
        clamp()
    }

    private mutating func clamp() {
        chunksPerSecond = min(max(chunksPerSecond, Self.floorRate), effectiveCeiling)
    }
}
