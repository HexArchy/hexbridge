import Foundation
import Testing

@testable import HexBridgeText

/// What holds the two string tables together.
///
/// The interesting failures here are not "a word is wrong" — no test can catch
/// that — but "one file has a key the other does not" and "the two formats take
/// different arguments". The first shows a key name on screen; the second is a
/// crash, because `String(format:)` reads whatever is at the end of the varargs
/// list when a format asks for one more than it was given.
///
/// The tables are read from the source tree rather than from a bundle. That is
/// deliberate: these tests are about the files a translator edits, and the
/// bundle is a copy of them made by the build.
struct StringTable {
    let language: String
    let entries: [String: String]
    let order: [String]

    static let en = StringTable(language: "en")
    static let ru = StringTable(language: "ru")

    init(language: String) {
        self.language = language
        let url = StringTable.resources
            .appendingPathComponent("\(language).lproj/Localizable.strings")
        let text = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
        var entries: [String: String] = [:]
        var order: [String] = []
        for (key, value) in StringTable.parse(text) {
            entries[key] = value
            order.append(key)
        }
        self.entries = entries
        self.order = order
    }

    static var resources: URL {
        // .../Tests/HexBridgeTextTests/LocalizationTests.swift → .../Sources/HexBridgeText/Resources
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Sources/HexBridgeText/Resources")
    }

    static var sources: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Sources")
    }

    /// A deliberately small `.strings` reader: `"key" = "value";`, with `/* */`
    /// comments and the handful of escapes these files actually use.
    ///
    /// `PropertyListSerialization` would also read them, but it returns a
    /// dictionary — and one of the things worth checking is that a key is not
    /// written twice, which a dictionary has already silently forgiven.
    static func parse(_ text: String) -> [(String, String)] {
        var pairs: [(String, String)] = []
        var scalars = Array(text.unicodeScalars)
        var index = 0

        func skipTrivia() {
            while index < scalars.count {
                let scalar = scalars[index]
                if scalar == " " || scalar == "\n" || scalar == "\t" || scalar == "\r" {
                    index += 1
                } else if scalar == "/", index + 1 < scalars.count, scalars[index + 1] == "*" {
                    index += 2
                    while index + 1 < scalars.count, !(scalars[index] == "*" && scalars[index + 1] == "/") {
                        index += 1
                    }
                    index = min(index + 2, scalars.count)
                } else if scalar == "/", index + 1 < scalars.count, scalars[index + 1] == "/" {
                    while index < scalars.count, scalars[index] != "\n" { index += 1 }
                } else {
                    return
                }
            }
        }

        func readQuoted() -> String? {
            guard index < scalars.count, scalars[index] == "\"" else { return nil }
            index += 1
            var out = String.UnicodeScalarView()
            while index < scalars.count, scalars[index] != "\"" {
                if scalars[index] == "\\", index + 1 < scalars.count {
                    let escape = scalars[index + 1]
                    index += 2
                    switch escape {
                    case "n": out.append("\n")
                    case "t": out.append("\t")
                    case "\"": out.append("\"")
                    case "\\": out.append("\\")
                    case "U", "u":
                        var hex = ""
                        while hex.count < 4, index < scalars.count, scalars[index].properties.isASCIIHexDigit {
                            hex.append(Character(scalars[index]))
                            index += 1
                        }
                        if let code = UInt32(hex, radix: 16), let value = Unicode.Scalar(code) {
                            out.append(value)
                        }
                    default: out.append(escape)
                    }
                } else {
                    out.append(scalars[index])
                    index += 1
                }
            }
            index += 1
            return String(String.UnicodeScalarView(out))
        }

        while true {
            skipTrivia()
            guard let key = readQuoted() else { break }
            skipTrivia()
            guard index < scalars.count, scalars[index] == "=" else { break }
            index += 1
            skipTrivia()
            guard let value = readQuoted() else { break }
            skipTrivia()
            if index < scalars.count, scalars[index] == ";" { index += 1 }
            pairs.append((key, value))
        }
        scalars = []
        return pairs
    }

    /// The substitutions a format asks for, in order: `%@`, `%d`, `%1$@`, `%%`.
    ///
    /// Returned as a list rather than a count, because two formats agreeing on
    /// how many arguments they take and disagreeing on the types is the failure
    /// that crashes rather than the one that reads oddly.
    static func specifiers(in format: String) -> [String] {
        var found: [String] = []
        let scalars = Array(format)
        var index = 0
        while index < scalars.count {
            guard scalars[index] == "%" else {
                index += 1
                continue
            }
            var cursor = index + 1
            var spec = "%"
            // A positional argument: `%2$@`.
            var position = ""
            while cursor < scalars.count, scalars[cursor].isNumber {
                position.append(scalars[cursor])
                cursor += 1
            }
            if cursor < scalars.count, scalars[cursor] == "$" {
                spec += position + "$"
                cursor += 1
            } else {
                cursor = index + 1
            }
            guard cursor < scalars.count else { break }
            spec.append(scalars[cursor])
            found.append(spec)
            index = cursor + 1
        }
        // `%%` is a literal per cent sign, not an argument.
        return found.filter { $0 != "%%" }
    }
}

