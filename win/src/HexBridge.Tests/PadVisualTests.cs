using HexBridge.DualSense;

namespace HexBridge.Tests;

/// <summary>
/// The arithmetic behind the controller visualisation (DESIGN.md §8.3–8.4).
///
/// The drawing itself cannot be asserted on — it is pixels — so everything that can be
/// decided without a screen lives in <c>PadVisual.cs</c> and is decided here: which arms a
/// hat lights, how fast the frame loop should run, and what the smoothing filter does at
/// its edges.
/// </summary>
public class PadVisualTests
{
    // MARK: - The d-pad

    [Theory]
    [InlineData(PadHat.Up, new[] { PadDirection.Up })]
    [InlineData(PadHat.Down, new[] { PadDirection.Down })]
    [InlineData(PadHat.Left, new[] { PadDirection.Left })]
    [InlineData(PadHat.Right, new[] { PadDirection.Right })]
    [InlineData(PadHat.UpLeft, new[] { PadDirection.Up, PadDirection.Left })]
    [InlineData(PadHat.UpRight, new[] { PadDirection.Up, PadDirection.Right })]
    [InlineData(PadHat.DownLeft, new[] { PadDirection.Down, PadDirection.Left })]
    [InlineData(PadHat.DownRight, new[] { PadDirection.Down, PadDirection.Right })]
    [InlineData(PadHat.Centre, new PadDirection[0])]
    public void AHatLightsOneArmOrTwo(PadHat hat, PadDirection[] expected)
    {
        var lit = Enum.GetValues<PadDirection>().Where(direction => hat.Includes(direction)).ToArray();

        Assert.Equal(expected.Order(), lit.Order());
    }

    [Fact]
    public void OppositeArmsAreNeverLitTogether()
    {
        foreach (var hat in Enum.GetValues<PadHat>())
        {
            Assert.False(hat.Includes(PadDirection.Up) && hat.Includes(PadDirection.Down));
            Assert.False(hat.Includes(PadDirection.Left) && hat.Includes(PadDirection.Right));
        }
    }

    // MARK: - The frame-rate ladder

    [Fact]
    public void NothingOnScreenAsksForNoFramesAtAll()
    {
        // The single biggest saving in the feature: not a slow timer, no timer.
        Assert.Equal(0, PadFrameRate.Hz(visible: false, hasController: true, windowActive: true, reducedMotion: false, idle: TimeSpan.Zero));
        Assert.Equal(0, PadFrameRate.Hz(visible: true, hasController: false, windowActive: true, reducedMotion: false, idle: TimeSpan.Zero));
        Assert.Equal(0, PadFrameRate.Hz(visible: false, hasController: false, windowActive: false, reducedMotion: true, idle: TimeSpan.MaxValue));
    }

    [Fact]
    public void AnActiveWindowWithInputGetsTheFullRate()
    {
        // Sticks and triggers are analogue; below 60 the steps are visible.
        Assert.Equal(60, PadFrameRate.Hz(true, true, windowActive: true, reducedMotion: false, idle: TimeSpan.Zero));
        Assert.Equal(60, PadFrameRate.Hz(true, true, true, false, TimeSpan.FromSeconds(1.9)));
    }

    [Fact]
    public void AVisibleButUnfocusedWindowIsBeingGlancedAt()
    {
        Assert.Equal(20, PadFrameRate.Hz(true, true, windowActive: false, reducedMotion: false, idle: TimeSpan.Zero));
    }

    [Fact]
    public void TwoSecondsWithoutInputDropsToTheIdleRate()
    {
        Assert.Equal(8, PadFrameRate.Hz(true, true, true, false, PadFrameRate.IdleAfter));
        Assert.Equal(8, PadFrameRate.Hz(true, true, true, false, TimeSpan.FromMinutes(5)));
        // Nothing has ever arrived: also idle, and specifically not a crash.
        Assert.Equal(8, PadFrameRate.Hz(true, true, true, false, TimeSpan.MaxValue));
    }

