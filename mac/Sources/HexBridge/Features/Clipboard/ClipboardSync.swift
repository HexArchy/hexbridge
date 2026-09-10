import CryptoKit
import Foundation

/// One thing the clipboard can hold, in the only two shapes this feature carries:
/// UTF-8 text and a PNG. Everything else on the pasteboard is left alone.
struct ClipboardItem {
    let format: BulkFormat
    let bytes: [UInt8]
    let hash: [UInt8]

    init(format: BulkFormat, bytes: [UInt8]) {
        self.format = format
        self.bytes = bytes
        self.hash = Array(SHA256.hash(data: bytes))
    }

    /// What the UI is told about a transfer. Deliberately the shape and the size
    /// and nothing else: the description travels over the wire and lands in a
    /// log, and the clipboard is where passwords live.
    var describe: String {
        switch format {
        case .utf8Text: return "текст, \(Self.size(bytes.count))"
        case .png: return "изображение, \(Self.size(bytes.count))"
        case .opaque: return "данные, \(Self.size(bytes.count))"
        }
    }

    static func size(_ bytes: Int) -> String {
        if bytes >= 1024 * 1024 {
            return String(format: "%.1f МБ", Double(bytes) / (1024 * 1024))
        }
        if bytes >= 1024 {
            return String(format: "%.1f КБ", Double(bytes) / 1024)
        }
        return "\(bytes) Б"
    }
}

/// The system clipboard, reduced to what synchronising it needs.
///
/// A change counter rather than a notification because macOS has none —
/// `NSPasteboard` offers `changeCount` and nothing else — and because a counter
/// is trivial to fake in a test.
protocol ClipboardSurface: AnyObject {
    /// Bumped by the system on every change, by anyone, including us.
    var changeCount: Int { get }
    /// nil when the pasteboard holds nothing we carry, or could not be read.
    func read() -> ClipboardItem?
    func write(_ item: ClipboardItem)
}

/// The loop breaker.
///
/// Two machines that mirror each other's clipboard will, done naively, hand the
/// same object back and forth forever: we paste what arrived, our own watcher
/// sees the change, and off it goes again. The contract's hash is enough to stop
/// that, but only if this side knows which hashes describe *its own current
/// clipboard* — which is not the same thing as remembering the last few objects
/// seen. Copying A, then B, then A again has to send A a second time, and a
/// suppression list would eat it.
///
/// So there are exactly two facts here: what our clipboard currently holds (one
/// or two hashes, because writing a PNG and reading it back does not always give
/// the same bytes), and what the peer is known to hold. Nothing else.
///
/// `owns` is asked from the socket queue while `poll` runs on the main thread,
/// so the two facts live behind a lock.
final class ClipboardSync: @unchecked Sendable {
    private let surface: ClipboardSurface
    private let lock = NSLock()
    private var owned: [[UInt8]] = []
    private var peerHash: [UInt8]?
    private var lastChangeCount = Int.min

    init(surface: ClipboardSurface) {
        self.surface = surface
    }

    /// The answer to a BULK_OFFER: we already hold exactly these bytes, so
    /// nothing needs to cross the wire. Safe to call from any thread.
    func owns(_ hash: [UInt8]) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return owned.contains(hash)
    }

    /// Reads the clipboard if it has changed and returns what should be offered
    /// to the peer, or nil when there is nothing new to say.
    func poll() -> ClipboardItem? {
        let change = surface.changeCount
        guard change != lastChangeCount else { return nil }
        lastChangeCount = change

        guard let item = surface.read() else { return nil }

        // Still the content we already know about — the counter moved for some
        // other format, or this is the echo of our own paste.
        guard !owns(item.hash) else { return nil }

        lock.lock()
        owned = [item.hash]
        let peer = peerHash
        lock.unlock()

        // The peer sent us this in the first place, or already has it: offering
        // it back is the second half of the loop, and this is where it stops.
        if peer == item.hash { return nil }
        return item
    }

    /// Puts a received object on the clipboard without letting our own watcher
    /// bounce it back. The read-back matters: both systems re-encode some
    /// formats, so the bytes that come out are not always the bytes that went
    /// in, and it is the ones that come out that our watcher will see.
    func apply(_ item: ClipboardItem) {
        lock.lock()
        owned = [item.hash]
        peerHash = item.hash
        lock.unlock()

        surface.write(item)

        lastChangeCount = surface.changeCount
        guard let readBack = surface.read() else { return }

        lock.lock()
        if !owned.contains(readBack.hash) { owned.append(readBack.hash) }
        lock.unlock()
    }

    /// The peer confirmed it holds this object, so we must not offer it again.
    func notePeerHas(_ hash: [UInt8]) {
        lock.lock()
        peerHash = hash
        lock.unlock()
    }

    /// The link came back up and nothing is known about the other side any more.
    /// What our own clipboard holds is still true, so that is kept.
    func forgetPeer() {
        lock.lock()
        peerHash = nil
        lock.unlock()
    }
}
