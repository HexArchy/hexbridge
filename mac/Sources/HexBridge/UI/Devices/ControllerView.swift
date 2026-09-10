import AppKit
import SwiftUI

/// Live schematic of the controller (DESIGN.md §8).
///
/// Deliberately **not** a DualSense silhouette. Sony holds industrial design
/// registrations on the body and a trademark on the △ ○ ✕ □ set, and no freely
/// licensed vector outline of the pad exists (§3.3–3.4). What is drawn here is
/// a geometric abstraction — a rounded body, two grips, circles and capsules —
/// that reads as "gamepad", imitates nothing, and repaints for either theme
/// from tokens.
///
/// Everything is laid out in the normalised 400 × 260 grid from §8.2 and scaled
/// as a whole, so every number below is in that grid and never in points.
enum ControllerGeometry {
    /// The trigger petals stick out above y = 0, so the drawn box starts at −20.
    static let box = CGRect(x: 0, y: -20, width: 400, height: 280)

    static let body = CGRect(x: 60, y: 20, width: 280, height: 120)
    static let bodyRadius: CGFloat = 40

    static let gripSize = CGSize(width: 56, height: 130)
    static let gripRadius: CGFloat = 28
    static let leftGripCenter = CGPoint(x: 118, y: 178)
    static let rightGripCenter = CGPoint(x: 282, y: 178)
    static let gripTilt: CGFloat = 12

    static let touchpad = CGRect(x: 148, y: 38, width: 104, height: 60)
    static let touchpadRadius: CGFloat = 8

    static let leftStick = CGPoint(x: 150, y: 122)
    static let rightStick = CGPoint(x: 250, y: 122)
    static let stickRim: CGFloat = 26
    static let stickCap: CGFloat = 17
    /// §8.3: the cap travels 14 px, the rim stays put.
    static let stickTravel: CGFloat = 14

    static let dpadCenter = CGPoint(x: 96, y: 74)
    static let dpadArm = CGSize(width: 13, height: 22)
    static let dpadRadius: CGFloat = 5

    static let faceCenter = CGPoint(x: 304, y: 74)
    static let faceRadius: CGFloat = 11
    static let faceSpread: CGFloat = 26

    static let l1 = CGRect(x: 78, y: 6, width: 52, height: 12)
    static let r1 = CGRect(x: 270, y: 6, width: 52, height: 12)
    static let shoulderRadius: CGFloat = 6

    static let l2 = CGRect(x: 78, y: -16, width: 52, height: 22)
    static let r2 = CGRect(x: 270, y: -16, width: 52, height: 22)
    static let triggerRadius: CGFloat = 8
    /// §8.3: full pull rotates the petal 18° about its upper edge.
    static let triggerTilt: CGFloat = 18

    static let create = CGRect(x: 126, y: 46, width: 10, height: 18)
    static let options = CGRect(x: 264, y: 46, width: 10, height: 18)
    static let smallRadius: CGFloat = 5

    static let psCenter = CGPoint(x: 200, y: 152)
    static let psRadius: CGFloat = 9
    static let mute = CGRect(x: 190, y: 126, width: 20, height: 10)

    static let lightbarLeftX: CGFloat = 142
    static let lightbarRightX: CGFloat = 258
    static let lightbarTop: CGFloat = 44
    static let lightbarBottom: CGFloat = 92
    static let lightbarWidth: CGFloat = 4

    /// The touchpad reports 1920 × 1080 counts across its surface.
    static let touchResolution = CGSize(width: 1920, height: 1080)
}

/// Exponential smoothing of the analogue axes (§8.4).
///
/// Buttons deliberately do not go through it: a filtered button press reads as
/// lag, and §5.5 rule 1 forbids animating real-time input at all.
@MainActor
final class ControllerFilter {
    private(set) var left = CGPoint(x: 0, y: 0)
    private(set) var right = CGPoint(x: 0, y: 0)
    private(set) var l2: Double = 0
    private(set) var r2: Double = 0
    private(set) var tilt: Double = 0
    private(set) var pitch: Double = 0
    /// Eight most recent positions per finger, newest last.
    private(set) var trails: [[CGPoint]] = [[], []]

