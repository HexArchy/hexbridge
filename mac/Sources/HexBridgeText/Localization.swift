import Foundation

/// The language the interface is written in.
///
/// Two languages and a "follow the system" option, which is exactly the shape
/// the Windows side offers. `system` is not a third language: it resolves to one
/// of the other two at launch and is stored as a preference rather than as a
/// result, so a user who moves their Mac from English to Russian gets a Russian
/// HexBridge without touching anything.
public enum AppLanguage: String, CaseIterable, Codable, Sendable {
    case system
    case en
    case ru

    /// The language actually used when this option is chosen.
    public var resolved: String {
        switch self {
        case .en: return "en"
        case .ru: return "ru"
        case .system: return AppLanguage.systemChoice
        }
    }

    /// What the system asks for, narrowed to something we have strings for.
    ///
    /// `Locale.preferredLanguages` is ordered by preference and carries region
    /// tags — `ru-RU`, `en-GB` — so only the language subtag is compared, and the
    /// first one we can honour wins. English rather than Russian when neither is
    /// on the list: English is the default language of the app.
    static var systemChoice: String {
        for tag in Locale.preferredLanguages {
            let code = String(tag.split(separator: "-").first ?? "")
            if code == "ru" { return "ru" }
            if code == "en" { return "en" }
        }
        return "en"
    }

    /// Every language the app carries strings for, in menu order.
    public static let available: [AppLanguage] = [.system, .en, .ru]
}

/// The one plural distinction the two languages need between them.
///
/// English has two forms and Russian has three, so the table carries four keys
/// per counted noun and each language picks from the ones it uses. Doing this in
/// code rather than in a `.stringsdict` is deliberate: the plural rule has to
/// follow the language the *user chose*, and Foundation's stringsdict machinery
/// follows the language the *system* is in. Those are the same thing right up
/// until somebody switches the app to English on a Russian Mac.
public enum PluralCategory: String, Sendable, CaseIterable {
    case one, few, many, other

    /// CLDR cardinal rules, restricted to integers — which is all this app counts.
    public static func of(_ count: Int, language: String) -> PluralCategory {
        let n = abs(count)
        switch language {
        case "ru":
            let mod10 = n % 10
            let mod100 = n % 100
            if mod10 == 1, mod100 != 11 { return .one }
            if (2...4).contains(mod10), !(12...14).contains(mod100) { return .few }
            return .many
        default:
            return n == 1 ? .one : .other
        }
    }
}

/// Everything the interface says, in the language it was asked for.
///
/// One global rather than an injected dependency, for two reasons. Text is
/// produced far from the views — a capture error is worded on the audio thread,
/// a device failure on the HID queue — and threading a translator through those
/// layers would put the presentation layer inside the transport. And the choice
/// is genuinely global: there is one window, one popover and one user.
///
/// Reads are lock-protected and safe from any thread. Writes happen twice: once
/// at launch, once when somebody moves the picker in settings.
public enum L {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var state = Store(language: .system)

    // MARK: - Choosing

    /// Applies a language. Call once at launch and on every change of the picker.
    public static func select(_ language: AppLanguage) {
        lock.lock()
        state = Store(language: language)
        lock.unlock()
    }

    public static var language: AppLanguage {
        lock.lock()
        defer { lock.unlock() }
        return state.language
    }

    /// `en` or `ru` — never `system`.
    public static var code: String {
        lock.lock()
        defer { lock.unlock() }
        return state.code
    }

    /// The locale numbers and dates are formatted in. Tied to the chosen
    /// language, not to the system region: a comma in an otherwise English
    /// sentence reads as a typo.
    public static var locale: Locale {
        lock.lock()
        defer { lock.unlock() }
        return state.locale
    }

    // MARK: - Looking up

    /// One string, by key. A missing key returns the key, which is loud enough
    /// to notice on screen and is checked for by the tests rather than by eye.
    public static func t(_ key: String) -> String {
        lock.lock()
        let store = state
        lock.unlock()
        return store.string(key)
    }

    /// A string with substitutions. The argument list has to match the format in
    /// both tables; `LocalizationTests` compares the two and fails if it does not.
    public static func t(_ key: String, _ arguments: any CVarArg...) -> String {
        lock.lock()
        let store = state
        lock.unlock()
        return String(format: store.string(key), locale: store.locale, arguments: arguments)
    }

