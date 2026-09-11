import AppKit
import Foundation
import HexBridgeFiles
import HexBridgeText
import Observation
import SwiftUI

/// Moving one file across: drop it on the popover, find it in Downloads on the
/// other machine, and the other way round.
///
/// It owns no transport. `BridgeRuntime.bulk` is the contract's reliable layer
/// and knows nothing about files; this class decides what to hand it, and what
/// to do on disk with what comes back. The clipboard rides the same channel and
/// the two never meet: every observer filters on its own `BulkKind`.
///
/// What it deliberately does **not** do is answer `BulkChannel.owns`. That
/// question is "we already hold these exact bytes, do not send them", which is
/// right for a clipboard syncing in both directions at once and wrong for a
/// file: somebody who sends the same file twice means it twice, and the second
/// one would vanish with no explanation anybody could act on.
@Observable
@MainActor
final class FilesFeature: Feature {

    /// One file that arrived and where it went.
    struct Arrival: Identifiable {
        let id = UUID()
        let url: URL
        let at: Date

        var name: String { url.lastPathComponent }
    }

    /// How many arrivals are remembered. The popover shows the newest one and
    /// the settings pane shows the list; a list that grows for as long as the
    /// app runs is a leak with an interface in front of it.
    private static let remembered = 8

    let id = "files"
    var title: String { L.t("files.title") }
    let symbolName = "arrow.down.doc"
    var summary: String { L.t("files.summary") }

    private unowned let host: FeatureHost
    private let inbox = FileInbox()

    /// Whether the macOS firewall is keeping incoming connections from this app,
    /// which costs the fast path in one direction only: files still leave this
    /// machine at full speed and still arrive, just the slow way. Answered off
    /// the main thread once the listener is up, because asking runs a tool.
    private var firewallWithholds = false

    /// The fast path's listener, up only while the feature is. See
    /// `FileFastPath` and docs/PROTOCOL.md, «The fast path for files».
    private var listener: FileStreamListener?
    /// The port and the room the listener was built for, so that a new pairing
    /// replaces it instead of being turned away by it.
    private var listening: (port: UInt16, room: UInt64)?
    /// What the fast path is moving, for the same bar the block channel draws
    /// on: a stream keeps no bookkeeping of its own.
    private let fastProgress = FileStreamProgress()

    private(set) var status = FeatureStatus()
    private(set) var arrivals: [Arrival] = []
    private(set) var sent = 0
    /// Non-nil while something is on the wire, which is what the progress bar draws.
    private(set) var flight: BulkProgress?
    /// One file did not go, or did not land. Not a state of the feature — the
    /// feature is fine — so it is shown where it happened and cleared by hand.
    private(set) var failure: String?

    /// Handles for this feature's slots on the shared bulk channel, so turning
    /// file transfer off takes only its own observers away and leaves whatever
    /// the clipboard is doing alone.
    private var deliveryToken: Int?
    private var finishToken: Int?

    private var running = false

    init(host: FeatureHost) {
        self.host = host
        status = FeatureStatus(state: .off, tone: .off, headline: L.t("files.off.headline"))
    }

    // MARK: - Feature

    var isEnabled: Bool {
        get { host.config.transfersFiles }
        set {
            host.config.files = newValue
            host.saveSoon()
            host.runtime.config = host.config
            // This may be the only thing switched on, in which case there is no
            // bridge yet for it to travel on.
            host.reconcilePipeline()
            if newValue { start() } else { stop() }
        }
    }

