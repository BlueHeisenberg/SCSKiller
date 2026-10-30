using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui;

/// <summary>The headings and bullets of a version's CHANGELOG section, with their bold, code and links.</summary>
public static class WhatsNewDialog
{
    static readonly FontFamily Code = new("Consolas, DejaVu Sans Mono");

    public static ContentDialog Build(WhatsNewSection section)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var (heading, line, group) in AboutVm.WhatsNewLines(section))
        {
            var text = new LinkText { TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection(), Margin = heading && panel.Children.Count > 0 ? new Thickness(0, group ? 28 : 16, 0, 0) : default };
            if (group) (text.FontSize, text.TextDecorations) = (16, TextDecorations.Underline);
            var links = new Dictionary<Run, Uri>();
            foreach (var s in line)
            {
                var run = new Run(s.Text);
                switch (s.Kind)
                {
                    case SpanKind.Bold: run.FontWeight = FontWeight.Bold; break;
                    case SpanKind.Code: run.FontFamily = Code; break;
                    case SpanKind.Link:
                        run.Classes.Add("Link");
                        run.TextDecorations = TextDecorations.Underline;
                        run.Bind(TextElement.ForegroundProperty, text.GetResourceObservable("AccentTextFillColorPrimaryBrush"));
                        links[run] = new Uri(s.Url!);
                        break;
                }
                text.Inlines!.Add(run);
            }
            text.LinkClick += (_, run) => _ = TopLevel.GetTopLevel(text)?.Launcher.LaunchUriAsync(links[run]);
            panel.Children.Add(text);
        }
        // as tall as the window leaves room for: the release notes run long
        return new ContentDialog { Title = $"What's new in {section.Version}", Content = panel, CloseButtonText = "Close", MaxWidth = 880, ContentMaxHeight = double.PositiveInfinity, Margin = new(0, 32) };
    }
}
