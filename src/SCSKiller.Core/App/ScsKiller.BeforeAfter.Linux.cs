using System.Globalization;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    partial void LinuxAside(ref IBeforeRunAside? aside) => aside = AppCache switch
    {
        MesaLinuxAppCache => SteamAside(SteamSetAside.Mesa),
        NvidiaLinuxAppCache => SteamAside(SteamSetAside.Nvidia),
        _ => aside,
    };

    SteamSetAside SteamAside(string leaf) =>
        new(Path.Combine(Store.DataDir, leaf == SteamSetAside.Mesa ? "mesa-set-aside.json" : "nvidiav1-set-aside.json"), Path.Combine(Store.DataDir, "before-run-caches"), leaf)
        {
            Log = m => Log?.Report(m),
            Settled = leaf == SteamSetAside.Nvidia ? Merged : null,
        };

    static string NvidiaFolder(string apps, uint app) => Path.Combine(apps, "shadercache", app.ToString(CultureInfo.InvariantCulture), SteamSetAside.Nvidia);

    /// <summary>On Linux, the before run's clear: the game's driver cache folder in its Steam library and vkd3d-proton's cache
    /// where the placer finds the game's (SteamSetAside), those set aside for its last run put back first.
    /// InvalidOperationException with why, nothing set aside.</summary>
    partial void SetAsideSteam(string gameId)
    {
        if (SetAside() is not SteamSetAside q) return;
        var s = Find(gameId);
        var g = s.Game;
        if (MesaLinux.SteamFolder(g, PlaceSteamRoot) is not var (apps, app)) return;
        if (GameRunning(g)) throw new InvalidOperationException($"{g.Name} is running");
        var folder = q.Leaf == SteamSetAside.Mesa ? MesaLinux.Folder(MesaLinux.SteamCache(apps, app))! : NvidiaFolder(apps, app);
        // a game on the Vulkan route reaches the driver without vkd3d-proton
        var dir = g.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !VulkanRoute(g, s.Engine) ? WorkingFolder(g, PlaceSteamRoot, app, Store.GameDir(gameId), out _)?.Dir : null;
        // the placer and the warm write them under it
        using var own = CompileLock(gameId) ?? throw new InvalidOperationException($"{g.Name}'s cache is being compiled or put in Steam's folder, try again in a moment");
        lock (_setAsideLock)
        {
            if (q.PendingGame is { } pending)
            {
                if (pending != gameId)
                    throw new InvalidOperationException($"{Games.FirstOrDefault(x => x.Game.Id == pending)?.Game.Name ?? pending}'s before run still has its cache set aside");
                if (q.Restore() is { Count: > 0 } left) throw new InvalidOperationException(F(RestoreFailedNote, left.Count, q.Folder));
                if (q.PendingGame != null) throw new InvalidOperationException(AsideHeldNote);
            }
            q.SetAside(gameId, folder, dir == null ? [] : [Path.Combine(dir, SteamCachePlace.Vkd3dCache), Path.Combine(dir, SteamCachePlace.Vkd3dWrite)]);
        }
    }

    // Steam merges at the game's exit: a fragment unchanged this long isn't being merged
    static readonly TimeSpan MergeWait = TimeSpan.FromMinutes(2);

    /// <summary>Steam folds the game's steamapp_shader_cache&lt;n&gt; into its merged file after the game exits, then deletes it:
    /// the NVIDIA folder a before run made moves away once none is left, or once nothing in it changed for
    /// <see cref="MergeWait"/>.</summary>
    static bool Merged(string made)
    {
        var slot = NvidiaLinux.SteamTargets.Single(t => t.Slot).Name;
        bool Fragment(string name) => (name.EndsWith(".toc", StringComparison.Ordinal) || name.EndsWith(".bin", StringComparison.Ordinal))
            && name[..^4] is var set && set.Length > slot.Length && set.StartsWith(slot, StringComparison.Ordinal) && set[slot.Length..].All(char.IsAsciiDigit);
        try
        {
            var all = new DirectoryInfo(made).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
            return !all.Any(f => f is FileInfo && Fragment(f.Name)) || all.Append(new DirectoryInfo(made)).Max(f => f.LastWriteTimeUtc) < DateTime.UtcNow - MergeWait;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>An unlisted Steam game also runs while a process started with its SteamAppId runs (Steam gives every launch
    /// it), or one runs from its folder in Steam's libraries.</summary>
    partial void UnlistedRuns(string gameId, List<string> roots, ref bool runs)
    {
        if ((SteamSource.AppId(gameId) ?? SteamShortcuts.AppId(gameId)) is not { } app) return;
        if (SteamSource.AppFolder(PlaceSteamRoot, app) is { } folder) roots.Add(folder);
        var id = app.ToString(CultureInfo.InvariantCulture);
        runs = Processes(true).Any(p => ProcessTree.StartEnvironment(p.Pid, "SteamAppId") == id);
    }

    /// <summary>The other Linux driver's set-aside: what it holds was set aside before the GPU changed.</summary>
    partial void OtherAsides(List<IBeforeRunAside> asides)
    {
        var current = (SetAside() as SteamSetAside)?.Leaf;
        foreach (var leaf in new[] { SteamSetAside.Mesa, SteamSetAside.Nvidia })
            if (leaf != current) asides.Add(SteamAside(leaf));
    }

    /// <summary>A native Vulkan game reads Steam's NVIDIA folder only under Steam's __GL_SHADER_DISK_CACHE_PATH, which its last
    /// recorded launch didn't have: the user's own NVIDIA cache, never touched here, may hold its pipelines.</summary>
    partial void NotColdNote(string gameId, ref string? note)
    {
        var s = Find(gameId);
        if (SetAside() is not SteamSetAside { Leaf: SteamSetAside.Nvidia } || s.Game.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !VulkanRoute(s.Game, s.Engine)
            || MesaLinux.SteamFolder(s.Game, PlaceSteamRoot) is not var (apps, app))
            return;
        var path = Store.LoadGame(gameId).VulkanCacheEnv?.FirstOrDefault(v => v.Key.Equals("__GL_SHADER_DISK_CACHE_PATH", StringComparison.OrdinalIgnoreCase)).Value;
        if (path == null || !Path.IsPathFullyQualified(path) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != NvidiaFolder(apps, app)) note = VulkanNotColdNote;
    }
}
