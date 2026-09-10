import CryptoKit
import Foundation
import HexBridgeText
import Network
import Observation

/// The "Check the link" run from DESIGN.md §9.4.
///
/// Six lines, appearing one after another with at least 250 ms between them:
/// six results arriving at once are a wall of text nobody reads, six results
/// arriving in sequence are a story.
@Observable
@MainActor
final class LinkCheck {
    struct Row: Identifiable {
        let id: Int
        let title: String
        var state: CheckRow.State = .pending
        var detail: String = ""
        /// Shown expanded under the row when it fails (§9.4 final paragraph).
        var explanation: String?
    }

    private(set) var rows: [Row] = LinkCheck.blank
    private(set) var running = false
    private(set) var finished = false
    /// Set once the run is over: the one sentence at the top of the result.
    private(set) var verdict: String?
    private(set) var verdictTone: Tone = .neutral

    /// Rebuilt on every run rather than held as a constant: the titles are
    /// words, and the language they are in can change between two runs.
    private static var blank: [Row] {
        [
            Row(id: 0, title: L.t("check.row.resolve")),
            Row(id: 1, title: L.t("check.row.packets")),
            Row(id: 2, title: L.t("check.row.keys")),
            Row(id: 3, title: L.t("check.row.accepts")),
            Row(id: 4, title: L.t("check.row.audio")),
            Row(id: 5, title: L.t("check.row.devices")),
        ]
    }

    private var task: Task<Void, Never>?

    func cancel() {
        task?.cancel()
        task = nil
        running = false
    }

    /// Throws away a finished run. Called when the interface language changes:
    /// the verdict and the six rows are sentences in the language they were
    /// worded in, and nothing would re-run the check on its own.
    func clear() {
        cancel()
        rows = Self.blank
        verdict = nil
        verdictTone = .neutral
        finished = false
    }

    /// `live` is the running pipeline, when there is one. Reusing it matters:
    /// opening a second socket to a receiver that is already streaming would
    /// look to it like a second Mac claiming the room.
    func run(config: Config, live: BridgeRuntime?, devices: DeviceBridge.Status?) {
        cancel()
        rows = Self.blank
        verdict = nil
        verdictTone = .neutral
        finished = false
        running = true

        let usesLive = live?.isRunning == true
            && live?.config.target == config.target
            && live?.config.psk == config.psk

        task = Task { [weak self] in
            await self?.perform(config: config, live: usesLive ? live : nil, devices: devices)
        }
    }

    // MARK: - The run