    /// A counted noun. The table holds `<key>.one`, `.few`, `.many` and `.other`;
    /// the language decides which one is asked for and `%d` carries the number.
    public static func plural(_ key: String, _ count: Int) -> String {
        lock.lock()
        let store = state
        lock.unlock()
        let category = PluralCategory.of(count, language: store.code)
        let format = store.string("\(key).\(category.rawValue)", fallback: "\(key).other")
        return String(format: format, locale: store.locale, count)
    }

    /// True when the table has this key. Used by the diagnostics report, which
    /// must not print a key name at a user.
    public static func has(_ key: String) -> Bool {
        lock.lock()
        let store = state
        lock.unlock()
        return store.lookup(key) != nil
    }

    // MARK: - Numbers

    /// A number with a fixed number of decimals, in the language's own notation:
    /// `12.5` in English, `12,5` in Russian.
    public static func number(_ value: Double, decimals: Int = 1) -> String {
        lock.lock()
        let store = state
        lock.unlock()
        return store.number(value, decimals: decimals)
    }

    public static func integer(_ value: Int) -> String {
        number(Double(value), decimals: 0)
    }

    /// The same, for the unsigned counters the wire protocol keeps.
    public static func integer(_ value: some BinaryInteger) -> String {
        number(Double(value), decimals: 0)
    }

    /// `12.5 %` / `12,5 %`. A non-breaking space before the sign, because a
    /// percentage split across two lines is a number without a unit.
    public static func percent(_ value: Double, decimals: Int = 1) -> String {
        t("unit.percent", number(value, decimals: decimals))
    }

    /// `24 ms` / `24 мс`.
    public static func milliseconds(_ value: Double) -> String {
        t("unit.ms", number(value, decimals: 0))
    }

    /// `32 kbit/s` / `32 кбит/с`, from bits per second.
    public static func kilobits(perSecond bits: Int) -> String {
        t("unit.kbits", integer(bits / 1000))
    }

    /// `48000 Hz` / `48000 Гц`.
    public static func hertz(_ value: Int) -> String {
        t("unit.hz", integer(value))
    }

    /// A size in binary units, the way a clipboard object is described.
    public static func bytes(_ count: Int) -> String {
        if count >= 1024 * 1024 { return t("unit.mb", number(Double(count) / (1024 * 1024))) }
        if count >= 1024 { return t("unit.kb", number(Double(count) / 1024)) }
        return t("unit.bytes", integer(count))
    }

    /// How long something has been running: `48 s`, `12 min`, `3 h 05 min`.
    public static func duration(_ interval: TimeInterval) -> String {
        let seconds = Int(interval)
        if seconds < 60 { return t("unit.seconds", integer(seconds)) }
        if seconds < 3600 { return t("unit.minutes", integer(seconds / 60)) }
        return t("unit.hoursMinutes", integer(seconds / 3600), String(format: "%02d", (seconds % 3600) / 60))
    }

    /// How long ago something happened, in the four steps a person cares about.
    public static func ago(_ interval: TimeInterval) -> String {
        if interval < 10 { return t("ago.justNow") }
        if interval < 60 { return t("ago.seconds", integer(Int(interval))) }
        if interval < 3600 { return t("ago.minutes", integer(Int(interval / 60))) }
        return t("ago.hours", integer(Int(interval / 3600)))
    }

    /// A date and time, short form, in the chosen language.
    public static func timestamp(_ date: Date) -> String {
        lock.lock()
        let store = state
        lock.unlock()
        return store.timestamp(date)
    }

    // MARK: - The table

    /// One resolved language: its bundle, its locale and its formatters.
    ///
    /// Built once per language change and then read-only, so the lock above only
    /// ever guards a pointer swap rather than every formatter inside it.
    private final class Store: @unchecked Sendable {
        let language: AppLanguage
        let code: String
        let locale: Locale

        private let bundle: Bundle?
        private let fallback: Bundle?
        private let numbers: NumberFormatter
        private let dates: DateFormatter

