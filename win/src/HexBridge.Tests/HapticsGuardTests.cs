using HexBridge.Devices;

namespace HexBridge.Tests;

/// <summary>
/// The catch that keeps HD haptics from turning one crash into a loop.
///
/// <para>
/// Haptics is on by default now, and it is the only part of this that can take the whole
/// machine down: it needs an isochronous endpoint, which means Windows loads usbaudio.sys
/// on top of the vhci driver, which is the path usbip-win2 issue #181 bugchecks on. The
/// fixes shipped in the version we require and the issue is still open, so "on by
/// default" is only defensible with something that notices the machine went down and
/// stands back.
/// </para>
/// </summary>
public class HapticsGuardTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"hexbridge-guard-{Guid.NewGuid():N}");

    private string Marker => Path.Combine(_folder, "haptics.running");

    private HapticsGuard Guard() => new(Marker, (_, _) => { });

    public HapticsGuardTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AFirstRunIsAllowed() => Assert.True(Guard().MayStart());

    [Fact]
    public void AStopFollowedByAStartIsAllowed()
    {
        var first = Guard();
        Assert.True(first.MayStart());
        first.Finished();

        Assert.True(Guard().MayStart());
    }

    /// <summary>
    /// The whole point: a run that started haptics and never said it finished is a machine
    /// that went down, and the next one keeps away from the path that took it down.
    /// </summary>
    [Fact]
    public void ARunThatNeverFinishedCostsTheNextOneItsHaptics()
    {
        Assert.True(Guard().MayStart());
        // No Finished() — the process is gone.

        Assert.False(Guard().MayStart());
    }

    /// <summary>
    /// And exactly one. Sitting out for ever would be a feature that disables itself
    /// permanently on a single power cut, with nothing on screen to say why.
    /// </summary>
    [Fact]
    public void OnlyTheNextOne()
    {
        Assert.True(Guard().MayStart());
        Assert.False(Guard().MayStart());

        Assert.True(Guard().MayStart());
    }

    [Fact]
    public void FinishingTwiceIsHarmless()
    {
        var guard = Guard();
        Assert.True(guard.MayStart());
        guard.Finished();
        guard.Finished();

        Assert.True(Guard().MayStart());
    }

    /// <summary>
    /// Somewhere it cannot write is a lost safety net, not a lost feature: refusing
    /// haptics because a marker file would not open would be the tail wagging the dog.
    /// </summary>
    [Fact]
    public void SomewhereUnwritableStillAllowsHaptics()
    {
        var impossible = Path.Combine(_folder, "no", "such", "folder", "haptics.running");

        Assert.True(new HapticsGuard(impossible, (_, _) => { }).MayStart());
    }

    [Fact]
    public void TheMarkerSitsBesideTheConfig()
    {
        var path = HapticsGuard.PathBesideConfig(Path.Combine("C:", "somewhere", "config.json"));

        Assert.Equal(Path.Combine("C:", "somewhere", "haptics.running"), path);
    }
}
