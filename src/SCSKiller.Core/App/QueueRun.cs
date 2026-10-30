namespace SCSKiller.Core.App;

/// <summary>The CLI's compile run: queues the games, then starts the queue or leaves them to the PC's idle time. A
/// <see cref="Stop"/> from any thread at any point wins: nothing is queued or started after it.</summary>
public sealed class QueueRun(ScsKiller k)
{
    readonly object _gate = new();
    bool _stopped;

    public bool Stopped { get { lock (_gate) return _stopped; } }

    /// <summary>Queues the games in order (as "when idle" items with <paramref name="whenIdle"/>) and starts the queue
    /// for normal ones; false when the run was stopped first.</summary>
    public bool Start(IEnumerable<string> gameIds, bool whenIdle)
    {
        foreach (var id in gameIds)
            lock (_gate)
            {
                if (_stopped) return false;
                if (whenIdle) k.EnqueueWhenIdle(id); else k.Enqueue(id);
            }
        lock (_gate)
        {
            if (_stopped) return false;
            if (!whenIdle) k.StartQueue();
            return true;
        }
    }

    /// <summary>Takes the waiting items out and stops the queue: the running item stops gracefully, so the driver writes
    /// its cache. A later call does it again.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            foreach (var q in k.Queue.Where(q => q.Stage == QueueStage.Waiting)) k.Remove(q.GameId);
            k.StopQueue();
        }
    }
}
