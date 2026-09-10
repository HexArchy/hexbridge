using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using HexBridge.DualSense;

namespace HexBridge.App.Controls;

/// <summary>How the outline is coloured, which is how DESIGN.md §7.3 tells its states apart.</summary>
public enum PadMood
{
    /// <summary>Nothing plugged in: flat <c>off</c> at 0.35 alpha, and no reaction to input.</summary>
    Inactive,

    /// <summary>Read but not forwarded: fully live, drawn in <c>textDim</c>.</summary>
    Reading,

    /// <summary>Forwarded and acknowledged by Windows: live and accented.</summary>
    Forwarding,
}

/// <summary>
/// The normalised 400 × 260 grid from DESIGN.md §8.2. Every number in this file is in that
/// grid and never in pixels; the whole thing is scaled uniformly at draw time.
///
/// The numbers are the same ones the Mac's <c>ControllerGeometry</c> uses, so the two
/// visualisations are recognisably the same object rather than two people's idea of a
/// gamepad.
/// </summary>
internal static class PadGeometry
{
    /// <summary>The trigger petals stick out above y = 0, so the drawn box starts at −20.</summary>
    internal static readonly Rect Box = new(0, -20, 400, 280);

    internal static readonly Rect Body = new(60, 20, 280, 120);
    internal const double BodyRadius = 40;

    internal const double GripWidth = 56;
    internal const double GripHeight = 130;
    internal const double GripRadius = 28;
    internal static readonly Point LeftGrip = new(118, 178);
    internal static readonly Point RightGrip = new(282, 178);
    internal const double GripTilt = 12;

    internal static readonly Rect Touchpad = new(148, 38, 104, 60);
    internal const double TouchpadRadius = 8;

    internal static readonly Point LeftStick = new(150, 122);
    internal static readonly Point RightStick = new(250, 122);
    internal const double StickRim = 26;
    internal const double StickCap = 17;
    /// <summary>§8.3: the cap travels 14 units, the rim stays where it is.</summary>
    internal const double StickTravel = 14;

    internal static readonly Point DpadCentre = new(96, 74);
    internal const double DpadArmShort = 13;
    internal const double DpadArmLong = 22;
    internal const double DpadRadius = 5;

    internal static readonly Point FaceCentre = new(304, 74);
    internal const double FaceRadius = 11;
    internal const double FaceSpread = 26;

    internal static readonly Rect L1 = new(78, 6, 52, 12);
    internal static readonly Rect R1 = new(270, 6, 52, 12);
    internal const double ShoulderRadius = 6;

    internal static readonly Rect L2 = new(78, -16, 52, 22);
    internal static readonly Rect R2 = new(270, -16, 52, 22);
    internal const double TriggerRadius = 8;
    /// <summary>§8.3: a full pull swings the petal 18° about its lower edge.</summary>
    internal const double TriggerTilt = 18;

    internal static readonly Rect Create = new(126, 46, 10, 18);
    internal static readonly Rect Options = new(264, 46, 10, 18);
    internal const double SmallRadius = 5;

    internal static readonly Point PsCentre = new(200, 152);
    internal const double PsRadius = 9;
    internal static readonly Rect Mute = new(190, 126, 20, 10);

    internal const double LightbarLeftX = 142;
    internal const double LightbarRightX = 258;
    internal const double LightbarTop = 44;
    internal const double LightbarBottom = 92;
    internal const double LightbarWidth = 4;
}

