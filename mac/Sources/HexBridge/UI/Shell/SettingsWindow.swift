import HexBridgeText
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
            Pane(id: "general", title: L.t("settings.pane.general"), symbol: "gearshape",
                 content: AnyView(GeneralPane(model: model)))
        ]
        list += model.features.map { feature in
            Pane(id: feature.id, title: feature.title, symbol: feature.symbolName,
                 content: feature.settingsPane())
        }
        list += [
            Pane(id: "link", title: L.t("settings.pane.link"), symbol: "network",
                 content: AnyView(ConnectionPane(model: model))),
            Pane(id: "diagnostics", title: L.t("settings.pane.diagnostics"), symbol: "stethoscope",
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
        panes.first { $0.id == model.selectedPane }?.title ?? L.t("settings.title")
    }
}

// MARK: - General

struct GeneralPane: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        Form {
            Section(L.t("settings.general.startup")) {
                Toggle(L.t("settings.general.openAtLogin"), isOn: $model.autostart)
                Text(model.autostartNote)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
            }

            Section(L.t("settings.general.appearance")) {
                Picker(L.t("settings.general.theme"),
                       selection: Binding(get: { model.theme }, set: { model.theme = $0 })) {
                    ForEach(AppTheme.allCases, id: \.self) { theme in
                        Text(theme.title).tag(theme)
                    }
                }
                .pickerStyle(.segmented)
            }

            // Three options, no restart, and no warning that one is needed:
            // `Themed` keys the whole view tree on this value, so the window
            // rewrites itself between one frame and the next. The note under the
            // picker says what «Системный» means and nothing else, because
            // there is nothing else to say.
            Section(L.t("settings.general.language.section")) {
                Picker(L.t("settings.general.language"),
                       selection: Binding(get: { model.language }, set: { model.language = $0 })) {
                    ForEach(AppLanguage.available, id: \.self) { language in
                        Text(title(of: language)).tag(language)
                    }
                }
                .pickerStyle(.segmented)

                Text(L.t("settings.general.language.note"))
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Section(L.t("settings.general.features")) {
                // No feature is named here either: this list is the contract.
                ForEach(model.features, id: \.id) { feature in
                    Toggle(isOn: Binding(get: { feature.isEnabled }, set: { feature.isEnabled = $0 })) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(feature.title)
                            Text(Wording.plain(feature.summary))
                                .font(.dsCaption)
                                .foregroundStyle(palette.textDim)
                        }
                    }
                }
            }

            Section(L.t("settings.general.updates")) {
                Toggle(L.t("settings.general.checkDaily"), isOn: Binding(
                    get: { model.checksForUpdates },
                    set: { model.checksForUpdates = $0 }
                ))
                .disabled(!model.updater.isAvailable)

                Text(model.updateNote)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .fixedSize(horizontal: false, vertical: true)

                // Said here rather than in the update dialog, because the
                // dialog is where the user is already committed. This is the
                // screen where they decide.
                Text(Wording.updateWillReaskForMicrophone)
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .fixedSize(horizontal: false, vertical: true)

                if let failure = model.updater.lastError {
                    Text(failure)
                        .font(.dsCaption)
                        .foregroundStyle(palette.badText)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Button(L.t("settings.general.checkNow")) { model.updater.checkNow() }
                    .disabled(!model.updater.isAvailable)
            }

            Section(L.t("settings.general.about")) {
                LabeledContent(
                    L.t("settings.general.version"),
                    value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
                )
                Text(L.t("settings.general.trademarks"))
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
                    .fixedSize(horizontal: false, vertical: true)
            }

            RestartBanner(model: model)
        }
        .formStyle(.grouped)
    }

    /// The three options name themselves: a language menu that says
    /// «Английский» to a reader who does not read Russian is no help at all, so
    /// each language is written the way its own speakers write it.
    private func title(of language: AppLanguage) -> String {
        L.t("language.\(language.rawValue)")
    }
}

// MARK: - Connection

