using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>A notification as each OS shows it: its two lines, the arguments a click on its body hands to the app, and
/// its buttons. <paramref name="Lifetime"/>: gone from the notification list after it.</summary>
public sealed record Toast(string Title, string Text, IReadOnlyList<KeyValuePair<string, string>> Args, IReadOnlyList<ToastButton> Buttons,
    TimeSpan? Lifetime = null);

public sealed record ToastButton(string Label, IReadOnlyList<KeyValuePair<string, string>> Args);

/// <summary>The app's notifications, as the WinUI app words them. Clicks reach Activation with their arguments.</summary>
public static class Toasts
{
    static KeyValuePair<string, string> Arg(string key, string value) => new(key, value);

    // Toast.dc.html. Windows draws the buttons, so "Compile now" can't be accent-coloured.
    public static Toast Driver(IScsKiller core, IReadOnlyList<GameState> stale)
    {
        var time = TimeSpan.FromTicks(stale.Sum(g => (g.EstimatedWarmTime ?? TimeSpan.Zero).Ticks));
        string games = stale.Count == 1 ? "1 game needs" : $"{stale.Count} games need";
        return new($"{Fmt.Vendor(core.Vendor.Vendor)} driver updated",
            $"Driver {core.Vendor.Gpu.DriverVersion} cleared the shader cache. {games} rebuilding: " +
            $"{string.Join(", ", stale.Select(g => g.Game.Name))}" + (time > TimeSpan.Zero ? $", about {Format.Duration(time)}." : "."),
            [Arg("action", "open")],
            [new("Compile now", [Arg("action", "now")]), new("When idle", [Arg("action", "idle")]), new("Skip", [Arg("action", "skip")])]);
    }

    public static Toast NewShaders(IReadOnlyList<GameState> games, bool compiling = false)
    {
        var ids = string.Join('|', games.Select(g => g.Game.Id));
        var n = SCSKiller.Core.App.NewShaders.Count(games[0]);
        var one = $"{games[0].Game.Name} has {n:N0} new pipeline{(n == 1 ? "" : "s")}.";
        var (title, text) = compiling
            ? ("Compiling new shaders", $"{(games.Count == 1 ? one : $"{games.Count} games have new shaders.")} {SCSKiller.Core.App.NewShaders.When(Host.Core.Settings)}")
            : ("New shaders to compile", games.Count == 1 ? $"{one} Compile now so they don't stutter." : $"{games.Count} games have new shaders to compile");
        return new(title, text, [Arg("action", "shaders-show"), Arg("games", ids)],
            [new("Compile now", [Arg("action", "shaders-compile"), Arg("games", ids)]), new("Show", [Arg("action", "shaders-show"), Arg("games", ids)])]);
    }

    /// <summary>An unread news item: its title and the first line of its body. A click opens the bell.</summary>
    public static Toast News(NewsItem item) => new(item.Title, NewsState.FirstLine(item.Body), [Arg("action", "news")], []);

    public static Toast NewsMore(int n) => new("SCSKiller news", NewsState.MoreLine(n), [Arg("action", "news")], []);

    /// <summary>The close button hid the window to the notification area, the first time ever.</summary>
    public static Toast TrayNotice { get; } = new("SCSKiller is still running", "It's in the notification area, right-click the icon to quit.",
        [Arg("action", "open")], []);

    /// <summary>A compile the user started finished or failed (<see cref="ScsKiller.EndedNotice"/>): a click opens its page.</summary>
    public static Toast Ended(string gameId, (string Title, string Text) n) => new(n.Title, n.Text, [Arg("action", "shaders-show"), Arg("games", gameId)], []);

    /// <summary>A step the user takes in another app, shown over it, stale after a few minutes.</summary>
    public static Toast Hint(string title, string text) => new(title, text, [], [], TimeSpan.FromMinutes(5));
}
