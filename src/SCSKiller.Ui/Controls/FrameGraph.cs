using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Controls;

/// <summary>A launch's frame times as the graph draws them: its length, the startup, the longest frame of each of the
/// equal slices of the launch (<see cref="FrameReport.Peaks"/>) and the frames of 50 ms or more. <paramref name="Neutral"/>:
/// the hitches of play are drawn alike, without a cause (DirectX 11 can't tell shader stutters from the rest).</summary>
public sealed record FrameData(double DurationMs, double StartupMs, IReadOnlyList<float> Peaks, IReadOnlyList<Hitch> Hitches, bool Neutral = false)
{
    public static FrameData Of(FrameReport f) => new(f.Duration.TotalMilliseconds, f.Startup.TotalMilliseconds, f.Peaks, f.Hitches);
    public static FrameData Of(SessionSnapshot s) => new(s.DurationMs, s.StartupMs, s.Peaks, s.Hitches);

    /// <summary>Play: after startup, before the quit window (FrameLog's last 10 s).</summary>
    public (double From, double To) Play => (StartupMs, Math.Max(StartupMs, DurationMs - FrameLog.QuitMs));
}

/// <summary>Frame times over a view of a launch: the longest frame of each pixel column (a spike is never averaged away),
/// loading and quitting shaded (a load that compiled shaders in the warning colour), each hitch of play a dot with its
/// time, length and cause on hover, a dashed 50 ms line. The y axis stops at <see cref="TopFor"/> unless the caller gives one.</summary>
public sealed class FrameGraph : UserControl
{
    FrameData? data;
    double from, to;
    double? top;
    (FrameData? Data, double From, double To, double? Top, double W, double H) drawn;

    readonly Path loadingBand = new() { Opacity = 0.45 }, compilingBand = new() { Opacity = 0.3 }, otherDots = new(), shaderDots = new();
    readonly Line line50 = new() { StrokeThickness = 1, StrokeDashArray = [4, 3] };
    readonly TextBlock label50 = new() { Text = "50 ms", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    readonly Polyline times = new() { StrokeThickness = 1 };
    readonly Canvas tips = new();
    readonly Panel area;

    public FrameGraph()
    {
        Res(loadingBand, Shape.FillProperty, "ControlStrongFillColorDisabledBrush");
        Res(compilingBand, Shape.FillProperty, "WarnBrush");
        Res(line50, Shape.StrokeProperty, "TextFillColorTertiaryBrush");
        Res(label50, TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Res(times, Shape.StrokeProperty, "AccentFillColorDefaultBrush");
        Res(otherDots, Shape.FillProperty, "TextFillColorSecondaryBrush");
        Res(shaderDots, Shape.FillProperty, "WarnBrush");
        area = new Panel { Background = Brushes.Transparent, Children = { loadingBand, compilingBand, line50, label50, times, otherDots, shaderDots, tips } };
        area.SizeChanged += (_, _) => Draw();
        Content = area;
    }

    static void Res(StyledElement e, AvaloniaProperty p, string key) => e.Bind(p, e.GetResourceObservable(key));

    /// <summary>The view from <paramref name="fromMs"/> to <paramref name="toMs"/> (default the whole launch); with
    /// <paramref name="secondsPerPixel"/> the control takes the width that scale gives the view.</summary>
    public void Show(FrameData? d, double fromMs = 0, double? toMs = null, double? topMs = null, double? secondsPerPixel = null)
    {
        (data, from, to, top) = (d, fromMs, toMs ?? d?.DurationMs ?? 0, topMs);
        if (secondsPerPixel is > 0 and var spp) Width = Math.Max(10, (to - from) / 1000 / spp);
        Draw();
    }

    /// <summary>The game page's rule: the worst hitch of play × 1.15, within 60-250 ms.</summary>
    public static double TopFor(IEnumerable<Hitch> hitches) =>
        Math.Clamp(hitches.Where(x => x.Cause is HitchCause.Shader or HitchCause.Other).Select(x => x.Ms).DefaultIfEmpty(FrameLog.HitchMs).Max() * 1.15, 60, 250);

    void Draw()
    {
        double w = area.Bounds.Width, h = area.Bounds.Height;
        if (data is not { Peaks.Count: > 0 } f || w < 10 || h < 10 || drawn == (f, from, to, top, w, h)) return;
        drawn = (f, from, to, top, w, h);
        double total = Math.Max(1, f.DurationMs), span = Math.Max(1, to - from);
        int n = f.Peaks.Count;
        // only slices wholly in the view: the one holding the end of startup would put a loading frame at play's start
        int i0 = Math.Clamp((int)Math.Ceiling(from / total * n), 0, n), i1 = Math.Clamp((int)(to / total * n), i0, n);
        int count = Math.Max(1, i1 - i0);
        // fewer slices than pixels in a short view: one point per slice, so an empty column never reads as 0 ms
        int cols = Math.Max(1, Math.Min((int)w, count));
        var col = new double[cols];
        for (int i = i0; i < i1; i++) col[(i - i0) * cols / count] = Math.Max(col[(i - i0) * cols / count], f.Peaks[i]);
        double y = top ?? TopFor(f.Hitches);
        double X(double ms) => (ms - from) / span * w;
        double Y(double ms) => h - 1 - Math.Min(ms, y) / y * (h - 6);

        var points = new AvaloniaList<Point>();
        for (int c = 0; c < cols; c++) points.Add(new Point((c + 0.5) * w / cols, Y(col[c])));
        times.Points = points;
        (line50.StartPoint, line50.EndPoint) = (new Point(0, Y(50)), new Point(w, Y(50)));
        label50.Margin = new Thickness(0, Y(50) - 16, 2, 0);

        static void Band(GeometryGroup g, double left, double right, double h, double w)
        {
            (left, right) = (Math.Max(0, left), Math.Min(w, right));
            if (right > left) g.Children.Add(new RectangleGeometry(new Rect(left, 0, right - left, h)));
        }
        var loading = new GeometryGroup();
        Band(loading, X(0), X(f.StartupMs), h, w);
        var (shader, other, compiling) = (new GeometryGroup(), new GeometryGroup(), new GeometryGroup());
        tips.Children.Clear();
        foreach (var x in f.Hitches)
        {
            double at = x.At.TotalMilliseconds;
            if (at + x.Ms < from || at > to) continue;
            if (x.Cause is HitchCause.Loading or HitchCause.Quitting or HitchCause.LoadingShaders)
            {
                Band(x.Cause == HitchCause.LoadingShaders ? compiling : loading, X(at), X(at) + Math.Max(2, x.Ms / span * w), h, w);
                continue;
            }
            var p = new Point(X(at), Y(x.Ms));
            (x.Cause == HitchCause.Shader && !f.Neutral ? shader : other).Children.Add(new EllipseGeometry { Center = p, RadiusX = 3.5, RadiusY = 3.5 });
            var tip = new Rectangle { Width = 14, Height = 14, Fill = Brushes.Transparent };
            ToolTip.SetTip(tip, DetailVm.HitchTip(x, f.Neutral));
            Canvas.SetLeft(tip, p.X - 7);
            Canvas.SetTop(tip, p.Y - 7);
            tips.Children.Add(tip);
        }
        (loadingBand.Data, compilingBand.Data, shaderDots.Data, otherDots.Data) = (loading, compiling, shader, other);
    }
}