// MARK: - The two files agree

@Suite("Таблицы строк")
struct StringTableTests {

    @Test("Обе таблицы читаются и не пусты")
    func bothTablesLoad() {
        #expect(StringTable.en.entries.count > 200)
        #expect(StringTable.ru.entries.count > 200)
    }

    @Test("У каждого ключа есть обе строки")
    func everyKeyExistsInBothLanguages() {
        let en = Set(StringTable.en.entries.keys)
        let ru = Set(StringTable.ru.entries.keys)

        let missingInRussian = en.subtracting(ru).sorted()
        let missingInEnglish = ru.subtracting(en).sorted()

        #expect(missingInRussian.isEmpty, "нет в ru.lproj: \(missingInRussian.joined(separator: ", "))")
        #expect(missingInEnglish.isEmpty, "нет в en.lproj: \(missingInEnglish.joined(separator: ", "))")
    }

    @Test("Ни один ключ не написан дважды")
    func noKeyIsDefinedTwice() {
        for table in [StringTable.en, StringTable.ru] {
            var seen: Set<String> = []
            var duplicates: [String] = []
            for key in table.order where !seen.insert(key).inserted {
                duplicates.append(key)
            }
            #expect(duplicates.isEmpty, "\(table.language): дубли \(duplicates.joined(separator: ", "))")
        }
    }

    @Test("Ни одна строка не пуста")
    func noValueIsEmpty() {
        for table in [StringTable.en, StringTable.ru] {
            let blank = table.entries.filter { $0.value.trimmingCharacters(in: .whitespaces).isEmpty }
            #expect(blank.isEmpty, "\(table.language): пустые значения \(blank.keys.sorted())")
        }
    }

    @Test("Подстановки совпадают по числу и по типу")
    func substitutionsMatchInCountAndKind() {
        var mismatches: [String] = []
        for (key, english) in StringTable.en.entries {
            guard let russian = StringTable.ru.entries[key] else { continue }
            let left = StringTable.specifiers(in: english)
            let right = StringTable.specifiers(in: russian)
            if left != right {
                mismatches.append("\(key): en\(left) ru\(right)")
            }
        }
        #expect(mismatches.isEmpty, "\(mismatches.joined(separator: "; "))")
    }

    @Test("У счётных существительных есть все четыре формы")
    func everyPluralKeyCarriesAllFourForms() {
        // The convention the lookup depends on, and therefore a convention no
        // other key may borrow: a key ending in one of the four category names
        // is a plural form, and its three siblings have to exist.
        let suffixes = PluralCategory.allCases.map { ".\($0.rawValue)" }
        for table in [StringTable.en, StringTable.ru] {
            let stems = Set(table.entries.keys.compactMap { key -> String? in
                guard let suffix = suffixes.first(where: { key.hasSuffix($0) }) else { return nil }
                return String(key.dropLast(suffix.count))
            })
            for stem in stems.sorted() {
                for suffix in suffixes {
                    #expect(
                        table.entries[stem + suffix] != nil,
                        "\(table.language): у «\(stem)» нет формы \(suffix)"
                    )
                }
            }
        }
    }

    @Test("Восклицательных знаков в интерфейсе нет")
    func nothingShouts() {
        // docs/GLOSSARY.md, последний абзац. Ловится дёшево, а стоит дорого.
        for table in [StringTable.en, StringTable.ru] {
            let shouting = table.entries.filter { $0.value.contains("!") }.keys.sorted()
            #expect(shouting.isEmpty, "\(table.language): \(shouting.joined(separator: ", "))")
        }
    }

    @Test("Слов «приёмник» и «receiver» в интерфейсе нет")
    func theForbiddenWordsAreAbsent() {
        // docs/GLOSSARY.md: человек думает не про роли, а про то, где микрофон.
        let forbidden = ["receiver", "sender", "приёмник", "приемник", "отправител"]
        var hits: [String] = []
        for table in [StringTable.en, StringTable.ru] {
            for (key, value) in table.entries {
                for word in forbidden where value.localizedCaseInsensitiveContains(word) {
                    hits.append("\(table.language)/\(key): \(word)")
                }
            }
        }
        #expect(hits.isEmpty, "\(hits.sorted().joined(separator: "; "))")
    }
}

