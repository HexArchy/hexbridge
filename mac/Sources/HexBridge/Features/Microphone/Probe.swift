import Foundation
import HexBridgeText

/// Captures for a few seconds and reports what CoreAudio actually delivers.
///
/// Handy when nothing reaches the host: it separates "mic is silent" from
/// "mic is not being captured at all". Shared by the `probe` subcommand and by
/// the diagnostics tab, so both always report the same numbers.
enum MicProbe {
    /// Blocks for `seconds`. Call it off the main thread from the UI.
    static func run(deviceSelector: String?, seconds: Double = 3, log: (String) -> Void) throws {
        var frames = 0
        var badSamples = 0
        var minimum = Float.greatestFiniteMagnitude
        var maximum = -Float.greatestFiniteMagnitude
        let lock = NSLock()

        let probe = AudioCapture(gain: 1) { pcm, _ in
            lock.lock()
            defer { lock.unlock() }
            frames += 1
            for i in 0..<VoiceEncoder.frameSamples {
                let value = pcm[i]
                if !value.isFinite {
                    badSamples += 1
                    continue
                }
                minimum = min(minimum, value)
                maximum = max(maximum, value)
            }
        }

        try probe.start(deviceSelector: deviceSelector)

        log(L.t("probe.device", probe.deviceName))
        log(L.t("probe.format", probe.inputFormatDescription))
        Thread.sleep(forTimeInterval: seconds)
        probe.stop()

        lock.lock()
        defer { lock.unlock() }
        let expected = Int(seconds * 50)
        log(L.t("probe.frames", L.integer(Int(seconds)), L.integer(frames), L.integer(expected)))
        log(L.t("probe.badSamples", L.integer(badSamples)))
        if frames > 0 && badSamples < frames * VoiceEncoder.frameSamples {
            log(L.t(
                "probe.range",
                L.number(Double(minimum), decimals: 6),
                L.number(Double(maximum), decimals: 6)
            ))
        }
    }
}
