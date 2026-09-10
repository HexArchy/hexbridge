import CryptoKit
import Foundation
import Network
import Observation

/// The "Проверить связь" run from DESIGN.md §9.4.
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

    private static let blank: [Row] = [
        Row(id: 0, title: "Адрес разрешается"),
        Row(id: 1, title: "Пакеты доходят"),
        Row(id: 2, title: "Ключи совпадают"),
        Row(id: 3, title: "Приёмник принимает поток"),
        Row(id: 4, title: "Звук проходит насквозь"),
        Row(id: 5, title: "Проброшенные устройства"),
    ]

    private var task: Task<Void, Never>?

    func cancel() {
        task?.cancel()
        task = nil
        running = false
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
            await set(0, .failed, detail: "адрес не задан", explanation: "Укажите адрес приёмника в разделе «Соединение».")
            await failRest(from: 1)
            conclude(bad: "Адрес приёмника не задан")
            return
        }

        // 1. Address resolves.
        await mark(0, .running)
        let resolved = await Self.resolve(host: parts.host)
        guard let resolved else {
            await set(0, .failed, detail: "имя не разрешается", explanation: "«\(parts.host)» не превращается в IP-адрес. Проверьте написание или укажите адрес цифрами.")
            await failRest(from: 1)
            conclude(bad: "Адрес не разрешается")
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
            await set(1, .ok, detail: String(format: "%.0f мс", rtt))
            await set(2, .ok, detail: Pairing.fingerprint(ofBase64Key: config.psk) ?? "—")

            // 4. The receiver's own counters, echoed back inside the PONG.
            if received > 0 {
                let percent = lost + received > 0 ? Double(lost) * 100 / Double(lost + received) : 0
                await set(3, .ok, detail: String(format: "%llu пакетов, потери %.1f %%", received, percent))
            } else {
                await set(3, .failed, detail: "0 пакетов",
                          explanation: "Приёмник отвечает, но ни одного аудиопакета не досчитался. Включите микрофон и повторите проверку.")
            }

        case .silent:
            await set(1, .failed, detail: "ответа нет за 3 с",
                      explanation: "Пакеты уходят на \(parts.host):\(parts.port), но подтверждений оттуда нет. Проверьте, что на игровом ПК запущен HexBridge и что порт UDP \(parts.port) открыт.")
            // Without an answer there is nothing to decrypt, so the key cannot
            // be judged either way. Saying "не совпадают" here would be a lie.
            await set(2, .failed, detail: "не проверено",
                      explanation: "Ключи проверяются по ответу приёмника. Ответа нет, поэтому проверить нечего. Отпечаток ключа на этом Mac: \(Pairing.fingerprint(ofBase64Key: config.psk) ?? "—")")
            await set(3, .failed, detail: "—")

        case .badKey:
            await set(1, .ok, detail: "пакеты доходят")
            await set(2, .failed, detail: "ключи не совпадают",
                      explanation: "Пакеты доходят до Windows, но расшифровать их не получается — на Mac и на ПК записаны разные ключи. Отпечаток на этом Mac: \(Pairing.fingerprint(ofBase64Key: config.psk) ?? "—")")
            await set(3, .failed, detail: "—")

        case .misconfigured(let reason):
            await set(1, .failed, detail: reason)
            await set(2, .failed, detail: "не проверено")
            await set(3, .failed, detail: "—")
        }

        // 5. End-to-end audio. Honest: the protocol carries no level from the
        // receiver, so this cannot be proven from the Mac alone.
        await set(4, .pending, detail: "нет в протоколе",
                  explanation: "Сквозная проверка требует, чтобы приёмник присылал свой уровень звука. В текущей версии протокола такого поля нет — проверьте звук в самой игре.")

        // 6. Forwarded devices.
        if !config.forwardsDevices {
            await set(5, .pending, detail: "проброс выключен")
        } else if config.selectedDevices.isEmpty {
            await set(5, .pending, detail: "ничего не выбрано",
                      explanation: "По умолчанию не пробрасывается ничего. Выберите устройства в настройках — по одному, явно.")
        } else if let devices, !devices.devices.isEmpty {
            let names = devices.devices.map(\.product).joined(separator: ", ")
            let silent = devices.devices.filter { !$0.attachAcknowledged }
            if silent.isEmpty {
                await set(5, .ok, detail: "\(names) — приёмник подтвердил")
            } else {
                await set(5, .failed, detail: "приёмник молчит про \(silent.map(\.product).joined(separator: ", "))",
                          explanation: "Устройство прочитано на Mac, но приёмник не прислал DEV_ACK — виртуальное устройство на Windows, скорее всего, не создано.")
            }
        } else {
            await set(5, .failed, detail: "выбранные устройства не подключены",
                      explanation: "Подключите выбранные устройства к Mac кабелем USB. По Bluetooth проброс не работает: приёмнику нужны USB-дескрипторы.")
        }

        let failures = rows.filter { $0.state == .failed }
        if failures.isEmpty {
            conclude(ok: "Всё работает")
        } else if let first = failures.first {
            conclude(bad: firstFailureHeadline(first))
        }
    }

    private func firstFailureHeadline(_ row: Row) -> String {
        // §9.4: the heading is never "Ошибка", it is the concrete thing that
        // did not work.
        switch row.id {
        case 0: return "Адрес не разрешается"
        case 1: return "Windows не отвечает"
        case 2: return "Ключи не совпадают"
        case 3: return "Звук не доходит до Windows"
        case 5: return "Устройства не проброшены"
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
            rows[index].detail = "не проверено"
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
    /// otherwise be reported to the user as "Windows не отвечает".
    private static func probeDirect(config: Config, host: String, port: UInt16) async -> ProbeResult {
        let key: SymmetricKey
        do {
            key = try config.symmetricKey()
        } catch {
            return .misconfigured("\(error)")
        }
        guard let nwPort = NWEndpoint.Port(rawValue: port) else {
            return .misconfigured("неверный порт")
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
