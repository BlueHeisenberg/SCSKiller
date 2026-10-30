using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SCSKiller.Core;
using SCSKiller.Core.App;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace SCSKiller.App;

/// <summary>A game's before and after report (doc 6.1), one window per game. Only the main window's close handler can
/// quit or hide the app: closing this one just closes it.</summary>
public sealed partial class ReportWindow : Window
{
    const int ImageWidth = 1200, ImageHeight = 675, GraphWidth = 1104;   // the card's width less its padding
    static readonly Dictionary<string, ReportWindow> open = [];

    readonly string id, game;
    readonly BeforeAfterResult r;

    ReportWindow(string id, string game, BeforeAfterResult r)
    {
        (this.id, this.game, this.r) = (id, game, r);
        InitializeComponent();
        Title = $"Before and after · {game}";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(940 * scale)));
        Root.Loaded += (_, _) => Fit();
        Icons.Failed += ShowIcon;
        Closed += (_, _) => { if (open.GetValueOrDefault(id) == this) open.Remove(id); Icons.Failed -= ShowIcon; };
        Fill();
    }

    /// <summary>Opens the game's report, or brings its window to the front; null when there's no report (not Done). A
    /// window still showing an earlier pair is replaced. <paramref name="offscreen"/>: screenshots, never activated.</summary>
    public static async Task<ReportWindow?> OpenAsync(string id, bool offscreen = false)
    {
        BeforeAfterResult? r;
        try { r = await Task.Run(() => App.Core.BeforeAfterReport(id)); }
        catch (Exception) { r = null; }
        var g = App.Core.Games.FirstOrDefault(x => x.Game.Id == id);
        if (r == null || g == null) return null;
        if (open.TryGetValue(id, out var w))
        {
            if (w.r.Before.LaunchUnixMs == r.Before.LaunchUnixMs && w.r.After.LaunchUnixMs == r.After.LaunchUnixMs) { w.Activate(); return w; }
            w.Close();
        }
        open[id] = w = new ReportWindow(id, g.Game.Name, r);
        if (offscreen)
        {
            w.AppWindow.IsShownInSwitchers = false;
            w.AppWindow.Move(new PointInt32(-20000, -20000));
            w.AppWindow.Show(false);
            w.ShotBackground.Visibility = Visibility.Visible;
        }
        else w.Activate();
        return w;
    }

    public static void CloseAll()
    {
        foreach (var w in open.Values.ToList()) w.Close();
    }

    Comparison C => r.Compare;

    /// <summary>Tall enough to show the whole report without scrolling, within the work area of the window's monitor.</summary>
    void Fit()
    {
        double scale = Root.XamlRoot.RasterizationScale;
        var content = (FrameworkElement)Scroller.Content;
        content.Measure(new Windows.Foundation.Size(AppWindow.ClientSize.Width / scale, double.PositiveInfinity));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int chrome = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        int height = Math.Min(area.Height, (int)Math.Ceiling(content.DesiredSize.Height * scale) + chrome);
        AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, height));
        if (AppWindow.Position.Y + height > area.Y + area.Height)
            AppWindow.Move(new PointInt32(AppWindow.Position.X, area.Y + area.Height - height));
    }

    void Fill()
    {
        Heading.Text = $"Before and after · {game}";
        CardGame.Text = game.ToUpperInvariant();
        ShowIcon();
        var text = BeforeAfter.Report(game, r, App.Core.Vendor.Vendor);
        CardSub.Text = text.Subtitle;
        (LabelStutters.Text, TileStutters.Text) = text.Tiles[0];
        (LabelReady.Text, TileReady.Text) = text.Tiles[1];
        (LabelWorst.Text, TileWorst.Text) = text.Tiles[2];

        var (before, after) = (FrameData.Of(r.Before) with { Neutral = C.Dx11 }, FrameData.Of(r.After) with { Neutral = C.Dx11 });
        if (C.Dx11) (LegendShader.Visibility, LegendOther.Text) = (Visibility.Collapsed, "stutter");
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

        ShowGpu.IsChecked = App.Core.Settings.BeforeAfterShowGpu;
        if (BeforeAfterUi.Voided(App.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.BeforeAfter)) RecordAgain.Visibility = Visibility.Collapsed;
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
        CardIcon.Source = App.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.Game.ExePath is { } exe ? Icons.Get(exe) : null;
        (CardTile.Background, CardInitials.Text) = (GameRow.TileOf(game), GameRow.InitialsOf(game));
        CardTile.Visibility = CardIcon.Source == null ? Visibility.Visible : Visibility.Collapsed;
    }

    static double Span(FrameData d) => d.Play.To - d.Play.From;

    void Footer()
    {
        CardFooter.Text = BeforeAfter.Footer(r, App.Core.Settings.BeforeAfterShowGpu ? App.Core.Vendor.Gpu.Name : null);
        CardVersions.Text = BeforeAfter.Versions(App.Core.Settings.BeforeAfterShowGpu ? App.Core.Vendor.Gpu.DriverVersion : null);
    }

    void OnShowGpu(object _, RoutedEventArgs __)
    {
        App.Core.Settings = App.Core.Settings with { BeforeAfterShowGpu = ShowGpu.IsChecked == true };
        Footer();
    }

    void Row(ReportRow row, bool header = false)
    {
        var (label, before, after, note) = row;
        int at = Details.RowDefinitions.Count;
        Details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var style = (Style)Application.Current.Resources[header ? "Section" : "BodyTextBlockStyle"];
        var cells = new List<TextBlock>
        {
            new() { Text = label, Style = style, TextWrapping = TextWrapping.Wrap },
            new() { Text = before, Style = style },
            new() { Text = after, Style = style, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
        };
        if (note != null) cells.Add(new() { Text = note, Style = (Style)Application.Current.Resources["Caption"], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        for (int i = 0; i < cells.Count; i++)
        {
            Grid.SetRow(cells[i], at);
            Grid.SetColumn(cells[i], i);
            Details.Children.Add(cells[i]);
        }
    }

    void Note(string text) => Notes.Children.Add(new TextBlock
    {
        TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"],
        Inlines =
        {
            new Microsoft.UI.Xaml.Documents.Run { Text = "  ", FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"] },
            new Microsoft.UI.Xaml.Documents.Run { Text = text },
        },
    });

    /// <summary>The card as a 1200 × 675 PNG, whatever the display scale (doc 6.3).</summary>
    public async Task<InMemoryRandomAccessStream> PngAsync()
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(Card, ImageWidth, ImageHeight);
        var pixels = await rtb.GetPixelsAsync();
        var png = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
        png.Seek(0);
        return png;
    }

    void Tell(string message, InfoBarSeverity severity = InfoBarSeverity.Success) => (Status.Message, Status.Severity, Status.IsOpen) = (message, severity, true);

    async void OnSave(object _, RoutedEventArgs __)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = BeforeAfter.ImageName(game) };
            picker.FileTypeChoices.Add("PNG image", [".png"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSaveFileAsync() is not { } file) return;
            using var png = await PngAsync();
            using var target = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            target.Size = 0;
            await RandomAccessStream.CopyAsync(png, target);
            await target.FlushAsync();
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

    /// <summary>Flushed: the clipboard keeps it after the app quits.</summary>
    async Task<bool> CopyAsync(bool image, string? text)
    {
        try
        {
            var data = new DataPackage();
            if (image) data.SetBitmap(RandomAccessStreamReference.CreateFromStream(await PngAsync()));
            if (text != null) data.SetText(text);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            return true;
        }
        catch (Exception e) { Tell("Couldn't copy: " + BeforeAfterUi.Describe(e), InfoBarSeverity.Error); return false; }
    }

    async void OnCopy(object _, RoutedEventArgs __)
    {
        if (await CopyAsync(image: true, Text())) Tell("Image copied");
    }

    async void OnDiscord(object _, RoutedEventArgs __)
    {
        if (await CopyAsync(image: true, Text())) Tell("Image and text copied: paste them in Discord");
    }

    async void OnCopyText(object _, RoutedEventArgs __)
    {
        if (Text() is { } text && await CopyAsync(image: false, text)) Tell("Text copied");
    }

    // neither composer takes an image from a link: it goes to the clipboard, and a toast over the browser says to paste it
    void OnX(object _, RoutedEventArgs __) => Post(BeforeAfter.PostOnX);
    void OnReddit(object _, RoutedEventArgs __) => Post(BeforeAfter.PostOnReddit);

    async void Post(Func<string, string> url)
    {
        if (Text() is not { } text || !await CopyAsync(image: true, null)) return;
        try { Process.Start(new ProcessStartInfo(url(text)) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) { Tell("Couldn't open the browser: " + BeforeAfterUi.Describe(e), InfoBarSeverity.Error); return; }
        Tell("Paste (Ctrl+V) to add the image", InfoBarSeverity.Informational);
        App.Hint("The image is copied", "Press Ctrl+V in the post to add it.");
    }

    async void OnRecordAgain(object _, RoutedEventArgs __)
    {
        if (await BeforeAfterUi.RecordAfterAgainAsync(Content.XamlRoot, id) is { } error) Tell(error, InfoBarSeverity.Error);
        else if (App.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.BeforeAfter?.Stage != BeforeAfterStage.Done) Close();
    }

    async void OnStartOver(object _, RoutedEventArgs __)
    {
        if (await BeforeAfterUi.StartAsync(Content.XamlRoot, id, over: true) is { } error) Tell(error, InfoBarSeverity.Error);
        else if (App.Core.Games.FirstOrDefault(g => g.Game.Id == id)?.BeforeAfter?.Stage != BeforeAfterStage.Done) Close();
    }

    // screenshots
    public void ScrollToTop() => Scroller.ChangeView(null, 0, null, true);
    public void ScrollToEnd() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
    public FrameworkElement ShotRoot => Root;

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
}
