import SwiftUI

/// The settings window (§7.1, macOS).
///
/// A toolbar with panes, because HIG says so in as many words — *"use a
/// noncustomizable toolbar"* — and never mentions a sidebar for this window.
/// The feature panes are not listed here: they come from `model.features`, in
/// the order the features are declared.
///
/// Two HIG requirements are implemented explicitly rather than hoped for: the
/// window title follows the visible pane, and the last pane is restored.
struct SettingsWindow: View {
    @Bindable var model: AppModel

    /// Panes in display order. Built as data so the feature panes can be
    /// spliced in without the shell knowing what they are — and so the window
    /// title can be looked up by the same key the selection uses.
    private struct Pane: Identifiable {
        let id: String
        let title: String
        let symbol: String
        let content: AnyView
    }

    private var panes: [Pane] {
        var list = [
            Pane(id: "general", title: "Основное", symbol: "gearshape",
                 content: AnyView(GeneralPane(model: model)))
        ]
        list += model.features.map { feature in
            Pane(id: feature.id, title: feature.title, symbol: feature.symbolName,
                 content: feature.settingsPane())
        }
        list += [
            Pane(id: "link", title: "Соединение", symbol: "network",
                 content: AnyView(ConnectionPane(model: model))),
            Pane(id: "diagnostics", title: "Диагностика", symbol: "stethoscope",
                 content: AnyView(DiagnosticsPane(model: model))),
        ]
        return list
    }

    var body: some View {
        // `Tab(_:systemImage:value:)` is macOS 15; the deployment target is 14,
        // so this stays on `.tabItem`.
        TabView(selection: $model.selectedPane) {
            ForEach(panes) { pane in
                pane.content
                    .tabItem {
                        Label(pane.title, systemImage: pane.symbol)
                            .labelStyle(.titleAndIcon)
                    }
                    .tag(pane.id)
            }
        }
        .frame(width: Metrics.settingsWidth, height: Metrics.settingsHeight)
        .navigationTitle(paneTitle)
        .onDisappear { model.saveNow() }
    }

    /// HIG: "Update the window's title to reflect the currently visible pane."
    private var paneTitle: String {
        panes.first { $0.id == model.selectedPane }?.title ?? "Настройки"
    }
}

// MARK: - Общее

struct GeneralPane: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        Form {
            Section("Запуск") {
                Toggle("Запускать при входе в систему", isOn: $model.autostart)
                Text(model.autostartNote)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
            }

            Section("Оформление") {
                Picker("Тема", selection: Binding(get: { model.theme }, set: { model.theme = $0 })) {
                    ForEach(AppTheme.allCases, id: \.self) { theme in
                        Text(theme.title).tag(theme)
                    }
                }
                .pickerStyle(.segmented)
            }

            Section("Фичи") {
                // No feature is named here either: this list is the contract.
                ForEach(model.features, id: \.id) { feature in
                    Toggle(isOn: Binding(get: { feature.isEnabled }, set: { feature.isEnabled = $0 })) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(feature.title)
                            Text(feature.summary)
                                .font(.dsCaption)
                                .foregroundStyle(palette.textDim)
                        }
                    }
                }
            }

            Section("О программе") {
                LabeledContent("Версия", value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev")
                Text("«PlayStation» и «DualSense» — товарные знаки Sony Interactive Entertainment Inc. HexBridge не связан с Sony; изображение контроллера в приложении — собственная схематичная абстракция, а не воспроизведение продукта.")
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .fixedSize(horizontal: false, vertical: true)
            }

            RestartBanner(model: model)
        }
        .formStyle(.grouped)
    }
}

// MARK: - Соединение