/// <summary>
/// The live schematic of the controller (DESIGN.md §8) — the app's one deliberately pretty
/// screen, and the one that justifies itself functionally: the user pulls a trigger and
/// sees that it arrived. No sentence does that.
///
/// <para>
/// Deliberately <b>not</b> a DualSense silhouette. Sony holds industrial design
/// registrations on the body and a trademark on the △ ○ ✕ □ set, and no freely licensed
/// vector outline of the pad exists (§3.3–3.4). What is drawn is a geometric abstraction —
/// a rounded body, two grips, circles and capsules — that reads as «gamepad», imitates
/// nothing, and repaints for either theme from tokens.
/// </para>
///
/// <para>
/// <b>Deviation from §8.4 rule 0, recorded rather than hidden.</b> The document asks for
/// <c>CompositionCustomVisualHandler</c>, which owns a frame loop on the render thread.
/// That handler draws through <c>ImmediateDrawingContext</c>, and in Avalonia 12.1.2 that
/// type exposes rectangles, ellipses, bitmaps and glyph runs — it has no
/// <c>DrawGeometry</c>. The grips, the trigger petals and the touch trails are geometry, so
/// the handler would have to reach for platform implementation interfaces to draw them.
/// This control therefore uses <see cref="Control.Render"/> behind a timer, and buys back
/// the cost the document was worried about with the four rules underneath it: no children,
/// so no layout pass per frame; no pen rebuilt per frame; no invalidation when the report
/// has not changed and the filter has caught up; and — the one that actually matters — the
/// timer drops to a watchdog when nothing is on screen and stops outright when the control
/// leaves the visual tree, which is what a tab switch does to it.
/// </para>
///
/// <para>
/// The one thing rule 3 asks for and does not get is zero allocation: the touch trail and
/// the lightbar glow need a brush at a changing alpha, and Avalonia's brushes carry their
/// opacity rather than taking it per draw. That is at most two dozen 32-byte immutable
/// brushes a frame, all of which die in gen 0.
/// </para>
/// </summary>
public sealed class ControllerVisual : Control
{
    public static readonly StyledProperty<DualSenseInputSource?> SourceProperty =
        AvaloniaProperty.Register<ControllerVisual, DualSenseInputSource?>(nameof(Source));

    public static readonly StyledProperty<PadMood> MoodProperty =
        AvaloniaProperty.Register<ControllerVisual, PadMood>(nameof(Mood), PadMood.Inactive);