    [Fact]
    public void ReducedMotionHalvesTheRateWithoutHidingTheData()
    {
        // §5.6: reduced motion means less movement, never less information.
        Assert.Equal(30, PadFrameRate.Hz(true, true, true, reducedMotion: true, idle: TimeSpan.Zero));
        Assert.Equal(30, PadFrameRate.Hz(true, true, false, true, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void EveryRateIsOneTheLadderNames()
    {
        foreach (var visible in new[] { true, false })
        foreach (var controller in new[] { true, false })
        foreach (var active in new[] { true, false })
        foreach (var reduced in new[] { true, false })
        foreach (var idle in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1.99), PadFrameRate.IdleAfter, TimeSpan.MaxValue })
        {
            Assert.Contains(PadFrameRate.Hz(visible, controller, active, reduced, idle), new[] { 0, 8, 20, 30, 60 });
        }
    }

    // MARK: - Smoothing

    private static DualSenseInput Sticks(byte leftX = 128, byte leftY = 128, byte l2 = 0) => new()
    {
        LeftX = leftX,
        LeftY = leftY,
        RightX = 128,
        RightY = 128,
        L2 = l2,
    };

    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1.0 / 60);

    [Fact]
    public void TheFilterConvergesOnItsTarget()
    {
        var filter = new PadFilter();
        var input = Sticks(leftX: 255, l2: 255);

        for (var i = 0; i < 240; i++) filter.Step(input, Frame, smoothTilt: false);

        Assert.Equal(1.0, filter.LeftX, 3);
        Assert.Equal(1.0, filter.L2, 3);
    }

    [Fact]
    public void OneFrameMovesAboutATheThirdOfTheWay()
    {
        // §8.4 fixes the coefficient at 0.35 per frame at 60 Hz. It is what makes a stick
        // read as «плавно» rather than as «тормозит», so it is worth pinning.
        var filter = new PadFilter();

        filter.Step(Sticks(leftX: 255), Frame, smoothTilt: false);

        Assert.Equal(PadFilter.Coefficient, filter.LeftX, 2);
    }

