namespace SCSKiller.Core.Warming;

/// <summary>Where a run's scskiller_warm processes run, one host per run (<see cref="WarmHost.For"/>): on Windows they are
/// Core's children. Made before the run's first process, disposed after its last.</summary>
interface IWarmHost : IDisposable
{
    /// <summary>A path of Core's as the warm opens it.</summary>
    string WarmPath(string host);

    /// <summary>A path the warm names (its stage event) as Core opens it.</summary>
    string HostPath(string warm);

    /// <summary>The warm's arguments through which <see cref="Stop"/> reaches it.</summary>
    IReadOnlyList<string> StopArgs { get; }

    /// <summary>The warm's arguments that make it run on <paramref name="gpu"/>; empty when the host picks it another way.</summary>
    IReadOnlyList<string> AdapterArgs(GpuInfo gpu);

    /// <summary>Asks the run's processes to stop gracefully, those it starts later too. Throws ObjectDisposedException once
    /// disposed.</summary>
    void Stop();

    /// <summary><paramref name="errorLine"/> gets each non-empty stderr line from the start.</summary>
    IWarmProcess Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, Action<string> errorLine);

    /// <summary>Runs after each process has exited, before the next starts. <paramref name="note"/> gets what it does when it
    /// does anything.</summary>
    Task<ProcessEnd> AfterProcess(Action<string> note) => Task.FromResult(ProcessEnd.None);
}

/// <summary>What a host says once a process has exited (<see cref="IWarmHost.AfterProcess"/>): the cache keys of the files it
/// added (<see cref="WarmResult.CacheKeysAdded"/>); <paramref name="Segment"/>: it stopped the process, and the run goes on
/// from where that stopped; <paramref name="Halt"/>: why no further process may start; <paramref name="Full"/>: the halt is
/// the compile's cache holding no more, so what ran is the compile (<see cref="WarmResult.Full"/>).</summary>
sealed record ProcessEnd(IReadOnlyCollection<string> Added, bool Segment = false, string? Halt = null, bool Full = false)
{
    public static readonly ProcessEnd None = new([]);
}

/// <summary>The compile's cache holds no more before its first process (<see cref="WarmResult.Full"/>): <paramref name="keys"/>
/// are the cache keys of what earlier compiles left there.</summary>
sealed class CacheFullException(string message, IReadOnlyCollection<string> keys) : InvalidOperationException(message)
{
    public IReadOnlyCollection<string> Keys { get; } = keys;
}

/// <summary>One scskiller_warm process: the warm parent and the staged child it runs.</summary>
interface IWarmProcess
{
    /// <summary>Its stdout, the protocol's lines (ARCHITECTURE.md).</summary>
    TextReader Output { get; }

    /// <summary>Its exit code once it and its stderr have ended; null when it died without one.</summary>
    Task<int?> Exited { get; }

    bool HasExited { get; }

    /// <summary>Suspends or resumes the parent and its staged child.</summary>
    void Suspend(bool suspend);

    /// <summary>Ends the parent and its staged child, never anything else; one that exited meanwhile is no harm.</summary>
    void Kill();
}

static partial class WarmHost
{
    /// <param name="gpu">the adapter the run is for</param>
    /// <param name="steamRoot">the Steam folder of a Steam game (default: <see cref="Games.SteamSource.Root"/>)</param>
    /// <param name="vulkan">the run is a Vulkan warm (<see cref="WarmOptions.Vulkan"/>)</param>
    /// <param name="cacheCap">what the compile's cache folder may hold (<see cref="WarmOptions.CacheCap"/>)</param>
    /// <param name="mesa">the backend is Mesa's (<see cref="Vendors.MesaLinux.Is"/>)</param>
    public static partial IWarmHost For(Game game, string workDir, GpuInfo gpu, IProgress<string>? log, string? steamRoot = null, bool vulkan = false, long? cacheCap = null,
        bool mesa = false);

    /// <summary>Why <paramref name="game"/>'s warm can't run here, as a queue item fails with it; null when it can. Starts
    /// nothing.</summary>
    /// <param name="mesa">the backend is Mesa's (<see cref="Vendors.MesaLinux.Is"/>)</param>
    public static partial string? Refusal(Game game, string? steamRoot = null, bool vulkan = false, bool mesa = false);
}
