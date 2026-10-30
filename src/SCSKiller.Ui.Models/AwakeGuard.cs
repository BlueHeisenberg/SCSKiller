using SCSKiller.Core;

namespace SCSKiller.Ui.Models;

/// <summary>Calls <c>hold</c> with true when a compile starts and false when none runs, once per change, in order. It must
/// not block: it runs under a lock. Compiling can end without a QueueChanged (a removed item's warm), hence the poll.</summary>
public sealed class AwakeGuard : IDisposable
{
    readonly IScsKiller _core;
    readonly Action<bool> _hold;
    readonly Lock _gate = new();
    readonly Timer _poll;
    bool _held, _disposed;

    public AwakeGuard(IScsKiller core, Action<bool> hold)
    {
        (_core, _hold) = (core, hold);
        core.QueueChanged += OnQueue;
        _poll = new Timer(_ => Update(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Update();
    }

    void OnQueue(QueueItem _) => Update();

    void Update()
    {
        lock (_gate)
        {
            var want = !_disposed && _core.Compiling;
            if (want == _held) return;
            _held = want;
            try { _hold(want); }
            catch (Exception) { }   // best effort: a compile never fails over it
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _core.QueueChanged -= OnQueue;
        _poll.Dispose();
        Update();
    }
}
