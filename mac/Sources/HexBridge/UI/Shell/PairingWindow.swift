import AppKit
import SwiftUI

/// «Подключиться к ПК» — the Mac half of the pairing wizard (§9.3).
///
/// The key is generated on Windows, never here: Windows is the side that
/// listens, so it is the side that knows the address, and the address has to
/// travel together with the key (§9.1). This window only ever *receives*.
///
/// Three ways in, in the order the document puts them:
///  • a `hexbridge://pair?…` link — pasted here, or opened from anywhere;
///  • a short code typed by hand, exchanged with the PC for the full link;
///  • automatic discovery on the local network.
///
/// QR scanning by camera is not implemented in this revision — see the note at
/// the bottom of the method chooser, which says so to the user rather than
/// hiding a dead button.
struct PairingWindow: View {
    @Bindable var model: AppModel

    enum Step: Int { case choose, code, check, done }

    @State private var step: Step = .choose
    @State private var linkText = ""
    @State private var codeText = ""
    @State private var pcAddress = ""
    @State private var busy = false
    @State private var failure: String?
    @State private var payload: Pairing.Payload?

    @Environment(\.palette) private var palette
    @Environment(\.motionSettings) private var motion

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            header

            Group {
                switch step {
                case .choose: chooser
                case .code: codeEntry
                case .check: LinkCheckView(model: model)
                case .done: doneScreen
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)

            Divider()
            footer
        }
        .padding(Space.xl)
        .frame(width: Metrics.wizardWidth, height: Metrics.wizardHeight)
        .background(palette.bg)
        .animation(Motion.emphasis(Motion.slow, reduced: motion.reduceMotion), value: step)
        .onAppear {
            // Reopening the window after a successful pairing should not drop
            // the user back on step one.
            if model.config.isConfigured, step == .choose, model.config.paired == true {
                step = .check
                model.runLinkCheck()
            }
        }
    }

    // MARK: - Chrome

    private var header: some View {
        VStack(alignment: .leading, spacing: Space.xs) {
            Text(titleText)
                .font(.dsDisplay)
                .foregroundStyle(palette.text)
            Text(subtitleText)
                .font(.dsBody)
                .foregroundStyle(palette.textDim)
                .fixedSize(horizontal: false, vertical: true)
            StepDots(count: 3, current: min(step.rawValue, 2))
                .padding(.top, Space.xs)
        }
    }

    private var titleText: String {
        switch step {
        case .choose: return "Подключиться к ПК"
        case .code: return "Ввести код"
        case .check: return "Проверка связи"
        case .done: return model.linkCheck.verdict ?? "Готово"
        }
    }

    private var subtitleText: String {
        switch step {
        case .choose:
            return "Ключ создаётся на игровом ПК и переносится сюда целиком вместе с адресом. На Mac ничего генерировать не нужно."
        case .code:
            return "Введите двенадцать символов, которые HexBridge показал на экране ПК, и адрес ПК в сети."
        case .check:
            return "Шесть проверок подряд. Каждая говорит, что именно работает, а что нет."
        case .done:
            return ""
        }
    }

    private var footer: some View {
        HStack(spacing: Space.sm) {
            if step != .choose {
                Button("Назад") { step = .choose }
                    .buttonStyle(.dsSecondary)
            }
            Spacer()
            if let failure {
                Text(failure)
                    .font(.dsCaption)
                    .foregroundStyle(palette.badText)
                    .lineLimit(2)
                    .frame(maxWidth: 360, alignment: .trailing)
            }
            if step == .check {
                Button(model.linkCheck.running ? "Идёт проверка…" : "Проверить ещё раз") {
                    model.runLinkCheck()
                }
                .buttonStyle(.dsSecondary)
                .disabled(model.linkCheck.running)
            }
            Button("Закрыть") {
                NSApp.keyWindow?.close()
            }
            .buttonStyle(.dsSecondary)
        }
    }

    // MARK: - Step 1: how

    private var chooser: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Card {
                VStack(alignment: .leading, spacing: Space.sm) {
                    SectionLabel(text: "Ссылка с ПК")
                    Text("Самый короткий путь: скопируйте ссылку с экрана ПК и вставьте её сюда. Ссылка несёт и адрес, и ключ, и имя машины.")
                        .font(.dsCaption)
                        .foregroundStyle(palette.textDim)
                        .fixedSize(horizontal: false, vertical: true)
                    HStack(spacing: Space.sm) {
                        TextField("hexbridge://pair?v=1&h=…", text: $linkText)
                            .textFieldStyle(.roundedBorder)
                            .font(.dsMono)
                        Button("Вставить") {
                            linkText = NSPasteboard.general.string(forType: .string) ?? ""
                        }
                        Button("Связать") { applyLink() }
                            .buttonStyle(.dsPrimary)
                            .fixedSize()
                            .disabled(linkText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                }
            }

            HStack(alignment: .top, spacing: Space.lg) {
                Card {
                    VStack(alignment: .leading, spacing: Space.sm) {
                        SectionLabel(text: "Короткий код")
                        Text("Если ссылку скопировать некуда — на ПК есть код из двенадцати символов.")
                            .font(.dsCaption)
                            .foregroundStyle(palette.textDim)
                            .fixedSize(horizontal: false, vertical: true)
                        Button("Ввести код") { step = .code }
                            .buttonStyle(.dsSecondary)
                    }
                }

                Card {
                    VStack(alignment: .leading, spacing: Space.sm) {
                        SectionLabel(text: "Автопоиск в сети")
                        discoveryBody
                    }
                }
            }

            Text("Сканирования QR камерой в этой версии нет. Оно требует разрешения на камеру и появится вместе с экраном QR на стороне Windows — до тех пор кнопки, которая ничего не делает, здесь не будет.")
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder
    private var discoveryBody: some View {
        if model.discovery.hosts.isEmpty {
            Text(model.discovery.searching
                 ? "Ищу HexBridge в локальной сети…"
                 : "Mac находит игровой ПК сам, если тот в той же сети и HexBridge на нём запущен.")
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .fixedSize(horizontal: false, vertical: true)
            if let error = model.discovery.lastError {
                Text("Поиск не работает: \(error). Ссылка и код работают как обычно.")
                    .font(.dsCaption)
                    .foregroundStyle(palette.badText)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button(model.discovery.searching ? "Остановить поиск" : "Искать") {
                if model.discovery.searching {
                    model.discovery.stop()
                } else {
                    model.discovery.start()
                }
            }
            .buttonStyle(.dsSecondary)
        } else {
            ForEach(model.discovery.hosts) { found in
                CheckRow(
                    title: found.name,
                    detail: [found.target, model.discovery.note(for: found)]
                        .filter { !$0.isEmpty }
                        .joined(separator: " · "),
                    state: model.discovery.isOurs(found) ? .ok : .pending,
                    action: FeatureAction(title: "Выбрать") {
                        pcAddress = found.address.isEmpty ? found.name : found.address
                        step = .code
                    }
                )
            }

            // §9.3, and the reason autodiscovery is safe to have at all: the
            // list is a shortcut past typing an address, never past the code.
            // Without this sentence a user looking at one row labelled «связан
            // с другим Mac» has no way to know why nothing happened.
            Text("Найденный ПК ещё нужно подтвердить кодом с его экрана — сам по себе Mac подключается только к ПК, с которым уже связан.")
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: - Step 1в: short code

    private var codeEntry: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Card {
                VStack(alignment: .leading, spacing: Space.md) {
                    SectionLabel(text: "Код с экрана ПК")
                    TextField("ABCD-EFGH-JKLM", text: $codeText)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(size: 20, design: .monospaced))
                        .onChange(of: codeText) { _, new in
                            // Uppercase, drop I/O/0/1, regroup — on every
                            // keystroke, so the field can never hold something
                            // the PC would refuse.
                            let formatted = Pairing.formatCode(new)
                            if formatted != new { codeText = formatted }
                        }

                    SectionLabel(text: "Адрес ПК в сети")
                    TextField("192.168.1.10", text: $pcAddress)
                        .textFieldStyle(.roundedBorder)
                        .font(.dsMono)
                    Text("HexBridge на ПК показывает этот адрес рядом с кодом. Обмен идёт на порт \(Pairing.exchangePort(forDataPort: 47702)) и живёт три минуты.")
                        .font(.dsCaption)
                        .foregroundStyle(palette.textDim)

                    HStack(spacing: Space.sm) {
                        Button(busy ? "Спрашиваю ПК…" : "Получить ключ") { exchange() }
                            .buttonStyle(.dsPrimary)
                            .fixedSize()
                            .disabled(busy || !Pairing.isCompleteCode(codeText) || pcAddress.isEmpty)
                        if busy { Spinner() }
                        Spacer()
                    }
                }
            }

            InlineAlert(
                text: "Обмен по короткому коду на стороне Windows пишется отдельно и на момент сборки этой версии ещё не существовал. Если ПК не отвечает — это ожидаемо; воспользуйтесь ссылкой.",
                tone: .warn
            )
        }
    }

    // MARK: - Done

    private var doneScreen: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            SuccessTick()
            Text(model.config.peerName.map { "Звук идёт на «\($0)»." } ?? "Связь настроена.")
                .font(.dsBody)
                .foregroundStyle(palette.text)
            Text("В играх выбирайте микрофон Steam Streaming Microphone.")
                .font(.dsCaption)
                .foregroundStyle(palette.textDim)
        }
    }

    // MARK: - Actions

    private func applyLink() {
        failure = nil
        switch Pairing.parse(text: linkText) {
        case .success(let value):
            payload = value
            model.apply(value)
            step = .check
            model.runLinkCheck()
        case .failure(let error):
            failure = error.description
        }
    }

    private func exchange() {
        failure = nil
        busy = true
        let host = pcAddress.trimmingCharacters(in: .whitespaces)
        let code = codeText
        Task {
            let result = await PairingExchange.fetch(
                host: host,
                port: Pairing.exchangePort(forDataPort: 47702),
                code: code
            )
            busy = false
            switch result {
            case .success(let value):
                payload = value
                model.apply(value)
                step = .check
                model.runLinkCheck()
            case .failure(let error):
                failure = error.description
            }
        }
    }
}

