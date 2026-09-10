import Foundation
import HexBridgeText

/// The last stop between feature-authored text and the user.
///
/// A feature writes its status for whoever is reading — and that is sometimes
/// the log, sometimes the diagnostics report, sometimes a person who opened the
/// menu bar popover to find out whether the microphone works. Those are not the
/// same audience. `Network.NWError error 64 - Host is down` and `054C:0CE6` are
/// both true, both useful in a bug report, and both noise in a 340 pt popover.
///
/// What used to live here as well was a Russian jargon table: the features said
/// «приёмник» and this layer rewrote it as «Windows» on the way to the screen.
/// That is now settled where it belongs, in `Localizable.strings`, which the
/// shared glossary governs on both platforms. What is left is the part that is
/// about machinery rather than about language, and it is language-independent:
/// a hex status code is noise in English too.
enum Wording {

    // MARK: - Machinery out

    /// Feature text with the protocol filed off.
    ///
    /// Two kinds of leak: USB ids and hexadecimal status codes, both of which
    /// arrive interpolated from the system rather than written by us.
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
        return tidy(result)
    }

    /// The first sentence only.
    ///
    /// A status line is one line (§7.0). Everything a feature adds after the
    /// full stop is elaboration, and elaboration belongs on the pane it
    /// elaborates, not in the sentence that answers "does it work or not".
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
    ///
    /// Matched on the English text and on the POSIX number, because both are
    /// what `\(error)` prints whatever language the app is running in.
    static func humanError(_ text: String) -> String? {
        guard looksMechanical(text) else { return nil }
        for (marker, key) in errorMeanings where text.localizedCaseInsensitiveContains(marker) {
            return L.t(key)
        }
        return L.t("error.generic")
    }

    /// True when the string is a system error object printed with `\(error)`
    /// rather than a sentence somebody wrote.
    private static func looksMechanical(_ text: String) -> Bool {
        for marker in mechanicalMarkers where text.contains(marker) { return true }
        return false
    }

    // MARK: - Feature rows

    /// The second line of a feature row in the popover.
    ///
    /// One word, from the fixed list in docs/GLOSSARY.md, the same seven words
    /// for every feature. The card already carries the feature's name in bold
    /// directly above, so a headline here stutters — «Буфер обмена / Буфер
    /// обмена общий» was two lines to say one thing — and three cards whose
    /// second lines are three different shapes of sentence do not read as a
    /// column at all.
    static func stateWord(_ status: FeatureStatus) -> String {
        L.t("state.\(status.word.rawValue)")
    }

    // MARK: - Tables

    private static let scrubs: [(pattern: String, replacement: String)] = [
        // «DualSense Wireless Controller» 054C:0CE6 → «DualSense Wireless Controller»
        (#"\s*\b[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}\b"#, ""),
        // ", the device answers 0xE00002C1" and any other bare hex code.
        (#",?\s*[^,.]*\b0x[0-9A-Fa-f]{4,}\b"#, ""),
    ]

    private static let mechanicalMarkers = [
        "NWError", "Errno", "OSStatus", "NSError", "Error Domain", "error 0x",
    ]

    /// Marker → key. `Network.framework` localises the sentence but not the
    /// number, so both forms of each meaning are listed.
    private static let errorMeanings: [(String, String)] = [
        ("Host is down", "error.hostDown"),
        ("error 64", "error.hostDown"),
        ("Network is unreachable", "error.noNetwork"),
        ("error 51", "error.noNetwork"),
        ("error 50", "error.noNetwork"),
        ("Connection refused", "error.refused"),
        ("error 61", "error.refused"),
        ("No route to host", "error.noRoute"),
        ("error 65", "error.noRoute"),
        ("Operation timed out", "error.timedOut"),
        ("error 60", "error.timedOut"),
    ]

    // MARK: - Said before an update, not after

    /// HexBridge is signed ad-hoc rather than with a Developer ID, and an
    /// ad-hoc signature has no stable identity: the code hash changes with
    /// every build, so after an update macOS treats this as a new application
    /// and TCC asks for the microphone again. Nothing has been reset and
    /// nothing is broken — but a permission prompt right after an update reads
    /// as damage unless it was announced first.
    static var updateWillReaskForMicrophone: String {
        L.t("update.reasksForMicrophone")
    }

    private static func tidy(_ text: String) -> String {
        text
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s+([,.;:])"#, with: "$1", options: .regularExpression)
            .replacingOccurrences(of: #"«\s*»"#, with: "", options: .regularExpression)
            .replacingOccurrences(of: #"“\s*”"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
