import Foundation
import Testing

@testable import HexBridgeFiles

/// The name on an incoming file is the one piece of a transfer that is written
/// by the other machine and then handed to the filesystem. These are the cases
/// where getting it wrong is not a cosmetic bug: a name that still carries a
/// path writes outside Downloads, and a name that matches an existing file
/// destroys it.
@Suite("Имя пришедшего файла")
struct SanitisingTests {

    @Test("От пути остаётся только последний кусок")
    func onlyTheLastComponentSurvives() {
        #expect(SafeFileName.sanitised("../../etc/passwd") == "passwd")
        #expect(SafeFileName.sanitised("/etc/passwd") == "passwd")
        #expect(SafeFileName.sanitised("notes/2026/september.md") == "september.md")
        // Windows writes the name, and a Windows path is separated the other
        // way round. Splitting on "/" alone would leave this whole thing as one
        // «component».
        #expect(SafeFileName.sanitised(#"..\..\Windows\System32\drivers\etc\hosts"#) == "hosts")
        #expect(SafeFileName.sanitised(#"C:\Users\hexarch\notes.txt"#) == "notes.txt")
    }

    @Test("Ссылки на каталог именем файла не становятся")
    func directoryReferencesBecomeTheFallback() {
        #expect(SafeFileName.sanitised("..") == SafeFileName.fallback)
        #expect(SafeFileName.sanitised(".") == SafeFileName.fallback)
        #expect(SafeFileName.sanitised("") == SafeFileName.fallback)
        #expect(SafeFileName.sanitised("....") == SafeFileName.fallback)
        #expect(SafeFileName.sanitised("   ") == SafeFileName.fallback)
        #expect(SafeFileName.sanitised("/") == SafeFileName.fallback)
        #expect(SafeFileName.sanitised("../") == SafeFileName.fallback)
    }

    @Test("Разделители и управляющие символы не доезжают до диска")
    func structuralCharactersAreRemoved() {
        // A NUL truncates the name at the POSIX layer: what the interface shows
        // and what is created on disk would be two different files.
        #expect(SafeFileName.sanitised("report.txt\u{0}.command") == "report.txt.command")
        #expect(SafeFileName.sanitised("a\u{0}/b.txt") == "b.txt")
        // The colon is HFS's own path separator and Finder still draws one as a
        // slash.
        #expect(SafeFileName.sanitised("Macintosh HD:notes.txt") == "Macintosh HDnotes.txt")
        #expect(SafeFileName.sanitised("two\nlines.txt") == "twolines.txt")
        #expect(SafeFileName.sanitised("\u{0}") == SafeFileName.fallback)
    }

    @Test("Обычное имя проходит нетронутым")
    func anOrdinaryNameIsLeftAlone() {
        #expect(SafeFileName.sanitised("notes.txt") == "notes.txt")
        #expect(SafeFileName.sanitised("Отчёт за сентябрь.pdf") == "Отчёт за сентябрь.pdf")
        #expect(SafeFileName.sanitised(".gitignore") == ".gitignore")
        #expect(SafeFileName.sanitised("archive.tar.gz") == "archive.tar.gz")
    }

    @Test("Длинное имя укорачивается, а расширение остаётся")
    func anOverlongNameKeepsItsExtension() {
        let long = String(repeating: "a", count: 400) + ".keynote"
        let short = SafeFileName.sanitised(long)

        #expect(short.utf8.count <= SafeFileName.maxNameBytes)
        #expect(short.hasSuffix(".keynote"))
        #expect(short.hasPrefix("aaaa"))
    }

    @Test("Предел считается в байтах, а не в буквах")
    func theLimitIsCountedInBytes() {
        // Two bytes a letter: 200 of them are 400 bytes and would sail past a
        // check written in characters, then fail the write.
        let long = String(repeating: "я", count: 200) + ".txt"
        let short = SafeFileName.sanitised(long)

        #expect(short.utf8.count <= SafeFileName.maxNameBytes)
        #expect(short.hasSuffix(".txt"))
        // Cut between the bytes of a letter, the tail would come back as U+FFFD.
        #expect(!short.contains("\u{FFFD}"))
    }

    @Test("Расширение шире бюджета — режется всё имя")
    func anExtensionWiderThanTheBudgetIsCutToo() {
        let name = "x." + String(repeating: "e", count: 40)
        let short = SafeFileName.shortened(name, toBytes: 16)

        #expect(short.utf8.count <= 16)
        #expect(short.hasPrefix("x."))
    }
}

