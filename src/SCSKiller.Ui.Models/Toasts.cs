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
        string games = stale.Count == 1 ? Loc.T("Toast_GameNeedsOne") : Loc.T("Toast_GamesNeedMany", stale.Count);
        return new(Loc.T("Toast_DriverUpdated", Fmt.Vendor(core.Vendor.Vendor)),
            Loc.T("Toast_DriverCleared", core.Vendor.Gpu.DriverVersion, games, string.Join(", ", stale.Select(g => g.Game.Name)),
                time > TimeSpan.Zero ? Loc.T("Toast_AboutTime", Format.Duration(time)) : "."),
            [Arg("action", "open")],
            [new(Loc.T("Toast_CompileNow"), [Arg("action", "now")]), new(Loc.T("Toast_WhenIdle"), [Arg("action", "idle")]), new(Loc.T("Toast_Skip"), [Arg("action", "skip")])]);
    }

    public static Toast NewShaders(IReadOnlyList<GameState> games, bool compiling = false)
    {
        var ids = string.Join('|', games.Select(g => g.Game.Id));
        var n = SCSKiller.Core.App.NewShaders.Count(games[0]);
        var one = Loc.T("Toast_GameNewPipelines", games[0].Game.Name, n == 1 ? Loc.T("Toast_PipelineOne", $"{n:N0}") : Loc.T("Toast_PipelinesMany", $"{n:N0}"));
        var (title, text) = compiling
            ? (Loc.T("Toast_CompilingNew"), games.Count == 1 ? one + " " + SCSKiller.Core.App.NewShaders.When(Host.Core.Settings)
                : Loc.T("Toast_ManyGamesNew", games.Count) + " " + SCSKiller.Core.App.NewShaders.When(Host.Core.Settings))
            : (Loc.T("Toast_NewToCompile"), games.Count == 1 ? one + " " + Loc.T("Toast_CompileNowHint") : Loc.T("Toast_ManyGamesToCompile", games.Count));
        return new(title, text, [Arg("action", "shaders-show"), Arg("games", ids)],
            [new(Loc.T("Toast_CompileNow"), [Arg("action", "shaders-compile"), Arg("games", ids)]), new(Loc.T("Toast_Show"), [Arg("action", "shaders-show"), Arg("games", ids)])]);
    }

    /// <summary>An unread news item: its title and the first line of its body. A click opens the bell.</summary>
    public static Toast News(NewsItem item) => new(item.Title, NewsState.FirstLine(item.Body), [Arg("action", "news")], []);

    public static Toast NewsMore(int n) => new(Loc.T("App_NewsTitle"), NewsState.MoreLine(n), [Arg("action", "news")], []);

    /// <summary>The close button hid the window to the notification area, the first time ever.</summary>
    public static Toast TrayNotice { get; } = new(Loc.T("App_StillRunningTitle"), Loc.T("App_StillRunningText"),
        [Arg("action", "open")], []);

    /// <summary>A compile the user started finished or failed (<see cref="ScsKiller.EndedNotice"/>): a click opens its page.</summary>
    public static Toast Ended(string gameId, (string Title, string Text) n) => new(n.Title, n.Text, [Arg("action", "shaders-show"), Arg("games", gameId)], []);

    /// <summary>A step the user takes in another app, shown over it, stale after a few minutes.</summary>
    public static Toast Hint(string title, string text) => new(title, text, [], [], TimeSpan.FromMinutes(5));
}
