using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>The real quit (the tray's Quit, or the close button when it quits). With a compile running, the compile stops
/// gracefully and the app exits once the warm has exited, however long the driver's cache write takes (scskiller_warm
/// bounds it: 600 s after its last line, a warm that ignores the stop is ended after 30 s). Quit again meanwhile
/// ("Quit now (loses unsaved cache)") exits at once: the warm job kills the warm.</summary>
public sealed class QuitFlow
{
    TaskCompletionSource? now;   // set while the quit waits for the compile

    public bool Quitting { get; private set; }
    public string Label => now != null ? "Quit now (loses unsaved cache)" : "Quit";

    /// <summary>False: a quit already runs, and one waiting for the compile stops waiting.</summary>
    public bool Begin()
    {
        if (now != null) { now.TrySetResult(); return false; }
        if (Quitting) return false;
        return Quitting = true;
    }

    /// <summary>Stops the running compile and waits for its warm to exit or for a second <see cref="Begin"/>.
    /// <paramref name="stopping"/> runs first, only when there is a compile to wait for.</summary>
    public async Task StopAsync(IScsKiller core, Action stopping)
    {
        if (!core.Compiling) return;   // a removed item's warm too: it is still saving
        now = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stopping();
        StopAll(core);
        await Task.WhenAny(Task.Run(async () => { while (core.Compiling) await Task.Delay(250); }), now.Task);
    }

    public static QueueItem? Running(IScsKiller core) => core.Queue.FirstOrDefault(Format.Running);

    /// <summary>Stops the running compile gracefully (the stop event: in-flight items finish, the driver writes its cache)
    /// and drops the waiting items, which don't outlive the app anyway ("when idle" ones would otherwise start).</summary>
    public static void StopAll(IScsKiller core)
    {
        foreach (var q in core.Queue.Where(q => q.Stage == QueueStage.Waiting).ToList()) core.Remove(q.GameId);
        core.StopQueue();
    }

    /// <summary>The session's end: blocks until the warm exited (a removed item's too), at most <paramref name="max"/>.</summary>
    public static void WaitStopped(IScsKiller core, TimeSpan max)
    {
        for (var clock = System.Diagnostics.Stopwatch.StartNew(); core.Compiling && clock.Elapsed < max;) Thread.Sleep(100);
    }
}

/// <summary>A quit signal on Linux: the session's end as on Windows, then the app exits, even with its UI thread blocked.</summary>
public sealed class SignalQuit
{
    /// <summary>Before the stop, off the UI thread: calls off a wake's resume that would start the queue again.</summary>
    public Action CallOff { get; init; } = () => { };
    /// <summary>On the UI thread, first.</summary>
    public Action Hide { get; init; } = () => { };
    /// <summary>After the wait, off the UI thread.</summary>
    public Func<Task> Flush { get; init; } = () => Task.CompletedTask;
    /// <summary>On the UI thread, last: ends the app.</summary>
    public Action End { get; init; } = () => { };
    /// <summary>Ends the process when <see cref="End"/> hasn't, <see cref="Grace"/> after the wait.</summary>
    public Action Exit { get; init; } = () => Environment.Exit(0);
    /// <summary>The most the stopped warm gets.</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>From the signal's thread. The stop and the wait run on the thread pool, so a blocked UI thread can't hold
    /// them up: the queue stops with or without a warm yet, and the waiting items are dropped.</summary>
    public async Task RunAsync(IScsKiller core, IUiThread ui)
    {
        ui.TryEnqueue(Hide);
        try
        {
            await Task.Run(() =>
            {
                CallOff();
                QuitFlow.StopAll(core);
                QuitFlow.WaitStopped(core, Wait);
            }).ConfigureAwait(false);
            await Flush().ConfigureAwait(false);
        }
        finally
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.TryEnqueue(() =>
            {
                End();
                ended.TrySetResult();
            });
            if (await Task.WhenAny(ended.Task, Task.Delay(Grace)).ConfigureAwait(false) != ended.Task) Exit();
        }
    }
}
