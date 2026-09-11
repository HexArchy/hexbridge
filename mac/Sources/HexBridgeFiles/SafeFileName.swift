import Foundation

/// Turning a name that arrived over the network into a file on this disk.
///
/// Its own target rather than a file inside the app, for the same reason as
/// `HexBridgeDiscovery` and `HexBridgePairing`: the name crossed a network, the
/// code below writes to disk with it, and a decision of that kind has to be
/// reachable from a test. The app target drags in AppKit, SwiftUI, CoreAudio and
/// a static libopus, none of which can be linked into a test bundle. This is
/// Foundation and nothing else.
///
/// docs/PROTOCOL.md, «Files, kind 2», is the contract and says it plainly: the
/// name is not to be trusted. Only its own last component survives, every
/// character the filesystem reads as structure is taken out, and what is left of
/// `.`, of `..` or of nothing at all is ``fallback``. A received name decides
/// what the file is called and never which directory it lands in.
public enum SafeFileName {

    /// What a name that sanitises away to nothing becomes.
    public static let fallback = "file"

    /// APFS and HFS+ both cap one path component at 255 **bytes** of UTF-8,
    /// not at 255 characters. «Отчёт за сентябрь» is two bytes a letter, so a
    /// limit counted in characters would pass a name the write then refuses.
    public static let maxNameBytes = 255

    /// How far the ` (2)`, ` (3)` sequence is followed before giving up.
    ///
    /// A bound rather than a loop that trusts the answer: `isTaken` is a
    /// filesystem, and a filesystem that says "taken" to everything — a disk
    /// with no space, a folder whose permissions changed under us — would spin
    /// here for as long as the app is running.
    public static let maxCopies = 10_000

    // MARK: - The name itself

    /// The name stripped of everything but itself.
    ///
    /// The result is always a single path component, never empty, never `.` or
    /// `..`, and never longer than ``maxNameBytes``. It is not yet known to be
    /// free — see ``firstFree(_:toBytes:isTaken:)``.
    public static func sanitised(_ raw: String) -> String {
        // Both separators, and both before anything else is looked at. The name
        // was written by the other machine, where a path is separated by a
        // backslash: splitting on "/" alone would leave «..\..\startup.bat» as a
        // single component that is still a path.
        var name = raw
        for separator in ["/", "\\"] {
            name = name.components(separatedBy: separator).last ?? name
        }

        let kept = name.unicodeScalars.filter { scalar in
            // A NUL ends the name the moment it reaches a POSIX call, so
            // «report.txt\u{0}.command» is one file on screen and another on
            // disk. The rest of the control range is nothing a name needs and
            // everything a terminal reading a directory listing can be steered
            // with.
            if scalar.value < 0x20 || scalar.value == 0x7F { return false }
            // The colon is the path separator HFS was built on. The POSIX layer
            // accepts one, and then Finder draws it as a slash — a name that
            // reads as a folder it is not.
            return scalar != ":"
        }
        let cleaned = String(String.UnicodeScalarView(kept))
            .trimmingCharacters(in: .whitespacesAndNewlines)

        // `.` and `..` are references to directories rather than names, and a
        // name made of nothing but dots is a hidden file with no name at all.
        guard cleaned.contains(where: { $0 != "." }) else { return fallback }

        return shortened(cleaned, toBytes: maxNameBytes)
    }

    /// The name cut to fit a byte budget, with the extension kept.
    ///
    /// The extension is what decides which application opens the file, so it is
    /// the last part to give up: `quarterly-report-final.numbers` cut from the
    /// end is a file nothing will open, while `quarterly-rep.numbers` is the
    /// same file under a shorter name.
    ///
    /// Also used on the way out. The contract caps a description at 256 bytes
    /// and says a longer name is trimmed before it is sent, keeping the
    /// extension — that is this function with a different budget.
    public static func shortened(_ name: String, toBytes limit: Int) -> String {
        guard limit > 0 else { return fallback }
        guard name.utf8.count > limit else { return name }

        let (stem, suffix) = split(name)

        // An extension that does not fit on its own is not an extension any
        // more; at that point the name is simply cut, dot and all.
        guard suffix.utf8.count < limit else { return clipped(name, toBytes: limit) }

        let head = clipped(stem, toBytes: limit - suffix.utf8.count)
        // A single multi-byte character can be wider than the room the
        // extension left, and `name.ext` with nothing before the dot is a
        // hidden file rather than a shorter name.
        guard !head.isEmpty else { return clipped(name, toBytes: limit) }
        return head + suffix
    }

    // MARK: - Not writing over anything

    /// The first of `name`, `name (2)`, `name (3)` … that is not already taken,
    /// or nil when ``maxCopies`` of them were.
    ///
    /// The counter goes before the extension rather than after it, which is
    /// where macOS itself puts it and, more to the point, keeps the file
    /// openable.
    public static func firstFree(
        _ name: String,
        toBytes limit: Int = maxNameBytes,
        isTaken: (String) -> Bool
    ) -> String? {
        guard isTaken(name) else { return name }

        let (stem, suffix) = split(name)
        for copy in 2...maxCopies {
            let tail = " (\(copy))" + suffix
            // The budget shrinks by what the counter costs: a name already at
            // the 255-byte limit has to give up characters to make room for it,
            // or the write fails on the one file the user was watching.
            let head = clipped(stem, toBytes: max(1, limit - tail.utf8.count))
            let candidate = head + tail
            if !isTaken(candidate) { return candidate }
        }
        return nil
    }

    /// Where a file called `rawName` goes inside `directory`, or nil when no
    /// free name could be found there.
    ///
    /// `exists` is a parameter so the rule can be tested without a disk; the
    /// default is the disk.
    public static func destination(
        for rawName: String,
        in directory: URL,
        exists: (URL) -> Bool = { FileManager.default.fileExists(atPath: $0.path) }
    ) -> URL? {
        let folder = directory.standardizedFileURL
        let name = sanitised(rawName)

        guard let free = firstFree(name, isTaken: { exists(folder.appendingPathComponent($0)) }) else {
            return nil
        }

        let url = folder.appendingPathComponent(free)
        // Belt and braces on top of the sanitiser. The guarantee that nothing is
        // ever written outside the folder that was asked for should not rest on
        // a character filter alone — the filter is the part somebody will one
        // day shorten, and this is the line that fails loudly when they do.
        guard url.deletingLastPathComponent().standardizedFileURL.path == folder.path else {
            return nil
        }
        return url
    }

    // MARK: - Pieces

    /// A name split into the part that may be shortened and the part that may
    /// not. A leading dot is not an extension: `.gitignore` is a name.
    static func split(_ name: String) -> (stem: String, suffix: String) {
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else {
            return (name, "")
        }
        return (String(name[name.startIndex..<dot]), String(name[dot...]))
    }

    /// Characters dropped from the end until the whole thing fits the budget.
    ///
    /// Characters rather than bytes: cutting a UTF-8 sequence in the middle
    /// produces a name that is not text any more, and `String` would replace
    /// what is left of it with a replacement character.
    static func clipped(_ text: String, toBytes limit: Int) -> String {
        var result = text
        while result.utf8.count > limit, !result.isEmpty {
            result.removeLast()
        }
        return result
    }
}
