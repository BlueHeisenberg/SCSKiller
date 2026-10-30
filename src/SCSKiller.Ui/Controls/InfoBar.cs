using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Controls;

/// <summary>WinUI's InfoBar: a severity icon, a title, a message, an optional action button and close button, on the
/// severity's background. The title, message and action go on one line when they fit, else under each other.</summary>
public sealed class InfoBar : UserControl
{
    public static readonly StyledProperty<InfoBarSeverity> SeverityProperty = AvaloniaProperty.Register<InfoBar, InfoBarSeverity>(nameof(Severity));
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<InfoBar, string?>(nameof(Title));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<InfoBar, string?>(nameof(Message));
    public static readonly StyledProperty<Control?> ActionButtonProperty = AvaloniaProperty.Register<InfoBar, Control?>(nameof(ActionButton));
    public static readonly StyledProperty<bool> IsClosableProperty = AvaloniaProperty.Register<InfoBar, bool>(nameof(IsClosable), true);
    public static readonly RoutedEvent<RoutedEventArgs> CloseButtonClickEvent = RoutedEvent.Register<InfoBar, RoutedEventArgs>(nameof(CloseButtonClick), RoutingStrategies.Bubble);

    public InfoBarSeverity Severity { get => GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public Control? ActionButton { get => GetValue(ActionButtonProperty); set => SetValue(ActionButtonProperty, value); }
    public bool IsClosable { get => GetValue(IsClosableProperty); set => SetValue(IsClosableProperty, value); }
    public event EventHandler<RoutedEventArgs>? CloseButtonClick { add => AddHandler(CloseButtonClickEvent, value); remove => RemoveHandler(CloseButtonClickEvent, value); }

    readonly Border bar = new() { CornerRadius = new(4), BorderThickness = new(1) };
    readonly Ellipse dot = new() { Width = 16, Height = 16 };
    readonly TextBlock mark = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    readonly ContentControl action = new();
    readonly Button close = new() { Classes = { "Subtle" }, Width = 38, Height = 38, Margin = new(0, 5, 5, 5), VerticalAlignment = VerticalAlignment.Top,
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        Content = new TextBlock { Classes = { "Icon" }, Text = Glyphs.Dismiss } };

    public InfoBar()
    {
        AutomationProperties.SetName(close, "Close");
        ToolTip.SetTip(close, "Close");
        close.Click += (_, e) => { e.Handled = true; RaiseEvent(new RoutedEventArgs(CloseButtonClickEvent)); };
        var icon = new Panel { Margin = new(0, 16, 14, 16), VerticalAlignment = VerticalAlignment.Top, Children = { dot, mark } };
        var panel = new InfoBarPanel { Margin = new(0, 0, 16, 0), Children = { title, message, action } };
        InfoBarPanel.SetMargins(title, new(0, 14, 0, 0), new(0, 14, 0, 0));
        InfoBarPanel.SetMargins(message, new(12, 14, 0, 0), new(0, 4, 0, 0));
        InfoBarPanel.SetMargins(action, new(16, 8, 0, 0), new(0, 12, 0, 0));
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), MinHeight = 48, Margin = new(16, 0, 0, 0), Children = { icon, panel, close } };
        Grid.SetColumn(panel, 1);
        Grid.SetColumn(close, 2);
        bar.Child = grid;
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("CardStrokeColorDefaultBrush"));
        title.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty));
        message.Bind(TextBlock.TextProperty, this.GetObservable(MessageProperty));
        action.Bind(ContentProperty, this.GetObservable(ActionButtonProperty));
        close.Bind(IsVisibleProperty, this.GetObservable(IsClosableProperty));
        Content = bar;
        Shown();
        Look();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeverityProperty) Look();
        else if (change.Property == TitleProperty || change.Property == MessageProperty || change.Property == ActionButtonProperty) Shown();
    }

    void Shown()
    {
        title.IsVisible = !string.IsNullOrEmpty(Title);
        message.IsVisible = !string.IsNullOrEmpty(Message);
        action.IsVisible = ActionButton != null;
        if (ActionButton is { } b) action.HorizontalContentAlignment = b.HorizontalAlignment;
    }

    void Look()
    {
        var (fill, back, text, size) = Severity switch
        {
            InfoBarSeverity.Error => ("SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", Glyphs.Dismiss, 10.0),
            InfoBarSeverity.Warning => ("SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush", "!", 12.0),
            InfoBarSeverity.Success => ("SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush", "✓", 11.0),
            _ => ("AccentFillColorDefaultBrush", "SystemFillColorAttentionBackgroundBrush", "i", 12.0),
        };
        dot.Bind(Shape.FillProperty, dot.GetResourceObservable(fill));
        bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable(back));
        mark.Bind(TextBlock.ForegroundProperty, mark.GetResourceObservable("TextOnAccentFillColorPrimaryBrush"));
        (mark.Text, mark.FontSize) = (text, size);
        mark.FontFamily = Severity == InfoBarSeverity.Error ? (FontFamily)Application.Current!.FindResource("IconFont")! : FontFamily.Default;
        mark.FontWeight = Severity == InfoBarSeverity.Error ? FontWeight.Normal : FontWeight.Bold;
    }
}

