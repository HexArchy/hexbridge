import SwiftUI

/// Colour tokens from DESIGN.md §4.1.
///
/// Two flat structs rather than asset catalogue colours: SwiftPM executables
/// have no asset catalogue, and a plain struct is also the only form that lets
/// the "Оформление" setting override the system appearance without the two
/// disagreeing (an `NSColor` dynamic provider resolves from `NSAppearance`,
/// which `preferredColorScheme` does not change everywhere).
///
/// Contrast ratios for every pair used for text were checked in the document;
/// changing a value here means recomputing that table.
struct Palette: Sendable {
    let bg: Color
    let surface: Color
    let surfaceAlt: Color
    let border: Color
    let borderStrong: Color
    let text: Color
    let textDim: Color
    let accent: Color
    let accentSoft: Color
    let accentText: Color
    /// Text drawn *on* an `accent` fill. White in light, near-black in dark.
    let accentInk: Color
    let okFg: Color
    let okText: Color
    let okBg: Color
    let okBorder: Color
    let warnFg: Color
    let warnText: Color
    let warnBg: Color
    let warnBorder: Color
    let badFg: Color
    let badText: Color
    let badBg: Color
    let badBorder: Color
    let off: Color
    let offBg: Color
    let meterTrack: Color

    // Controller-specific surfaces (§8.5). They are not reused anywhere else,
    // which is why they live here rather than in the general scale.
    let padBody: Color
    let padButton: Color
    let padButtonBorder: Color
    let padStickRim: Color
    let padStickCap: Color
    let padTouch: Color
    let padTouchBorder: Color
    let padLightbarOff: Color

    static let light = Palette(
        bg: .hex(0xF4F6F9),
        surface: .hex(0xFFFFFF),
        surfaceAlt: .hex(0xF8FAFC),
        border: .hex(0xE2E6ED),
        borderStrong: .hex(0x7E8899),
        text: .hex(0x1B2330),
        textDim: .hex(0x5C6675),
        accent: .hex(0x1D65C4),
        accentSoft: .hex(0xE9F1FD),
        accentText: .hex(0x1A5AAC),
        accentInk: .hex(0xFFFFFF),
        okFg: .hex(0x189055),
        okText: .hex(0x136B41),
        okBg: .hex(0xE7F6EE),
        okBorder: .hex(0xB4E0C9),
        warnFg: .hex(0xB87D00),
        warnText: .hex(0x7A4E00),
        warnBg: .hex(0xFDF3E0),
        warnBorder: .hex(0xF0DCB0),
        badFg: .hex(0xC62F2F),
        badText: .hex(0x9E2B2B),
        badBg: .hex(0xFCECEC),
        badBorder: .hex(0xF1C6C6),
        off: .hex(0x7E8899),
        offBg: .hex(0xEEF1F5),
        meterTrack: .hex(0xE4E8EF),
        padBody: .hex(0xFFFFFF),
        padButton: .hex(0xEEF1F5),
        padButtonBorder: .hex(0xC7CEDA),
        padStickRim: .hex(0xC7CEDA),
        padStickCap: .hex(0xFFFFFF),
        padTouch: .hex(0xF4F6F9),
        padTouchBorder: .hex(0xDCE2EA),
        padLightbarOff: .hex(0xDCE2EA)
    )

    static let dark = Palette(
        bg: .hex(0x15181E),
        surface: .hex(0x1E222A),
        surfaceAlt: .hex(0x242933),
        border: .hex(0x2E343F),
        borderStrong: .hex(0x66738A),
        text: .hex(0xE6EAF0),
        textDim: .hex(0x9AA4B4),
        accent: .hex(0x5A9BFF),
        accentSoft: .hex(0x16233A),
        accentText: .hex(0x8DB9FF),
        accentInk: .hex(0x0F1216),
        okFg: .hex(0x4FD18E),
        okText: .hex(0x5FD39B),
        okBg: .hex(0x132A20),
        okBorder: .hex(0x28503A),
        warnFg: .hex(0xE9B65B),
        warnText: .hex(0xE9B65B),
        warnBg: .hex(0x2B2417),
        warnBorder: .hex(0x55431F),
        badFg: .hex(0xF08A8A),
        badText: .hex(0xF5A0A0),
        badBg: .hex(0x2E1C1C),
        badBorder: .hex(0x5A2F2F),
        off: .hex(0x7A8496),
        offBg: .hex(0x20252E),
        meterTrack: .hex(0x2A303A),
        padBody: .hex(0x262B34),
        padButton: .hex(0x2E3440),
        padButtonBorder: .hex(0x3E4757),
        padStickRim: .hex(0x3E4757),
        padStickCap: .hex(0x39414F),
        padTouch: .hex(0x1A1E25),
        padTouchBorder: .hex(0x333A46),
        padLightbarOff: .hex(0x333A46)
    )