    private var lastStep = Date()

    /// `coefficient` is 0.35 per frame at 60 Hz in the spec; it is rescaled by
    /// the real frame time so a 8 Hz redraw does not crawl.
    func step(_ state: GamepadState, smoothTilt: Bool) {
        let now = Date()
        let dt = min(0.25, max(0.001, now.timeIntervalSince(lastStep)))
        lastStep = now
        let k = min(1, 0.35 * dt * 60)

        let l = state.left.normalized
        let r = state.right.normalized
        left.x += (l.x - left.x) * k
        left.y += (l.y - left.y) * k
        right.x += (r.x - right.x) * k
        right.y += (r.y - right.y) * k
        l2 += (Double(state.l2) / 255 - l2) * k
        r2 += (Double(state.r2) / 255 - r2) * k

        if smoothTilt {
            // §8.3: ±12° maximum. The amplitude is deliberately small — this
            // should read as "оно живое", not as a fairground ride.
            let targetTilt = max(-12, min(12, Double(state.gyro.z) / 32767 * 12))
            let targetPitch = max(-10, min(10, Double(state.gyro.x) / 32767 * 10))
            tilt += (targetTilt - tilt) * k
            pitch += (targetPitch - pitch) * k
        } else {
            tilt = 0
            pitch = 0
        }

        for finger in 0..<2 {
            let point = state.touch[finger]
            if point.active {
                let position = CGPoint(
                    x: min(1, Double(point.x) / ControllerGeometry.touchResolution.width),
                    y: min(1, Double(point.y) / ControllerGeometry.touchResolution.height)
                )
                trails[finger].append(position)
                if trails[finger].count > 8 { trails[finger].removeFirst() }
            } else if !trails[finger].isEmpty {
                trails[finger].removeAll()
            }
        }
    }
}

enum ControllerVariant {
    /// Settings window: the whole pad, gyro tilt, touchpad, lightbar.
    case full
    /// Menu bar popover, §8.7: sticks, triggers and button highlights only.
    /// A popover must not turn into an application.
    case compact

    var aspect: CGSize {
        switch self {
        case .full: return CGSize(width: 400, height: 280)
        case .compact: return CGSize(width: 300, height: 100)
        }
    }
}

/// How the outline is coloured, which is how §7.3 distinguishes its states.
enum ControllerMood {
    /// Nothing plugged in: flat `off`, 0.35 alpha, no reaction.
    case inactive
    /// Read but not forwarded: fully live, drawn in `textDim`.
    case reading
    /// Forwarded and acknowledged: live and accented.
    case forwarding
}

struct ControllerView: View {
    /// Pulled once per frame. A closure rather than a value so the view is not
    /// re-created 60 times a second by SwiftUI's diffing.
    let snapshot: () -> (state: GamepadState, lightbar: GamepadOutput.Color?, idle: TimeInterval)?
    var variant: ControllerVariant = .full
    var mood: ControllerMood = .reading

    @Environment(\.palette) private var palette
    @Environment(\.colorScheme) private var scheme
    @Environment(\.motionSettings) private var motion

    @State private var filter = ControllerFilter()
    @State private var visible = false
    /// Recomputed twice a second, never per frame: changing the timeline's
    /// `minimumInterval` on every tick would rebuild the schedule constantly.
    @State private var interval: Double = 1.0 / 8
    @State private var paused = true

