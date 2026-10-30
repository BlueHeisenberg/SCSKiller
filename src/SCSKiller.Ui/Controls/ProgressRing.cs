using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SCSKiller.Ui.Controls;

/// <summary>WinUI's indeterminate progress ring: an arc that turns while it grows and shrinks.</summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsActive), true);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Foreground));

    static ProgressRing() => AffectsRender<ProgressRing>(IsActiveProperty, ForegroundProperty);

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    TimeSpan? start;
    bool frameAsked;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty) AskFrame();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AskFrame();
    }

    void AskFrame()
    {
        if (frameAsked || !IsActive || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top) return;
        frameAsked = true;
        top.RequestAnimationFrame(t =>
        {
            frameAsked = false;
            start ??= t;
            phase = (t - start.Value).TotalSeconds;
            InvalidateVisual();
            AskFrame();
        });
    }

    double phase;

    public override void Render(DrawingContext context)
    {
        if (!IsActive || Foreground is not { } brush) return;
        double size = Math.Min(Bounds.Width, Bounds.Height), thickness = Math.Max(2, size / 10.6), r = (size - thickness) / 2;
        if (r <= 0) return;
        // one turn per second, the arc between 20 and 270 degrees over two
        double sweep = 20 + 250 * (1 - Math.Cos(phase * Math.PI)) / 2, begin = phase * 360 - 90;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        Point At(double degrees) => new(center.X + r * Math.Cos(degrees * Math.PI / 180), center.Y + r * Math.Sin(degrees * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(begin), false);
            g.ArcTo(At(begin + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round), geometry);
    }
}
