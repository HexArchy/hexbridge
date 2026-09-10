using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HexBridge.App.Controls;

/// <summary>
/// A horizontal loudness bar. The gradient is fixed to the track rather than to the current
/// level, so a given colour always means the same dBFS — that is what makes it readable at
/// a glance while someone is talking.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Peak));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(MutedBrush));

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<LevelMeter, bool>(nameof(IsMuted));

    /// <summary>Normalised 0..1 position of the current level on the meter scale.</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>Normalised 0..1 position of the decaying peak marker.</summary>
    public double Peak
    {
        get => GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Flat colour used instead of the scale while the microphone is muted.</summary>
    public IBrush? MutedBrush
    {
        get => GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    /// <summary>
    /// Muted still shows movement — the microphone is being read, it is just not being
    /// sent — but drops the colour scale, so "hot" and "quiet" stop competing for
    /// attention when neither matters.
    /// </summary>
    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(
            LevelProperty, PeakProperty, TrackBrushProperty, MutedBrushProperty, IsMutedProperty);
    }

    private static readonly LinearGradientBrush Scale = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0x2E, 0xA0, 0x6B), 0.00),   // -60 dBFS
            new GradientStop(Color.FromRgb(0x3E, 0xC4, 0x7F), 0.55),   // -27 dBFS, normal speech
            new GradientStop(Color.FromRgb(0xE3, 0xB3, 0x41), 0.83),   // -10 dBFS, hot
            new GradientStop(Color.FromRgb(0xDE, 0x4A, 0x4A), 1.00),   //   0 dBFS, clipping
        },
    };

    public override void Render(DrawingContext context)
    {
        var height = Bounds.Height;
        var width = Bounds.Width;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        context.DrawRectangle(TrackBrush, null, new RoundedRect(new Rect(0, 0, width, height), radius));

        var level = Math.Clamp(Level, 0, 1);
        if (level > 0)
        {
            // The gradient is painted across the whole track and then clipped, so the fill
            // keeps its absolute colour instead of restretching as the level moves.
            using (context.PushClip(new Rect(0, 0, width * level, height)))
            {
                var fill = IsMuted ? MutedBrush : Scale;
                context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, width, height), radius));
            }
        }

        var peak = Math.Clamp(Peak, 0, 1);
        if (!IsMuted && peak > 0.005)
        {
            var x = Math.Clamp(width * peak, 1.5, width - 1.5);
            context.DrawRectangle(Brushes.White, null, new Rect(x - 1, 1, 2, height - 2));
        }
    }
}
