using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core;

/// <summary>Every reader's per-item gate (<see cref="ReaderLoops"/> loops). The read budget measures the managed heap only: a
/// read's committed but free heap, large-object holes and native buffers are invisible to it (21-25 GB held on 8 GB of
/// heap). Over <see cref="PlanBudget.DefaultMemory"/> in private bytes, one compacting collect, then items run one at a time
/// until the process is back under. Slower, never fewer shaders.</summary>
public static class ReadGate
{
    static readonly SemaphoreSlim one = new(1, 1);
    static readonly AsyncLocal<bool> inside = new();   // flows into an item's own loops: they run under its gate
    static readonly long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    static readonly object collecting = new();
    static long sampled, sampledAt, collectAt, wait = 5000, alone;

    /// <summary>What the gate compares against the cap: replaceable for tests.</summary>
    internal static Func<long> Measure { get; set; } = ProcessMemory.Private;
    /// <summary>The cap; null: <see cref="PlanBudget.DefaultMemory"/> of the flow (background or not).</summary>
    internal static long? Cap { get; set; }
    public static long Limit => Cap ?? PlanBudget.DefaultMemory(ram);

    /// <summary>Items that ran alone over the cap since the process started.</summary>
    public static long Alone => Interlocked.Read(ref alone);

    /// <summary>Private bytes or the managed heap, whichever is more, sampled at most every 100 ms.</summary>
    public static long Used(bool fresh = false)
    {
        var now = Environment.TickCount64;
        if (!fresh && now - Volatile.Read(ref sampledAt) < 100) return Volatile.Read(ref sampled);
        var used = Math.Max(Measure(), GC.GetTotalMemory(false));
        Volatile.Write(ref sampled, used);
        Volatile.Write(ref sampledAt, now);
        return used;
    }

    public static Held Enter(CancellationToken ct = default)
    {
        if (inside.Value) return default;
        var cap = Limit;
        if (Used() <= cap)
        {
            if (Volatile.Read(ref wait) != 5000) Volatile.Write(ref wait, 5000);
            return new(null);
        }
        lock (collecting)   // a collect that leaves it over waits twice as long for the next, up to a minute: a held index over the cap isn't collected each item
            if (Environment.TickCount64 >= collectAt)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                wait = Used(fresh: true) <= cap ? 5000 : Math.Min(wait * 2, 60_000);
                collectAt = Environment.TickCount64 + wait;
            }
        if (Used() <= cap) return new(null);
        one.Wait(ct);
        Interlocked.Increment(ref alone);
        return new(one);
    }

    public readonly struct Held : IDisposable
    {
        readonly SemaphoreSlim? held;
        readonly bool set;

        internal Held(SemaphoreSlim? held)
        {
            (this.held, set) = (held, true);
            inside.Value = true;
        }

        public void Dispose()
        {
            if (!set) return;
            inside.Value = false;
            held?.Release();
        }
    }
}
