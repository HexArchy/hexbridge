import AppKit
import Foundation

/// `NSPasteboard.general` behind the two calls the sync layer needs.
///
/// macOS has no notification for a clipboard change — there is no
/// `NSPasteboardDidChange`, and there never has been. `changeCount` is the whole
/// of the API, so the feature polls it; reading it is cheap enough that four
/// times a second costs nothing measurable.
///
/// Only two types are touched, as the feature promises: `public.utf8-plain-text`
/// and `public.png`. TIFF is read *as* PNG when a PNG is not offered — Preview
/// and the Photos app put only TIFF on the pasteboard, and refusing them would
/// make «скопировал картинку» work by accident rather than by rule. Nothing else
/// on the pasteboard is read, and nothing else is ever written.
final class SystemPasteboard: ClipboardSurface {
    private let pasteboard: NSPasteboard

    init(pasteboard: NSPasteboard = .general) {
        self.pasteboard = pasteboard
    }

    var changeCount: Int { pasteboard.changeCount }

    func read() -> ClipboardItem? {
        // A picture wins over text: an application that puts an image on the
        // pasteboard usually adds a text label next to it, and the picture is
        // what the user copied.
        if let png = pasteboard.data(forType: .png), !png.isEmpty {
            return ClipboardItem(format: .png, bytes: Array(png))
        }
        if let tiff = pasteboard.data(forType: .tiff), let png = Self.pngFromTIFF(tiff) {
            return ClipboardItem(format: .png, bytes: Array(png))
        }
        if let text = pasteboard.string(forType: .string), !text.isEmpty {
            return ClipboardItem(format: .utf8Text, bytes: Array(text.utf8))
        }
        return nil
    }

    func write(_ item: ClipboardItem) {
        pasteboard.clearContents()
        switch item.format {
        case .utf8Text:
            pasteboard.setString(String(decoding: item.bytes, as: UTF8.self), forType: .string)
        case .png:
            pasteboard.setData(Data(item.bytes), forType: .png)
        case .opaque:
            // Nothing else is a clipboard object as far as this feature is
            // concerned, and guessing at a type would put junk on the pasteboard.
            break
        }
    }

    /// Deterministic by construction: the same TIFF always produces the same
    /// PNG, which is what keeps the loop breaker's hashes stable across polls.
    private static func pngFromTIFF(_ tiff: Data) -> Data? {
        guard let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}
