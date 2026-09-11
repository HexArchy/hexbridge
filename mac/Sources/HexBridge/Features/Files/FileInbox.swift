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

    /// Writes an arrived object to Downloads and reports back on the main queue.
    func save(_ bytes: [UInt8], as rawName: String, completion: @escaping (Result<URL, Error>) -> Void) {
        queue.async {
            let result = Result { try Self.write(bytes, as: rawName) }
            DispatchQueue.main.async { completion(result) }
        }
    }

    private static func write(_ bytes: [UInt8], as rawName: String) throws -> URL {
        let folder = downloads
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let data = Data(bytes)
        for _ in 0..<attempts {
            guard let url = SafeFileName.destination(for: rawName, in: folder) else {
                throw FileInboxError.noFreeName
            }
            do {
                // `withoutOverwriting` rather than a plain write, and the reason
                // is not the name we just chose but the moment after we chose
                // it: a browser finishing a download of its own into the same
                // folder in that instant would otherwise be overwritten. The
                // write refuses, and the next turn of the loop picks the next
                // free name.
                try data.write(to: url, options: .withoutOverwriting)
                return url
            } catch let error as NSError where error.code == NSFileWriteFileExistsError {
                continue
            }
        }
        throw FileInboxError.noFreeName
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