        init(language: AppLanguage) {
            self.language = language
            let code = language.resolved
            self.code = code
            self.locale = Locale(identifier: code)
            self.bundle = StringsBundle.lproj(code)
            // English is the language the table is written in, so it is also
            // what a key missing from the other file falls back to. A missing
            // Russian line shows English text rather than a key name.
            self.fallback = code == "en" ? nil : StringsBundle.lproj("en")

            let numbers = NumberFormatter()
            numbers.locale = locale
            numbers.numberStyle = .decimal
            // Grouping separators inside a 340 pt popover cost more than they
            // buy: «12 345 пакетов» wraps where «12345» does not.
            numbers.usesGroupingSeparator = false
            self.numbers = numbers

            let dates = DateFormatter()
            dates.locale = locale
            dates.dateStyle = .short
            dates.timeStyle = .short
            self.dates = dates
        }

        func lookup(_ key: String) -> String? {
            // `localizedString(forKey:value:)` cannot say "missing" — it returns
            // the key. A sentinel value can, and a sentinel is the only way to
            // fall through to English on a key the Russian file has not got.
            let missing = "\u{0}missing\u{0}"
            if let bundle {
                let value = bundle.localizedString(forKey: key, value: missing, table: nil)
                if value != missing { return value }
            }
            if let fallback {
                let value = fallback.localizedString(forKey: key, value: missing, table: nil)
                if value != missing { return value }
            }
            return nil
        }

        func string(_ key: String, fallback secondKey: String? = nil) -> String {
            if let value = lookup(key) { return value }
            if let secondKey, let value = lookup(secondKey) { return value }
            return key
        }

        func number(_ value: Double, decimals: Int) -> String {
            numbers.minimumFractionDigits = decimals
            numbers.maximumFractionDigits = decimals
            return numbers.string(from: NSNumber(value: value)) ?? String(value)
        }

        func timestamp(_ date: Date) -> String {
            dates.string(from: date)
        }
    }
}

/// Finds the `.lproj` directories, wherever this binary happens to be running from.
///
/// `Bundle.module` is deliberately not used. Its generated accessor looks in two
/// places — the package's build directory and `Bundle.main.bundleURL` — and calls
/// `fatalError` when it finds neither. `HexBridge.app` is neither: the strings
/// live in `Contents/Resources`, which is where every other Mac application puts
/// them and where `Bundle.main` looks by itself. So the search is written out,
/// covers the app bundle, a bare `.build/release` binary and the test runner, and
/// returns nil instead of killing the process.
enum StringsBundle {
    private final class Anchor {}

    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: [String: Bundle] = [:]

    /// The bundle holding one language's strings, or nil when the resources did
    /// not travel with the binary.
    static func lproj(_ code: String) -> Bundle? {
        lock.lock()
        defer { lock.unlock() }
        if let known = cache[code] { return known }
        for root in roots {
            guard let path = root.path(forResource: code, ofType: "lproj"),
                  let bundle = Bundle(path: path) else { continue }
            cache[code] = bundle
            return bundle
        }
        return nil
    }

    /// Bundles that could hold `en.lproj`, most likely first.
    private static var roots: [Bundle] {
        // The name SwiftPM gives the resource bundle of this target.
        let packaged = "HexBridge_HexBridgeText.bundle"
        var urls: [URL] = []

        // 1. HexBridge.app — Contents/Resources/en.lproj, put there by
        //    mac/scripts/build-app.sh.
        if let resources = Bundle.main.resourceURL { urls.append(resources) }

        // 2. A bare binary from .build/release, and the same path inside an app
        //    bundle in case the resource bundle was copied wholesale.
        let neighbours = [
            Bundle.main.bundleURL,
            Bundle.main.resourceURL,
            Bundle(for: Anchor.self).bundleURL.deletingLastPathComponent(),
            Bundle(for: Anchor.self).resourceURL,
        ]
        for neighbour in neighbours.compactMap({ $0 }) {
            urls.append(neighbour.appendingPathComponent(packaged))
        }

        // 3. The test runner, whose own bundle carries the resources directly.
        if let resources = Bundle(for: Anchor.self).resourceURL { urls.append(resources) }

        return urls.compactMap(Bundle.init(url:))
    }
}
