import AppKit
import Foundation
import HexBridgeFiles
import HexBridgeText

/// Downloads, and the one queue that writes into it.
///
/// Everything about *what* a file may be called lives in `HexBridgeFiles`,
/// where it can be tested. What is left here is the part that needs a disk: the
/// folder, the queue and the write itself.
///
/// `@unchecked Sendable`: it holds a queue and nothing else, and the queue is
/// the only thing that touches the folder.
final class FileInbox: @unchecked Sendable {

    /// Serial, and it matters. Choosing a free name and then writing to it are
    /// two separate questions to the filesystem, and two files arriving at once
    /// would both be told the same name is free.
    private let queue = DispatchQueue(label: "ru.hexarch.hexbridge.files", qos: .utility)

    /// How many goes at the race described in `write`. Five, because the window
    /// is microseconds wide and something has to bound the loop.
    private static let attempts = 5

    /// The user's Downloads folder.
    ///
    /// The search path first and `~/Downloads` only as a fallback: the search
    /// path is what honours a Downloads folder somebody has moved, and it is
    /// allowed to return nothing at all.
    static var downloads: URL {
        FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads")
    }

    /// Takes over a file that arrived whole and gives it its real name.
    ///
    /// A move rather than a write, and that is the whole point of the file
    /// having been assembled here in the first place: the object was written
    /// straight into this folder under a hidden name as it arrived, so putting
    /// it in place is a rename on the same volume. Copying the bytes a second
    /// time would double the cost of every large transfer.
    ///
    /// The temporary file is this method's to dispose of from the moment it is
    /// called: it either becomes the named file or it is removed.
    func adopt(_ temporary: URL, as rawName: String, completion: @escaping (Result<URL, Error>) -> Void) {
        queue.async {
            let result = Result { try Self.place(temporary, as: rawName) }
            if case .failure = result { try? FileManager.default.removeItem(at: temporary) }
            DispatchQueue.main.async { completion(result) }
        }
    }

    private static func place(_ temporary: URL, as rawName: String) throws -> URL {
        let folder = downloads
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        for _ in 0..<attempts {
            guard let url = SafeFileName.destination(for: rawName, in: folder) else {
                throw FileInboxError.noFreeName
            }
            do {
                // `moveItem` refuses rather than overwrites, and the reason is
                // not the name we just chose but the moment after we chose it: a
                // browser finishing a download of its own into the same folder in
                // that instant would otherwise be destroyed. The move refuses,
                // and the next turn of the loop picks the next free name.
                try FileManager.default.moveItem(at: temporary, to: url)
                return url
            } catch let error as NSError where error.code == NSFileWriteFileExistsError {
                continue
            }
        }
        throw FileInboxError.noFreeName
    }

    /// Removes half-finished files from an earlier run.
    ///
    /// A transfer abandoned while the app is running cleans up after itself —
    /// the object holding the temporary file removes it when it is dropped. What
    /// that cannot cover is the app being killed mid-transfer, which leaves a
    /// hidden part-file that nothing will ever ask for again. The hour is the
    /// guard: it must never take a file that is being written right now.
    func discardLeftovers(prefix: String, suffix: String, olderThan age: TimeInterval = 3600) {
        queue.async {
            let folder = Self.downloads
            guard let names = try? FileManager.default.contentsOfDirectory(
                at: folder, includingPropertiesForKeys: [.contentModificationDateKey]
            ) else { return }

            let cutoff = Date().addingTimeInterval(-age)
            for url in names {
                let name = url.lastPathComponent
                guard name.hasPrefix(prefix), name.hasSuffix(suffix) else { continue }
                let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
                    .contentModificationDate
                guard let modified, modified < cutoff else { continue }
                try? FileManager.default.removeItem(at: url)
            }
        }
    }

    /// Puts Finder in front of the file. The same gesture as the log button in
    /// Diagnostics, and for the same reason: the interface's job ends at saying
    /// where the file is.
    static func reveal(_ url: URL) {
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}

enum FileInboxError: Error, CustomStringConvertible {
    case noFreeName

    var description: String {
        switch self {
        case .noFreeName:
            return L.t("files.error.noRoom")
        }
    }
}