    private func perform(config: Config, live: BridgeRuntime?, devices: DeviceBridge.Status?) async {
        defer {
            running = false
            finished = true
        }

        let parts: (host: String, port: UInt16)
        do {
            parts = try config.endpointParts()
        } catch {
            await set(0, .failed, detail: L.t("check.noAddress"), explanation: L.t("check.noAddress.why"))
            await failRest(from: 1)
            conclude(bad: L.t("check.verdict.noAddress"))
            return
        }

        // 1. Address resolves.
        await mark(0, .running)
        let resolved = await Self.resolve(host: parts.host)
        guard let resolved else {
            await set(0, .failed, detail: L.t("check.unresolved"),
                      explanation: L.t("check.unresolved.why", parts.host))
            await failRest(from: 1)
            conclude(bad: L.t("check.verdict.unresolved"))
            return
        }
        await set(0, .ok, detail: resolved)

        // 2/3. Packets get through, and the key that decrypted the answer is
        // the same key on both sides — an authenticated PONG proves both.
        await mark(1, .running)
        let probe: ProbeResult
        if let live {
            probe = await Self.waitForLivePong(live)
        } else {
            probe = await Self.probeDirect(config: config, host: parts.host, port: parts.port)
        }

        switch probe {
        case .pong(let rtt, let received, let lost):
            await set(1, .ok, detail: L.milliseconds(rtt))
            await set(2, .ok, detail: Pairing.fingerprint(ofBase64Key: config.psk) ?? L.t("unit.none"))

            // 4. The PC's own counters, echoed back inside the PONG.
            if received > 0 {
                let percent = lost + received > 0 ? Double(lost) * 100 / Double(lost + received) : 0
                await set(3, .ok, detail: L.t(
                    "check.stream.detail",
                    L.plural("packets", Int(received)),
                    L.percent(percent)
                ))
            } else {
                await set(3, .failed, detail: L.plural("packets", 0),
                          explanation: L.t("check.stream.none.why"))
            }

        case .silent:
            await set(1, .failed, detail: L.t("check.silent"),
                      explanation: L.t(
                          "check.silent.why",
                          parts.host,
                          L.integer(Int(parts.port)),
                          L.integer(Int(parts.port))
                      ))
            // Without an answer there is nothing to decrypt, so the key cannot
            // be judged either way. Saying "they do not match" would be a lie.
            await set(2, .failed, detail: L.t("check.notChecked"),
                      explanation: L.t("check.keys.unproven.why", fingerprint(config)))
            await set(3, .failed, detail: L.t("unit.none"))

        case .badKey:
            await set(1, .ok, detail: L.t("check.packets.ok"))
            await set(2, .failed, detail: L.t("check.keys.mismatch"),
                      explanation: L.t("check.keys.mismatch.why", fingerprint(config)))
            await set(3, .failed, detail: L.t("unit.none"))

        case .misconfigured(let reason):
            await set(1, .failed, detail: reason)
            await set(2, .failed, detail: L.t("check.notChecked"))
            await set(3, .failed, detail: L.t("unit.none"))
        }

        // 5. End-to-end audio. Honest: the protocol carries no level from the
        // receiver, so this cannot be proven from the Mac alone.
        await set(4, .pending, detail: L.t("check.audio.notInProtocol"),
                  explanation: L.t("check.audio.why"))

        // 6. Forwarded devices.
        if !config.forwardsDevices {
            await set(5, .pending, detail: L.t("check.devices.off"))
        } else if config.selectedDevices.isEmpty {
            await set(5, .pending, detail: L.t("check.devices.none"),
                      explanation: L.t("check.devices.none.why"))
        } else if let devices, !devices.devices.isEmpty {
            let names = devices.devices.map(\.product).joined(separator: ", ")
            let silent = devices.devices.filter { !$0.attachAcknowledged }
            if silent.isEmpty {
                await set(5, .ok, detail: L.t("check.devices.ok", names))
            } else {
                await set(5, .failed,
                          detail: L.t("check.devices.silent", silent.map(\.product).joined(separator: ", ")),
                          explanation: L.t("check.devices.silent.why"))
            }
        } else {
            await set(5, .failed, detail: L.t("check.devices.absent"),
                      explanation: L.t("check.devices.absent.why"))
        }

        let failures = rows.filter { $0.state == .failed }
        if failures.isEmpty {
            conclude(ok: L.t("check.verdict.ok"))
        } else if let first = failures.first {
            conclude(bad: firstFailureHeadline(first))
        }
    }

    private func fingerprint(_ config: Config) -> String {
        Pairing.fingerprint(ofBase64Key: config.psk) ?? L.t("unit.none")
    }

    private func firstFailureHeadline(_ row: Row) -> String {
        // §9.4: the heading is never "Error", it is the concrete thing that
        // did not work.
        switch row.id {
        case 0: return L.t("check.verdict.unresolved")
        case 1: return L.t("check.verdict.noAnswer")
        case 2: return L.t("check.verdict.keys")
        case 3: return L.t("check.verdict.audio")
        case 5: return L.t("check.verdict.devices")
        default: return row.title
        }
    }

    private func conclude(ok: String) {
        verdict = ok
        verdictTone = .ok
    }

    private func conclude(bad: String) {
        verdict = bad
        verdictTone = .bad
    }

    // MARK: - Row plumbing

