import SwiftUI

/// The microphone block inside the menu bar popover (§7.1 point 2).
///
/// Level meter, mute button, input picker. Nothing else fits in 340 pt and
/// nothing else answers "звук идёт или нет" faster.
struct MicrophoneCard: View {
    @Bindable var feature: MicrophoneFeature

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            LevelMeter(peak: feature.peak, muted: feature.isMuted)

            if !feature.status.detail.isEmpty {
                Text(feature.status.detail)
                    .font(.dsCaption)
                    .foregroundStyle(feature.status.tone.text(palette))
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let alert = feature.status.alert, feature.status.state == .error {
                InlineAlert(text: alert, tone: .bad)
            }

            // One line instead of three tiles: the popover answers "идёт или
            // нет", the numbers belong in the settings pane.
            Text("\(feature.packetsPerSecond) пак/с · RTT \(feature.rttText) · потери \(feature.lossText)")
                .font(.dsCaption.monospacedDigit())
                .foregroundStyle(palette.textDim)

            if let action = feature.status.primaryAction {
                // §6.1: one primary button per screen. The popover shows two
                // feature cards, so their actions are secondary — otherwise the
                // window has two equally loud "main" buttons and neither reads
                // as the main one.
                Button(action.title, action: action.perform)
                    .buttonStyle(.dsSecondary)
                    .frame(maxWidth: .infinity)
                    .keyboardShortcut("m", modifiers: .command)
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
                StatusCard(status: feature.status) {
                    if let action = feature.status.primaryAction {
                        Button(action.title, action: action.perform)
                            .buttonStyle(.dsSecondary)
                            .fixedSize()
                    }
                }

                if !feature.isEnabled {
                    EmptyState(
                        symbolName: "mic.slash",
                        title: "Микрофон выключен",
                        text: "Звук на игровой ПК не отправляется. Включите фичу, когда она понадобится.",
                        action: FeatureAction(title: "Включить микрофон") { feature.isEnabled = true }
                    )
                } else if !(model?.config.isConfigured ?? false) {
                    EmptyState(
                        symbolName: "link.badge.plus",
                        title: "HexBridge готов к настройке",
                        text: "Осталось связать Mac и игровой ПК: сгенерировать ключ и указать адрес. Это занимает около минуты.",
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
                MetricTile(caption: "RTT", value: feature.rttText,
                           help: "Оценка по меткам времени в служебных пакетах. Требует синхронных часов на обеих машинах.")
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
