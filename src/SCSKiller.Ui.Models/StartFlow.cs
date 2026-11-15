using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>The welcome's boxes and whether its Sign in button closed it. <paramref name="RecordAll"/>: null where it has no recorder box.</summary>
public sealed record WelcomeAnswer(bool Share, bool Diagnostics, bool ActiveCheck, bool SignIn, bool? RecordAll = null);

/// <summary>The dialogs at the start, one at a time. An ask returns null when its dialog couldn't open (another one was
/// up): nothing is saved, so the next start asks again.</summary>
public static class StartFlow
{
    /// <summary>The first-run welcome: closing it either way marks it seen and saves its boxes. True: Sign in was chosen.</summary>
    public static async Task<bool> WelcomeAsync(IScsKiller core, Func<Task<WelcomeAnswer?>> ask)
    {
        if (await ask() is not { } a) return false;
        core.Settings = ActiveCheck.Answered(Diagnostics.Answered(core.Settings, a.Diagnostics), a.ActiveCheck) with { WelcomeSeen = true, ShareRecordings = a.Share, RecordAllGames = a.RecordAll ?? core.Settings.RecordAllGames };
        return a.SignIn;
    }

    /// <summary>A later start's one-time questions: the daily check, then diagnostics. True from an ask turns it on.</summary>
    public static async Task QuestionsAsync(IScsKiller core, Func<Task<bool?>> activeCheck, Func<Task<bool?>> diagnostics)
    {
        if (ActiveCheck.Asks(core.Settings) && await activeCheck() is { } on) core.Settings = ActiveCheck.Answered(core.Settings, on);
        if (Diagnostics.Asks(core.Settings) && await diagnostics() is { } share) core.Settings = Diagnostics.Answered(core.Settings, share);
    }

    /// <summary>The traversal stutter fix's one-time question, after the welcome or a later start's questions.</summary>
    public static async Task TraversalAsync(IScsKiller core, Func<Task<TraversalMode?>> ask)
    {
        if (TraversalFix.Asks(core.Settings) && await ask() is { } mode) core.Settings = TraversalFix.Answered(core.Settings, mode);
    }
}

/// <summary>A window's one dialog at a time: a show that mustn't be lost waits for the open one to close, never dropped.</summary>
public sealed class DialogWait(Action<Action> post)
{
    TaskCompletionSource? freed;

    /// <summary>Shows now, or each time the open dialog closes until it can. <paramref name="tryShow"/>: null while another is open.</summary>
    public async Task<T> ShowAsync<T>(Func<Task<T>?> tryShow, string what)
    {
        for (var waited = false; ; waited = true)
        {
            if (tryShow() is { } shown)
            {
                if (waited) Host.Log?.Report($"dialog \"{what}\" shows: the one before it closed");
                return await shown;
            }
            Host.Log?.Report($"dialog \"{what}\" waits: another dialog is open");
            await (freed ??= new()).Task;
        }
    }

    /// <summary>The open dialog closed. Posted: the closed dialog's caller shows its follow-up (confirm, then its error) first.</summary>
    public void Closed()
    {
        if (freed is not { } f) return;
        freed = null;
        post(() => f.TrySetResult());
    }
}
