using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using SCSKiller.Core.App;

namespace SCSKiller.Ui;

/// <summary>An alpha or beta build's start until a backer or supporter signs in (<see cref="AlphaGate"/>): nothing else of the app exists yet.</summary>
sealed class GateWindow : Window
{
    readonly Account account;
    readonly string dataDir;
    readonly Action opened;
    int attempt;
    readonly TextBlock why = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button signIn = new() { Content = AlphaGate.SignIn, Classes = { "accent" } };

    public GateWindow(Account account, string dataDir, Action opened)
    {
        (this.account, this.dataDir, this.opened) = (account, dataDir, opened);
        (Title, Width, Height, CanResize) = ("SCSKiller", 520, 300, false);
        Icon = new WindowIcon(AssetLoader.Open(App.Asset("app.ico")));
        Bind(BackgroundProperty, this.GetResourceObservable("SolidBackgroundFillColorBaseBrush"));
        Bind(ForegroundProperty, this.GetResourceObservable("TextFillColorPrimaryBrush"));
        var quit = new Button { Content = AlphaGate.Quit };
        quit.Click += (_, _) => Close();
        signIn.Click += async (_, _) => await OpenAsync(signIn: true);
        var stable = new HyperlinkButton { Content = AlphaGate.Stable, NavigateUri = AlphaGate.StableUri, Padding = new Thickness(0) };
        Content = new StackPanel
        {
            Margin = new Thickness(32),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = AlphaGate.Title, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = AlphaGate.Intro, TextWrapping = TextWrapping.Wrap },
                why,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { signIn, quit } },
                stable,
            },
        };
    }

    /// <summary>At start: a signed-in account may already carry alpha (one refresh). Then the screen waits for the button.</summary>
    public Task StartAsync() => OpenAsync(signIn: false);

    /// <summary>The line under the intro; the button stays off when no sign-in could open the gate.</summary>
    public void ShowLine(string line) => (Why, signIn.IsEnabled) = (line, line != AlphaGate.TooOld);

    /// <summary>Hidden while empty: an empty text block is an unnamed element to a screen reader.</summary>
    string Why { set => (why.Text, why.IsVisible) = (value, value.Length > 0); }

    async Task OpenAsync(bool signIn)
    {
        var mine = ++attempt;
        this.signIn.IsEnabled = signIn;   // a second click starts the sign-in again, as after a closed browser tab
        Why = signIn ? AlphaGate.Waiting : "";
        var line = await AlphaGate.OpenAsync(account, dataDir, signIn);
        if (mine != attempt) return;   // a later click's sign-in answers instead
        if (line == null)
        {
            opened();
            Close();
            return;
        }
        ShowLine(line);
    }
}
