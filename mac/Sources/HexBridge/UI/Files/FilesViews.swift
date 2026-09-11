import AppKit
import HexBridgeText
import SwiftUI

/// The file block inside the menu bar popover.
///
/// One obvious path and nothing beside it: a strip you drop a file on, which is
/// also the button that opens a file picker. Everything else on this card
/// appears only when there is something to say — the bar while a file is
/// moving, the name of the last one to arrive, the sentence about one that did
/// not make it.
struct FilesCard: View {
    @Bindable var feature: FilesFeature

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            FileDropZone { url in feature.send(url) }

            if let flight = feature.flight {
                ProgressView(value: flight.fraction)
                    .progressViewStyle(.linear)
                    .tint(palette.okFg)
            }

            if let arrival = feature.arrivals.first {
                ArrivedRow(name: arrival.name, when: feature.whenText(arrival)) {
                    feature.reveal(arrival)
                }
            }

            if let failure = feature.failure {
                InlineAlert(
                    text: failure,
                    tone: .warn,
                    action: FeatureAction(title: L.t("action.gotIt")) { feature.clearFailure() }
                )
            }
        }
    }
}

/// The files pane of the settings window.
struct FilesSettingsPane: View {
    @Bindable var feature: FilesFeature

    @Environment(\.palette) private var palette

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                // §6.1: one action per screen. The switch in General is what
                // turns file transfer on and off, so it is not offered again
                // here — only a remedy that is not the switch.
                StatusCard(status: feature.status) {
                    if let action = feature.status.primaryAction, !action.togglesFeature {
                        Button(action.title, action: action.perform)
                            .buttonStyle(.dsSecondary)
                            .fixedSize()
                    }
                }

                if !feature.isEnabled {
                    EmptyState(
                        symbolName: "arrow.down.doc",
                        title: L.t("files.off.headline"),
                        text: L.t("files.off.emptyText"),
                        action: FeatureAction(title: L.t("files.action.turnOn")) { feature.isEnabled = true }
                    )
                } else {
                    FileDropZone { url in feature.send(url) }
                    transfer
                    arrived
                }
            }
            .padding(Space.xl)
        }
        .background(palette.bg)
    }

    /// The bar, and how far along it is. What is moving and which way is
    /// already the status card's headline right above.
    @ViewBuilder
    private var transfer: some View {
        if let flight = feature.flight {
            Card(padding: Space.md) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    ProgressView(value: flight.fraction)
                        .progressViewStyle(.linear)
                        .tint(palette.okFg)
                    Text(L.t(
                        "files.progress.blocks",
                        L.integer(flight.chunksDone),
                        L.plural("blocks", Int(flight.chunkCount))
                    ))
                        .font(.dsCaption.monospacedDigit())
                        .foregroundStyle(palette.textDim)
                }
            }
        }
    }

    /// What has come in, newest first, each one a way into Finder.
    private var arrived: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: L.t("files.section.arrived"))
            if feature.arrivals.isEmpty {
                Text(L.t("files.last.none"))
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
            } else {
                ForEach(feature.arrivals) { arrival in
                    ArrivedRow(name: arrival.name, when: feature.whenText(arrival)) {
                        feature.reveal(arrival)
                    }
                }
            }
            Text(L.t("files.savedTo"))
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

// MARK: - The strip you drop a file on

/// Drop target and file picker in one control.
///
/// Two separate affordances — a dashed rectangle *and* a "Choose…" button
/// beside it — is two ways to do one thing in a 340 pt popover. So the strip
/// itself is the button: drag if there is something to drag, click if there is
/// not.
private struct FileDropZone: View {
    let send: (URL) -> Void

    @State private var targeted = false

    @Environment(\.palette) private var palette
    @Environment(\.motionSettings) private var motion

    var body: some View {
        Button(action: choose) {
            HStack(spacing: Space.sm) {
                Image(systemName: targeted ? "arrow.down.doc.fill" : "arrow.down.doc")
                    .symbolRenderingMode(.hierarchical)
                    .foregroundStyle(targeted ? palette.okFg : palette.textDim)
                Text(targeted ? L.t("files.drop.active") : L.t("files.drop"))
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, Space.sm)
            .padding(.vertical, Space.sm + 2)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: Radius.md, style: .continuous)
                    .fill(targeted ? palette.okBg : Color.clear)
            )
            .overlay(
                RoundedRectangle(cornerRadius: Radius.md, style: .continuous)
                    .strokeBorder(
                        targeted ? palette.okBorder : palette.border,
                        // Dashed says "put something here" without a word being
                        // spent on it, which is the only reason it is not a
                        // plain border like every other surface in the app.
                        style: StrokeStyle(lineWidth: 1, dash: [4, 3])
                    )
            )
        }
        .buttonStyle(.plain)
        .contentShape(Rectangle())
        .help(L.t("files.drop.help"))
        // `URL` rather than an item provider: a Finder drag carries a file URL,
        // and `Transferable` unpacks it without a word of NSItemProvider.
        .dropDestination(for: URL.self) { urls, _ in
            let files = urls.filter { $0.isFileURL }
            guard !files.isEmpty else { return false }
            for url in files { send(url) }
            return true
        } isTargeted: { targeted = $0 }
        .animation(Motion.standard(Motion.short, reduced: motion.reduceMotion), value: targeted)
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.message = L.t("files.choose.message")
        panel.prompt = L.t("files.choose.prompt")

        // HexBridge has no Dock icon, so nothing brings its panels forward on
        // their own: without this the picker opens behind whatever the user was
        // looking at and the app appears to have swallowed the click.
        NSApp.activate(ignoringOtherApps: true)

        guard panel.runModal() == .OK, let url = panel.url else { return }
        send(url)
    }
}

// MARK: - One file that arrived

/// A name, when it arrived, and a way into Finder. The whole row is the link:
/// the only thing anybody wants from a file that has already been saved is to
/// see where it is.
private struct ArrivedRow: View {
    let name: String
    let when: String
    let reveal: () -> Void

    @Environment(\.palette) private var palette

    var body: some View {
        Button(action: reveal) {
            HStack(spacing: Space.xs) {
                Image(systemName: "doc")
                    .symbolRenderingMode(.hierarchical)
                    .foregroundStyle(palette.textDim)
                Text(name)
                    .font(.dsCaption)
                    .foregroundStyle(palette.text)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(when)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .lineLimit(1)
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(L.t("files.reveal"))
        .accessibilityLabel(L.t("files.reveal.accessibility", name))
    }
}