    // Object colours, §8.5. They are set from Styles/Theme.axaml and never written here:
    // the visualisation has its own scale, and it must not drift with the surface tokens.
    public static readonly StyledProperty<IBrush?> BodyBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(BodyBrush));

    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(OutlineBrush));

    public static readonly StyledProperty<IBrush?> ButtonBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(ButtonBrush));

    public static readonly StyledProperty<IBrush?> ButtonEdgeBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(ButtonEdgeBrush));

    public static readonly StyledProperty<IBrush?> StickRimBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(StickRimBrush));

    public static readonly StyledProperty<IBrush?> StickWellBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(StickWellBrush));

    public static readonly StyledProperty<IBrush?> StickCapBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(StickCapBrush));

    public static readonly StyledProperty<IBrush?> TouchBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(TouchBrush));

    public static readonly StyledProperty<IBrush?> TouchEdgeBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(TouchEdgeBrush));

    public static readonly StyledProperty<IBrush?> LightbarOffBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(LightbarOffBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> DimBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(DimBrush));

    public static readonly StyledProperty<IBrush?> OffBrushProperty =
        AvaloniaProperty.Register<ControllerVisual, IBrush?>(nameof(OffBrush));

    /// <summary>Live input, polled at this control's own frame rate rather than pushed.</summary>
    public DualSenseInputSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public PadMood Mood
    {
        get => GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public IBrush? BodyBrush { get => GetValue(BodyBrushProperty); set => SetValue(BodyBrushProperty, value); }
    public IBrush? OutlineBrush { get => GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }
    public IBrush? ButtonBrush { get => GetValue(ButtonBrushProperty); set => SetValue(ButtonBrushProperty, value); }
    public IBrush? ButtonEdgeBrush { get => GetValue(ButtonEdgeBrushProperty); set => SetValue(ButtonEdgeBrushProperty, value); }
    public IBrush? StickRimBrush { get => GetValue(StickRimBrushProperty); set => SetValue(StickRimBrushProperty, value); }
    public IBrush? StickWellBrush { get => GetValue(StickWellBrushProperty); set => SetValue(StickWellBrushProperty, value); }
    public IBrush? StickCapBrush { get => GetValue(StickCapBrushProperty); set => SetValue(StickCapBrushProperty, value); }
    public IBrush? TouchBrush { get => GetValue(TouchBrushProperty); set => SetValue(TouchBrushProperty, value); }
    public IBrush? TouchEdgeBrush { get => GetValue(TouchEdgeBrushProperty); set => SetValue(TouchEdgeBrushProperty, value); }
    public IBrush? LightbarOffBrush { get => GetValue(LightbarOffBrushProperty); set => SetValue(LightbarOffBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? DimBrush { get => GetValue(DimBrushProperty); set => SetValue(DimBrushProperty, value); }
    public IBrush? OffBrush { get => GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    static ControllerVisual()
    {
        AffectsRender<ControllerVisual>(
            MoodProperty, BodyBrushProperty, OutlineBrushProperty, ButtonBrushProperty,
            ButtonEdgeBrushProperty, StickRimBrushProperty, StickWellBrushProperty,
            StickCapBrushProperty, TouchBrushProperty, TouchEdgeBrushProperty,
            LightbarOffBrushProperty, AccentBrushProperty, DimBrushProperty, OffBrushProperty);
    }

    private readonly PadFilter _filter = new();
    private readonly DispatcherTimer _timer;

    private DualSenseInput _input;
    private long _version = -1;
    private DateTime _lastReportAt = DateTime.MinValue;
    private DateTime _lastFrameAt = DateTime.UtcNow;
    private int _hz = -1;

    // Built once at the top of every Render and then left alone.
    //
    // They must be immutable. A DrawingContext records draw commands into the scene graph
    // rather than rasterising them, and a mutable Pen goes in by reference — so one pen
    // reused across twenty shapes and re-coloured between them would paint all twenty in
    // whatever colour it happened to hold when the frame was rasterised. Six small
    // immutable pens a frame is the cheapest way to be certainly right.
    private ImmutablePen _penOutline = new(Brushes.Transparent, 2);
    private ImmutablePen _penRim = new(Brushes.Transparent, 2);
    private ImmutablePen _penEdge = new(Brushes.Transparent, 1.5);
    private ImmutablePen _penEdgeActive = new(Brushes.Transparent, 1.5);
    private ImmutablePen _penHair = new(Brushes.Transparent, 1);
    private ImmutablePen _penTouchEdge = new(Brushes.Transparent, 1);

    /// <summary>
    /// What the timer costs while nothing is happening. Two wake-ups a second, each of
    /// which compares four booleans and returns — that is the price of noticing that the
    /// window came back, and it is smaller than any way of being told.
    /// </summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(0.5);

    public ControllerVisual() =>
        _timer = new DispatcherTimer(Watchdog, DispatcherPriority.Render, OnTick);

    /// <summary>
    /// §8.4 rule 4: the frame loop follows visibility, and the strongest form of that is
    /// this — a tab the user is not looking at has its content detached by
    /// <c>TabControl</c>, and the timer stops outright rather than merely slowing down.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hz = -1;
        _lastFrameAt = DateTime.UtcNow;
        Reassess();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _hz = -1;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            // A different controller — or none — must not inherit the previous one's
            // smoothed position and trails.
            _filter.Reset();
            _version = -1;
            _lastReportAt = DateTime.MinValue;
        }

        if (change.Property == SourceProperty || change.Property == MoodProperty) Reassess();
    }

    /// <summary>The 400 × 280 box, scaled to whatever it is given, aspect preserved.</summary>
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? PadGeometry.Box.Width : available.Width;
        var height = double.IsInfinity(available.Height) ? PadGeometry.Box.Height : available.Height;
        var scale = Math.Min(width / PadGeometry.Box.Width, height / PadGeometry.Box.Height);
        return new Size(PadGeometry.Box.Width * scale, PadGeometry.Box.Height * scale);
    }

    // MARK: - The frame loop

    private void OnTick(object? sender, EventArgs e)
    {
        if (_hz == 0)
        {
            // Nothing is on screen. This tick exists only to notice when that changes.
            Reassess();
            return;
        }

        var source = Source;
        if (source is null)
        {
            Reassess();
            return;
        }

        // §8.4 rule 2: a snapshot that has not changed does not get a frame. A controller
        // sitting perfectly still is the common case and it should cost nothing.
        var version = source.Version;
        if (version != _version)
        {
            _version = version;
            _input = source.Read();
            _lastReportAt = DateTime.UtcNow;
        }
        else if (_filter.Tilt == 0 && _filter.Pitch == 0 && Settled())
        {
            // Nothing moved and nothing is still catching up, so this frame is skipped.
            // The clock still advances: leaving it behind would make the first frame after
            // an idle stretch see a huge elapsed time and snap the filter straight to its
            // target, which reads as the smoothing having been switched off.
            _lastFrameAt = DateTime.UtcNow;
            Reassess();
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - _lastFrameAt;
        _lastFrameAt = now;

        _filter.Step(_input, elapsed, smoothTilt: Mood != PadMood.Inactive && !ReducedMotion.IsEnabled);
        InvalidateVisual();
        Reassess();
    }

    /// <summary>True when every smoothed value has caught up with its target.</summary>
    private bool Settled()
    {
        const double Epsilon = 0.002;
        return Math.Abs(_filter.LeftX - DualSenseInput.Axis(_input.LeftX)) < Epsilon
            && Math.Abs(_filter.LeftY - DualSenseInput.Axis(_input.LeftY)) < Epsilon
            && Math.Abs(_filter.RightX - DualSenseInput.Axis(_input.RightX)) < Epsilon
            && Math.Abs(_filter.RightY - DualSenseInput.Axis(_input.RightY)) < Epsilon
            && Math.Abs(_filter.L2 - DualSenseInput.Travel(_input.L2)) < Epsilon
            && Math.Abs(_filter.R2 - DualSenseInput.Travel(_input.R2)) < Epsilon;
    }

    /// <summary>Picks the rate from §8.4 and restarts the timer only when it changed.</summary>
    private void Reassess()
    {
        var idle = _lastReportAt == DateTime.MinValue
            ? TimeSpan.MaxValue
            : DateTime.UtcNow - _lastReportAt;

        var hz = PadFrameRate.Hz(
            visible: IsEffectivelyVisible && ((ILogical)this).IsAttachedToLogicalTree,
            hasController: Source is not null && Mood != PadMood.Inactive,
            windowActive: TopLevel.GetTopLevel(this) is not Window window || window.IsActive,
            reducedMotion: ReducedMotion.IsEnabled,
            idle: idle);

        if (hz == _hz) return;
        _hz = hz;

        _timer.Interval = hz == 0 ? Watchdog : TimeSpan.FromSeconds(1.0 / hz);
        // Restarting is what makes a changed interval take effect; a running timer keeps
        // its old one until it next fires.
        _timer.Stop();
        _lastFrameAt = DateTime.UtcNow;
        _timer.Start();
    }

    // MARK: - Colours

    private bool Inactive => Mood is PadMood.Inactive;

    /// <summary>§7.3: an unplugged controller is drawn flat and does not react.</summary>
    private const double InactiveAlpha = 0.35;

    private IBrush? Outline => Inactive ? Fade(OffBrush, InactiveAlpha) : OutlineBrush;
    private IBrush? Body => Inactive ? Fade(OffBrush, 0.12) : BodyBrush;
    private IBrush? Passive => Inactive ? Fade(OffBrush, 0.15) : ButtonBrush;
    private IBrush? PassiveEdge => Inactive ? Fade(OffBrush, 0.3) : ButtonEdgeBrush;

    /// <summary>
    /// The accent for a pressed button and a pulled trigger. «Read» uses the dim colour so
    /// the difference between «прочитан» and «проброшен» is visible at a glance (§7.3).
    /// </summary>
    private IBrush? Active => Mood switch
    {
        PadMood.Forwarding => AccentBrush,
        PadMood.Reading => DimBrush,
        _ => Fade(OffBrush, InactiveAlpha),
    };

    /// <summary>
    /// A brush a recorded draw command may safely hold on to. A theme brush is mutable and
    /// a live DynamicResource target, so freezing it is what stops a theme switch halfway
    /// through a frame from recolouring shapes already handed to the renderer.
    /// </summary>
    private static IImmutableBrush? Frozen(IBrush? brush) => brush?.ToImmutable();

    private static IBrush? Fade(IBrush? brush, double opacity)
    {
        if (brush is not ISolidColorBrush solid) return brush;
        var colour = solid.Color;
        return new ImmutableSolidColorBrush(colour, opacity);
    }

    // MARK: - Drawing

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) return;

        var scale = Math.Min(width / PadGeometry.Box.Width, height / PadGeometry.Box.Height);
        var offsetX = (width - PadGeometry.Box.Width * scale) / 2;
        var offsetY = (height - PadGeometry.Box.Height * scale) / 2;

        var fit = Matrix.CreateTranslation(-PadGeometry.Box.X, -PadGeometry.Box.Y)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(offsetX, offsetY);

        // §8.3: the whole outline leans with the gyro. Small on purpose — this has to read
        // as «оно живое», not as a fairground ride. The pitch is faked with a skew rather
        // than a projection: Avalonia has no 3-D transform, and at ten degrees nobody can
        // tell the difference.
        if (_filter.Tilt != 0 || _filter.Pitch != 0)
        {
            var centre = new Point(200, 130);
            fit = Matrix.CreateTranslation(-centre.X, -centre.Y)
                * Matrix.CreateSkew(0, _filter.Pitch * Math.PI / 180 * 0.35)
                * Matrix.CreateRotation(_filter.Tilt * Math.PI / 180)
                * Matrix.CreateTranslation(centre.X, centre.Y)
                * fit;
        }

        using var _ = context.PushTransform(fit);

        _penOutline = new ImmutablePen(Frozen(Outline), 2);
        _penRim = new ImmutablePen(Frozen(Inactive ? Fade(OffBrush, 0.3) : StickRimBrush), 2);
        _penEdge = new ImmutablePen(Frozen(PassiveEdge), 1.5);
        _penEdgeActive = new ImmutablePen(Frozen(Active), 1.5);
        _penHair = new ImmutablePen(Frozen(OutlineBrush), 1);
        _penTouchEdge = new ImmutablePen(Frozen(Inactive ? Fade(OffBrush, 0.3) : TouchEdgeBrush), 1);

        var input = Inactive ? default : _input;

        // Bottom up, in exactly the order §8.2 lists.
        Grip(context, PadGeometry.LeftGrip, -PadGeometry.GripTilt);
        Grip(context, PadGeometry.RightGrip, PadGeometry.GripTilt);
        context.DrawRectangle(Body, _penOutline, new RoundedRect(PadGeometry.Body, PadGeometry.BodyRadius));

        Lightbar(context);
        Touchpad(context, input);

        Dpad(context, input);
        Face(context, input);
        Capsule(context, PadGeometry.Create, PadGeometry.SmallRadius, input.IsPressed(PadButtons.Create));
        Capsule(context, PadGeometry.Options, PadGeometry.SmallRadius, input.IsPressed(PadButtons.Options));
        Capsule(context, PadGeometry.Mute, PadGeometry.SmallRadius, input.IsPressed(PadButtons.Mute));
        Circle(context, PadGeometry.PsCentre, PadGeometry.PsRadius, input.IsPressed(PadButtons.Home));
        Capsule(context, PadGeometry.L1, PadGeometry.ShoulderRadius, input.IsPressed(PadButtons.L1));
        Capsule(context, PadGeometry.R1, PadGeometry.ShoulderRadius, input.IsPressed(PadButtons.R1));

        Stick(context, PadGeometry.LeftStick, _filter.LeftX, _filter.LeftY, input.IsPressed(PadButtons.L3));
        Stick(context, PadGeometry.RightStick, _filter.RightX, _filter.RightY, input.IsPressed(PadButtons.R3));

        Trigger(context, PadGeometry.L2, _filter.L2);
        Trigger(context, PadGeometry.R2, _filter.R2);
    }

    private void Grip(DrawingContext context, Point centre, double degrees)
    {
        var rect = new Rect(
            centre.X - PadGeometry.GripWidth / 2,
            centre.Y - PadGeometry.GripHeight / 2,
            PadGeometry.GripWidth,
            PadGeometry.GripHeight);

        using var _ = context.PushTransform(
            Matrix.CreateTranslation(-centre.X, -centre.Y)
            * Matrix.CreateRotation(degrees * Math.PI / 180)
            * Matrix.CreateTranslation(centre.X, centre.Y));

        context.DrawRectangle(Body, _penOutline, new RoundedRect(rect, PadGeometry.GripRadius));
    }

    private void Lightbar(DrawingContext context)
    {
        var colour = Inactive ? null : Source?.Lightbar;

        foreach (var x in (double[])[PadGeometry.LightbarLeftX, PadGeometry.LightbarRightX])
        {
            var rect = new Rect(
                x - PadGeometry.LightbarWidth / 2,
                PadGeometry.LightbarTop,
                PadGeometry.LightbarWidth,
                PadGeometry.LightbarBottom - PadGeometry.LightbarTop);
            var radius = PadGeometry.LightbarWidth / 2;

            if (colour is not { } rgb)
            {
                context.DrawRectangle(LightbarOffBrush, null, new RoundedRect(rect, radius));
                continue;
            }

            // §8.5: the light theme desaturates to 0.9, or a white lightbar disappears
            // into a white body.
            var dark = ActualThemeVariant == ThemeVariant.Dark;
            var saturation = dark ? 1.0 : 0.9;
            var real = Color.FromRgb(
                Mix(rgb.R, saturation), Mix(rgb.G, saturation), Mix(rgb.B, saturation));

            if (dark)
            {
                // Avalonia has no per-shape blur in a DrawingContext, so the glow is two
                // wider, fainter passes rather than a filter. At four units across, the
                // difference is not visible; the cost of a real blur would be.
                context.DrawRectangle(
                    new ImmutableSolidColorBrush(real, 0.22), null,
                    new RoundedRect(rect.Inflate(4), radius + 4));
                context.DrawRectangle(
                    new ImmutableSolidColorBrush(real, 0.30), null,
                    new RoundedRect(rect.Inflate(2), radius + 2));
            }

            context.DrawRectangle(new ImmutableSolidColorBrush(real), null, new RoundedRect(rect, radius));

            // A near-white bar in the light theme still needs an edge.
            if (!dark && (rgb.R + rgb.G + rgb.B) / 765.0 > 0.9)
            {
                context.DrawRectangle(null, _penHair, new RoundedRect(rect, radius));
            }
        }
    }

    private static byte Mix(byte value, double saturation) =>
        (byte)Math.Clamp(value / 255.0 * saturation * 255 + (1 - saturation) * 127.5, 0, 255);

    private void Touchpad(DrawingContext context, DualSenseInput input)
    {
        var pressed = input.IsPressed(PadButtons.TouchpadClick);
        var fill = pressed ? Fade(Active, 0.25) : Inactive ? Fade(OffBrush, 0.12) : TouchBrush;

        context.DrawRectangle(fill, _penTouchEdge, new RoundedRect(PadGeometry.Touchpad, PadGeometry.TouchpadRadius));

        if (Inactive) return;

        for (var finger = 0; finger < 2; finger++)
        {
            var trail = _filter.Trail(finger);
            for (var i = 0; i < trail.Count; i++)
            {
                var (x, y) = trail[i];
                var point = new Point(
                    PadGeometry.Touchpad.X + x * PadGeometry.Touchpad.Width,
                    PadGeometry.Touchpad.Y + y * PadGeometry.Touchpad.Height);

                // Newest is opaque and large; the eight-sample tail fades away behind it.
                var newest = i == trail.Count - 1;
                var fade = (i + 1) / (double)trail.Count;
                var radius = newest ? 7.0 : 4.0;

                if (newest && ActualThemeVariant == ThemeVariant.Dark)
                {
                    context.DrawEllipse(Fade(Active, 0.35), null, point, radius + 4, radius + 4);
                }
                context.DrawEllipse(Fade(Active, fade), null, point, radius, radius);
            }
        }
    }

    private void Dpad(DrawingContext context, DualSenseInput input)
    {
        var c = PadGeometry.DpadCentre;
        const double Short = PadGeometry.DpadArmShort;
        const double Long = PadGeometry.DpadArmLong;

        Capsule(context, new Rect(c.X - Short / 2, c.Y - Long - 4, Short, Long),
            PadGeometry.DpadRadius, input.Hat.Includes(PadDirection.Up));
        Capsule(context, new Rect(c.X - Short / 2, c.Y + 4, Short, Long),
            PadGeometry.DpadRadius, input.Hat.Includes(PadDirection.Down));
        Capsule(context, new Rect(c.X - Long - 4, c.Y - Short / 2, Long, Short),
            PadGeometry.DpadRadius, input.Hat.Includes(PadDirection.Left));
        Capsule(context, new Rect(c.X + 4, c.Y - Short / 2, Long, Short),
            PadGeometry.DpadRadius, input.Hat.Includes(PadDirection.Right));
    }

    /// <summary>
    /// Four plain circles. §8.6: the △ ○ ✕ □ set is a Sony trademark, so the buttons are
    /// identified by position — the same positions the hardware uses — and never by glyph.
    /// </summary>
    private void Face(DrawingContext context, DualSenseInput input)
    {
        var c = PadGeometry.FaceCentre;
        const double S = PadGeometry.FaceSpread;

        Circle(context, new Point(c.X, c.Y - S), PadGeometry.FaceRadius, input.IsPressed(PadButtons.Triangle));
        Circle(context, new Point(c.X + S, c.Y), PadGeometry.FaceRadius, input.IsPressed(PadButtons.Circle));
        Circle(context, new Point(c.X, c.Y + S), PadGeometry.FaceRadius, input.IsPressed(PadButtons.Cross));
        Circle(context, new Point(c.X - S, c.Y), PadGeometry.FaceRadius, input.IsPressed(PadButtons.Square));
    }

    private void Stick(DrawingContext context, Point centre, double x, double y, bool pressed)
    {
        context.DrawEllipse(
            Inactive ? Fade(OffBrush, 0.12) : StickWellBrush, _penRim,
            centre, PadGeometry.StickRim, PadGeometry.StickRim);

        // The grid's y grows downwards and so does the raw stick value, so the cap follows
        // the axis directly — no flip, unlike the Mac, whose normalised value is flipped
        // upstream to match the platform's gamepad conventions.
        var cap = new Point(
            centre.X + x * PadGeometry.StickTravel,
            centre.Y + y * PadGeometry.StickTravel);

        context.DrawEllipse(
            pressed ? Active : Inactive ? Fade(OffBrush, 0.25) : StickCapBrush,
            pressed ? _penEdgeActive : _penEdge,
            cap, PadGeometry.StickCap, PadGeometry.StickCap);
    }

    /// <summary>
    /// §8.3: the petal both swings about its lower edge and fills from the top in
    /// proportion to the pull. Two cues for one value, because at this scale the rotation
    /// alone is too subtle and the fill alone reads as a progress bar.
    /// </summary>
    private void Trigger(DrawingContext context, Rect rect, double pull)
    {
        var pivot = new Point(rect.Center.X, rect.Bottom);
        using var _ = context.PushTransform(
            Matrix.CreateTranslation(-pivot.X, -pivot.Y)
            * Matrix.CreateRotation(-PadGeometry.TriggerTilt * pull * Math.PI / 180)
            * Matrix.CreateTranslation(pivot.X, pivot.Y));

        var shape = new RoundedRect(rect, PadGeometry.TriggerRadius);
        context.DrawRectangle(Passive, null, shape);

        if (pull > 0.01 && !Inactive)
        {
            using (context.PushClip(new Rect(rect.X, rect.Y, rect.Width, rect.Height * pull)))
            {
                var dark = ActualThemeVariant == ThemeVariant.Dark;
                context.DrawRectangle(Fade(Active, dark ? 0.9 : 0.85), null, shape);
            }
        }

        context.DrawRectangle(null, _penEdge, shape);
    }

    /// <summary>
    /// §8.3 and §5.4 #19: a pressed button fills with the accent and shrinks to 0.94 — with
    /// no animation at all. Real-time input is never animated.
    /// </summary>
    private void Capsule(DrawingContext context, Rect rect, double radius, bool pressed)
    {
        var target = pressed ? rect.Inflate(new Thickness(-rect.Width * 0.03, -rect.Height * 0.03)) : rect;
        context.DrawRectangle(
            pressed ? Active : Passive, pressed ? _penEdgeActive : _penEdge, new RoundedRect(target, radius));
    }

    private void Circle(DrawingContext context, Point centre, double radius, bool pressed)
    {
        var r = pressed ? radius * 0.94 : radius;
        context.DrawEllipse(pressed ? Active : Passive, pressed ? _penEdgeActive : _penEdge, centre, r, r);
    }
}
