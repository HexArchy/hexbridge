using HexBridge.Localization;

namespace HexBridge.Devices;

/// <summary>
/// A catch that stops HD haptics turning one crash into a loop.
///
/// <para>
/// Haptics is the only part of this that can take the whole machine down. It needs an
/// isochronous endpoint, which means presenting the controller as a composite device,
/// which means Windows loads <c>usbaudio.sys</c> on top of the vhci driver — and that is
/// the path usbip-win2 issue #181 bugchecks on when an audio pin closes. The fixes for it
/// are merged and shipped in 0.9.8.0, which is why this is on by default at all; the
/// issue is still open, and somebody was still reproducing it weeks after those fixes.
/// </para>
///
/// <para>
/// So: a marker is written the moment the audio function is about to be presented, and
/// removed on a clean stop. Finding it at startup means the last run reached that point
/// and never came back — a bugcheck, a power cut, or a process killed outright. Haptics
/// then sits out exactly one run, and the marker is cleared so the run after that tries
/// again. Without this, "on by default" would mean a machine that blue-screens, reboots,
/// starts HexBridge, and blue-screens again, with no way in to switch anything off.
/// </para>
///
/// <para>
/// Being killed with Task Manager also leaves the marker, and costs one run of haptics.
/// That is the right way round: the cost of a false positive is a quiet controller for a
/// few minutes, and the cost of a false negative is somebody's machine in a reboot loop.
/// </para>
/// </summary>
public sealed class HapticsGuard(string markerPath, Action<LogLevel, string> log)
{
    /// <summary>Where the marker lives, beside the config rather than in a temp folder that
    /// a cleaner could empty between the crash and the next start.</summary>
    public static string PathBesideConfig(string configPath) =>
        Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "haptics.running");

    /// <summary>
    /// Whether haptics may start this time, marking that it did.
    ///
    /// Returns false exactly once after an unclean stop.
    /// </summary>
    public bool MayStart()
    {
        try
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
                log(LogLevel.Warning, Strings.Log_Haptics_SkippedAfterCrash);
                return false;
            }

            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex)
        {
            // A read-only directory, a permission, an antivirus holding the file. None of
            // that is a reason to refuse haptics — it is a reason to lose the safety net,
            // and to say so rather than fail silently.
            log(LogLevel.Warning, Loc.F(Strings.Log_Haptics_GuardUnavailable, ex.Message));
            return true;
        }
    }

    /// <summary>Records that this run ended on purpose.</summary>
    public void Finished()
    {
        try
        {
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }
        catch (Exception)
        {
            // Leaving it behind costs one run of haptics next time, which is the safe
            // direction to fail in.
        }
    }
}
