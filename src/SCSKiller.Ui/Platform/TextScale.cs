using Avalonia;

namespace SCSKiller.Ui.Platform;

/// <summary>Settings > Accessibility > Text size, applied as WinUI applies it: every text size times the factor, icons
/// unchanged. Text sizes are the app resources named TextSize.&lt;size&gt; and the Fluent theme's ControlContentThemeFontSize
/// (what text inherits) and tooltip sizes. TextFit.&lt;length&gt; is a box sized to its text.</summary>
static partial class TextScale
{
    static Dictionary<string, double>? sizes;

    static void Apply(double factor)
    {
        var resources = Application.Current!.Resources;
        sizes ??= resources.Keys.OfType<string>()
            .Where(k => k.StartsWith("TextSize.") || k.StartsWith("TextFit.")
                || k is "ControlContentThemeFontSize" or "ToolTipContentThemeFontSize" or "ToolTipContentMaxWidth")
            .ToDictionary(k => k, k => (double)resources[k]!);
        foreach (var (key, size) in sizes) resources[key] = size * factor;
    }
}
