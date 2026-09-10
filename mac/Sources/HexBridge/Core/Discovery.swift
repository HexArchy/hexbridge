import Foundation
import HexBridgeDiscovery
import Network
import Observation

/// Bonjour search for a HexBridge host on the local network (PROTOCOL.md,
/// «Автопоиск хоста»).
///
/// The browser finds every HexBridge on the link. Which of them this Mac is
/// allowed to talk to is not decided here — that is `DiscoveryMatch`, and it
/// decides on the tag alone. This file's job ends at «вот что видно в сети и вот
/// их метки».
///
/// Everything runs on `queue`, callbacks included: `NWBrowser` delivers results
/// to whichever queue it was started on, and the resolve below finishes on
/// another one.
final class DiscoveryBrowser {
    /// The service type the two sides agree on. UDP, because the data channel is.
    static let serviceType = "_hexbridge._udp"

    /// How long to wait for one browse result to turn into an address.
    private static let resolveTimeout: TimeInterval = 3

    private let queue = DispatchQueue(label: "ru.hexarch.hexbridge.discovery")
    private var browser: NWBrowser?

    /// Instance name → host. Keyed by instance name because that is the only
    /// identity Bonjour guarantees; the display name inside TXT is chosen by the
    /// other machine, and two of them may well collide.
    private var found: [String: DiscoveredHost] = [:]

    /// Addresses learned by resolving, kept across browse cycles so a result
    /// that reappears is usable at once instead of blank for three seconds.
    private var addresses: [String: (host: String, port: UInt16)] = [:]
    private var resolving: Set<String> = []

    /// Called on `queue` whenever the visible set changes.
    var onChange: (([DiscoveredHost]) -> Void)?

    /// Called on `queue` when the browser itself fails; nil clears a past error.
    var onError: ((String?) -> Void)?

    func start() {
        queue.async { [weak self] in self?.startLocked() }
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            self.browser?.cancel()
            self.browser = nil
            self.found = [:]
            self.resolving = []
            self.onChange?([])
        }
    }

    private func startLocked() {
        guard browser == nil else { return }

        let parameters = NWParameters()
        parameters.includePeerToPeer = false

        // `bonjourWithTXTRecord`, not `bonjour`: the tag lives in TXT, and a
        // browse without it hands back a list of names — exactly the thing that
        // must never be enough to decide anything.
        let browser = NWBrowser(
            for: .bonjourWithTXTRecord(type: Self.serviceType, domain: nil),
            using: parameters
        )

        browser.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            self.queue.async {
                switch state {
                case .failed(let error):
                    self.onError?(error.localizedDescription)
                case .ready:
                    self.onError?(nil)
                default:
                    break
                }
            }
        }

        browser.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self else { return }
            self.queue.async { self.apply(results) }
        }

        browser.start(queue: queue)
        self.browser = browser
    }

    /// Turns one browse pass into hosts, and asks for an address for each one
    /// that does not have one yet.
    private func apply(_ results: Set<NWBrowser.Result>) {
        var next: [String: DiscoveredHost] = [:]

        for result in results {
            guard case .service(let name, _, _, _) = result.endpoint else { continue }
            guard case .bonjour(let record) = result.metadata else {
                // A host advertising no TXT at all publishes no version, no port
                // and no tag: nothing to dial and nothing to compare.
                continue
            }
            guard var host = DiscoveryTxt.parse(
                Self.values(of: record), id: name, fallbackName: name
            ) else { continue }

            if let known = addresses[name] { host.address = known.host }
            next[name] = host
        }

        found = next
        onChange?(sorted())

        for name in next.keys where addresses[name] == nil {
            resolve(name)
        }
    }

    private func resolve(_ name: String) {
        guard !resolving.contains(name) else { return }
        resolving.insert(name)

        Self.address(ofService: name, timeout: Self.resolveTimeout) { [weak self] resolved in
            guard let self else { return }
            self.queue.async {
                self.resolving.remove(name)
                guard let resolved else { return }
                self.addresses[name] = resolved
                guard var host = self.found[name] else { return }
                host.address = resolved.host
                self.found[name] = host
                self.onChange?(self.sorted())
            }
        }
    }

    /// Resolved hosts first, then by name: the list is read top-down, and a row
    /// with no address yet is a row nothing can be done with.
    private func sorted() -> [DiscoveredHost] {
        found.values.sorted { left, right in
            if left.address.isEmpty != right.address.isEmpty { return !left.address.isEmpty }
            return left.name.localizedCaseInsensitiveCompare(right.name) == .orderedAscending
        }
    }

    /// Flattens an `NWTXTRecord` into the `[key: value]` the parser wants.
    ///
    /// A DNS-SD attribute may legally have no value, and one may carry bytes
    /// that are not text. Neither is anything the four keys we read can be, so
    /// both become nothing rather than an empty string — an empty string would
    /// be a tag, and an empty tag compares equal to another empty tag.
    private static func values(of record: NWTXTRecord) -> [String: String] {
        var values: [String: String] = [:]
        for (key, entry) in record {
            switch entry {
            case .string(let text):
                values[key] = text
            case .data(let data):
                if let text = String(data: data, encoding: .utf8) { values[key] = text }
            default:
                break
            }
        }
        return values
    }

    /// Resolves a browse result to an address by opening — and immediately
    /// dropping — a connection to it. `NWBrowser` never hands out addresses on
    /// its own, and both the short-code exchange and the audio socket need one.
    static func address(
        ofService name: String,
        timeout: TimeInterval,
        completion: @escaping ((host: String, port: UInt16)?) -> Void
    ) {
        let endpoint = NWEndpoint.service(
            name: name, type: serviceType, domain: "local.", interface: nil
        )
        let connection = NWConnection(to: endpoint, using: .udp)
        let lock = NSLock()
        var finished = false

        let finish: ((host: String, port: UInt16)?) -> Void = { value in
            lock.lock()
            let alreadyDone = finished
            finished = true
            lock.unlock()
            guard !alreadyDone else { return }
            connection.cancel()
            completion(value)
        }

        connection.stateUpdateHandler = { state in
            switch state {
            case .ready:
                if case .hostPort(let host, let port) = connection.currentPath?.remoteEndpoint {
                    // `NWEndpoint.Host` prints IPv6 with a `%en0` scope; nothing
                    // downstream wants one.
                    let text = "\(host)".split(separator: "%").first.map(String.init) ?? "\(host)"
                    finish((text, port.rawValue))
                } else {
                    finish(nil)
                }
            case .failed, .cancelled:
                finish(nil)
            default:
                break
            }
        }
        connection.start(queue: .global(qos: .userInitiated))
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { finish(nil) }
    }

    /// One-shot scan for the command line. Prints nothing itself; hands back
    /// whatever was visible after `seconds`.
    static func scan(seconds: TimeInterval) async -> [DiscoveredHost] {
        let browser = DiscoveryBrowser()
        let box = Box()
        browser.onChange = { hosts in box.set(hosts) }
        browser.start()
        try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        browser.stop()
        return box.get()
    }

    /// A lock around one array, so the browse queue and the caller can share it
    /// without either of them being an actor.
    private final class Box: @unchecked Sendable {
        private let lock = NSLock()
        private var hosts: [DiscoveredHost] = []

        func set(_ value: [DiscoveredHost]) {
            lock.lock()
            hosts = value
            lock.unlock()
        }

        func get() -> [DiscoveredHost] {
            lock.lock()
            defer { lock.unlock() }
            return hosts
        }
    }
}

