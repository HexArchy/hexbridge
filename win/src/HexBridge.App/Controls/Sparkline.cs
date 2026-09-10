using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HexBridge.App.Controls;

/// <summary>
/// A minute of history drawn as a filled line. No axes: the point is the shape — steady,
/// dipping, or stalled — and the exact numbers are already on the tiles next to it.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 1.0);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> AreaBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(AreaBrush));

    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(BaselineBrush));

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? AreaBrush
    {
        get => GetValue(AreaBrushProperty);
        set => SetValue(AreaBrushProperty, value);
    }

    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, MaximumProperty, StrokeProperty,
            AreaBrushProperty, BaselineBrushProperty);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        if (BaselineBrush is { } baseline)
        {
            context.DrawRectangle(baseline, null, new Rect(0, h - 1, w, 1));
        }

        var values = Values;
        if (values is null || values.Count < 2) return;

        var max = Maximum > 0 ? Maximum : 1;
        // Leave a sliver at the top so a pegged value still reads as a line, not as the edge.
        var usable = h - 3;

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = w * i / (values.Count - 1.0);
                var y = h - 1 - Math.Clamp(values[i] / max, 0, 1) * usable;
                if (i == 0) sink.BeginFigure(new Point(x, y), isFilled: false);
                else sink.LineTo(new Point(x, y));
            }
            sink.EndFigure(false);
        }

        if (AreaBrush is { } area)
        {
            var filled = new StreamGeometry();
            using (var sink = filled.Open())
            {
                sink.BeginFigure(new Point(0, h), isFilled: true);
                for (var i = 0; i < values.Count; i++)
                {
                    var x = w * i / (values.Count - 1.0);
                    var y = h - 1 - Math.Clamp(values[i] / max, 0, 1) * usable;
                    sink.LineTo(new Point(x, y));
                }
                sink.LineTo(new Point(w, h));
                sink.EndFigure(true);
            }
            context.DrawGeometry(area, null, filled);
        }

        if (Stroke is { } stroke)
        {
            context.DrawGeometry(null, new Pen(stroke, 1.6, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