// MARK: - Link check screen

/// §9.4 — the six checks, and the one screen that is also reachable from the
/// settings window at any time.
struct LinkCheckView: View {
    @Bindable var model: AppModel

    @Environment(\.palette) private var palette

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            if let verdict = model.linkCheck.verdict {
                StatusCard(status: FeatureStatus(
                    state: model.linkCheck.verdictTone == .ok ? .live : .error,
                    tone: model.linkCheck.verdictTone,
                    headline: verdict,
                    detail: model.linkCheck.verdictTone == .ok
                        ? (model.config.peerName.map { "Звук идёт на «\($0)». В играх выбирайте микрофон Steam Streaming Microphone." } ?? "Приёмник отвечает.")
                        : "Ниже раскрыт только тот пункт, который не прошёл."
                ))
            }

            Card {
                VStack(alignment: .leading, spacing: Space.md) {
                    ForEach(model.linkCheck.rows) { row in
                        VStack(alignment: .leading, spacing: Space.xs) {
                            CheckRow(title: row.title, detail: row.detail, state: row.state)
                            if row.state == .failed, let explanation = row.explanation {
                                Text(explanation)
                                    .font(.dsCaption)
                                    .foregroundStyle(palette.textDim)
                                    .fixedSize(horizontal: false, vertical: true)
                                    .padding(.leading, Space.xl)
                            }
                        }
                    }
                }
            }

