using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>When the Enjoying SCSKiller? popup asks, from local state only.</summary>
public static class SupportPopup
{
    public static readonly TimeSpan AfterFirstUse = TimeSpan.FromDays(3);
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(30);
    public const int MaxAsks = 2;
    public const int MinCompiles = 2;
    public const int MinCoverage = 99;
    /// <summary>The app's own verdict: above this share a first launch reads as partly compiled (<see cref="ScsKiller.IsPartlyWarmed(LaunchCheck?)"/>).</summary>
    public const double MaxFirstLaunchCompiled = ScsKiller.PartlyWarmedShare;
    public const string Link = "https://www.patreon.com/SCSKiller";
    /// <summary>How long an opening waits for the first scan to list the games. A slower one is asked at the next opening.</summary>
    public const int ListChecks = 30;
    public static readonly TimeSpan ListCheckEvery = TimeSpan.FromSeconds(2);

    public static bool TimeToAsk(Settings s, DateTimeOffset now) => s.SupportAsks switch
    {
        0 => now - s.SupportFirstUse >= AfterFirstUse,
        1 => now - s.SupportAskedAt >= AskAgainAfter,
        _ => false,
    };

    /// <summary>A compile of a recorded game whose first launch after it barely compiled, at the coverage its page shows.</summary>
    public static bool GoodFirstLaunch(GameState s, GameRecord r) =>
        s.RecorderInstalled && r is { WarmedAt: not null, FirstLaunch: { } l } && l.Compiled <= MaxFirstLaunchCompiled && DetailVm.PagePercent(s) >= MinCoverage;

    /// <summary><see cref="MinCompiles"/> compiles that created pipelines, on any game, or one <see cref="GoodFirstLaunch"/>.</summary>
    public static bool Earned(IEnumerable<GameState> games, Func<string, GameRecord> record)
    {
        var compiles = 0;
        foreach (var s in games)
        {
            var r = record(s.Game.Id);
            compiles += r.CreatingCompiles;
            if (compiles >= MinCompiles || GoodFirstLaunch(s, r)) return true;
        }
        return false;
    }

    public static bool Due(Settings s, DateTimeOffset now, bool signedIn, bool gameOpen, bool compiling, bool earned) =>
        !signedIn && !gameOpen && !compiling && earned && TimeToAsk(s, now);

    public static Settings Asked(Settings s, DateTimeOffset now) => s with { SupportAsks = s.SupportAsks + 1, SupportAskedAt = now };
    public static Settings Answered(Settings s) => s with { SupportAsks = MaxAsks };
}
