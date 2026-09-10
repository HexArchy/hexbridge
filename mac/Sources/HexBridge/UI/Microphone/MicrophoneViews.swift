import HexBridgeText
import SwiftUI

/// The microphone block inside the menu bar popover (§7.1 point 2).
///
/// A moving bar and a mute button. The bar answers "is sound going through"
/// before anybody has read a word, and mute is the one thing people open this
/// popover to press — everything else the microphone knows (packets per second,
/// round trip, loss) is diagnostics and lives one window away, in the settings
/// pane.
struct MicrophoneCard: View {
    @Bindable var feature: MicrophoneFeature

    @Environment(\.palette) private var palette

    var body: some View {
        HStack(spacing: Space.sm) {
            LevelMeter(peak: feature.peak, muted: feature.isMuted, showsCaption: false)

            if feature.status.state == .live {
                // Not a card action but a control: it changes the state rather
                // than repairing it, which is why it is an icon next to the
                // thing it mutes and not a button under it.
                Button {
                    feature.toggleMute()
                } label: {
                    Image(systemName: feature.isMuted ? "mic.slash.fill" : "mic.fill")
                        .symbolRenderingMode(.hierarchical)
                        .font(.system(size: 13))
                        .foregroundStyle(feature.isMuted ? palette.warnFg : palette.textDim)
                        .frame(width: 18, height: 18)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .keyboardShortcut("m", modifiers: .command)
                .help(feature.isMuted ? L.t("mic.action.unmute") : L.t("mic.action.mute"))
                .accessibilityLabel(
                    feature.isMuted ? L.t("mic.unmute.accessibility") : L.t("mic.mute.accessibility")
                )
            }
        }
    }
}

/// The microphone pane of the settings window (§7.2 in full).
struct MicrophoneSettingsPane: View {
    @Bindable var feature: MicrophoneFeature
    let host: FeatureHost

    @Environment(\.palette) private var palette

    /// The host is the model; the pane needs its bindings for the shared
    /// controls. Casting once here keeps `FeatureHost` from growing a member
    /// for every control a feature happens to want.
    private var model: AppModel? { host as? AppModel }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Space.lg) {
                // §6.1: one action per screen. The pane below already offers the
                // switch's counterpart in its empty state, so the status card
                // keeps only what the empty state cannot say.
                StatusCard(status: feature.status) {
                    if let action = feature.status.primaryAction, !action.togglesFeature {
                        Button(action.title, action: action.perform)
                            .buttonStyle(.dsSecondary)
                            .fixedSize()
                    }
                }

                if !feature.isEnabled {
                    EmptyState(
                        symbolName: "mic.slash",
                        title: L.t("mic.off.headline"),
                        text: L.t("mic.off.emptyText"),
                        action: FeatureAction(title: L.t("mic.action.turnOn")) { feature.isEnabled = true }
                    )
                } else if !(model?.config.isConfigured ?? false) {
                    EmptyState(
                        symbolName: "link.badge.plus",
                        title: L.t("mic.unpaired.title"),
                        text: L.t("mic.unpaired.text"),
                        action: FeatureAction(title: L.t("mic.unpaired.action")) { host.openPairing() }
                    )
                } else {
                    telemetry
                    levels
                    input
                }
            }
            .padding(Space.xl)
        }
        .background(palette.bg)
    }

    private var telemetry: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: L.t("mic.section.telemetry"))
            HStack(spacing: Space.sm) {
                MetricTile(caption: L.t("mic.metric.packets"), value: L.integer(feature.packetsPerSecond))
                MetricTile(caption: L.t("mic.metric.latency"), value: feature.rttText,
                           help: L.t("mic.metric.latency.help"))
                MetricTile(caption: L.t("mic.metric.uptime"), value: feature.uptimeText)
                MetricTile(caption: L.t("mic.metric.loss"), value: feature.lossText)
            }
            Sparkline(samples: feature.history, tone: feature.status.tone == .ok ? .ok : .warn)
                .frame(height: 44)
        }
    }

    private var levels: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: L.t("mic.section.level"))
            LevelMeter(peak: feature.peak, muted: feature.isMuted)
            if let model {
                HStack {
                    Text(L.t("mic.gain")).font(.dsLabel).foregroundStyle(palette.textDim)
                    Slider(value: Binding(get: { model.gain }, set: { model.gain = $0 }), in: 0.1...8)
                    Text(L.t("unit.gain", L.number(model.gain, decimals: 2)))
                        .font(.dsLabel.monospacedDigit())
                        .foregroundStyle(palette.text)
                        .frame(width: 56, alignment: .trailing)
                }
            }
        }
    }

    @ViewBuilder
    private var input: some View {
        if let model {
            VStack(alignment: .leading, spacing: Space.sm) {
                SectionLabel(text: L.t("mic.section.input"))
                Picker(L.t("mic.device"), selection: Binding(
                    get: { model.inputDeviceSelector },
                    set: { model.inputDeviceSelector = $0 }
                )) {
                    Text(L.t("mic.device.systemDefault")).tag("")
                    ForEach(model.devices, id: \.uid) { device in
                        Text(L.t("mic.device.row", device.name, L.plural("channels", device.inputChannels)))
                            .tag(device.uid)
                    }
                }
                LabeledContent(L.t("mic.capturingNow")) {
                    Text(feature.deviceName).font(.dsLabel).foregroundStyle(palette.text)
                }
                .font(.dsLabel)
                .foregroundStyle(palette.textDim)
            }
        }
    }
}
