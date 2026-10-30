using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui;

/// <summary>The WinUI app's start-failure message box, on Linux, where no system one exists.</summary>
sealed class StartFailedWindow : Window
{
    public StartFailedWindow(StartFailure failure)
    {
        (Title, SizeToContent, CanResize, WindowStartupLocation) = (StartFailure.Title, SizeToContent.WidthAndHeight, false, WindowStartupLocation.CenterScreen);
        Icon = new WindowIcon(AssetLoader.Open(App.Asset("app.ico")));
        this.Bind(BackgroundProperty, this.GetResourceObservable("SolidBackgroundFillColorBaseBrush"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("TextFillColorPrimaryBrush"));
        var icon = new TextBlock { Text = Glyphs.Warning, FontSize = 24, VerticalAlignment = VerticalAlignment.Top };
        icon.Classes.Add("Icon");
        icon.Bind(TextBlock.ForegroundProperty, icon.GetResourceObservable("SystemFillColorCriticalBrush"));
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new(24), Spacing = 20,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 16,
                    Children = { icon, new SelectableTextBlock { Text = failure.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, VerticalAlignment = VerticalAlignment.Center } },
                },
                ok,
            },
        };
        Opened += (_, _) => ok.Focus();
    }

    /// <summary>Shows <paramref name="failure"/> until it is closed. A start that failed before Avalonia was set up starts it
    /// for this window alone. False: it couldn't show (no display).</summary>
    public static bool Show(StartFailure failure)
    {
        try
        {
            if (Application.Current == null)
            {
                App.Failed = failure;
                Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
                return true;
            }
            // the app's own start failed: Avalonia is set up, but its loop never ran
            var window = new StartFailedWindow(failure);
            using var closed = new CancellationTokenSource();
            window.Closed += (_, _) => closed.Cancel();
            window.Show();
            Dispatcher.UIThread.MainLoop(closed.Token);
            return true;
        }
        catch (Exception) { return false; }
    }
}