@Suite("Свободное имя")
struct VacancyTests {

    @Test("Свободное имя остаётся собой")
    func aFreeNameIsNotTouched() {
        #expect(SafeFileName.firstFree("notes.txt", isTaken: { _ in false }) == "notes.txt")
    }

    @Test("Занятое имя получает (2), потом (3)")
    func takenNamesGetTheCountedSequence() {
        var taken: Set<String> = ["notes.txt"]
        #expect(SafeFileName.firstFree("notes.txt", isTaken: { taken.contains($0) }) == "notes (2).txt")

        taken.insert("notes (2).txt")
        #expect(SafeFileName.firstFree("notes.txt", isTaken: { taken.contains($0) }) == "notes (3).txt")

        taken.insert("notes (3).txt")
        #expect(SafeFileName.firstFree("notes.txt", isTaken: { taken.contains($0) }) == "notes (4).txt")
    }

    @Test("Счётчик встаёт перед расширением")
    func theCounterGoesBeforeTheExtension() {
        // «notes.txt (2)» would open in nothing at all.
        #expect(SafeFileName.firstFree("archive.tar.gz", isTaken: { $0 == "archive.tar.gz" })
            == "archive.tar (2).gz")
        #expect(SafeFileName.firstFree("README", isTaken: { $0 == "README" }) == "README (2)")
    }

    @Test("Счётчику освобождают место в пределе длины")
    func theCounterIsGivenRoomWithinTheLimit() {
        let name = SafeFileName.sanitised(String(repeating: "a", count: 400) + ".txt")
        let next = SafeFileName.firstFree(name, isTaken: { $0 == name })

        #expect(next != nil)
        #expect(next?.utf8.count ?? 0 <= SafeFileName.maxNameBytes)
        #expect(next?.hasSuffix(" (2).txt") == true)
    }

    @Test("Каталог, который на всё отвечает «занято», не подвешивает поток")
    func aDirectoryThatSaysTakenToEverythingEndsTheSearch() {
        #expect(SafeFileName.firstFree("notes.txt", isTaken: { _ in true }) == nil)
    }
}

@Suite("Куда именно кладём")
struct DestinationTests {

    static let folder = URL(fileURLWithPath: "/Users/someone/Downloads")

    @Test("Файл всегда оказывается внутри указанной папки")
    func nothingEverLandsOutsideTheFolder() {
        for hostile in ["../../etc/passwd", "/etc/passwd", "..", "", #"..\..\hosts"#, "a/b/c"] {
            let url = SafeFileName.destination(for: hostile, in: Self.folder, exists: { _ in false })
            #expect(url?.deletingLastPathComponent().path == Self.folder.path, "«\(hostile)»")
        }
    }

    @Test("Занятое имя на диске обходится так же")
    func anOccupiedNameIsStillNotWrittenOver() {
        let taken = Self.folder.appendingPathComponent("passwd").path
        let url = SafeFileName.destination(for: "../../etc/passwd", in: Self.folder, exists: { $0.path == taken })

        #expect(url?.lastPathComponent == "passwd (2)")
    }

    @Test("Пустое имя становится file")
    func anEmptyNameBecomesTheFallback() {
        let url = SafeFileName.destination(for: "", in: Self.folder, exists: { _ in false })
        #expect(url?.lastPathComponent == SafeFileName.fallback)
    }
}

/// The same rule, against a real directory rather than a closure.
///
/// The three tests above prove the arithmetic; this one proves that the
/// arithmetic survives `URL` — that a folder path and a name put together and
/// taken apart again still compare equal, which is what the containment check
/// rests on.
@Suite("На настоящем диске")
struct RealDirectoryTests {

    @Test("Второй файл с тем же именем не затирает первый")
    func theSecondFileDoesNotReplaceTheFirst() throws {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("hexbridge-files-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }

        let first = try #require(SafeFileName.destination(for: "../../notes.txt", in: folder))
        try Data("first".utf8).write(to: first, options: .withoutOverwriting)

        let second = try #require(SafeFileName.destination(for: "notes.txt", in: folder))
        try Data("second".utf8).write(to: second, options: .withoutOverwriting)

        #expect(first.lastPathComponent == "notes.txt")
        #expect(second.lastPathComponent == "notes (2).txt")
        #expect(try String(contentsOf: first, encoding: .utf8) == "first")
    }
}
