namespace HexBridge.DualSense;

/// <summary>The four arms of the d-pad cross, which is what a hat value lights up.</summary>
public enum PadDirection { Up, Down, Left, Right }

public static class PadHatExtensions
{
    /// <summary>
    /// A hat value lights one arm, or two on a diagonal. Reading the enum's number would
    /// be shorter and wrong: the values walk clockwise from up, so no arithmetic on them
    /// says anything about which arms are involved.
    /// </summary>
    public static bool Includes(this PadHat hat, PadDirection direction) => direction switch
    {
        PadDirection.Up => hat is PadHat.Up or PadHat.UpLeft or PadHat.UpRight,
        PadDirection.Down => hat is PadHat.Down or PadHat.DownLeft or PadHat.DownRight,
        PadDirection.Left => hat is PadHat.Left or PadHat.UpLeft or PadHat.DownLeft,
        PadDirection.Right => hat is PadHat.Right or PadHat.UpRight or PadHat.DownRight,
        _ => false,
    };
}

/// <summary>
/// How often the visualisation should ask for a frame (DESIGN.md §8.4).
///
/// Written as a pure function of what is on screen rather than as a tangle of timer
/// restarts: «0 Hz when nothing is looking» is the single biggest saving in the whole
/// feature and it has to be provably right, not probably right.
/// </summary>
public static class PadFrameRate
{
    /// <summary>Two seconds without a new report and the pad counts as idle.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(2);

    public static int Hz(bool visible, bool hasController, bool windowActive, bool reducedMotion, TimeSpan idle)
    {
        // Not on screen: no frames are requested at all. Not a slow timer — none.
        if (!visible || !hasController) return 0;

        // Reduced motion still shows the data, just without the inertia and the tilt, so
        // half the frames carry all of the meaning.
        if (reducedMotion) return 30;

        // Sticks and triggers are analogue; below 60 the steps are visible. A window that
        // is visible but not focused is being glanced at, and 20 is enough for a glance.
        if (idle < IdleAfter) return windowActive ? 60 : 20;

        // Nothing is moving. Only the battery and the status can change, and the eye does
        // not notice either arriving an eighth of a second late.
        return 8;
    }
}

/// <summary>
/// Exponential smoothing of the analogue axes, and the touch trails.
///
/// <para>
/// §8.4: <c>v += (target − v) × 0.35</c> per frame at 60 Hz. The coefficient is rescaled
/// by the real frame time, or the same filter would crawl at 8 Hz and snap at 120.
/// </para>
///
/// <para>
/// Buttons deliberately do not pass through it. A filtered press reads as lag, and §5.5
/// rule 1 forbids animating real-time input at all — the smoothing here exists to take
/// the jitter out of a stick sitting still, not to make anything look nicer.
/// </para>
/// </summary>
public sealed class PadFilter
{
    /// <summary>The per-frame coefficient at 60 Hz, from §8.4.</summary>
    public const double Coefficient = 0.35;

    /// <summary>Maximum tilt of the whole outline, in degrees (§8.3).</summary>
    public const double MaxTilt = 12;
    public const double MaxPitch = 10;

    /// <summary>How many touch positions the trail behind a finger keeps.</summary>
    public const int TrailLength = 8;

    private readonly List<(double X, double Y)>[] _trails = [new(TrailLength), new(TrailLength)];

    /// <summary>−1…+1, with y growing downwards, the way the drawing grid does.</summary>
    public double LeftX { get; private set; }
    public double LeftY { get; private set; }
    public double RightX { get; private set; }
    public double RightY { get; private set; }

    /// <summary>0…1 trigger travel.</summary>
    public double L2 { get; private set; }
    public double R2 { get; private set; }

    /// <summary>Degrees, clamped to <see cref="MaxTilt"/> and <see cref="MaxPitch"/>.</summary>
    public double Tilt { get; private set; }
    public double Pitch { get; private set; }

    /// <summary>Normalised touch positions, oldest first, newest last.</summary>
    public IReadOnlyList<(double X, double Y)> Trail(int finger) => _trails[finger];

    public void Step(DualSenseInput input, TimeSpan elapsed, bool smoothTilt)
    {
        // A frame that took a quarter of a second — the app was swapped out, or the
        // debugger was sitting on a breakpoint — must not make the filter jump the whole
        // way in one step, and a zero-length frame must not divide anything by nothing.
        var dt = Math.Clamp(elapsed.TotalSeconds, 0.001, 0.25);
        var k = Math.Min(1, Coefficient * dt * 60);

        LeftX += (DualSenseInput.Axis(input.LeftX) - LeftX) * k;
        LeftY += (DualSenseInput.Axis(input.LeftY) - LeftY) * k;
        RightX += (DualSenseInput.Axis(input.RightX) - RightX) * k;
        RightY += (DualSenseInput.Axis(input.RightY) - RightY) * k;
        L2 += (DualSenseInput.Travel(input.L2) - L2) * k;
        R2 += (DualSenseInput.Travel(input.R2) - R2) * k;

        if (smoothTilt)
        {
            Tilt += (Math.Clamp(input.GyroZ / 32767.0 * MaxTilt, -MaxTilt, MaxTilt) - Tilt) * k;
            Pitch += (Math.Clamp(input.GyroX / 32767.0 * MaxPitch, -MaxPitch, MaxPitch) - Pitch) * k;
        }
        else
        {
            Tilt = 0;
            Pitch = 0;
        }

        Track(0, input.Touch0);
        Track(1, input.Touch1);
    }

    /// <summary>Back to a controller sitting still, with no trail left over.</summary>
    public void Reset()
    {
        LeftX = LeftY = RightX = RightY = 0;
        L2 = R2 = 0;
        Tilt = Pitch = 0;
        foreach (var trail in _trails) trail.Clear();
    }

    private void Track(int finger, PadTouch touch)
    {
        var trail = _trails[finger];
        if (!touch.Active)
        {
            // A lifted finger loses its tail at once rather than fading: a trail that
            // outlived the touch would read as a ghost input.
            trail.Clear();
            return;
        }

        trail.Add((touch.NormalisedX, touch.NormalisedY));
        if (trail.Count > TrailLength) trail.RemoveAt(0);
    }
}
