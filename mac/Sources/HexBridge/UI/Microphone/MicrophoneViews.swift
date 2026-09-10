import SwiftUI

/// The microphone block inside the menu bar popover (§7.1 point 2).
///
/// A moving bar and a mute button. The bar answers "звук идёт или нет" before
/// anybody has read a word, and mute is the one thing people open this popover
/// to press — everything else the microphone knows (пакетов/с, RTT, потери) is
/// diagnostics and lives one window away, in the settings pane.
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
                .help(feature.isMuted ? "Включить микрофон" : "Заглушить")
                .accessibilityLabel(feature.isMuted ? "Включить микрофон" : "Заглушить микрофон")
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
                    if let action = feature.status.primaryAction, !Wording.duplicatesSwitch(action.title) {
                        Button(action.title, action: action.perform)
                            .buttonStyle(.dsSecondary)
                            .fixedSize()
                    }
                }

                if !feature.isEnabled {
                    EmptyState(
                        symbolName: "mic.slash",
                        title: "Микрофон выключен",
                        text: "Звук на игровой ПК не отправляется.",
                        action: FeatureAction(title: "Включить микрофон") { feature.isEnabled = true }
                    )
                } else if !(model?.config.isConfigured ?? false) {
                    EmptyState(
                        symbolName: "link.badge.plus",
                        title: "Mac и игровой ПК ещё не связаны",
                        text: "Это занимает около минуты и делается один раз.",
                        action: FeatureAction(title: "Начать настройку") { host.openPairing() }
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
            SectionLabel(text: "Телеметрия")
            HStack(spacing: Space.sm) {
                MetricTile(caption: "пакетов/с", value: "\(feature.packetsPerSecond)")
                MetricTile(caption: "задержка", value: feature.rttText,
                           help: "Время до игрового ПК и обратно. Оценка: нужны синхронные часы на обеих машинах.")
                MetricTile(caption: "аптайм", value: feature.uptimeText)
                MetricTile(caption: "потери", value: feature.lossText)
            }
            Sparkline(samples: feature.history, tone: feature.status.tone == .ok ? .ok : .warn)
                .frame(height: 44)
        }
    }

    private var levels: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            SectionLabel(text: "Уровень")
            LevelMeter(peak: feature.peak, muted: feature.isMuted)
            if let model {
                HStack {
                    Text("Усиление").font(.dsLabel).foregroundStyle(palette.textDim)
                    Slider(value: Binding(get: { model.gain }, set: { model.gain = $0 }), in: 0.1...8)
                    Text(String(format: "%.2f×", model.gain))
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
                SectionLabel(text: "Вход")
                Picker("Устройство", selection: Binding(
                    get: { model.inputDeviceSelector },
                    set: { model.inputDeviceSelector = $0 }
                )) {
                    Text("Системный по умолчанию").tag("")
                    ForEach(model.devices, id: \.uid) { device in
                        Text("\(device.name) — \(device.inputChannels) ch").tag(device.uid)
                    }
                }
                LabeledContent("Сейчас захватывается") {
                    Text(feature.deviceName).font(.dsLabel).foregroundStyle(palette.text)
                }
                .font(.dsLabel)
                .foregroundStyle(palette.textDim)
            }
        }
    }
}