    static func of(_ scheme: ColorScheme) -> Palette {
        scheme == .dark ? .dark : .light
    }
}

extension Color {
    /// `0xRRGGBB`. Kept private-ish in spirit: the only place that should call
    /// it is the token table above.
    static func hex(_ value: UInt32) -> Color {
        Color(
            .sRGB,
            red: Double((value >> 16) & 0xFF) / 255,
            green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255,
            opacity: 1
        )
    }
}

// MARK: - Spacing, radii, sizes

/// 4 pt base grid (§4.3). Nothing outside this list is allowed as a literal.
enum Space {
    static let xs: CGFloat = 4
    static let sm: CGFloat = 8
    static let md: CGFloat = 12
    static let lg: CGFloat = 16
    static let xl: CGFloat = 24
    static let xxl: CGFloat = 32
}

enum Radius {
    static let xs: CGFloat = 4
    static let sm: CGFloat = 6
    static let md: CGFloat = 8
    static let lg: CGFloat = 12
    static let xl: CGFloat = 16
}

enum Metrics {
    /// §4.3: popover is 340 wide, content-height capped at 520.
    static let popoverWidth: CGFloat = 340
    static let popoverMaxHeight: CGFloat = 520
    /// §4.3: settings window 620 × 480, width fixed.
    static let settingsWidth: CGFloat = 620
    static let settingsHeight: CGFloat = 480
    /// §9: the pairing wizard is its own window, 720 × 520.
    static let wizardWidth: CGFloat = 720
    static let wizardHeight: CGFloat = 520
    /// §7.2: level meter height inside a macOS popover.
    static let meterHeight: CGFloat = 8
    static let statusDot: CGFloat = 8
}

// MARK: - Typography (§4.2)

extension Font {
    static let dsDisplay = Font.system(size: 28, weight: .semibold)
    static let dsTitle = Font.title3
    static let dsHeading = Font.headline
    static let dsBody = Font.body
    static let dsLabel = Font.callout
    static let dsCaption = Font.caption
    static let dsSection = Font.caption.weight(.semibold)
    static let dsMetric = Font.system(size: 22, weight: .semibold).monospacedDigit()
    static let dsMono = Font.system(.caption, design: .monospaced)
}

// MARK: - Environment plumbing

private struct PaletteKey: EnvironmentKey {
    static let defaultValue = Palette.light
}

extension EnvironmentValues {
    var palette: Palette {
        get { self[PaletteKey.self] }
        set { self[PaletteKey.self] = newValue }
    }
}

/// The three-way appearance setting from §7.4. Exactly three options, on purpose.
enum AppTheme: String, CaseIterable, Codable, Sendable {
    case system, light, dark

    var title: String {
        switch self {
        case .system: return "Системная"
        case .light: return "Светлая"
        case .dark: return "Тёмная"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

/// Applies the appearance preference and then publishes the matching palette.
///
/// Two nested views rather than one: `preferredColorScheme` travels up to the
/// window and comes back down as `\.colorScheme`, so the palette can only be
/// derived one level below where the preference is applied.
struct Themed<Content: View>: View {
    let theme: AppTheme
    @ViewBuilder var content: Content

    var body: some View {
        PaletteBridge { content }
            .preferredColorScheme(theme.colorScheme)
    }
}

private struct PaletteBridge<Content: View>: View {
    @Environment(\.colorScheme) private var scheme
    @ViewBuilder var content: Content

    var body: some View {
        content.environment(\.palette, Palette.of(scheme))
    }
}

// MARK: - Russian plurals (§11.3)

/// «1 пакет / 2 пакета / 5 пакетов». The document requires plural agreement to
/// be a function rather than a noun glued to a number, because the glued form
/// is what produces «1 репортов» in a UI nobody re-reads.
enum Plural {
    static func of(_ count: Int, _ one: String, _ few: String, _ many: String) -> String {
        let mod100 = abs(count) % 100
        let mod10 = abs(count) % 10
        if mod100 >= 11 && mod100 <= 14 { return many }
        switch mod10 {
        case 1: return one
        case 2, 3, 4: return few
        default: return many
        }
    }

    static func reports(_ count: Int) -> String {
        "\(count) \(of(count, "репорт", "репорта", "репортов"))"
    }

    static func packets(_ count: Int) -> String {
        "\(count) \(of(count, "пакет", "пакета", "пакетов"))"
    }
}
