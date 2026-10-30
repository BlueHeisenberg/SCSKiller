using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SCSKiller.Ui.Controls;

/// <summary>The WinUI app's WrapPanel: children left to right, wrapping to a new line instead of clipping. With
/// <see cref="MinItemWidth"/>: equal columns at least that wide, as many as divide the visible children evenly (4 cards: 4,
/// 2 or 1 per row), each stretched to its row's tallest. <see cref="LineAlignment"/>: each line flush left, centred or flush
/// right. Hidden children take no place.</summary>
public sealed class FlowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<FlowPanel, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<FlowPanel, double>(nameof(MinItemWidth));
    public static readonly StyledProperty<HorizontalAlignment> LineAlignmentProperty = AvaloniaProperty.Register<FlowPanel, HorizontalAlignment>(nameof(LineAlignment));

    static FlowPanel() => AffectsMeasure<FlowPanel>(SpacingProperty, MinItemWidthProperty, LineAlignmentProperty);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public HorizontalAlignment LineAlignment { get => GetValue(LineAlignmentProperty); set => SetValue(LineAlignmentProperty, value); }

    protected override Size MeasureOverride(Size available) => Layout(available.Width, arrange: false);

    protected override Size ArrangeOverride(Size final)
    {
        Layout(final.Width, arrange: true);
        return final;
    }

    Size Layout(double width, bool arrange)
    {
        var items = Children.Where(c => c.IsVisible).ToList();
        double col = 0;   // 0: each child at its own width
        if (MinItemWidth > 0 && items.Count > 0 && !double.IsInfinity(width))
        {
            int n = Math.Clamp((int)((width + Spacing) / (MinItemWidth + Spacing)), 1, items.Count);
            while (items.Count % n != 0) n--;
            col = (width - (n - 1) * Spacing) / n;
        }
        if (!arrange) foreach (var c in items) c.Measure(new Size(col > 0 ? col : width, double.PositiveInfinity));

        var rows = new List<List<Control>>();
        double x = 0;
        foreach (var c in items)
        {
            double w = col > 0 ? col : c.DesiredSize.Width;
            if (rows.Count == 0 || (x > 0 && x + w > width + 0.5)) { rows.Add([]); x = 0; }
            rows[^1].Add(c);
            x += w + Spacing;
        }
        double y = 0, used = 0;
        foreach (var row in rows)
        {
            double h = row.Max(c => c.DesiredSize.Height), rx = 0;
            if (arrange && col == 0 && LineAlignment is HorizontalAlignment.Right or HorizontalAlignment.Center)
            {
                double free = Math.Max(0, width - (row.Sum(c => c.DesiredSize.Width) + (row.Count - 1) * Spacing));
                rx = LineAlignment == HorizontalAlignment.Right ? free : free / 2;
            }
            foreach (var c in row)
            {
                double w = col > 0 ? col : c.DesiredSize.Width;
                if (arrange) c.Arrange(new Rect(rx, y, w, col > 0 ? h : c.DesiredSize.Height));
                rx += w + Spacing;
            }
            used = Math.Max(used, rx - Spacing);
            y += h + Spacing;
        }
        return new Size(col > 0 ? width : used, rows.Count > 0 ? y - Spacing : 0);
    }
}
