import Foundation
import Network
import Observation

/// Bonjour search for a HexBridge receiver on the local network (§9.3, step 1а).
///
/// ⚠️ The Windows side does not advertise this service yet — it is being
/// written separately. The browser is real and works; until the receiver
/// publishes `_hexbridge._udp`, it will honestly report an empty list rather
/// than pretend. The UI says so in as many words.
@Observable
@MainActor
final class ReceiverDiscovery {
    struct Found: Identifiable, Equatable {
        let id: String
        let name: String
        /// Filled in once the endpoint is resolved; nil while resolving.
        var host: String?
        var port: UInt16?
    }

    /// The service type the two sides agree on. UDP because the data channel
    /// is UDP; the receiver advertises the same port it listens on.
    static let serviceType = "_hexbridge._udp"

    private(set) var results: [Found] = []
    private(set) var searching = false
    /// nil until at least one browse cycle has completed.
    private(set) var lastError: String?

    private var browser: NWBrowser?

    func start() {
        guard browser == nil else { return }
        searching = true
        lastError = nil
        results = []

        let parameters = NWParameters()
        parameters.includePeerToPeer = false
        let browser = NWBrowser(
            for: .bonjour(type: Self.serviceType, domain: nil),
            using: parameters
        )

        browser.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                guard let self else { return }
                switch state {
                case .failed(let error):
                    self.lastError = error.localizedDescription
                    self.searching = false
                case .ready:
                    self.lastError = nil
                default:
                    break
                }
            }
        }

        browser.browseResultsChangedHandler = { [weak self] found, _ in
            Task { @MainActor in
                self?.apply(found)
            }
        }

        browser.start(queue: .main)
        self.browser = browser
    }

    func stop() {
        browser?.cancel()
        browser = nil
        searching = false
    }

    private func apply(_ found: Set<NWBrowser.Result>) {
        results = found.compactMap { result in
            guard case .service(let name, _, _, _) = result.endpoint else { return nil }
            var entry = Found(id: name, name: name)
            // A Bonjour result carries the name; the address needs a resolve,
            // which only happens when a connection is attempted. The wizard
            // therefore still asks for the confirmation code, and the address
            // comes back inside the pairing payload.
            if case .hostPort(let host, let port) = result.endpoint {
                entry.host = "\(host)"
                entry.port = port.rawValue
            }
            return entry
        }
        .sorted { $0.name < $1.name }
    }

    /// Resolves a browse result to an address by opening (and immediately
    /// dropping) a connection to it. `NWBrowser` never hands out addresses on
    /// its own, and the short-code exchange needs one.
    static func resolve(serviceName: String, timeout: TimeInterval = 3) async -> (host: String, port: UInt16)? {
        let endpoint = NWEndpoint.service(
            name: serviceName, type: serviceType, domain: "local.", interface: nil
        )
        let connection = NWConnection(to: endpoint, using: .udp)

        return await withCheckedContinuation { continuation in
            var resumed = false
            let finish: ((host: String, port: UInt16)?) -> Void = { value in
                guard !resumed else { return }
                resumed = true
                connection.cancel()
                continuation.resume(returning: value)
            }

            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if case .hostPort(let host, let port) = connection.currentPath?.remoteEndpoint {
                        // `NWEndpoint.Host` prints IPv6 with a `%en0` scope; the
                        // exchange client needs it without one.
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
    }
}
