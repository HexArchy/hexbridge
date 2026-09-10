import Foundation

/// The last stop between feature-authored text and the user.
///
/// A feature writes its status for whoever is reading — and that is sometimes
/// the log, sometimes the diagnostics report, sometimes a person who opened the
/// menu bar popover to find out whether the microphone works. Those are not the
/// same audience. `Network.NWError error 64 - Host is down`, `054C:0CE6` and
/// «приёмник ещё не подтвердил, что собрал виртуальное устройство» are all true,
/// all useful in a bug report, and all noise in a 340 pt popover.
///
/// So this is the presentation layer's own vocabulary pass: it never changes
/// what a feature *means*, only how much machinery leaks out with it. The
/// untouched original still goes to the log and to «Скопировать отчёт», which
/// is where it is worth having.
enum Wording {

    // MARK: - Machinery out

    /// Feature text with the protocol filed off.
    ///
    /// Three kinds of leak, in the order they are cheapest to fix:
    /// USB ids, hexadecimal status codes, and the protocol's word for the
    /// machine on the other end.
    static func plain(_ text: String) -> String {
        guard !text.isEmpty else { return text }
        if let human = humanError(text) { return human }

        var result = text
        for rule in scrubs {
            result = result.replacingOccurrences(
                of: rule.pattern,
                with: rule.replacement,
                options: .regularExpression
            )
        }
        for (jargon, plain) in glossary {
            result = result.replacingOccurrences(of: jargon, with: plain)
        }
        return tidy(result)
    }

    /// The first sentence only.
    ///
    /// A status line is one line (§7.0). Everything a feature adds after the
    /// full stop is elaboration, and elaboration belongs on the pane it
    /// elaborates, not in the sentence that answers "работает или нет".
    static func firstSentence(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let stop = trimmed.firstIndex(where: { $0 == "." }) else { return trimmed }
        // A decimal comma is written with a comma, but «1.2» would still split
        // here — so a full stop only ends a sentence when a space follows it.
        let after = trimmed.index(after: stop)
        guard after < trimmed.endIndex, trimmed[after] == " " else { return trimmed }
        return String(trimmed[trimmed.startIndex..<stop]) + "."
    }

    /// A system error turned into something a person can act on, or nil if the
    /// text was written for a person in the first place.
    ///
    /// The point is not to hide the failure — the state colour and the action
    /// still say something is wrong — but that «error 64» tells the user
    /// nothing they can use, and telling them three times over (heading,
    /// subtitle, red banner) tells them less than saying it once.
    static func humanError(_ text: String) -> String? {
        guard looksMechanical(text) else { return nil }
        for (marker, sentence) in errorMeanings where text.localizedCaseInsensitiveContains(marker) {
            return sentence
        }
        return "Не удалось соединиться с игровым ПК"
    }

    /// True when the string is a system error object printed with `\(error)`
    /// rather than a sentence somebody wrote.
    private static func looksMechanical(_ text: String) -> Bool {
        for marker in mechanicalMarkers where text.contains(marker) { return true }
        return false
    }

    // MARK: - Feature rows

    /// The second line of a feature row: what is happening *now*.
    ///
    /// The card already carries the feature's name above this line, so a
    /// headline that starts with that name stutters — «Буфер обмена / Буфер
    /// обмена общий», «Микрофон / Микрофон выключен». Dropping the repeated
    /// word is enough in most states; when all that is left is a bare adjective
    /// and nothing is wrong, the state word says it better.
    /// - Parameter avoiding: a sentence already on screen above this row —
    ///   the popover's summary is the worst feature's own headline, so without
    ///   this the card that produced it repeats it word for word two lines
    ///   below.
    static func stateLine(title: String, status: FeatureStatus, avoiding echoed: String = "") -> String {
        let headline = plain(status.headline)
        guard !headline.isEmpty else { return stateWord(status) }
        if !echoed.isEmpty, headline == plain(echoed) { return stateWord(status) }
        guard let tail = droppingTitle(title, from: headline) else {
            // Lower case throughout: this line is a caption under a name, and a
            // column of three where some start with a capital and some do not
            // reads as three unrelated things.
            return lowercasingFirst(headline)
        }
        if tail.split(separator: " ").count == 1, status.state == .live, status.tone == .ok {
            return stateWord(status)
        }
        return tail
    }

    /// The same five states, in the same five words, for every feature. §7.0:
    /// the user learns the rules once.
    static func stateWord(_ status: FeatureStatus) -> String {
        switch status.state {
        case .off: return "выключено"
        case .starting: return "запускается"
        case .waiting: return "ждёт Windows"
        case .live: return "работает"
        case .error: return "не работает"
        }
    }

