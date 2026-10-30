using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

/// <summary>A game's before and after report (doc 6.1), one window per game. Closing it never quits the app.</summary>
public sealed partial class ReportWindow : Window
{
    const int ImageWidth = 1200, ImageHeight = 675, GraphWidth = 1104;   // the card's width less its padding
    static readonly Dictionary<string, ReportWindow> open = [];

    readonly string id = "", game = "";
    readonly BeforeAfterResult r = null!;

    // the XAML loader's
    public ReportWindow() => InitializeComponent();

    ReportWindow(string id, string game, BeforeAfterResult r) : this()
    {
        (this.id, this.game, this.r) = (id, game, r);
        Title = $"Before and after · {game}";
        RequestedThemeVariant = App.Main.RequestedThemeVariant;
        App.Main.PropertyChanged += FollowTheme;
        Host.Icons.Changed += ShowIcon;
        Opened += (_, _) => Fit();
        Closed += (_, _) =>
        {
            if (open.GetValueOrDefault(id) == this) open.Remove(id);
            DialogLayer.HideOpen();   // its pending Start over or Record again ends as cancelled
            Host.Icons.Changed -= ShowIcon;
            App.Main.PropertyChanged -= FollowTheme;
        };
        Fill();
    }

    /// <summary>Opens the game's report, or brings its window to the front; null when there's no report (not Done). A
    /// window still showing an earlier pair is replaced.</summary>
    public static async Task<ReportWindow?> OpenAsync(string id)
    {
        BeforeAfterResult? r;
        try { r = await Task.Run(() => Host.Core.BeforeAfterReport(id)); }
        catch (Exception) { r = null; }
        var g = Host.Core.Games.FirstOrDefault(x => x.Game.Id == id);
        if (r == null || g == null) return null;
        if (open.TryGetValue(id, out var w))
        {
            if (w.r.Before.LaunchUnixMs == r.Before.LaunchUnixMs && w.r.After.LaunchUnixMs == r.After.LaunchUnixMs)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                return w;
            }
            w.Close();
        }
        open[id] = w = new ReportWindow(id, g.Game.Name, r);
        w.Show();
        w.Activate();
        return w;
    }

    public static void CloseAll()
    {
        foreach (var w in open.Values.ToList()) w.Close();
    }

    Comparison C => r.Compare;

    void FollowTheme(object? _, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RequestedThemeVariantProperty) RequestedThemeVariant = App.Main.RequestedThemeVariant;
    }

    /// <summary>Tall enough to show the whole report without scrolling, within the work area of the window's monitor.</summary>
    void Fit()
    {
        Body.Measure(new Size(ClientSize.Width, double.PositiveInfinity));
        double height = Body.DesiredSize.Height;
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            var area = screen.WorkingArea;
            double chrome = FrameSize is { } frame ? frame.Height - ClientSize.Height : 0;
            height = Math.Min(height, area.Height / screen.Scaling - chrome);
            int outer = (int)Math.Ceiling((height + chrome) * screen.Scaling);
            if (Position.Y + outer > area.Bottom) Position = new PixelPoint(Position.X, Math.Max(area.Y, area.Bottom - outer));
        }
        Height = height;
    }

    void Fill()
    {
        Heading.Text = $"Before and after · {game}";
        CardGame.Text = game.ToUpperInvariant();
        ShowIcon();
        var text = BeforeAfter.Report(game, r, Host.Core.Vendor.Vendor);
        CardSub.Text = text.Subtitle;
        (LabelStutters.Text, TileStutters.Text) = text.Tiles[0];
        (LabelReady.Text, TileReady.Text) = text.Tiles[1];
        (LabelWorst.Text, TileWorst.Text) = text.Tiles[2];

        var (before, after) = (FrameData.Of(r.Before) with { Neutral = C.Dx11 }, FrameData.Of(r.After) with { Neutral = C.Dx11 });
        if (C.Dx11) (LegendShader.IsVisible, LegendOther.Text) = (false, "stutter");
        double top = FrameGraph.TopFor(r.Before.Hitches.Concat(r.After.Hitches));
        double spp = Math.Max(Span(before), Span(after)) / 1000 / GraphWidth;
        Graph(BeforeGraph, BeforeAxis, BeforeEnd, before, C.Before);
        Graph(AfterGraph, AfterAxis, AfterEnd, after, C.After);
        void Graph(FrameGraph graph, Grid axis, TextBlock end, FrameData d, RunStats run)
        {
            graph.Show(d, d.Play.From, d.Play.To, top, spp);
            axis.Width = graph.Width;
            end.Text = BeforeAfter.Clock(run.PlaySeconds) + " of play";
        }

        ShowGpu.IsChecked = Host.Core.Settings.BeforeAfterShowGpu;
        if (BeforeAfterUi.Voided(Host.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.BeforeAfter)) RecordAgain.IsVisible = false;
        Footer();

        Details.Children.Clear();
        Details.RowDefinitions.Clear();
        Row(new("Details", "Before", "After"), header: true);
        foreach (var row in text.Rows) Row(row);

        Notes.Children.Clear();
        foreach (var n in text.Notes) Note(n);
    }

    /// <summary>The game's icon as the library shows it, or its tile.</summary>
    void ShowIcon()
    {
        CardIcon.Source = Host.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.Game.ExePath is { } exe ? Host.Icons.Get(exe) as IImage : null;
        (CardTile.Background, CardInitials.Text) = (new SolidColorBrush(GameRow.TileOf(game)), GameRow.InitialsOf(game));
        CardTile.IsVisible = CardIcon.Source == null;
    }

    static double Span(FrameData d) => d.Play.To - d.Play.From;

    void Footer()
    {
        CardFooter.Text = BeforeAfter.Footer(r, Host.Core.Settings.BeforeAfterShowGpu ? Host.Core.Vendor.Gpu.Name : null);
        CardVersions.Text = BeforeAfter.Versions(Host.Core.Settings.BeforeAfterShowGpu ? Host.Core.Vendor.Gpu.DriverVersion : null);
    }

    void OnShowGpu(object? _, RoutedEventArgs __)
    {
        Host.Core.Settings = Host.Core.Settings with { BeforeAfterShowGpu = ShowGpu.IsChecked == true };
        Footer();
    }

    void Row(ReportRow row, bool header = false)
    {
        var (label, before, after, note) = row;
        int at = Details.RowDefinitions.Count;
        Details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        TextBlock Cell(string text)
        {
            var cell = new TextBlock { Text = text };
            if (header) cell.Classes.Add("Section");
            return cell;
        }
        var cells = new List<TextBlock> { Cell(label), Cell(before), Cell(after) };
        cells[0].TextWrapping = TextWrapping.Wrap;
        cells[2].FontWeight = FontWeight.SemiBold;
        if (note != null) cells.Add(new() { Text = note, Classes = { "Caption" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        for (int i = 0; i < cells.Count; i++)
        {
            Grid.SetRow(cells[i], at);
            Grid.SetColumn(cells[i], i);
            Details.Children.Add(cells[i]);
        }
    }

    void Note(string text) => Notes.Children.Add(new TextBlock
    {
        TextWrapping = TextWrapping.Wrap, Classes = { "Secondary" },
        Inlines = [new Run(Glyphs.Info + "  ") { FontFamily = (FontFamily)Application.Current!.FindResource("IconFont")! }, new Run(text)],
    });

    /// <summary>The card as a 1200 × 675 image, whatever the display scale (doc 6.3): rendered at 96 DPI, at the card's own
    /// size, not the size the window shows it at.</summary>
    public RenderTargetBitmap CardImage()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(ImageWidth, ImageHeight), new Vector(96, 96));
        bitmap.Render(Card);
        return bitmap;
    }

    void Tell(string message, InfoBarSeverity severity = InfoBarSeverity.Success) => (Status.Message, Status.Severity, Status.IsVisible) = (message, severity, true);

    void OnStatusClose(object? _, RoutedEventArgs __) => Status.IsVisible = false;

    async void OnSave(object? _, RoutedEventArgs __)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                SuggestedFileName = BeforeAfter.ImageName(game), DefaultExtension = "png",
                SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures),
                FileTypeChoices = [new("PNG image") { Patterns = ["*.png"] }],
            });
            if (file == null) return;
            using (var image = CardImage())
            await using (var target = await file.OpenWriteAsync())
            {
                target.SetLength(0);
                image.Save(target, PngBitmapEncoderOptions.Default);
            }
            Tell($"Saved {file.Name}");
        }
        catch (Exception e) { Tell("Couldn't save the image: " + BeforeAfterUi.Describe(e), InfoBarSeverity.Error); }
    }

    /// <summary>Text from Core (BeforeAfter.ShareText); null with the error shown.</summary>
    string? Text()
    {
        try { return BeforeAfter.ShareText(game, C); }
        catch (Exception e) { Tell("Couldn't make the share text: " + BeforeAfterUi.Describe(e), InfoBarSeverity.Error); return null; }
    }

    /// <summary>Kept on the clipboard after the app quits (on Windows: X11 drops it unless a clipboard manager takes it).</summary>
    async Task<bool> CopyAsync(bool image, string? text)
    {
        try
        {
            var bitmap = image ? CardImage() : null;
            bool native;
            try { native = NativeClipboard.TrySet(this, bitmap, text); }
            catch
            {
                bitmap?.Dispose();
                throw;
            }
            if (native)
            {
                bitmap?.Dispose();
                return true;
            }
            // Avalonia's clipboard reads the bitmap when an app pastes: it stays undisposed
            var clipboard = Clipboard ?? throw new InvalidOperationException("No clipboard");
            var item = new DataTransferItem();
            if (bitmap != null) item.SetBitmap(bitmap);
            if (text != null) item.Set(DataFormat.Text, text);
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
            await clipboard.FlushAsync();
            return true;
        }
        catch (Exception e) { Tell("Couldn't copy: " + BeforeAfterUi.Describe(e), InfoBarSeverity.Error); return false; }
    }

    async void OnCopy(object? _, RoutedEventArgs __)
    {
        if (await CopyAsync(image: true, Text())) Tell("Image copied");
    }

    async void OnDiscord(object? _, RoutedEventArgs __)
    {
        if (await CopyAsync(image: true, Text())) Tell("Image and text copied: paste them in Discord");
    }

    async void OnCopyText(object? _, RoutedEventArgs __)
    {
        if (Text() is { } text && await CopyAsync(image: false, text)) Tell("Text copied");
    }

    // neither composer takes an image from a link: it goes to the clipboard, and a toast over the browser says to paste it
    void OnX(object? _, RoutedEventArgs __) => Post(BeforeAfter.PostOnX);
    void OnReddit(object? _, RoutedEventArgs __) => Post(BeforeAfter.PostOnReddit);

    async void Post(Func<string, string> url)
    {
        if (Text() is not { } text || !await CopyAsync(image: true, null)) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(url(text)))) throw new InvalidOperationException("No browser");
        }
        catch (Exception e) { Tell("Couldn't open the browser: " + BeforeAfterUi.Describe(e), InfoBarSeverity.Error); return; }
        Tell("Paste (Ctrl+V) to add the image", InfoBarSeverity.Informational);
        App.Hint?.Invoke("The image is copied", "Press Ctrl+V in the post to add it.");
    }

    async void OnRecordAgain(object? _, RoutedEventArgs __)
    {
        if (await BeforeAfterDialogs.RecordAfterAgainAsync(id, DialogLayer) is { } error) Tell(error, InfoBarSeverity.Error);
        else if (Host.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.BeforeAfter?.Stage != BeforeAfterStage.Done) Close();
    }

    async void OnStartOver(object? _, RoutedEventArgs __)
    {
        if (await BeforeAfterDialogs.StartAsync(id, over: true, DialogLayer) is { } error) Tell(error, InfoBarSeverity.Error);
        else if (Host.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.BeforeAfter?.Stage != BeforeAfterStage.Done) Close();
    }

    // screenshots
    public void ScrollToTop() => Scroller.Offset = default;
    public void ScrollToEnd() => Scroller.ScrollToEnd();
}
