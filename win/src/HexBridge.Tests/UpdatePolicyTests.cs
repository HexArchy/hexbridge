namespace HexBridge.Tests;

/// <summary>
/// The three promises the product makes about updates, as tests rather than as prose:
/// once a day, never without being asked, and switchable off.
///
/// <para>
/// The «never without being asked» half is structural — <see cref="UpdatePolicy"/> decides
/// only whether to <b>look</b>, and downloading and installing are separate commands the
/// user presses — so what is checked here is that looking is rare, and that the switch is
/// absolute.
/// </para>
/// </summary>
public class UpdatePolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheIntervalIsADay()
    {
        // The same number is written into the Mac's Info.plist as SUScheduledCheckInterval.
        // Two platforms, one promise.
        Assert.Equal(TimeSpan.FromDays(1), UpdatePolicy.CheckInterval);
        Assert.Equal(86400, (int)UpdatePolicy.CheckInterval.TotalSeconds);
    }

    [Fact]
    public void AMachineThatHasNeverCheckedChecks()
    {
        Assert.True(UpdatePolicy.ShouldCheck(enabled: true, lastCheckUtc: null, Now));
    }

    [Fact]
    public void ADayLaterIsDue()
    {
        Assert.True(UpdatePolicy.ShouldCheck(true, Now - TimeSpan.FromDays(1), Now));
        Assert.True(UpdatePolicy.ShouldCheck(true, Now - TimeSpan.FromDays(9), Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(23)]
    public void AnythingSoonerIsNot(int hoursAgo)
    {
        // A user who starts HexBridge with every game would otherwise be checking six
        // times an evening. The stamp is on disk precisely so a restart is not a check.
        Assert.False(UpdatePolicy.ShouldCheck(true, Now - TimeSpan.FromHours(hoursAgo), Now));
    }

    [Fact]
    public void AStampFromTheFutureIsTreatedAsNoStampAtAll()
    {
        // A time-zone fix, a dead CMOS battery, a VM restored from a snapshot. Waiting for
        // the clock to catch up could mean waiting years.
        Assert.True(UpdatePolicy.ShouldCheck(true, Now + TimeSpan.FromDays(400), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1000)]
    public void OffMeansOff(int? hoursAgo)
    {
        // No exception for «never checked», none for «very stale», none for a clock that
        // has gone backwards. The switch is the last word.
        DateTime? last = hoursAgo is { } hours ? Now - TimeSpan.FromHours(hours) : null;

        Assert.False(UpdatePolicy.ShouldCheck(enabled: false, last, Now));
    }

    [Fact]
    public void TheFirstCheckWaitsForTheAppToSettle()
    {
        // Launch is when the audio endpoint is claimed, the features start and a permission
        // prompt may be on screen. A network request nobody asked for belongs after that.
        Assert.True(UpdatePolicy.StartupDelay > TimeSpan.Zero);
        Assert.True(UpdatePolicy.StartupDelay < UpdatePolicy.CheckInterval);
    }

    [Fact]
    public void TheNextCheckIsADayAfterTheLastOne()
    {
        var last = Now - TimeSpan.FromHours(6);

        Assert.Equal(last + TimeSpan.FromDays(1), UpdatePolicy.NextCheck(last, Now));
    }

    [Fact]
    public void AMachineThatIsOverdueIsToldSoRatherThanGivenAPastDate()
    {
        // The settings line reads «следующая проверка …»; a date in the past there would
        // read as a bug even though the behaviour is right.
        Assert.Equal(Now, UpdatePolicy.NextCheck(null, Now));
    }

    [Fact]
    public void TheReleaseChannelIsTheRepositoryItself()
    {
        // Both platforms publish to the same place: Velopack reads this repository's
        // releases, Sparkle reads appcast.xml published as an asset of them.
        Assert.Equal("https://github.com/HexArchy/hexbridge", UpdatePolicy.Repository);
    }
}
