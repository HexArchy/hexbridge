import Foundation

/// Which chunks of one object have arrived, or have been put on the wire.
///
/// Its own target rather than a file inside the app, for the same reason as
/// `HexBridgeDiscovery` and `HexBridgeFiles`: the app target drags in AppKit,
/// SwiftUI, CoreAudio and a static libopus, none of which can be linked into a
/// test bundle, and the bookkeeping below is the part of a four-gigabyte
/// transfer that is worth proving rather than hoping about. This is Foundation
/// and nothing else.
///
/// A bit a chunk, packed into `UInt64` words. The obvious implementations cost
/// far more: a dictionary of chunk number to bytes costs the whole object, and
/// an array of `Bool` costs a byte a chunk. At the ceiling docs/PROTOCOL.md
/// fixes — 4 194 304 chunks for a 4 GiB object — that is four gigabytes or four
/// megabytes against this one's 512 KB. The bitmap is what makes such a
/// transfer a question about the disk rather than about the RAM.
///
/// A struct, and it is meant to be held as a stored property and mutated in
/// place. Copying one copies half a megabyte, so it is never passed by value.
public struct ChunkMap {

    /// How many chunks one word of the bitmap carries. The scan below skips a
    /// whole word at a time, and this is the width it skips by.
    public static let chunksPerWord = 64

    /// How many chunks the object has. Fixed at birth: it comes from the offer.
    public let count: Int

    private var words: [UInt64]

    /// How many chunks are marked. Kept rather than counted, because
    /// ``isComplete`` is asked once for every packet that arrives and counting
    /// four million bits at that rate is the whole cost of the transfer.
    public private(set) var present: Int

    /// Nothing below this index is missing any more.
    ///
    /// Only ever moves forward, and only ``missing(limit:)`` moves it. Without
    /// it every acknowledgement would start its search for the first hole at
    /// chunk zero, and on an object that is nearly complete that is a walk over
    /// the whole bitmap five times a second for nothing.
    private var settled: Int

    public init(count: Int) {
        precondition(count >= 0, "a chunk map cannot have a negative number of chunks")
        self.count = count
        self.words = [UInt64](repeating: 0, count: (count + Self.chunksPerWord - 1) / Self.chunksPerWord)
        self.present = 0
        self.settled = 0
    }

    public var isComplete: Bool { present == count }

    /// What the bitmap costs in memory. Exposed because "a bit a chunk rather
    /// than a byte a byte" is the claim this type exists to make, and a claim
    /// of that kind should be a test rather than a comment.
    public var storageBytes: Int { words.count * MemoryLayout<UInt64>.size }

    public func contains(_ index: Int) -> Bool {
        guard index >= 0, index < count else { return false }
        return words[index / Self.chunksPerWord] & Self.bit(index) != 0
    }

    /// Marks a chunk. Returns false when it was already marked, or when the
    /// index is not a chunk of this object at all.
    ///
    /// The answer is the point: it tells a duplicate chunk from a new one
    /// without a second lookup, and a duplicate must not be written to disk
    /// twice or counted twice.
    @discardableResult
    public mutating func insert(_ index: Int) -> Bool {
        guard index >= 0, index < count else { return false }
        let word = index / Self.chunksPerWord
        let bit = Self.bit(index)
        guard words[word] & bit == 0 else { return false }
        words[word] |= bit
        present += 1
        return true
    }

    /// Everything that arrived is wrong and will be asked for again.
    public mutating func removeAll() {
        for index in words.indices { words[index] = 0 }
        present = 0
        settled = 0
    }

    /// The lowest `limit` chunk numbers that are not marked, in order.
    ///
    /// A word at a time rather than a bit at a time: a word that is entirely
    /// marked is one comparison instead of sixty-four, which is what keeps a
    /// full sweep of a four-million-chunk object down to sixty-five thousand
    /// steps in the worst case and to almost nothing in the ordinary one, where
    /// the holes are at the front and the search stops as soon as it has enough
    /// of them.
    ///
    /// `mutating` because it is also what advances ``settled``. That is not a
    /// side effect worth hiding behind a second call: the scan has just proved
    /// where the first hole is, and throwing that away would mean finding it
    /// again from zero on the next acknowledgement.
    public mutating func missing(limit: Int) -> [Int] {
        guard limit > 0, present < count else { return [] }

        var found = [Int]()
        found.reserveCapacity(limit)

        let firstWord = settled / Self.chunksPerWord
        var word = firstWord
        while word < words.count, found.count < limit {
            // The bits that are *not* marked. Two corrections: the word the
            // search starts in may hold chunks below `settled`, which are known
            // to be marked, and the last word holds bits past the end of the
            // object, which are not chunks at all and must never be asked for.
            var holes = ~words[word]
            if word == firstWord {
                holes &= ~Self.mask(below: settled % Self.chunksPerWord)
            }
            if word == words.count - 1 {
                holes &= Self.tailMask(count: count)
            }

            while holes != 0, found.count < limit {
                let index = word * Self.chunksPerWord + holes.trailingZeroBitCount
                found.append(index)
                holes &= holes - 1
            }
            if holes == 0 { word += 1 }
        }

        // Everything between the old mark and the first hole just turned out to
        // be marked, so the next search may start there.
        settled = found.first ?? count
        return found
    }

    // MARK: - Bits

    private static func bit(_ index: Int) -> UInt64 {
        UInt64(1) << UInt64(index % chunksPerWord)
    }

    /// Ones in the `bits` lowest positions, zeroes above.
    private static func mask(below bits: Int) -> UInt64 {
        bits == 0 ? 0 : (UInt64.max >> UInt64(chunksPerWord - bits))
    }

    /// Which bits of the last word are chunks. All of them when the count
    /// divides evenly, and the rest are padding that is permanently unmarked —
    /// reporting one as a hole would have us ask for a chunk that does not
    /// exist, which the contract's own «nothing is missing» encoding exists to
    /// avoid.
    private static func tailMask(count: Int) -> UInt64 {
        let used = count % chunksPerWord
        return used == 0 ? UInt64.max : mask(below: used)
    }
}
