using System.Diagnostics;
using SCSKiller.Core;

namespace SCSKiller.Ui.Models;

/// <summary>systemd-logind's delay lock on shutdown and sleep, and its signals that either begins.</summary>
public interface IShutdownLock
{
    /// <summary>A delay lock on shutdown and sleep, released by disposing it. Null: logind refused it.</summary>
    Task<IDisposable?> TakeAsync(string why);

    /// <summary>True as a shutdown begins, false when it was called off.</summary>
    event Action<bool>? PrepareForShutdown;

    /// <summary>True as the system goes to sleep, false once it woke.</summary>
    event Action<bool>? PrepareForSleep;

    /// <summary>How long logind waits for a delay lock before it goes on regardless (InhibitDelayMaxSec).</summary>
    TimeSpan MaxDelay { get; }
}

/// <summary>Holds <see cref="IShutdownLock"/> only while a compile runs. A shutdown stops the compile gracefully and drops
/// the waiting items, as Windows' session end does. Sleep stops it gracefully and keeps them, and after the wake the stopped
/// item goes on: with the queue if the queue was running, else when the PC is idle again. Both stop a queue that runs with
/// no warm yet, and a compile that starts while they wait. The lock goes once no warm runs, or at logind's limit, past
/// which logind no longer waits for it.</summary>
public sealed class ShutdownGuard : IDisposable
{
    public const string Why = "Stopping the compile so the graphics driver can save its shader cache";

    static readonly TimeSpan Recheck = TimeSpan.FromSeconds(1);   // a removed item's warm ends without QueueChanged

    /// <summary>What a sleep stopped, to go on after the wake: the item whose warm the wake waits for (null: none, or a
    /// removed item's), the item that goes on (null: only the queue), and whether the queue ran it.</summary>
    sealed record Resume(string? Wait, string? Item, bool Queue);

    readonly IScsKiller _core;
    readonly IShutdownLock _bus;
    readonly Action<string>? _log;
    readonly Lock _gate = new();
    readonly Lock _turn = new();   // a wake's resume, and a shutdown, sleep or quit's stop, one at a time
    IDisposable? _held;
    bool _taking, _refused, _preparing, _shutdown, _quitting, _disposed;
    bool _letGo;   // this shutdown or sleep let the lock go: a lock that comes later isn't kept
    int _prepares;   // a release belongs to the shutdown or sleep that started it
    string? _stopped;   // the run this shutdown or sleep stopped last, until it ends
    Resume? _resume;   // until a wake acts on it, or a shutdown or the quit calls it off

    public ShutdownGuard(IScsKiller core, IShutdownLock bus, Action<string>? log = null)
    {
        (_core, _bus, _log) = (core, bus, log);
        core.QueueChanged += OnQueue;
        bus.PrepareForShutdown += OnShutdown;
        bus.PrepareForSleep += OnSleep;
        Update();
    }

    /// <summary>Whether the lock is held now.</summary>
    public bool Held { get { lock (_gate) return _held != null; } }

    void OnQueue(QueueItem _) => Update();
    void OnShutdown(bool start) => Prepare(start, shutdown: true);
    void OnSleep(bool start) => Prepare(start, shutdown: false);