    /// §9.4: at least 250 ms between rows, otherwise the whole list flickers
    /// past and the user learns nothing from it.
    private func pace() async {
        try? await Task.sleep(nanoseconds: 260_000_000)
    }

    private func mark(_ id: Int, _ state: CheckRow.State) async {
        guard let index = rows.firstIndex(where: { $0.id == id }) else { return }
        rows[index].state = state
    }

    private func set(_ id: Int, _ state: CheckRow.State, detail: String, explanation: String? = nil) async {
        await pace()
        guard let index = rows.firstIndex(where: { $0.id == id }) else { return }
        rows[index].state = state
        rows[index].detail = detail
        rows[index].explanation = explanation
    }

    private func failRest(from id: Int) async {
        for index in rows.indices where rows[index].id >= id {
            rows[index].state = .failed
            rows[index].detail = L.t("check.notChecked")
        }
    }

    // MARK: - Probes

    private enum ProbeResult {
        case pong(rtt: Double, received: UInt64, lost: UInt64)
        case silent
        case badKey
        case misconfigured(String)
    }

    /// DNS only. A literal address short-circuits, which also means an offline
    /// machine still passes step 1 for `192.168.…` — correct, that address is
    /// resolvable by definition.
    private static func resolve(host: String) async -> String? {
        if IPv4Address(host) != nil || IPv6Address(host) != nil { return host }
        return await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                var hints = addrinfo(
                    ai_flags: 0, ai_family: AF_UNSPEC, ai_socktype: SOCK_DGRAM,
                    ai_protocol: 0, ai_addrlen: 0, ai_canonname: nil, ai_addr: nil, ai_next: nil
                )
                var result: UnsafeMutablePointer<addrinfo>?
                guard getaddrinfo(host, nil, &hints, &result) == 0, let first = result else {
                    continuation.resume(returning: nil)
                    return
                }
                defer { freeaddrinfo(result) }

                var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                let ok = getnameinfo(
                    first.pointee.ai_addr, socklen_t(first.pointee.ai_addrlen),
                    &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST
                ) == 0
                continuation.resume(returning: ok ? String(cString: buffer) : nil)
            }
        }
    }

    /// The pipeline is already talking to this receiver: wait for the next
    /// keepalive answer rather than starting a competing session.
    private static func waitForLivePong(_ runtime: BridgeRuntime) async -> ProbeResult {
        let deadline = Date().addingTimeInterval(3.5)
        while Date() < deadline {
            let snapshot = runtime.snapshot()
            if let pong = snapshot.pong, Date().timeIntervalSince(pong) < 3, let rtt = snapshot.rtt {
                return .pong(rtt: rtt, received: snapshot.received, lost: snapshot.lost)
            }
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        return .silent
    }

    /// No pipeline (or a different target): open a throwaway socket, send three
    /// HELLOs and wait. Three because a single lost UDP datagram would
    /// otherwise be reported to the user as "the PC does not answer".
    private static func probeDirect(config: Config, host: String, port: UInt16) async -> ProbeResult {
        let key: SymmetricKey
        do {
            key = try config.symmetricKey()
        } catch {
            return .misconfigured("\(error)")
        }
        guard let nwPort = NWEndpoint.Port(rawValue: port) else {
            return .misconfigured(L.t("check.badPort"))
        }

        let sender = Sender(
            target: .hostPort(host: NWEndpoint.Host(host), port: nwPort),
            key: key,
            nodeName: config.name
        )
        sender.start()
        defer { sender.stop() }

        let deadline = Date().addingTimeInterval(3.0)
        var attempt = 0
        while Date() < deadline {
            if attempt % 5 == 0 { sender.sendHello() }
            attempt += 1
            try? await Task.sleep(nanoseconds: 200_000_000)
            let snapshot = sender.snapshot()
            if snapshot.pong != nil, let rtt = snapshot.rtt {
                return .pong(rtt: rtt, received: snapshot.received, lost: snapshot.lost)
            }
        }
        return .silent
    }
}