// MARK: - The table and the code agree

@Suite("Ключи и код")
struct KeyUsageTests {

    static let swiftSources: [String] = {
        var texts: [String] = []
        let files = FileManager.default.enumerator(
            at: StringTable.sources, includingPropertiesForKeys: nil
        )
        while let url = files?.nextObject() as? URL {
            guard url.pathExtension == "swift" else { continue }
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            texts.append(text)
        }
        return texts
    }()

    static func captures(_ pattern: String) -> Set<String> {
        let regex = try! NSRegularExpression(pattern: pattern)
        var found: Set<String> = []
        for text in swiftSources {
            let range = NSRange(text.startIndex..., in: text)
            for match in regex.matches(in: text, range: range) {
                guard let captured = Range(match.range(at: 1), in: text) else { continue }
                found.insert(String(text[captured]))
            }
        }
        return found
    }

    /// Keys named outright at a call site: `L.t("mic.title")`, and the four
    /// forms behind `L.plural("packets", n)`.
    ///
    /// A key built out of an expression — `L.t("state.\(word)")` — cannot be
    /// read this way and is listed in `assembled` below instead.
    static let askedFor: Set<String> = {
        var keys = captures(#"\bt\(\s*"([^"\\]+)""#)
        for stem in captures(#"\bplural\(\s*"([^"\\]+)""#) {
            for category in PluralCategory.allCases { keys.insert("\(stem).\(category.rawValue)") }
        }
        return keys
    }()

    /// Every string literal anywhere in the sources.
    ///
    /// Used for the other direction only. A key can reach `L.t` through a
    /// variable — a ternary, a lookup table, a `switch` that returns one — and
    /// asking "does this key appear in the code at all" is the question that
    /// survives all of those, at the price of not proving it was a *key*.
    static let mentioned: Set<String> = captures(#""([^"\\\n]+)""#)

    /// Keys the code builds at run time rather than writing out in one piece,
    /// so a search for string literals cannot see them.
    static let assembled: Set<String> = {
        var keys: Set<String> = []
        for word in ["off", "starting", "waiting", "working", "muted", "notWorking", "unavailable"] {
            keys.insert("state.\(word)")
        }
        for theme in ["system", "light", "dark"] { keys.insert("theme.\(theme)") }
        for language in AppLanguage.allCases { keys.insert("language.\(language.rawValue)") }
        // Plural families are referenced by stem; the four forms are built from it.
        for stem in ["packets", "devices", "channels", "blocks", "slots"] {
            for category in PluralCategory.allCases { keys.insert("\(stem).\(category.rawValue)") }
        }
        return keys
    }()

    @Test("Каждый ключ из кода есть в таблице")
    func everyKeyTheCodeAsksForExists() {
        let table = Set(StringTable.en.entries.keys)
        // Only what looks like a key: `t(` also matches unrelated one-argument
        // calls, and a false positive here would be a test failing about a
        // string that was never a key.
        let missing = Self.askedFor
            .filter { $0.contains(".") && !$0.contains(" ") }
            .subtracting(table)
            .subtracting(Self.assembled)
            .sorted()
        #expect(missing.isEmpty, "нет в en.lproj: \(missing.joined(separator: ", "))")
    }

    @Test("Ни один ключ не потерян")
    func noKeyInTheTableIsUnused() {
        // The other direction, and the one that actually rots: a string nobody
        // asks for any more stays in both files and goes on being translated.
        let table = Set(StringTable.en.entries.keys)
        let orphans = table
            .subtracting(Self.mentioned)
            .subtracting(Self.assembled)
            .sorted()
        #expect(orphans.isEmpty, "не используются: \(orphans.joined(separator: ", "))")
    }

    @Test("Русских строк в коде не осталось")
    func noRussianIsHardCodedInTheSources() {
        // The check the whole exercise turns on. Comments may be in either
        // language; a string literal may not be in Russian, because a Russian
        // literal is a string that cannot become English.
        var offenders: [String] = []
        let files = FileManager.default.enumerator(
            at: StringTable.sources, includingPropertiesForKeys: nil
        )
        let cyrillic = CharacterSet(charactersIn: "\u{0400}"..."\u{04FF}")
        while let url = files?.nextObject() as? URL {
            guard url.pathExtension == "swift" else { continue }
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            for (number, line) in text.split(separator: "\n", omittingEmptySubsequences: false).enumerated() {
                let trimmed = line.trimmingCharacters(in: .whitespaces)
                guard !trimmed.hasPrefix("//") else { continue }
                guard line.rangeOfCharacter(from: cyrillic) != nil else { continue }
                // Cyrillic to the right of a `//` on a line of code is still a
                // comment; anything else on such a line is not.
                let code = line.range(of: "//").map { String(line[line.startIndex..<$0.lowerBound]) } ?? String(line)
                guard code.rangeOfCharacter(from: cyrillic) != nil else { continue }
                offenders.append("\(url.lastPathComponent):\(number + 1)")
            }
        }
        #expect(offenders.isEmpty, "\(offenders.joined(separator: ", "))")
    }
}

// MARK: - The engine

@Suite("Множественное число")
struct PluralTests {

    @Test("Английский различает одно и остальное")
    func englishHasTwoForms() {
        #expect(PluralCategory.of(1, language: "en") == .one)
        #expect(PluralCategory.of(0, language: "en") == .other)
        #expect(PluralCategory.of(2, language: "en") == .other)
        #expect(PluralCategory.of(21, language: "en") == .other)
    }

    @Test("Русский различает три формы, включая 11–14")
    func russianHasThreeFormsAndTheTeensException() {
        #expect(PluralCategory.of(1, language: "ru") == .one)
        #expect(PluralCategory.of(21, language: "ru") == .one)
        #expect(PluralCategory.of(2, language: "ru") == .few)
        #expect(PluralCategory.of(24, language: "ru") == .few)
        #expect(PluralCategory.of(5, language: "ru") == .many)
        #expect(PluralCategory.of(0, language: "ru") == .many)
        // «11 пакетов», не «11 пакет» — ровно та ошибка, ради которой это
        // вообще функция, а не существительное, приклеенное к числу.
        #expect(PluralCategory.of(11, language: "ru") == .many)
        #expect(PluralCategory.of(12, language: "ru") == .many)
        #expect(PluralCategory.of(14, language: "ru") == .many)
        #expect(PluralCategory.of(111, language: "ru") == .many)
    }
}

/// `.serialized`, and it has to be: `L` is one global choice for one user, so
/// two of these running at once would be two tests fighting over the language
/// the third one is reading.
@Suite("Выбор языка", .serialized)
struct LanguageSelectionTests {

    @Test("Строки приходят из выбранной таблицы")
    func stringsFollowTheChosenLanguage() {
        L.select(.en)
        #expect(L.t("state.working") == "working")
        L.select(.ru)
        #expect(L.t("state.working") == "работает")
        L.select(.en)
    }

    @Test("Числа форматируются по выбранному языку, а не по системному")
    func numbersFollowTheChosenLanguage() {
        L.select(.en)
        #expect(L.number(12.5) == "12.5")
        L.select(.ru)
        #expect(L.number(12.5) == "12,5")
        L.select(.en)
    }

    @Test("Единицы переводятся вместе со строками")
    func unitsAreTranslatedToo() {
        L.select(.en)
        #expect(L.milliseconds(24).contains("ms"))
        #expect(L.kilobits(perSecond: 32000).contains("kbit/s"))
        L.select(.ru)
        #expect(L.milliseconds(24).contains("мс"))
        #expect(L.kilobits(perSecond: 32000).contains("кбит/с"))
        L.select(.en)
    }

    @Test("Число и единица не разъезжаются по строкам")
    func aNumberAndItsUnitAreGluedTogether() {
        // §10.1: перенос строки не должен разлучать число с единицей.
        L.select(.ru)
        #expect(L.milliseconds(24).contains("\u{00A0}"))
        #expect(L.percent(1.5).contains("\u{00A0}"))
        L.select(.en)
        #expect(L.milliseconds(24).contains("\u{00A0}"))
    }

    @Test("Счётные существительные согласуются")
    func countedNounsAgree() {
        L.select(.ru)
        #expect(L.plural("packets", 1) == "1 пакет")
        #expect(L.plural("packets", 3) == "3 пакета")
        #expect(L.plural("packets", 11) == "11 пакетов")
        L.select(.en)
        #expect(L.plural("packets", 1) == "1 packet")
        #expect(L.plural("packets", 3) == "3 packets")
    }

    @Test("«Системный» — это всегда один из двух языков")
    func systemResolvesToSomethingWeHaveStringsFor() {
        #expect(["en", "ru"].contains(AppLanguage.system.resolved))
        #expect(AppLanguage.en.resolved == "en")
        #expect(AppLanguage.ru.resolved == "ru")
    }

    @Test("Каждый ключ таблицы отдаётся обеими сборками")
    func everyKeyResolvesThroughTheRealLookup() {
        // Не по файлам, а через тот же путь, которым ходит приложение: если
        // ресурсы не доехали до бандла, это падает здесь, а не на экране.
        for language in [AppLanguage.en, .ru] {
            L.select(language)
            for key in StringTable.en.entries.keys {
                #expect(L.has(key), "\(language.rawValue): нет ключа \(key)")
            }
        }
        L.select(.en)
    }
}
