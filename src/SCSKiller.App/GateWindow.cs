using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SCSKiller.Core.App;
using Windows.Graphics;

namespace SCSKiller.App;

/// <summary>An alpha or beta build's start until a backer or supporter signs in (<see cref="AlphaGate"/>): nothing else of the app exists yet.</summary>
sealed class GateWindow : Window
{
    readonly Account account;
    readonly string dataDir;
    readonly Action opened;
    int attempt;
    readonly TextBlock why = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button signIn = new() { Content = AlphaGate.SignIn, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

    public Grid Root { get; }

    public GateWindow(Account account, string dataDir, Action opened)
    {
        (this.account, this.dataDir, this.opened) = (account, dataDir, opened);
        Title = "SCSKiller";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(520 * scale), (int)(300 * scale)));
        var quit = new Button { Content = AlphaGate.Quit };
        quit.Click += (_, _) => Application.Current.Exit();
        signIn.Click += async (_, _) => await OpenAsync(signIn: true);
        SystemBackdrop = new MicaBackdrop();   // as the main window: a theme brush read in code wouldn't follow the theme
        Root = new Grid { Padding = new Thickness(32) };
        Root.Children.Add(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = AlphaGate.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = AlphaGate.Intro, TextWrapping = TextWrapping.Wrap },
                why,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { signIn, quit } },
                new HyperlinkButton { Content = AlphaGate.Stable, NavigateUri = AlphaGate.StableUri, Padding = new Thickness(0) },
            },
        });
        Content = Root;
    }

    /// <summary>At start: a signed-in account may already carry alpha (one refresh). Then the screen waits for the button.</summary>
    public Task StartAsync() => OpenAsync(signIn: false);

    /// <summary>The line under the intro; the button stays off when no sign-in could open the gate.</summary>
    public void ShowLine(string line) => (why.Text, signIn.IsEnabled) = (line, line != AlphaGate.TooOld);

    async Task OpenAsync(bool signIn)
    {
        var mine = ++attempt;
        this.signIn.IsEnabled = signIn;   // a second click starts the sign-in again, as after a closed browser tab
        why.Text = signIn ? AlphaGate.Waiting : "";
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

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
}