            if !model.linkCheck.running, model.linkCheck.finished, model.linkCheck.verdictTone == .ok {
                SuccessTick()
            }
        }
    }
}

/// §9.4: the one celebratory moment allowed in the whole app — a tick drawn
/// with a stroke over 320 ms and one pulse. No confetti.
struct SuccessTick: View {
    @Environment(\.palette) private var palette
    @Environment(\.motionSettings) private var motion

    @State private var progress: CGFloat = 0
    @State private var scale: CGFloat = 1

    var body: some View {
        Path { path in
            path.move(to: CGPoint(x: 6, y: 17))
            path.addLine(to: CGPoint(x: 13, y: 24))
            path.addLine(to: CGPoint(x: 26, y: 8))
        }
        .trim(from: 0, to: progress)
        .stroke(palette.okFg, style: StrokeStyle(lineWidth: 3, lineCap: .round, lineJoin: .round))
        .frame(width: 32, height: 32)
        .scaleEffect(scale)
        .onAppear {
            guard !motion.reduceMotion else {
                progress = 1
                return
            }
            withAnimation(Motion.spring(Motion.long)) { progress = 1 }
            withAnimation(Motion.standard(Motion.base).delay(Motion.long)) { scale = 1.06 }
            withAnimation(Motion.standard(Motion.base).delay(Motion.long + Motion.base)) { scale = 1 }
        }
        .accessibilityLabel("Проверка пройдена")
    }
}