    var body: some View {
        TimelineView(.animation(minimumInterval: interval, paused: paused)) { _ in
            Canvas(rendersAsynchronously: false) { context, size in
                draw(context: context, size: size)
            }
            .rotation3DEffect(
                .degrees(tiltNow),
                axis: (x: 0, y: 0, z: 1),
                perspective: 0
            )
            .rotation3DEffect(
                .degrees(pitchNow),
                axis: (x: 1, y: 0, z: 0),
                perspective: 0.4
            )
        }
        .aspectRatio(variant.aspect.width / variant.aspect.height, contentMode: .fit)
        .onAppear {
            visible = true
            reassess()
        }
        .onDisappear {
            visible = false
            paused = true
        }
        // Twice a second is enough to notice "the user stopped touching it"
        // and to react to the window being hidden.
        //
        // Deliberately a `task`, not `Timer.publish(...).autoconnect()` inside
        // `body`: that publisher is rebuilt on every body evaluation, and since
        // `reassess()` writes @State, the body evaluates again, builds another
        // timer, and so on. The subscriptions pile up and the view graph ends up
        // dirty on every display frame — measured at a permanent 20-30% CPU with
        // nothing on screen. A `task` is bound to the view's lifetime instead.
        .task {
            while !Task.isCancelled {
                reassess()
                try? await Task.sleep(for: .milliseconds(500))
            }
        }
        .accessibilityHidden(true)
    }

    private var tiltNow: Double { variant == .full ? filter.tilt : 0 }
    private var pitchNow: Double { variant == .full ? filter.pitch : 0 }

    /// §8.4: 60 Hz with input, 20 Hz in a background window, 8 Hz idle,
    /// 0 Hz (paused, no frames requested at all) when nothing is on screen.
    private func reassess() {
        guard visible, snapshot() != nil, mood != .inactive else {
            paused = true
            return
        }
        let occluded = !NSApp.occlusionState.contains(.visible)
        let idle = snapshot()?.idle ?? 99

        let hz: Double
        if occluded {
            hz = 0
        } else if motion.reduceMotion {
            hz = 30
        } else if idle < 2 {
            hz = NSApp.isActive ? 60 : 20
        } else {
            hz = 8
        }

        paused = hz == 0
        if hz > 0 { interval = 1 / hz }
    }

    // MARK: - Drawing

    private func draw(context: GraphicsContext, size: CGSize) {
        var context = context
        let box = variant == .full
            ? ControllerGeometry.box
            : CGRect(x: 0, y: 0, width: 300, height: 100)
        let scale = min(size.width / box.width, size.height / box.height)
        context.translateBy(
            x: (size.width - box.width * scale) / 2,
            y: (size.height - box.height * scale) / 2
        )
        context.scaleBy(x: scale, y: scale)
        context.translateBy(x: -box.minX, y: -box.minY)

        let live = snapshot()
        if let live, mood != .inactive {
            filter.step(live.state, smoothTilt: variant == .full && !motion.reduceMotion)
        }
        let state = live?.state ?? GamepadState()

        switch variant {
        case .full:
            drawFull(&context, state: state, lightbar: live?.lightbar)
        case .compact:
            drawCompact(&context, state: state)
        }
    }

    // MARK: Colours

    private var alpha: Double { mood == .inactive ? 0.35 : 1 }

    private var outlineColor: Color {
        mood == .inactive ? palette.off.opacity(alpha) : palette.borderStrong
    }

    private var bodyFill: Color {
        mood == .inactive ? palette.off.opacity(0.12) : palette.padBody
    }

    /// The accent used for pressed buttons and trigger fill. `reading` uses
    /// `textDim` so the difference between "прочитан" and "проброшен" is
    /// visible at a glance (§7.3).
    private var activeColor: Color {
        switch mood {
        case .inactive: return palette.off.opacity(alpha)
        case .reading: return palette.textDim
        case .forwarding: return palette.accent
        }
    }

    private var pressedInk: Color {
        mood == .forwarding ? palette.accentInk : palette.surface
    }

    private var passiveFill: Color {
        mood == .inactive ? palette.off.opacity(0.15) : palette.padButton
    }

    private var passiveStroke: Color {
        mood == .inactive ? palette.off.opacity(0.3) : palette.padButtonBorder
    }

    // MARK: Full layout