/// <summary>WinUI's InfoBarPanel: its children side by side when they fit (the last one gets the rest of the line), else
/// under each other; a lone child goes under. Each child has a margin for either way, the outer edges' left out.</summary>
sealed class InfoBarPanel : Panel
{
    static readonly Thickness HorizontalPadding = new(0), VerticalPadding = new(0, 14, 0, 18);
    static readonly AttachedProperty<(Thickness Horizontal, Thickness Vertical)> MarginsProperty =
        AvaloniaProperty.RegisterAttached<InfoBarPanel, Control, (Thickness, Thickness)>("Margins");

    public static void SetMargins(Control c, Thickness horizontal, Thickness vertical) => c.SetValue(MarginsProperty, (horizontal, vertical));

    bool vertical;

    IEnumerable<Control> Shown => Children.Where(c => c.DesiredSize is { Width: > 0, Height: > 0 });

    protected override Size MeasureOverride(Size available)
    {
        foreach (var c in Children) c.Measure(available);
        var shown = Shown.ToList();
        double totalWidth = 0, totalHeight = 0, widest = 0, tallest = 0, tallestInLine = 0;
        for (int i = 0; i < shown.Count; i++)
        {
            var (h, v) = shown[i].GetValue(MarginsProperty);
            var d = shown[i].DesiredSize;
            totalWidth += d.Width + (i > 0 ? h.Left : 0) + (i < shown.Count - 1 ? h.Right : 0);
            totalHeight += d.Height + (i > 0 ? v.Top : 0) + (i < shown.Count - 1 ? v.Bottom : 0);
            (widest, tallest, tallestInLine) = (Math.Max(widest, d.Width), Math.Max(tallest, d.Height), Math.Max(tallestInLine, d.Height + h.Top + h.Bottom));
        }
        double minHeight = Parent is Control p ? p.MinHeight - Margin.Top - Margin.Bottom : 0;
        vertical = shown.Count == 1 || totalWidth > available.Width || minHeight > 0 && tallestInLine > minHeight;
        return vertical
            ? new(widest + VerticalPadding.Left + VerticalPadding.Right, totalHeight + VerticalPadding.Top + VerticalPadding.Bottom)
            : new(totalWidth + HorizontalPadding.Left + HorizontalPadding.Right, tallestInLine + HorizontalPadding.Top + HorizontalPadding.Bottom);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var shown = Shown.ToList();
        double offset = vertical ? VerticalPadding.Top : HorizontalPadding.Left;
        for (int i = 0; i < shown.Count; i++)
        {
            var (h, v) = shown[i].GetValue(MarginsProperty);
            var d = shown[i].DesiredSize;
            if (vertical)
            {
                offset += i > 0 ? v.Top : 0;
                shown[i].Arrange(new Rect(VerticalPadding.Left + v.Left, offset, d.Width, d.Height));
                offset += d.Height + v.Bottom;
            }
            else
            {
                offset += i > 0 ? h.Left : 0;
                double width = i < shown.Count - 1 ? d.Width : Math.Max(d.Width, final.Width - offset);
                shown[i].Arrange(new Rect(offset, HorizontalPadding.Top + h.Top, width, d.Height));
                offset += d.Width + h.Right;
            }
        }
        return final;
    }
}