    void Update()
    {
        IDisposable? release = null;
        bool take = false;
        int? preparing = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_preparing) preparing = _prepares;   // the lock goes once no warm runs (ReleaseWhenStoppedAsync)
            else if (!_core.Compiling) (release, _held, _refused) = (_held, null, false);
            else if (_held == null && !_taking && !_refused) take = _taking = true;
        }
        release?.Dispose();
        if (take) _ = TakeAsync();
        if (preparing is { } prepare) StopNew(prepare);
    }

    /// <summary>While shutdown or sleep <paramref name="prepare"/> still waits: a compile that started since is stopped too,
    /// once per run. A sleep's wake then goes on with it, unless it goes on with something else.</summary>
    void StopNew(int prepare)
    {
        lock (_turn)   // a wake's resume comes after this stop, or this finds the shutdown or sleep over
        {
            bool shutdown;
            lock (_gate)
            {
                if (_disposed || !_preparing || prepare != _prepares) return;
                var now = _core.Compiling ? QuitFlow.Running(_core) : null;
                if (now?.GameId == _stopped) return;
                _stopped = now?.GameId;   // null once the stopped run ended: the same game starting again is another run
                if (now == null) return;
                _resume = Planned(now, _core.QueueRunning);
                shutdown = _shutdown;
            }
            Stop(shutdown);
        }
    }

    /// <summary>Under the gate, as a sleep stops <paramref name="running"/>: what its wake goes on with. A wake's resume still
    /// waiting stays, and waits for this warm too.</summary>
    Resume? Planned(QueueItem? running, bool queue) =>
        _shutdown || _quitting ? null : _resume is { } kept ? kept with { Wait = running?.GameId ?? kept.Wait } : Plan(running, queue);

    static Resume? Plan(QueueItem? running, bool queue) =>
        running is { PlanCheck: false } ? new(running.GameId, running.GameId, queue) : queue ? new(running?.GameId, null, true) : null;

    void Stop(bool shutdown)
    {
        _log?.Invoke(shutdown ? "the system is shutting down: stopping the compile" : "the system is going to sleep: stopping the compile");
        if (shutdown) QuitFlow.StopAll(_core);
        else _core.StopQueue();
    }

    async Task TakeAsync()
    {
        IDisposable? got = null;
        try { got = await _bus.TakeAsync(Why).ConfigureAwait(false); }
        catch (Exception e) { _log?.Invoke($"no shutdown lock while compiling: {e.Message}"); }
        IDisposable? held = null;
        lock (_gate)
        {
            _taking = false;
            if (got == null) _refused = true;
            else if (_core.Compiling && !_disposed && !(_preparing && _letGo)) (_held, held, got) = (got, got, null);
        }
        got?.Dispose();   // the compile ended, or the shutdown or sleep let go already
        if (held != null) await RecheckAsync(held).ConfigureAwait(false);
    }

    async Task RecheckAsync(IDisposable held)
    {
        while (true)
        {
            await Task.Delay(Recheck).ConfigureAwait(false);
            lock (_gate)
                if (_held != held) return;
            if (!_core.Compiling) Update();
        }
    }

    void Prepare(bool start, bool shutdown)
    {
        if (!start)
        {
            bool resume;
            lock (_gate) (_preparing, resume) = (false, _resume != null);
            Update();
            if (resume) _ = ResumeAsync();
            return;
        }
        lock (_turn)
        {
            bool stop;
            int at;
            lock (_gate)
            {
                if (_disposed) return;
                (_preparing, _shutdown, _letGo, at) = (true, shutdown, false, ++_prepares);
                bool compiling = _core.Compiling, queue = _core.QueueRunning;
                var running = compiling ? QuitFlow.Running(_core) : null;
                _stopped = running?.GameId;
                _resume = Planned(running, queue);
                stop = compiling || queue;   // a queue started a moment ago has no warm yet
            }
            if (stop) Stop(shutdown);
            _ = ReleaseWhenStoppedAsync(at);
        }
    }

    /// <summary>Once no warm runs (each compile that starts meanwhile is stopped too), or at logind's limit. Not after the
    /// wake or the shutdown called off: the lock then stays while the compile goes on.</summary>
    async Task ReleaseWhenStoppedAsync(int prepare)
    {
        for (var clock = Stopwatch.StartNew(); _core.Compiling && clock.Elapsed < _bus.MaxDelay;) await Task.Delay(100).ConfigureAwait(false);
        IDisposable? release = null;
        lock (_gate)
            if (_preparing && prepare == _prepares) (release, _held, _letGo) = (_held, null, true);
        release?.Dispose();
    }

    /// <summary>The warm of <paramref name="game"/> still runs (null: of an item the queue no longer lists).</summary>
    bool Stopping(string? game) => _core.Compiling && (QuitFlow.Running(_core) is not { } now || now.GameId == game);

    /// <summary>After the wake, once the stopped warm has exited: the item goes on from its resume point, or the queue.
    /// Not when the item left the queue or another compile started. A shutdown, sleep or quit meanwhile comes first.</summary>
    async Task ResumeAsync()
    {
        while (true)
        {
            lock (_turn)   // check and act in one turn: a shutdown, sleep or quit comes first, or stops what this starts
            {
                Resume? r;
                lock (_gate) r = Pending();
                if (r == null) return;
                if (!Stopping(r.Wait))
                {
                    GoOn(r);
                    return;
                }
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    /// <summary>Under the turn: the resume is used up, whether or not anything goes on.</summary>
    void GoOn(Resume r)
    {
        lock (_gate) _resume = null;
        if (QuitFlow.Running(_core) != null || _core.QueueRunning) return;
        if (r.Item != null && !_core.Queue.Any(q => q.GameId == r.Item && q.Stage == QueueStage.Stopped)) return;
        _log?.Invoke("the system woke: the compile goes on");
        if (r.Queue) _core.StartQueue();
        else if (r.Item != null) _core.EnqueueWhenIdle(r.Item);
    }

    /// <summary>Under the gate: the resume to act on, while no shutdown or sleep prepares.</summary>
    Resume? Pending() => _disposed || _preparing ? null : _resume;

    /// <summary>The app quits on a signal: no wake's resume starts the queue again. The lock stays while a warm runs.</summary>
    public void Quit()
    {
        lock (_turn)
            lock (_gate) (_quitting, _resume) = (true, null);
    }

    public void Dispose()
    {
        IDisposable? release;
        lock (_gate) (_disposed, release, _held) = (true, _held, null);
        _core.QueueChanged -= OnQueue;
        _bus.PrepareForShutdown -= OnShutdown;
        _bus.PrepareForSleep -= OnSleep;
        release?.Dispose();
    }
}