    private func drawFull(_ context: inout GraphicsContext, state: GamepadState, lightbar: GamepadOutput.Color?) {
        typealias G = ControllerGeometry

        // Bottom up, exactly the order in §8.2.
        drawGrip(&context, center: G.leftGripCenter, angle: -G.gripTilt)
        drawGrip(&context, center: G.rightGripCenter, angle: G.gripTilt)
        let body = Path(roundedRect: G.body, cornerRadius: G.bodyRadius, style: .continuous)
        context.fill(body, with: .color(bodyFill))
        context.stroke(body, with: .color(outlineColor), lineWidth: 2)

        drawLightbar(&context, color: lightbar)
        drawTouchpad(&context, state: state)

        // Passive buttons.
        drawDPad(&context, state: state)
        drawFaceButtons(&context, state: state)
        capsule(&context, G.create, radius: G.smallRadius, pressed: state.buttons.contains(.create))
        capsule(&context, G.options, radius: G.smallRadius, pressed: state.buttons.contains(.options))
        capsule(&context, G.mute, radius: G.smallRadius, pressed: state.buttons.contains(.mute))
        circle(&context, center: G.psCenter, radius: G.psRadius, pressed: state.buttons.contains(.ps))
        capsule(&context, G.l1, radius: G.shoulderRadius, pressed: state.buttons.contains(.l1))
        capsule(&context, G.r1, radius: G.shoulderRadius, pressed: state.buttons.contains(.r1))

        drawStick(&context, center: G.leftStick, offset: filter.left, pressed: state.buttons.contains(.l3))
        drawStick(&context, center: G.rightStick, offset: filter.right, pressed: state.buttons.contains(.r3))

        drawTrigger(&context, rect: G.l2, pull: filter.l2)
        drawTrigger(&context, rect: G.r2, pull: filter.r2)
    }

    private func drawGrip(_ context: inout GraphicsContext, center: CGPoint, angle: CGFloat) {
        typealias G = ControllerGeometry
        let rect = CGRect(
            x: center.x - G.gripSize.width / 2,
            y: center.y - G.gripSize.height / 2,
            width: G.gripSize.width,
            height: G.gripSize.height
        )
        let path = Path(roundedRect: rect, cornerRadius: G.gripRadius, style: .continuous)
        let transform = CGAffineTransform(translationX: center.x, y: center.y)
            .rotated(by: angle * .pi / 180)
            .translatedBy(x: -center.x, y: -center.y)
        let tilted = path.applying(transform)
        context.fill(tilted, with: .color(bodyFill))
        context.stroke(tilted, with: .color(outlineColor), lineWidth: 2)
    }

    private func drawLightbar(_ context: inout GraphicsContext, color: GamepadOutput.Color?) {
        typealias G = ControllerGeometry
        let on = color.map { $0.red != 0 || $0.green != 0 || $0.blue != 0 } ?? false

        for x in [G.lightbarLeftX, G.lightbarRightX] {
            let rect = CGRect(
                x: x - G.lightbarWidth / 2,
                y: G.lightbarTop,
                width: G.lightbarWidth,
                height: G.lightbarBottom - G.lightbarTop
            )
            let path = Path(roundedRect: rect, cornerRadius: G.lightbarWidth / 2, style: .continuous)

            guard on, let color, mood != .inactive else {
                context.fill(path, with: .color(palette.padLightbarOff))
                continue
            }

            // §8.5: light theme desaturates to 0.9 so a white bar does not
            // vanish into a white body; dark theme adds an outer glow instead.
            let saturation: Double = scheme == .dark ? 1 : 0.9
            let real = Color(
                .sRGB,
                red: Double(color.red) / 255 * saturation + (1 - saturation) * 0.5,
                green: Double(color.green) / 255 * saturation + (1 - saturation) * 0.5,
                blue: Double(color.blue) / 255 * saturation + (1 - saturation) * 0.5,
                opacity: 1
            )
            if scheme == .dark {
                context.drawLayer { layer in
                    layer.addFilter(.blur(radius: 5))
                    layer.fill(path, with: .color(real.opacity(0.45)))
                }
            }
            context.fill(path, with: .color(real))

            // A near-white bar in the light theme still needs an edge.
            let brightness = (Double(color.red) + Double(color.green) + Double(color.blue)) / 765
            if scheme != .dark, brightness > 0.9 {
                context.stroke(path, with: .color(palette.borderStrong), lineWidth: 1)
            }
        }
    }

