namespace HexBridge;

/// <summary>
/// When an update check is due, and nothing else.
///
/// <para>
/// Separated from the machinery that performs one so the three promises the product makes
/// about updates can be tested rather than asserted in a README: <b>once a day</b>, <b>never
/// installed without the user saying so</b>, and <b>switchable off</b>. The first and the
/// third live here; the second is the absence of any «install» call on this path at all —
/// downloading and applying are separate steps the user starts.
/// </para>
///
/// <para>
/// Mirrored on the Mac by Sparkle's own scheduler, configured with the same interval in
/// <c>Info.plist</c> (<c>SUScheduledCheckInterval</c>) and the same off switch
/// (<c>SUEnableAutomaticChecks</c>, bound to the config). See docs/UPDATES.md.
/// </para>
/// </summary>
public static class UpdatePolicy
{
    /// <summary>Where releases live. The update channel is the repository's Releases page.</summary>
    public const string Repository = "https://github.com/HexArchy/hexbridge";

    /// <summary>
    /// Once a day. Not once an hour: this is a utility that sits next to a game stream, and
    /// a network round trip the user did not ask for is exactly the kind of thing that gets
    /// blamed for a stutter — fairly or not.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// How long after launch the first check waits.
    ///
    /// Launch is the busiest moment the app has — claiming an audio endpoint, starting the
    /// features, possibly answering a TCC prompt — and it is also the moment the user is
    /// watching. The check has nothing urgent about it and can wait until the app is idle.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// True when a check is due now.
    /// </summary>
    /// <param name="enabled">The setting. False means never, with no exceptions.</param>
    /// <param name="lastCheckUtc">When the last check finished, or null if there was none.</param>
    public static bool ShouldCheck(bool enabled, DateTime? lastCheckUtc, DateTime nowUtc)
    {
        if (!enabled) return false;
        if (lastCheckUtc is not { } last) return true;

        // A stamp in the future means the clock moved backwards — a time zone fix, a dead
        // CMOS battery, a VM restored from a snapshot. Waiting for it to catch up could
        // mean waiting years, so treat it as «no idea when we last looked» and look now.
        if (last > nowUtc) return true;

        return nowUtc - last >= CheckInterval;
    }

    /// <summary>When the next check falls due, for the line in the settings that says so.</summary>
    public static DateTime NextCheck(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is { } last && last <= nowUtc ? last + CheckInterval : nowUtc;
}

/// <summary>What the update UI is showing right now.</summary>
public enum UpdateStage
{
    /// <summary>Nothing to say. The overwhelmingly common state.</summary>
    Idle,

    Checking,

    /// <summary>A newer version exists. Nothing has been downloaded and nothing will be
    /// until the user asks.</summary>
    Available,

    Downloading,

    /// <summary>Downloaded and staged. Applied at the next restart, or now if asked.</summary>
    Ready,

    /// <summary>The check or the download failed. Says why, and the app keeps working.</summary>
    Failed,
}