struct ConnectionPane: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        Form {
            Section {
                if model.config.paired == true, let peer = model.config.peerName {
                    LabeledContent("Связан с", value: peer)
                }
                TextField("Адрес приёмника", text: $model.target, prompt: Text("192.168.1.10:47702"))
                TextField("Имя этого Mac", text: $model.nodeName)
                HStack {
                    Button("Связать заново") { model.openPairing() }
                    Button("Проверить связь") { model.openLinkCheck() }
                    Spacer()
                }
            } header: {
                Text("Приёмник")
            } footer: {
                Text("Адрес приёмника на Windows или релея на VPS. Порт по умолчанию — 47702.")
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
            }

            Section {
                KeyField(
                    text: $model.psk,
                    revealed: $model.revealPSK,
                    onCopy: { model.copyPSK() },
                    onGenerate: { model.generatePSK() }
                )
                if let fingerprint = model.fingerprint {
                    LabeledContent("Отпечаток") {
                        FingerprintLabel(fingerprint: fingerprint)
                    }
                }
            } header: {
                Text("Общий ключ")
            } footer: {
                Text("32 байта в base64. Должен совпадать на Mac и на ПК посимвольно. Отпечаток можно сравнить глазами, не раскрывая сам ключ.")
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
            }

            DisclosureGroup("Дополнительно") {
                Picker("Битрейт", selection: $model.bitrate) {
                    ForEach([16000, 24000, 32000, 48000, 64000], id: \.self) { rate in
                        Text("\(rate / 1000) кбит/с").tag(rate)
                    }
                }
                Stepper(
                    "Ожидаемые потери: \(model.expectedLossPercent) %",
                    value: $model.expectedLossPercent,
                    in: 0...50,
                    step: 5
                )
                .help("Управляют избыточностью Opus FEC: выше значение — устойчивее звук и больше трафика.")
            }

            RestartBanner(model: model)
        }
        .formStyle(.grouped)
    }
}

// MARK: - Диагностика

struct DiagnosticsPane: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        Form {
            Section("Проверки") {
                HStack {
                    Button("Проверить связь") { model.openLinkCheck() }
                    Button {
                        model.runProbe()
                    } label: {
                        Label("Проверить микрофон", systemImage: "waveform.badge.magnifyingglass")
                            .labelStyle(.titleAndIcon)
                    }
                    .disabled(model.probeRunning)
                    if model.probeRunning { Spinner() }
                    Spacer()
                }
                if !model.probeOutput.isEmpty {
                    ScrollView {
                        Text(model.probeOutput)
                            .font(.dsMono)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(height: 110)
                }
            }

            Section("Файлы") {
                LabeledContent("Журнал") {
                    HStack {
                        Text(LaunchAgent.logURL.path)
                            .font(.dsCaption)
                            .truncationMode(.head)
                            .lineLimit(1)
                        Button("Показать") { model.revealLog() }
                    }
                }
                LabeledContent("Конфиг") {
                    Text(model.runtime.configPath.path)
                        .font(.dsCaption)
                        .truncationMode(.head)
                        .lineLimit(1)
                }
                Button("Скопировать отчёт") { model.copyReport() }
                    .help("Версии, конфиг без ключа и последние 200 строк журнала")
            }

            if let notice = model.noticeText {
                Section {
                    InlineAlert(
                        text: notice,
                        tone: .warn,
                        action: FeatureAction(title: "Понятно") { model.noticeText = nil }
                    )
                }
            }
        }
        .formStyle(.grouped)
        .foregroundStyle(palette.text)
    }
}

// MARK: - Shared

/// Not every setting can be applied to a live socket or a live Opus encoder,
/// and pretending otherwise is worse than saying so.
struct RestartBanner: View {
    @Bindable var model: AppModel

    var body: some View {
        if model.needsRestart {
            Section {
                InlineAlert(
                    text: "Адрес, ключ и параметры кодека применятся после перезапуска передачи.",
                    tone: .warn,
                    action: FeatureAction(title: "Перезапустить") {
                        model.saveNow()
                        model.restartPipeline()
                    }
                )
            }
        }
    }
}