    private func drawTouchpad(_ context: inout GraphicsContext, state: GamepadState) {
        typealias G = ControllerGeometry
        let path = Path(roundedRect: G.touchpad, cornerRadius: G.touchpadRadius, style: .continuous)
        let pressed = state.buttons.contains(.touchpad)
        context.fill(path, with: .color(pressed ? activeColor.opacity(0.25) : (mood == .inactive ? palette.off.opacity(0.12) : palette.padTouch)))
        context.stroke(path, with: .color(mood == .inactive ? palette.off.opacity(0.3) : palette.padTouchBorder), lineWidth: 1)

        guard mood != .inactive else { return }

        for trail in filter.trails where !trail.isEmpty {
            for (index, point) in trail.enumerated() {
                let position = CGPoint(
                    x: G.touchpad.minX + point.x * G.touchpad.width,
                    y: G.touchpad.minY + point.y * G.touchpad.height
                )
                // Newest is opaque, the eight-sample tail fades to nothing.
                let fade = Double(index + 1) / Double(trail.count)
                let radius: CGFloat = index == trail.count - 1 ? 7 : 4
                let dot = Path(ellipseIn: CGRect(
                    x: position.x - radius, y: position.y - radius,
                    width: radius * 2, height: radius * 2
                ))
                if scheme == .dark, index == trail.count - 1 {
                    context.drawLayer { layer in
                        layer.addFilter(.blur(radius: 6))
                        layer.fill(dot, with: .color(activeColor.opacity(0.5)))
                    }
                }
                context.fill(dot, with: .color(activeColor.opacity(fade)))
            }
        }
    }

    private func drawDPad(_ context: inout GraphicsContext, state: GamepadState) {
        typealias G = ControllerGeometry
        let arms: [(GamepadState.DPad, CGRect)] = [
            (.up, CGRect(x: G.dpadCenter.x - G.dpadArm.width / 2, y: G.dpadCenter.y - G.dpadArm.height - 4,
                         width: G.dpadArm.width, height: G.dpadArm.height)),
            (.down, CGRect(x: G.dpadCenter.x - G.dpadArm.width / 2, y: G.dpadCenter.y + 4,
                           width: G.dpadArm.width, height: G.dpadArm.height)),
            (.left, CGRect(x: G.dpadCenter.x - G.dpadArm.height - 4, y: G.dpadCenter.y - G.dpadArm.width / 2,
                           width: G.dpadArm.height, height: G.dpadArm.width)),
            (.right, CGRect(x: G.dpadCenter.x + 4, y: G.dpadCenter.y - G.dpadArm.width / 2,
                            width: G.dpadArm.height, height: G.dpadArm.width)),
        ]
        for (direction, rect) in arms {
            capsule(&context, rect, radius: G.dpadRadius, pressed: Self.hat(state.dpad, includes: direction))
        }
    }

    /// A hat switch value lights one or two arms; the diagonals light both.
    private static func hat(_ value: GamepadState.DPad, includes direction: GamepadState.DPad) -> Bool {
        switch direction {
        case .up: return [.up, .upLeft, .upRight].contains(value)
        case .down: return [.down, .downLeft, .downRight].contains(value)
        case .left: return [.left, .upLeft, .downLeft].contains(value)
        case .right: return [.right, .upRight, .downRight].contains(value)
        default: return false
        }
    }

    private func drawFaceButtons(_ context: inout GraphicsContext, state: GamepadState) {
        typealias G = ControllerGeometry
        let half = G.faceSpread
        let buttons: [(CGPoint, GamepadState.Buttons)] = [
            (CGPoint(x: G.faceCenter.x, y: G.faceCenter.y - half), .triangle),
            (CGPoint(x: G.faceCenter.x + half, y: G.faceCenter.y), .circle),
            (CGPoint(x: G.faceCenter.x, y: G.faceCenter.y + half), .cross),
            (CGPoint(x: G.faceCenter.x - half, y: G.faceCenter.y), .square),
        ]
        for (center, button) in buttons {
            circle(&context, center: center, radius: G.faceRadius, pressed: state.buttons.contains(button))
        }
    }