    [Fact]
    public void ASlowFrameMovesFurtherThanAFastOne()
    {
        // The coefficient is per-second, not per-frame: at 8 Hz the same filter must not
        // crawl a seventh of the way there.
        var fast = new PadFilter();
        var slow = new PadFilter();

        fast.Step(Sticks(leftX: 255), Frame, smoothTilt: false);
        slow.Step(Sticks(leftX: 255), TimeSpan.FromSeconds(1.0 / 8), smoothTilt: false);

        Assert.True(slow.LeftX > fast.LeftX);
        Assert.True(slow.LeftX <= 1.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]
    public void AnAbsurdFrameTimeDoesNotBreakTheFilter(double seconds)
    {
        // A debugger breakpoint, a swapped-out process, a clock that went backwards.
        var filter = new PadFilter();

        filter.Step(Sticks(leftX: 255), TimeSpan.FromSeconds(seconds), smoothTilt: false);

        Assert.InRange(filter.LeftX, 0, 1);
        Assert.False(double.IsNaN(filter.LeftX));
    }

    [Fact]
    public void ACentredStickDoesNotDrift()
    {
        // 128 is the controller's nominal centre and 127.5 is the middle of the byte
        // range, so a centred stick lands 0.4 % off zero — a twentieth of a pixel at the
        // 14-unit travel, and the same half-step the Mac carries, since both sides scale
        // by 127.5. What matters is that it is a constant offset and not a drift.
        var filter = new PadFilter();

        for (var i = 0; i < 60; i++) filter.Step(Sticks(), Frame, smoothTilt: false);
        var settled = filter.LeftX;
        for (var i = 0; i < 600; i++) filter.Step(Sticks(), Frame, smoothTilt: false);

        Assert.Equal(settled, filter.LeftX, 6);
        foreach (var axis in new[] { filter.LeftX, filter.LeftY, filter.RightX, filter.RightY })
        {
            Assert.InRange(Math.Abs(axis), 0, 0.005);
        }
    }

    [Fact]
    public void StickYIsNotFlipped()
    {
        // The drawing grid's y grows downwards and so does the raw report, so the filter
        // passes the axis through untouched. The Mac flips it upstream instead, and the
        // two must not both flip — or both not.
        var filter = new PadFilter();

        for (var i = 0; i < 240; i++) filter.Step(Sticks(leftY: 255), Frame, smoothTilt: false);

        Assert.True(filter.LeftY > 0.99);
    }

    [Fact]
    public void TiltIsClampedToWhatTheDocumentAllows()
    {
        // §8.3: ±12°, deliberately small. This should read as «оно живое», not as a ride.
        var filter = new PadFilter();
        var spinning = new DualSenseInput { LeftX = 128, LeftY = 128, RightX = 128, RightY = 128, GyroZ = short.MaxValue, GyroX = short.MaxValue };

        for (var i = 0; i < 600; i++) filter.Step(spinning, Frame, smoothTilt: true);

        Assert.InRange(filter.Tilt, PadFilter.MaxTilt - 0.01, PadFilter.MaxTilt);
        Assert.InRange(filter.Pitch, PadFilter.MaxPitch - 0.01, PadFilter.MaxPitch);
    }

    [Fact]
    public void TiltIsSymmetric()
    {
        var filter = new PadFilter();
        var spinning = new DualSenseInput { GyroZ = short.MinValue, GyroX = short.MinValue };

        for (var i = 0; i < 600; i++) filter.Step(spinning, Frame, smoothTilt: true);

        Assert.InRange(filter.Tilt, -PadFilter.MaxTilt, -PadFilter.MaxTilt + 0.01);
        Assert.InRange(filter.Pitch, -PadFilter.MaxPitch, -PadFilter.MaxPitch + 0.01);
    }

    [Fact]
    public void ReducedMotionRemovesTheTiltEntirely()
    {
        // §8.4: «30 Гц, без инерции и без наклона по гироскопу».
        var filter = new PadFilter();
        var spinning = new DualSenseInput { GyroZ = short.MaxValue, GyroX = short.MaxValue };

        for (var i = 0; i < 120; i++) filter.Step(spinning, Frame, smoothTilt: true);
        filter.Step(spinning, Frame, smoothTilt: false);

        Assert.Equal(0, filter.Tilt);
        Assert.Equal(0, filter.Pitch);
    }

    // MARK: - Touch trails

    [Fact]
    public void ATouchTrailKeepsTheLastEightPositions()
    {
        var filter = new PadFilter();

        for (var i = 0; i < 30; i++)
        {
            filter.Step(new DualSenseInput { Touch0 = new PadTouch(true, i * 10, i * 5) }, Frame, false);
        }

        var trail = filter.Trail(0);
        Assert.Equal(PadFilter.TrailLength, trail.Count);
        // Newest last, so the head of the trail is where the finger is now.
        Assert.Equal(29 * 10 / 1919.0, trail[^1].X, 6);
    }

    [Fact]
    public void ALiftedFingerLosesItsTrailAtOnce()
    {
        // A trail that outlived the touch would read as a ghost input.
        var filter = new PadFilter();

        filter.Step(new DualSenseInput { Touch0 = new PadTouch(true, 500, 500) }, Frame, false);
        Assert.NotEmpty(filter.Trail(0));

        filter.Step(new DualSenseInput { Touch0 = new PadTouch(false, 500, 500) }, Frame, false);
        Assert.Empty(filter.Trail(0));
    }

    [Fact]
    public void TwoFingersKeepSeparateTrails()
    {
        var filter = new PadFilter();

        filter.Step(new DualSenseInput
        {
            Touch0 = new PadTouch(true, 0, 0),
            Touch1 = new PadTouch(true, 1919, 1079),
        }, Frame, false);

        Assert.Equal((0.0, 0.0), filter.Trail(0)[0]);
        Assert.Equal((1.0, 1.0), filter.Trail(1)[0]);
    }

    [Fact]
    public void TouchPositionsAreClampedIntoThePad()
    {
        // The pad occasionally reports a count past its own resolution; a dot drawn
        // outside the touchpad rectangle would look like a bug in the visualisation.
        var filter = new PadFilter();

        filter.Step(new DualSenseInput { Touch0 = new PadTouch(true, 4095, 4095) }, Frame, false);

        var (x, y) = filter.Trail(0)[0];
        Assert.InRange(x, 0, 1);
        Assert.InRange(y, 0, 1);
    }

    [Fact]
    public void ResettingLeavesAControllerSittingStill()
    {
        var filter = new PadFilter();
        for (var i = 0; i < 60; i++)
        {
            filter.Step(new DualSenseInput
            {
                LeftX = 255, L2 = 255, GyroZ = short.MaxValue, Touch0 = new PadTouch(true, 100, 100),
            }, Frame, smoothTilt: true);
        }

        filter.Reset();

        Assert.Equal(0, filter.LeftX);
        Assert.Equal(0, filter.L2);
        Assert.Equal(0, filter.Tilt);
        Assert.Empty(filter.Trail(0));
    }
}