struct ConnectionPane: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        Form {
            // §7.4 puts "Check the link" here, and this is the only place in
            // the app that has it: it is one check for all three features, so
            // repeating it in every card said the same thing three times.
            Section {
                if model.config.paired == true, let peer = model.config.peerName {
                    LabeledContent(L.t("link.pairedWith"), value: peer)
                }
                TextField(L.t("link.address"), text: $model.target, prompt: Text(verbatim: "192.168.1.10:47702"))
                TextField(L.t("link.macName"), text: $model.nodeName)
                HStack {
                    Button(L.t("action.checkLink")) { model.openLinkCheck() }
                    Button(L.t("link.pairAgain")) { model.openPairing() }
                    Spacer()
                }
            } header: {
                Text(L.t("link.section.pc"))
            } footer: {
                Text(L.t("link.defaultPort"))
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
                    LabeledContent(L.t("link.fingerprint")) {
                        FingerprintLabel(fingerprint: fingerprint)
                    }
                }
            } header: {
                Text(L.t("link.section.key"))
            } footer: {
                Text(L.t("link.key.footer"))
                    .font(.dsCaption)
                    .foregroundStyle(palette.textDim)
            }

            DisclosureGroup(L.t("link.advanced")) {
                Picker(L.t("link.bitrate"), selection: $model.bitrate) {
                    ForEach([16000, 24000, 32000, 48000, 64000], id: \.self) { rate in
                        Text(L.kilobits(perSecond: rate)).tag(rate)
                    }
                }
                Stepper(
                    L.t("link.expectedLoss", L.percent(Double(model.expectedLossPercent), decimals: 0)),
                    value: $model.expectedLossPercent,
                    in: 0...50,
                    step: 5
                )
                .help(L.t("link.expectedLoss.help"))

                // Here rather than on the Files pane: the same channel carries
                // the clipboard, and a speed that lives under one of the two
                // features would be a setting the other one silently obeys.
                Picker(L.t("link.sendRate"), selection: $model.sendRate) {
                    Text(L.t("link.sendRate.unlimited")).tag(0)
                    ForEach(rateChoices, id: \.self) { rate in
                        Text(rateTitle(rate)).tag(rate)
                    }
                }
                .help(L.t("link.sendRate.help"))
            }

            RestartBanner(model: model)
        }
        .formStyle(.grouped)
    }

    /// The offered speeds, in blocks a second. A value that is in the config but
    /// not on this list — somebody edited the file — is added rather than
    /// dropped, because a picker showing nothing at all is worse than a picker
    /// showing an odd number.
    private var rateChoices: [Int] {
        let offered = [1024, 2048, 5120, 10240, 25600]
        guard model.sendRate > 0, !offered.contains(model.sendRate) else { return offered }
        return (offered + [model.sendRate]).sorted()
    }

    /// Blocks a second read as nothing; megabytes a second read as a speed. The
    /// decimal only appears when dropping it would be a lie.
    private func rateTitle(_ blocks: Int) -> String {
        let perSecond = Double(blocks) * Double(Bulk.chunkSize) / (1024 * 1024)
        return L.t("unit.mbs", L.number(perSecond, decimals: perSecond == perSecond.rounded() ? 0 : 1))
    }
}

// MARK: - Diagnostics

struct DiagnosticsPane: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        Form {
            // "Check the link" is not repeated here: it lives on the
            // Connection pane, which is where §7.4 puts it and where somebody
            // looking for the address will already be.
            Section(L.t("diag.section.checks")) {
                HStack {
                    Button {
                        model.runProbe()
                    } label: {
                        Label(L.t("diag.checkMicrophone"), systemImage: "waveform.badge.magnifyingglass")
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

            Section(L.t("diag.section.files")) {
                LabeledContent(L.t("diag.log")) {
                    HStack {
                        Text(LaunchAgent.logURL.path)
                            .font(.dsCaption)
                            .truncationMode(.head)
                            .lineLimit(1)
                        Button(L.t("diag.reveal")) { model.revealLog() }
                    }
                }
                LabeledContent(L.t("diag.config")) {
                    Text(model.runtime.configPath.path)
                        .font(.dsCaption)
                        .truncationMode(.head)
                        .lineLimit(1)
                }
                Button(L.t("diag.copyReport")) { model.copyReport() }
                    .help(L.t("diag.copyReport.help"))
            }

            if let notice = model.noticeText {
                Section {
                    InlineAlert(
                        text: notice,
                        tone: .warn,
                        action: FeatureAction(title: L.t("action.gotIt")) { model.noticeText = nil }
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
                    text: L.t("restart.notice"),
                    tone: .warn,
                    action: FeatureAction(title: L.t("restart.action")) {
                        model.saveNow()
                        model.restartPipeline()
                    }
                )
            }
        }
    }
}
