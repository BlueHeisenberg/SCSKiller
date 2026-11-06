using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SCSKiller.Ui.Controls;

public enum ContentDialogResult { None, Primary, Secondary }
public enum ContentDialogButton { None, Primary, Secondary, Close }

/// <summary>WinUI's ContentDialog: a title, content and up to three buttons over the window. <see cref="ShowAsync"/>
/// shows it in a window's <see cref="DialogLayer"/>, the main window's by default.</summary>
public sealed class ContentDialog : Border
{
    string? title, primaryText, secondaryText, closeText;
    object? content;
    ContentDialogButton defaultButton;
    public string? Title { get => title; set { title = value; Build(); } }
    /// <summary>A string (wrapped text) or a control.</summary>
    public object? Content { get => content; set { content = value; Build(); } }
    public string? PrimaryButtonText { get => primaryText; set { primaryText = value; Build(); } }
    public string? SecondaryButtonText { get => secondaryText; set { secondaryText = value; Build(); } }
    public string? CloseButtonText { get => closeText; set { closeText = value; Build(); } }
    public ContentDialogButton DefaultButton { get => defaultButton; set { defaultButton = value; Build(); } }
    /// <summary>Runs on the primary button instead of closing; true closes the dialog, false keeps it open (an answer to show).</summary>
    public Func<Task<bool>>? PrimaryButtonClick { get; set; }
    public double ContentMaxHeight { get; set; } = 560;
    /// <summary>An X in the corner, which answers as Esc does.</summary>
    public bool ShowsX { get; set; }
    /// <summary>A row per button: texts too long for equal columns.</summary>
    public bool StackedButtons { get; set; }

    bool primaryEnabled = true;
    public bool IsPrimaryButtonEnabled { get => primaryEnabled; set { primaryEnabled = value; if (primary != null) primary.IsEnabled = value; } }

    Button? primary, preferred;
    Control? body;
    StackPanel? column;
    ScrollViewer? scroller;
    TaskCompletionSource<ContentDialogResult>? done;
    DialogLayer? layer;

    public ContentDialog()
    {
        (CornerRadius, BorderThickness, MinWidth) = (new(8), new(1), 320);
        (HorizontalAlignment, VerticalAlignment, ClipToBounds) = (HorizontalAlignment.Center, VerticalAlignment.Center, true);
        BoxShadow = BoxShadows.Parse("0 8 32 0 #47000000");
        Res(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        Res(BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
    }

    void Res(AvaloniaProperty p, string key, StyledElement? on = null) => (on ?? this).Bind(p, (on ?? this).GetResourceObservable(key));

    public Task<ContentDialogResult> ShowAsync(DialogLayer? on = null) => (on ?? App.Main.Dialogs).ShowAsync(this);

    public void Hide() => Close(ContentDialogResult.None);

    internal Task<ContentDialogResult> Open(DialogLayer on)
    {
        (done, layer) = (new(), on);
        Build();
        KeyDown += OnKey;
        return done.Task;
    }

    // the layout, again whenever a part changes while open
    void Build()
    {
        if (done == null) return;
        (primary, preferred, body) = (null, null, null);
        column?.Children.Clear();   // frees the content for the new column
        column = new StackPanel();
        if (Title != null)
        {
            var title = new TextBlock { Text = Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
            Res(TextBlock.FontSizeProperty, "TextSize.20", title);
            column.Children.Add(title);
        }
        if (Content is Control c) column.Children.Add(body = c);
        else if (Content != null) column.Children.Add(new TextBlock { Text = Content.ToString(), TextWrapping = TextWrapping.Wrap });
        var top = new Border { Padding = new(24), Child = scroller = new ScrollViewer { Content = column, MaxHeight = ContentMaxHeight } };
        Res(BackgroundProperty, "LayerFillColorAltBrush", top);

        // as WinUI lays them out: equal columns, a lone button in the right half
        var buttons = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        int count = new[] { PrimaryButtonText, SecondaryButtonText, CloseButtonText }.Count(b => !string.IsNullOrEmpty(b));
        if (count == 1) buttons.ColumnDefinitions.Add(new(GridLength.Star));
        void Add(string? text, ContentDialogButton kind, ContentDialogResult result)
        {
            if (string.IsNullOrEmpty(text)) return;
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (kind == DefaultButton) (preferred = b).Classes.Add("accent");
            b.Click += async (_, _) =>
            {
                if (result == ContentDialogResult.Primary && PrimaryButtonClick is { } run && !await run()) return;
                Close(result);
            };
            if (kind == ContentDialogButton.Primary) (primary, b.IsEnabled) = (b, primaryEnabled);
            if (StackedButtons)
            {
                Grid.SetRow(b, buttons.RowDefinitions.Count);
                buttons.RowDefinitions.Add(new(GridLength.Auto));
            }
            else
            {
                Grid.SetColumn(b, buttons.ColumnDefinitions.Count);
                buttons.ColumnDefinitions.Add(new(GridLength.Star));
            }
            buttons.Children.Add(b);
        }
        Add(PrimaryButtonText, ContentDialogButton.Primary, ContentDialogResult.Primary);
        Add(SecondaryButtonText, ContentDialogButton.Secondary, ContentDialogResult.Secondary);
        Add(CloseButtonText, ContentDialogButton.Close, ContentDialogResult.None);
        var bottom = new Border { Padding = new(24), BorderThickness = new(0, 1, 0, 0), Child = buttons };
        Res(BorderBrushProperty, "CardStrokeColorDefaultBrush", bottom);

        var grid = new Grid { RowDefinitions = new("*,Auto"), Children = { top, bottom } };
        Grid.SetRow(bottom, 1);
        if (ShowsX)
        {
            var x = new Button
            {
                Classes = { "Subtle" }, Width = 38, Height = 38, Padding = new(0), Margin = new(0, 5, 5, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = new TextBlock { Classes = { "Icon" }, Text = Glyphs.Dismiss },
            };
            AutomationProperties.SetName(x, "Close");
            x.Click += (_, _) => Close(ContentDialogResult.None);
            grid.Children.Add(x);
        }
        Child = grid;
    }

    /// <summary>As WinUI opens one: the content's first focusable control, else the default button.</summary>
    internal void FocusFirst()
    {
        var first = body?.GetSelfAndVisualDescendants().OfType<InputElement>().FirstOrDefault(e => e.Focusable && e.IsEffectivelyEnabled && e.IsEffectivelyVisible);
        (first ?? preferred ?? (InputElement)this).Focus();
        // focus scrolls a long content to that control: open at the title, as when it all fits
        Dispatcher.UIThread.Post(() => scroller?.ScrollToHome(), DispatcherPriority.Loaded);
    }

    void OnKey(object? _, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(ContentDialogResult.None); e.Handled = true; }
        else if (e.Key == Key.Enter && DefaultButton == ContentDialogButton.Primary && primaryEnabled && primary != null)
        {
            primary.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
    }

    void Close(ContentDialogResult result)
    {
        if (done == null) return;
        KeyDown -= OnKey;
        var d = done;
        done = null;
        layer?.Close(this);
        d.TrySetResult(result);
    }
}