    private func drawStick(_ context: inout GraphicsContext, center: CGPoint, offset: CGPoint, pressed: Bool) {
        typealias G = ControllerGeometry
        let rim = Path(ellipseIn: CGRect(
            x: center.x - G.stickRim, y: center.y - G.stickRim,
            width: G.stickRim * 2, height: G.stickRim * 2
        ))
        context.fill(rim, with: .color(mood == .inactive ? palette.off.opacity(0.12) : palette.surfaceAlt))
        context.stroke(rim, with: .color(mood == .inactive ? palette.off.opacity(0.3) : palette.padStickRim), lineWidth: 2)

        let capCenter = CGPoint(
            x: center.x + offset.x * G.stickTravel,
            // The grid's y grows downwards, the normalised stick value grows up.
            y: center.y - offset.y * G.stickTravel
        )
        let cap = Path(ellipseIn: CGRect(
            x: capCenter.x - G.stickCap, y: capCenter.y - G.stickCap,
            width: G.stickCap * 2, height: G.stickCap * 2
        ))
        context.fill(cap, with: .color(pressed ? activeColor : (mood == .inactive ? palette.off.opacity(0.25) : palette.padStickCap)))
        context.stroke(cap, with: .color(pressed ? activeColor : passiveStroke), lineWidth: 1.5)
    }

    /// §8.3: the petal both rotates about its upper edge and fills from the top
    /// in proportion to the pull. Two cues for one value, because the rotation
    /// alone is too subtle at this scale and the fill alone reads as a bar.
    private func drawTrigger(_ context: inout GraphicsContext, rect: CGRect, pull: Double) {
        typealias G = ControllerGeometry
        let pivot = CGPoint(x: rect.midX, y: rect.maxY)
        let angle = -G.triggerTilt * pull * .pi / 180
        let transform = CGAffineTransform(translationX: pivot.x, y: pivot.y)
            .rotated(by: angle)
            .translatedBy(x: -pivot.x, y: -pivot.y)

        let shape = Path(roundedRect: rect, cornerRadius: G.triggerRadius, style: .continuous).applying(transform)
        context.fill(shape, with: .color(passiveFill))

        if pull > 0.01, mood != .inactive {
            let filled = CGRect(
                x: rect.minX, y: rect.minY,
                width: rect.width, height: rect.height * pull
            )
            let clip = Path(roundedRect: rect, cornerRadius: G.triggerRadius, style: .continuous)
                .intersection(Path(filled))
            context.fill(
                clip.applying(transform),
                with: .color(activeColor.opacity(scheme == .dark ? 0.9 : 0.85))
            )
        }
        context.stroke(shape, with: .color(passiveStroke), lineWidth: 1.5)
    }

    // MARK: Primitives

    /// §8.3 and §5.4 #19: a pressed button fills with the accent and shrinks to
    /// 0.94 — with **no** animation. Input is never animated.
    private func capsule(_ context: inout GraphicsContext, _ rect: CGRect, radius: CGFloat, pressed: Bool) {
        let target = pressed ? rect.insetBy(dx: rect.width * 0.03, dy: rect.height * 0.03) : rect
        let path = Path(roundedRect: target, cornerRadius: radius, style: .continuous)
        context.fill(path, with: .color(pressed ? activeColor : passiveFill))
        context.stroke(path, with: .color(pressed ? activeColor : passiveStroke), lineWidth: 1.5)
    }