    func start() {
        guard isEnabled, !running else { return }
        running = true
        failure = nil

        // A run that was killed mid-transfer cannot have cleaned up after
        // itself. Done here rather than on every arrival: it is a sweep of a
        // folder, and once per switch-on is as often as it is worth.
        inbox.discardLeftovers(prefix: Bulk.partialPrefix, suffix: Bulk.partialSuffix)

        let bulk = host.runtime.bulk
        // Called from the socket queue, so both hop to the main actor before
        // they touch anything on this class.
        //
        // `.file` storage, and it is the whole of what makes a four-gigabyte
        // file possible: the object is written straight into Downloads under a
        // hidden name as it arrives, so nothing here ever holds it. The
        // directory is Downloads rather than a temporary one so that the last
        // step is a rename and not a second copy of the whole thing.
        deliveryToken = bulk.observeDeliveries(
            of: .file, storage: .file(directory: FileInbox.downloads)
        ) { [weak self] delivery in
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.accept(delivery) }
            }
        }
        finishToken = bulk.observeFinished { [weak self] result in
            guard result.kind == .file else { return }
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.finish(result) }
            }
        }

        startFastPath()
        status = derive()
    }

    /// Brings up the listener on the data port plus two.
    ///
    /// It needs no sound and no socket of the feature's own — the stream is its
    /// own connection — but it does need a key, so an unpaired Mac simply has no
    /// listener. A port held open by a feature that is switched off is a promise
    /// the app is not keeping, which is why this is started here and stopped in
    /// `stop()` rather than living for as long as the app does.
    private func startFastPath() {
        guard let plan = FileStreamPlan(config: host.config) else { return }
        // Pairing with a different machine changes the key, and the room with
        // it. A listener still holding the old one would refuse the new
        // machine's opening record — and the other side reads a refusal there as
        // «this file cannot be sent at all» rather than as «go the slow way», so
        // the file would simply not arrive until the app was restarted.
        if let listening, listening == (plan.port, plan.room) { return }
        listener?.stop()

        let listener = FileStreamListener(
            port: plan.port,
            key: plan.key,
            room: plan.room,
            directory: FileInbox.downloads,
            progress: fastProgress,
            onArrival: { [weak self] url, name, seconds, bytes in
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        self?.arrived(url, named: name, seconds: seconds, bytes: bytes)
                    }
                }
            },
            // Log only, like every other note from the transport: the interface
            // says what happened to a file, not what happened to a socket.
            onNote: { note in print("hexbridge: " + note) }
        )
        self.listener = listener
        self.listening = (plan.port, plan.room)
        listener.start()

        // A listener that is up says nothing about whether anything is allowed to
        // reach it. Off the main thread: this runs a tool, and the menu bar is
        // not waiting on the answer.
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let withheld = Firewall.withholdsIncoming()
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.firewallWithholds = withheld
                    self.status = self.derive()
                }
            }
        }
    }

    func stop() {
        guard running else {
            status = derive()
            return
        }
        running = false

        let bulk = host.runtime.bulk
        if let deliveryToken { bulk.removeDeliveryObserver(deliveryToken) }
        if let finishToken { bulk.removeFinishObserver(finishToken) }
        deliveryToken = nil
        finishToken = nil
        bulk.reset(kind: .file)
        listener?.stop()
        listener = nil
        listening = nil
        fastProgress.clearAll()
        flight = nil
        status = derive()
    }

    func refresh() {
        guard isEnabled else {
            if running { stop() }
            status = derive()
            return
        }
        if !running { start() }

        // A file on the fast path first, because it is the ordinary case now.
        // By kind for the other one, not `.first`: the clipboard is on the same
        // channel, and a progress bar that draws somebody else's transfer is
        // worse than no progress bar.
        flight = Self.flight(from: fastProgress)
            ?? host.runtime.bulk.progress().first { $0.kind == .file }
        // The listener is started with the feature, and the feature can be
        // running before there is a key — the pairing window is inside the same
        // app. This is where it catches up.
        startFastPath()
        status = derive()
    }

    // MARK: - Out

    /// Offers a file to the other machine. The only way anything leaves here.
    func send(_ url: URL) {
        guard isEnabled else { return }
        failure = nil

        guard host.runtime.isRunning else {
            // Offering into a dead socket would burn ten retries and then report
            // a failure the user cannot do anything about.
            failure = L.t("files.error.noLink")
            status = derive()
            return
        }

        let bulk = host.runtime.bulk
        let limit = Bulk.maxObjectSize(of: .file)
        // A relay forwards datagrams, and a stream is not one — docs/PROTOCOL.md
        // says so in as many words. Asking anyway would cost three seconds a
        // file to be told what is already known.
        let plan = host.runtime.throughRelay ? nil : FileStreamPlan(config: host.config)
        let progress = fastProgress

        // Off the main thread. The stream hashes the file before its first
        // record and the block channel hashes it before its first block, so
        // either way this is one pass over the whole of it — and four gigabytes
        // off a network volume or a sleeping disk is not something to do while
        // the popover waits to be drawn.
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let handover = Self.deliver(url, over: plan, or: bulk, limit: limit, progress: progress)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    switch handover {
                    case .sent:
                        // Nothing else will report this one: the stream has no
                        // acknowledgement to wait for, which is half of why it
                        // is quick.
                        self.sent += 1
                    case .offered:
                        break
                    case .failed(let problem):
                        self.failure = problem
                        print("hexbridge: \(url.lastPathComponent) was not sent — \(problem)")
                    }
                    self.status = self.derive()
                }
            }
        }
    }

    /// What became of one file, in the terms the main actor has to act on.
    private enum Handover {
        /// It crossed on the stream, whole. Counted here and nowhere else.
        case sent
        /// It is on the block channel; `finish` will say how it ended.
        case offered
        case failed(String)
    }

    /// The stream first, the block channel when there is no stream to be had.
    ///
    /// Both outcomes are in the log, and the fast one says how fast: somebody
    /// who wonders why a gigabyte took three seconds instead of six minutes
    /// deserves the answer without having to ask for it.
    nonisolated private static func deliver(
        _ url: URL,
        over plan: FileStreamPlan?,
        or bulk: BulkChannel,
        limit: Int,
        progress: FileStreamProgress
    ) -> Handover {
        if let problem = check(url, limit: limit) { return .failed(problem) }

        // The contract caps the name at 256 bytes with the extension kept, and
        // the two paths send the same name: a file must not arrive called one
        // thing on the fast path and another on the slow one.
        let name = SafeFileName.shortened(url.lastPathComponent, toBytes: Bulk.maxDescriptionBytes)

        if let plan {
            switch FileFastPath.send(file: url, named: name, over: plan, progress: progress) {
            case .sent(let seconds, let bytes):
                print("hexbridge: " + L.t(
                    "files.fast.sent", name, FileFastPath.speedText(bytes: bytes, seconds: seconds)
                ))
                return .sent
            case .noConnection(let reason):
                // The ordinary case on a machine that has not been updated yet,
                // and the reason the block channel is not going anywhere.
                print("hexbridge: " + L.t("files.fast.slow", name, reason))
            case .broke(let reason):
                // Connected and then died. Sending it all again over the path
                // that just failed is not a rescue, it is the same failure with
                // a longer wait in front of it.
                print("hexbridge: " + L.t("files.fast.broke", name, reason))
                return .failed(L.t("files.error.notDelivered", name))
            }
        }

        if let problem = offer(url, named: name, to: bulk) { return .failed(problem) }
        return .offered
    }

    /// The two questions that have the same answer whichever path the file
    /// takes: it must be a file, and it must fit what the format can carry.
    nonisolated private static func check(_ url: URL, limit: Int) -> String? {
        do {
            let values = try url.resourceValues(forKeys: [.fileSizeKey, .isDirectoryKey])
            if values.isDirectory == true { return L.t("files.error.folder") }
            // Asked of the directory entry, so something past the format's own
            // ceiling is refused before it is opened and hashed.
            if let size = values.fileSize, size > limit {
                return L.t("files.error.tooLarge", L.sizeLimit(limit))
            }
            return nil
        } catch {
            return "\(error)"
        }
    }

    /// Hands the file to the block channel, off the main actor. Returns the
    /// sentence to show when it did not happen, or nil when the object is on its
    /// way.
    nonisolated private static func offer(_ url: URL, named name: String, to bulk: BulkChannel) -> String? {
        do {
            try bulk.offer(
                kind: .file,
                // Never the text or the image format, whatever the file turns
                // out to hold: a .txt is a file because somebody sent a file,
                // and turning it into clipboard text on the other side would be
                // a surprise. PROTOCOL.md says so in as many words.
                format: .opaque,
                // By path, not by bytes. The channel reads each block off disk
                // as it goes, including when the other end asks for one again,
                // so nothing here is ever held whole.
                file: url,
                // The description is the name and nothing else, capped by the
                // contract at 256 bytes with the extension kept — done by the
                // caller, so that both paths name the file identically.
                description: name
            )
            return nil
        } catch {
            return "\(error)"
        }
    }

    private func finish(_ result: BulkResult) {
        switch result.outcome {
        case .delivered:
            sent += 1
            print("hexbridge: \(result.description) is on the other machine")
        case .alreadyThere:
            // This used to end here in silence: the file was dropped, nothing crossed, and
            // nothing said why. For a file the refusal can only mean one thing — nothing
            // on this channel ever already holds a copy of a file, unlike the clipboard —
            // so it can be named rather than guessed at.
            failure = L.t("files.error.refused", result.description)
            print("hexbridge: \(result.description) was turned down by the other machine")
        case .noAnswer, .stalled:
            failure = L.t("files.error.notDelivered", result.description)
            print("hexbridge: \(result.description) did not get across")
        }
        status = derive()
    }

    // MARK: - In

    private func accept(_ delivery: BulkDelivery) {
        // This feature registered for file storage, so an arrival is always a
        // file on disk. A `.bytes` payload would mean the channel had assembled
        // somebody else's object into ours; there is nothing sensible to do with
        // it and nothing to be gained by guessing.
        guard case .file(let temporary) = delivery.payload else { return }
        take(temporary, named: delivery.description)
    }

    /// A file that came in on the fast path.
    ///
    /// From here on it is not a different file from one that came the slow way:
    /// the same folder, the same never-overwrite naming, the same line in the
    /// log. All that is said extra is how quickly it got here, which is the one
    /// thing somebody would otherwise be left wondering about.
    private func arrived(_ temporary: URL, named name: String, seconds: TimeInterval, bytes: Int) {
        print("hexbridge: " + L.t(
            "files.fast.arrived", name, FileFastPath.speedText(bytes: bytes, seconds: seconds)
        ))
        take(temporary, named: name)
    }

    /// Puts one arrived file in Downloads, whichever path brought it.
    private func take(_ temporary: URL, named name: String) {
        inbox.adopt(temporary, as: name) { [weak self] result in
            MainActor.assumeIsolated {
                guard let self else { return }
                switch result {
                case .success(let url):
                    self.arrivals.insert(Arrival(url: url, at: Date()), at: 0)
                    if self.arrivals.count > Self.remembered { self.arrivals.removeLast() }
                    // The interface is invisible to the log, and this is the one
                    // line that says where a file actually went.
                    print("hexbridge: file saved to \(url.path)")
                case .failure(let error):
                    self.failure = "\(error)"
                    print("hexbridge: an arrived file could not be saved — \(error)")
                }
                self.status = self.derive()
            }
        }
    }

    /// The fast path drawn on the same bar as the block channel.
    ///
    /// Counted in blocks because that is what the caption beside the bar counts,
    /// and a file is the same number of 1024-byte blocks however it crosses. The
    /// transfer id is zero: nothing on this path has one, and nothing asks.
    private static func flight(from progress: FileStreamProgress) -> BulkProgress? {
        guard let moving = progress.snapshot().first else { return nil }
        return BulkProgress(
            transferID: 0,
            direction: moving.direction,
            kind: .file,
            format: .opaque,
            size: UInt32(clamping: moving.size),
            chunksDone: UInt32(clamping: moving.moved / Bulk.chunkSize),
            chunkCount: Bulk.chunkCount(forSize: moving.size),
            description: moving.name
        )
    }

    /// Dismisses the sentence about one file that did not make it.
    func clearFailure() {
        failure = nil
        status = derive()
    }

    func reveal(_ arrival: Arrival) {
        FileInbox.reveal(arrival.url)
    }

    // MARK: - State machine

    private func derive() -> FeatureStatus {
        guard isEnabled else {
            return FeatureStatus(
                state: .off,
                tone: .off,
                headline: L.t("files.off.headline"),
                detail: L.t("files.off.detail"),
                primaryAction: FeatureAction(title: L.t("files.action.turnOn"), togglesFeature: true) { [weak self] in
                    self?.isEnabled = true
                }
            )
        }

        if !host.config.isConfigured {
            return FeatureStatus(
                state: .error,
                tone: .bad,
                headline: L.t("files.unconfigured.headline"),
                detail: L.t("mic.unconfigured.detail"),
                primaryAction: FeatureAction(title: L.t("action.setUp")) { [weak self] in
                    self?.host.openPairing()
                }
            )
        }

        guard host.runtime.isRunning else {
            return FeatureStatus(
                state: .waiting,
                tone: .warn,
                headline: L.t("files.waiting.headline"),
                detail: L.t("files.waiting.detail"),
                primaryAction: FeatureAction(title: L.t("action.checkLink")) { [weak self] in
                    self?.host.openLinkCheck()
                }
            )
        }

        if let flight {
            let key = flight.direction == .outgoing ? "files.sending.headline" : "files.receiving.headline"
            return FeatureStatus(
                state: .live,
                tone: .ok,
                headline: L.t(key, flight.description),
                detail: L.percent(flight.fraction * 100, decimals: 0)
            )
        }

        if firewallWithholds {
            return FeatureStatus(
                state: .live,
                tone: .warn,
                headline: L.t("files.firewall.headline"),
                detail: L.t("files.firewall.detail"),
                primaryAction: FeatureAction(title: L.t("files.action.firewall")) {
                    NSWorkspace.shared.open(Firewall.settings)
                }
            )
        }

        return FeatureStatus(
            state: .live,
            tone: .ok,
            headline: L.t("files.live.headline"),
            detail: lastText
        )
    }

    // MARK: - Formatting used by both views

    /// What arrived last, or the sentence that stands in for it.
    var lastText: String {
        guard let arrival = arrivals.first else { return L.t("files.last.none") }
        return L.t("files.last.line", arrival.name, L.ago(Date().timeIntervalSince(arrival.at)))
    }

    func whenText(_ arrival: Arrival) -> String {
        L.ago(Date().timeIntervalSince(arrival.at))
    }

    // MARK: - Views

    func popoverCard() -> AnyView {
        AnyView(FilesCard(feature: self))
    }

    func settingsPane() -> AnyView {
        AnyView(FilesSettingsPane(feature: self))
    }
}