    // MARK: - Actions

    /// True when pressing this button would do exactly what the switch beside
    /// it does.
    ///
    /// §6.1 already says the switch is the only way to turn a feature on and
    /// off; this is that rule applied to the buttons a feature offers, so that
    /// «Выключить общий буфер» never appears two centimetres from the switch
    /// labelled with the same feature.
    static func duplicatesSwitch(_ title: String) -> Bool {
        switchLabels.contains(title)
    }

    private static let switchLabels: Set<String> = [
        "Включить микрофон", "Выключить микрофон",
        "Включить общий буфер", "Выключить общий буфер",
        "Включить проброс", "Выключить проброс",
    ]

    // MARK: - Tables

    private static let scrubs: [(pattern: String, replacement: String)] = [
        // «DualSense Wireless Controller» 054C:0CE6 → «DualSense Wireless Controller»
        (#"\s*\b[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}\b"#, ""),
        // ", устройство отвечает 0xE00002C1" and any other bare hex code.
        (#",?\s*[^,.]*\b0x[0-9A-Fa-f]{4,}\b"#, ""),
    ]

    /// Protocol words that reached the surface. Longest first: «приёмнику»
    /// has to be replaced before «приёмник» can eat its stem.
    private static let glossary: [(String, String)] = [
        ("приёмник ещё не подтвердил, что собрал виртуальное устройство", "Windows его пока не видит"),
        ("output-репорты", "обратные команды"),
        ("приёмника", "Windows"),
        ("приёмнику", "Windows"),
        ("приёмнике", "Windows"),
        ("приёмником", "Windows"),
        ("Приёмник", "Windows"),
        ("приёмник", "Windows"),
        ("репортов", "отчётов"),
        ("репорты", "отчёты"),
    ]

    private static let mechanicalMarkers = [
        "NWError", "Errno", "OSStatus", "NSError", "Error Domain", "error 0x",
    ]

    private static let errorMeanings: [(String, String)] = [
        ("Host is down", "Игровой ПК не отвечает"),
        ("Network is unreachable", "Нет сети"),
        ("Connection refused", "Игровой ПК не принимает соединение"),
        ("No route to host", "До игрового ПК нет маршрута"),
        ("Operation timed out", "Игровой ПК не ответил вовремя"),
    ]

    // MARK: - Plumbing

    /// Drops the feature's own name off the front of its headline, or nil when
    /// the headline does not start with it.
    ///
    /// Compared by stem, because «Устройства» and «Устройство» are the same
    /// word to a reader and two different words to `hasPrefix`.
    private static func droppingTitle(_ title: String, from headline: String) -> String? {
        let titleWords = title.split(separator: " ").map(stem)
        let headlineWords = headline.split(separator: " ")
        guard titleWords.count < headlineWords.count else { return nil }
        for (index, word) in titleWords.enumerated() where stem(headlineWords[index]) != word {
            return nil
        }
        let tail = headlineWords.dropFirst(titleWords.count).joined(separator: " ")
        return lowercasingFirst(tail)
    }

    /// Crude Russian stemming: drop the inflectional tail so that «устройства»,
    /// «устройство» and «устройств» compare equal. Good enough for matching a
    /// feature's own name against its own headline, and used for nothing else.
    private static func stem(_ word: some StringProtocol) -> String {
        var text = word.lowercased().filter { $0.isLetter || $0 == "-" }
        let endings: Set<Character> = ["а", "о", "ы", "и", "е", "я", "ю", "ь", "й", "у"]
        while let last = text.last, endings.contains(last), text.count > 3 {
            text.removeLast()
        }
        return text
    }

    private static func lowercasingFirst(_ text: String) -> String {
        guard let first = text.first, first.isUppercase else { return text }
        let word = String(text.prefix(while: { $0 != " " })).filter { $0.isLetter }
        // A sentence loses its capital; a name keeps it.
        guard !properNouns.contains(word) else { return text }
        return first.lowercased() + text.dropFirst()
    }

    /// Words that are capitalised because of what they are, not because of
    /// where they stand in the sentence.
    private static let properNouns: Set<String> = [
        "Windows", "Mac", "macOS", "HexBridge", "Steam", "USB", "HID", "Bluetooth", "DualSense",
    ]

    private static func tidy(_ text: String) -> String {
        text
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s+([,.;:])"#, with: "$1", options: .regularExpression)
            .replacingOccurrences(of: #"«\s*»"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
