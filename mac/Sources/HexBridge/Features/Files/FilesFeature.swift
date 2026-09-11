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

        status = derive()
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

        // By kind, not `.first`: the clipboard is on the same channel, and a
        // progress bar that draws somebody else's transfer is worse than no
        // progress bar.
        flight = host.runtime.bulk.progress().first { $0.kind == .file }
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

        // Off the main thread. The file is not read into memory any more, but
        // the hash in the offer still means one pass over the whole of it before
        // the first block moves, and four gigabytes off a network volume or a
        // sleeping disk is not something to do while the popover waits to be
        // drawn.
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let problem = Self.offer(url, to: bulk, limit: limit)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    if let problem {
                        self.failure = problem
                        print("hexbridge: \(url.lastPathComponent) was not sent — \(problem)")
                    }
                    self.status = self.derive()
                }
            }
        }
    }

    /// The reading and the size check, off the main actor. Returns the sentence
    /// to show when it did not happen, or nil when the object is on its way.
    nonisolated private static func offer(_ url: URL, to bulk: BulkChannel, limit: Int) -> String? {
        let ceiling = L.sizeLimit(limit)
        do {
            let values = try url.resourceValues(forKeys: [.fileSizeKey, .isDirectoryKey])
            if values.isDirectory == true { return L.t("files.error.folder") }
            // Asked of the directory entry first, so something past the
            // format's own ceiling is refused before it is opened and hashed.
            if let size = values.fileSize, size > limit { return L.t("files.error.tooLarge", ceiling) }

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
                // contract at 256 bytes with the extension kept.
                description: SafeFileName.shortened(
                    url.lastPathComponent, toBytes: Bulk.maxDescriptionBytes
                )
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

        inbox.adopt(temporary, as: delivery.description) { [weak self] result in
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
