using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Ui.Models;

/// <summary>The before and after recording's actions that the Library and the game page share; the dialogs are the UI's.</summary>
public static class BeforeAfterUi
{
    /// <summary>Starts the game through its store, or through the armed before and after run; the error to show, or null.</summary>
    public static async Task<string?> PlayAsync(GameState g)
    {
        await Task.Run(() => Host.Core.PutBackKept(g.Game.Id));
        return g.BeforeAfter is { Stage: BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed, ClearPending: false } ? await LaunchAsync(g.Game.Id) : Fmt.Play(g);
    }

    public static async Task<string?> LaunchAsync(string id)
    {
        try { await Task.Run(() => Host.Core.BeforeAfterRecorderReady(id)); }
        catch (Exception e) { return Describe(e); }
        return Game(id) is var g && Launchable(g) ? Fmt.Play(g) : null;
    }

    /// <summary>The intro's choice taken: the before run armed (off the UI thread, it deletes files), then the game started
    /// through its store. <paramref name="over"/>: the pair so far ends first.</summary>
    public static async Task<string?> BeginAsync(string id, int minutes, bool overlay, bool over, bool max = false)
    {
        try
        {
            await Task.Run(() =>
            {
                if (over) Host.Core.EndBeforeAfter(id, compile: false);
                Host.Core.StartBeforeAfter(id, minutes, overlay, max);
            });
        }
        catch (Exception e) { return Describe(e); }
        return await LaunchAsync(id);
    }

    /// <summary>AfterReady's Continue: arms the after run and starts the game, or (the game ran outside the flow) compiles first.
    /// BeforeArmed with a clear that failed: clears again and, cleared, starts the game.</summary>
    public static async Task<string?> ContinueAsync(string id)
    {
        try { await Task.Run(() => Host.Core.ContinueBeforeAfter(id)); }
        catch (Exception e) { return Describe(e); }
        return Game(id).BeforeAfter is { Stage: BeforeAfterStage.AfterArmed } or { Stage: BeforeAfterStage.BeforeArmed, ClearPending: false } ? await LaunchAsync(id) : null;
    }

    public static async Task<string?> RecordAfterAgainAsync(string id)
    {
        try { await Task.Run(() => Host.Core.RecordAfterAgain(id)); return null; }
        catch (Exception e) { return Describe(e); }
    }

    public static async Task<string?> EndAsync(string id, bool compile)
    {
        try { await Task.Run(() => Host.Core.EndBeforeAfter(id, compile)); return null; }
        catch (Exception e) { return Describe(e); }
    }

    public static bool Launchable(GameState g) => g.AntiCheat == AntiCheat.None && !ScsKiller.IsUnchecked(g) && StoreSupported(g.Game);   // unchecked: its anti-cheat isn't known

    // Core looks for GOG Galaxy and PURPLE in the registry, which only Windows has: elsewhere they have no launch
    static bool StoreSupported(Game g)
    {
        try { return StoreLaunch.Supported(g); }
        catch (PlatformNotSupportedException) { return false; }
    }

    /// <summary>A driver or game update since the before run: only "Start over" (or ending it) is left.</summary>
    public static bool Voided(BeforeAfterInfo? i) => i?.Problem is ScsKiller.DriverVoidNote or ScsKiller.BuildVoidNote;

    public static string Describe(Exception e) => !string.IsNullOrWhiteSpace(e.Message) ? e.Message : e.GetType().Name;

    static GameState Game(string id) => Host.Core.Games.First(g => g.Game.Id == id);
}