    private func circle(_ context: inout GraphicsContext, center: CGPoint, radius: CGFloat, pressed: Bool) {
        let r = pressed ? radius * 0.94 : radius
        let path = Path(ellipseIn: CGRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2))
        context.fill(path, with: .color(pressed ? activeColor : passiveFill))
        context.stroke(path, with: .color(pressed ? activeColor : passiveStroke), lineWidth: 1.5)
    }

    // MARK: Compact layout (§8.7)

    /// 300 × 100: two sticks, two triggers, the d-pad and the face buttons.
    /// No gyro, no touchpad, no lightbar — the popover must stay a popover.
    ///
    /// The x positions are chosen so no two groups overlap at their maximum
    /// radius; the first draft had the right stick's rim cutting through the
    /// face buttons.
    private func drawCompact(_ context: inout GraphicsContext, state: GamepadState) {
        // Triggers as vertical bars at the edges.
        drawCompactTrigger(&context, rect: CGRect(x: 8, y: 14, width: 22, height: 72), pull: filter.l2)
        drawCompactTrigger(&context, rect: CGRect(x: 270, y: 14, width: 22, height: 72), pull: filter.r2)

        drawCompactStick(&context, center: CGPoint(x: 70, y: 50), offset: filter.left, pressed: state.buttons.contains(.l3))
        drawCompactStick(&context, center: CGPoint(x: 236, y: 50), offset: filter.right, pressed: state.buttons.contains(.r3))

        // Mini d-pad.
        let dpad = CGPoint(x: 122, y: 50)
        let arm = CGSize(width: 9, height: 13)
        let arms: [(GamepadState.DPad, CGRect)] = [
            (.up, CGRect(x: dpad.x - arm.width / 2, y: dpad.y - arm.height - 3, width: arm.width, height: arm.height)),
            (.down, CGRect(x: dpad.x - arm.width / 2, y: dpad.y + 3, width: arm.width, height: arm.height)),
            (.left, CGRect(x: dpad.x - arm.height - 3, y: dpad.y - arm.width / 2, width: arm.height, height: arm.width)),
            (.right, CGRect(x: dpad.x + 3, y: dpad.y - arm.width / 2, width: arm.height, height: arm.width)),
        ]
        for (direction, rect) in arms {
            capsule(&context, rect, radius: 3, pressed: Self.hat(state.dpad, includes: direction))
        }

        // Mini face buttons.
        let face = CGPoint(x: 170, y: 50)
        let spread: CGFloat = 15
        let buttons: [(CGPoint, GamepadState.Buttons)] = [
            (CGPoint(x: face.x, y: face.y - spread), .triangle),
            (CGPoint(x: face.x + spread, y: face.y), .circle),
            (CGPoint(x: face.x, y: face.y + spread), .cross),
            (CGPoint(x: face.x - spread, y: face.y), .square),
        ]
        for (center, button) in buttons {
            circle(&context, center: center, radius: 7, pressed: state.buttons.contains(button))
        }
    }

    private func drawCompactTrigger(_ context: inout GraphicsContext, rect: CGRect, pull: Double) {
        let path = Path(roundedRect: rect, cornerRadius: 6, style: .continuous)
        context.fill(path, with: .color(passiveFill))
        if pull > 0.01, mood != .inactive {
            let filled = CGRect(
                x: rect.minX, y: rect.maxY - rect.height * pull,
                width: rect.width, height: rect.height * pull
            )
            context.fill(
                path.intersection(Path(filled)),
                with: .color(activeColor.opacity(0.85))
            )
        }
        context.stroke(path, with: .color(passiveStroke), lineWidth: 1.5)
    }

    private func drawCompactStick(_ context: inout GraphicsContext, center: CGPoint, offset: CGPoint, pressed: Bool) {
        let rimRadius: CGFloat = 28
        let capRadius: CGFloat = 18
        let travel: CGFloat = 10
        let rim = Path(ellipseIn: CGRect(
            x: center.x - rimRadius, y: center.y - rimRadius,
            width: rimRadius * 2, height: rimRadius * 2
        ))
        context.fill(rim, with: .color(mood == .inactive ? palette.off.opacity(0.12) : palette.surfaceAlt))
        context.stroke(rim, with: .color(mood == .inactive ? palette.off.opacity(0.3) : palette.padStickRim), lineWidth: 2)

        let capCenter = CGPoint(x: center.x + offset.x * travel, y: center.y - offset.y * travel)
        let cap = Path(ellipseIn: CGRect(
            x: capCenter.x - capRadius, y: capCenter.y - capRadius,
            width: capRadius * 2, height: capRadius * 2
        ))
        context.fill(cap, with: .color(pressed ? activeColor : (mood == .inactive ? palette.off.opacity(0.25) : palette.padStickCap)))
        context.stroke(cap, with: .color(pressed ? activeColor : passiveStroke), lineWidth: 1.5)
    }
}