/// The browser as the interface sees it, plus the one decision it drives.
///
/// The rule in full: this Mac dials a host it found by itself **only** when that
/// host's tag equals the tag of its own key. Everything else in the list is
/// shown and never dialled — including when the list holds exactly one host, and
/// including when this Mac has no key at all. Pairing still needs the short code
/// off the host's screen; autodiscovery only saves typing an address.
@Observable
@MainActor
final class ReceiverDiscovery {
    /// Every HexBridge visible on the link, ours and strangers' alike.
    private(set) var hosts: [DiscoveredHost] = []
    private(set) var searching = false
    private(set) var lastError: String?

    /// The verdict over the current list, recomputed whenever either side of it
    /// changes.
    private(set) var choice = DiscoveryChoice.idle

    /// Tag of this Mac's own key, or nil when it has none. Setting it re-decides
    /// at once: a Mac that has just been paired should follow its host without
    /// waiting for another browse cycle.
    var ownTag: String? {
        didSet {
            guard ownTag != oldValue else { return }
            decide()
        }
    }

    /// The target the decision is compared against; the shell keeps it in step
    /// with the config.
    var currentTarget: String = ""

    /// Called with a new `host:port` when the matching host turns out to be
    /// somewhere other than the config says. Never called for a stranger, and
    /// never called with a key — only the address moves.
    var onRetarget: ((String) -> Void)?

    private let browser = DiscoveryBrowser()

    init() {
        browser.onChange = { [weak self] hosts in
            Task { @MainActor in self?.receive(hosts) }
        }
        browser.onError = { [weak self] error in
            Task { @MainActor in
                self?.lastError = error
                if error != nil { self?.searching = false }
            }
        }
    }

    func start() {
        guard !searching else { return }
        searching = true
        lastError = nil
        browser.start()
    }

    func stop() {
        searching = false
        browser.stop()
        hosts = []
        decide()
    }

    private func receive(_ found: [DiscoveredHost]) {
        hosts = found
        decide()
    }

    private func decide() {
        choice = DiscoveryMatch.choose(ownTag: ownTag, hosts: hosts)
        guard let target = DiscoveryMatch.retarget(current: currentTarget, choice: choice) else { return }
        currentTarget = target
        onRetarget?(target)
    }

    /// True when this row is the host this Mac is paired with. The only thing
    /// in the interface allowed to say «свой».
    func isOurs(_ host: DiscoveredHost) -> Bool {
        DiscoveryTag.same(ownTag, host.tag)
    }

    /// What the list says about one row, in the pairing window.
    ///
    /// Deliberately plain about strangers rather than hiding them: a user who
    /// cannot see the neighbour's HexBridge has no way to understand why the one
    /// host on screen is not the one being connected to.
    func note(for host: DiscoveredHost) -> String {
        if DiscoveryTag.same(ownTag, host.tag) { return "это ваш ПК" }
        if host.tag == nil { return "ещё ни с кем не связан" }
        return "связан с другим Mac"
    }
}
