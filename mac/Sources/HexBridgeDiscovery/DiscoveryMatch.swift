import Foundation

/// Why this Mac did, or did not, dial one of the hosts it can see.
public enum DiscoveryVerdict: Equatable, Sendable {
    /// A host published our own tag. The only value that dials anything.
    case connect

    /// Nothing on this network published our tag.
    case noMatch

    /// This Mac has no key, so it has no tag to compare, and must not guess.
    case unpaired
}

/// The outcome of one pass over the browse results.
public struct DiscoveryChoice: Equatable, Sendable {
    public var verdict: DiscoveryVerdict
    public var host: DiscoveredHost?
    /// One sentence the interface can show as is.
    public var reason: String

    public init(verdict: DiscoveryVerdict, host: DiscoveredHost? = nil, reason: String) {
        self.verdict = verdict
        self.host = host
        self.reason = reason
    }

    public var shouldConnect: Bool { verdict == .connect }

    /// Before the browser has been started there is nothing to decide, and
    /// saying «ни один хост не наш» then would be a lie about a search that
    /// never happened.
    public static let idle = DiscoveryChoice(
        verdict: .noMatch, host: nil, reason: "Поиск в сети не запущен.")
}

/// The one rule autodiscovery exists to enforce, with no socket anywhere near
/// it so it can be tested for what it is: a security decision.
///
/// This Mac connects automatically **only** to a host whose tag equals the tag
/// of its own key. A stranger's host has a different key, so a different tag, so
/// it does not exist as far as this machine is concerned — whatever it calls
/// itself, however many of them there are, and whether or not ours is among
/// them.
///
/// A Mac with no key has no tag and therefore never connects on its own at all.
/// It still gets the list — that is what saves typing an address — but turning a
/// row of that list into a pairing needs the short code off the host's screen.
/// Without that rule the first stranger's host on the network would become
/// «свой», which is the whole thing being guarded against.
///
/// Mirrored in `win/src/HexBridge.Core/Discovery.cs`; the two are held together
/// by the same vectors on both sides.
public enum DiscoveryMatch {
    public static func choose(ownTag: String?, hosts: [DiscoveredHost]) -> DiscoveryChoice {
        guard DiscoveryTag.isWellFormed(ownTag) else {
            return DiscoveryChoice(
                verdict: .unpaired,
                host: nil,
                reason: "Этот Mac ещё не связан ни с одним ПК — нужен короткий код с экрана ПК."
            )
        }

        for host in hosts where DiscoveryTag.same(ownTag, host.tag) {
            // An address is what makes a match usable. A result that has not
            // resolved yet is not a match yet, and taking it would blank out a
            // target that works.
            guard !host.address.isEmpty else { continue }
            return DiscoveryChoice(verdict: .connect, host: host, reason: "Метка совпала: \(host.target)")
        }

        return DiscoveryChoice(
            verdict: .noMatch,
            host: nil,
            reason: hosts.isEmpty
                ? "В сети не видно ни одного HexBridge."
                : "В сети \(hosts.count) HexBridge, но ни один из них не наш."
        )
    }

    /// The new value for `target`, or nil when nothing should move.
    ///
    /// This is the whole of the «переехал на другой IP» fix. The tag is not tied
    /// to an address, so a host that comes back on a different one after the
    /// router hands out a new lease is still the same host, and asking the user
    /// to pair it again would be asking them to fix something that fixes
    /// itself. Returning nil when the address has not changed is what keeps
    /// this from restarting the pipeline on every browse cycle — each restart
    /// costs a word of speech.
    public static func retarget(current: String?, choice: DiscoveryChoice) -> String? {
        guard choice.shouldConnect, let host = choice.host else { return nil }

        let target = host.target
        guard !target.isEmpty else { return nil }
        let now = current?.trimmingCharacters(in: .whitespaces) ?? ""
        return now.caseInsensitiveCompare(target) == .orderedSame ? nil : target
    }
}
