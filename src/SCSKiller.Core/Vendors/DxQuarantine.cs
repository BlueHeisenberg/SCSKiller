using System.Runtime.InteropServices;
using System.Text.Json;

namespace SCSKiller.Core.Vendors;

/// <summary>AMD's DirectX 11 cache files set aside for a before run whose game's DxCache key isn't known: the driver doesn't
/// always hold that file open, so no clear or check can find the game's old cache among the others. Every file of a key
/// not kept is renamed into a folder beside DxCache (same volume: a rename, never a copy) and, once the run is over, put
/// back, except the files of the key the game made during the run, which are its old cache. A journal of the moves is
/// written before the first one, so a run cut off by a crash is put back at the next start.</summary>
public sealed class DxQuarantine(string dxDir, string journalPath)
{
    public sealed record Move(string From, string To);
    public sealed record Journal(string GameId, List<Move> Moves, List<string> Skipped);

    public string Folder => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dxDir))!, Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir)) + ".scskiller-quarantine");

    public Journal? Pending
    {
        get
        {
            try { return File.Exists(journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath)) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    /// <summary>Sets aside every DxCache file whose key isn't <paramref name="keep"/>'s, for <paramref name="gameId"/>'s run. A
    /// file a process holds open, or that doesn't rename, stays and is listed as skipped. Returns the journal, written even
    /// with nothing moved: the run's end learns the key the game made from it.</summary>
    public Journal SetAside(string gameId, Func<string, bool> keep)
    {
        var plan = new List<Move>();
        var skipped = new List<string>();
        if (Directory.Exists(dxDir))
            foreach (var f in Directory.EnumerateFiles(dxDir, "*.parc"))
            {
                if (AmdAppCache.Key(Path.GetFileName(f), d3d12: false) is not { } key || keep(key)) continue;
                if (AppCacheFiles.ProcessesUsing(f).Count > 0) skipped.Add(f);
                else plan.Add(new(f, Path.Combine(Folder, Path.GetFileName(f))));
            }
        if (plan.Count == 0)
        {
            var none = new Journal(gameId, [], skipped);
            Write(none);
            return none;
        }
        Directory.CreateDirectory(Folder);
        Write(new(gameId, plan, skipped));   // before the first move
        var moved = new List<Move>();
        foreach (var m in plan)
            if (Rename(m.From, m.To)) moved.Add(m);
            else skipped.Add(m.From);
        var j = new Journal(gameId, moved, skipped);
        Write(j);
        return j;
    }

    /// <summary>Puts the set-aside files back, except <paramref name="old"/>'s (the game's old cache), which are deleted. A
    /// file made again at its place meanwhile: the larger stays, it holds more (the set-aside one waits while the other is
    /// in use). Returns the files that couldn't be put back; the journal keeps them, and those waiting, for the next try.</summary>
    public List<string> Restore(string? old = null)
    {
        if (Pending is not { } j) return [];
        var left = new List<Move>();
        var waiting = new List<Move>();
        foreach (var m in j.Moves)
        {
            try
            {
                if (!File.Exists(m.To)) continue;
                if (old != null && string.Equals(AmdAppCache.Key(Path.GetFileName(m.To), d3d12: false), old, StringComparison.OrdinalIgnoreCase)) { File.Delete(m.To); continue; }
                if (File.Exists(m.From))
                {
                    if (new FileInfo(m.From).Length >= new FileInfo(m.To).Length) { File.Delete(m.To); continue; }
                    if (AppCacheFiles.ProcessesUsing(m.From).Count > 0) { waiting.Add(m); continue; }
                    File.Delete(m.From);
                }
                if (!Rename(m.To, m.From)) left.Add(m);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(m); }
        }
        if (left.Count + waiting.Count > 0) { Write(j with { Moves = [.. left, .. waiting] }); return left.Select(m => m.To).ToList(); }
        File.Delete(journalPath);
        try { Directory.Delete(Folder); }
        catch (IOException) { }   // not empty: something else put a file there
        return [];
    }

    void Write(Journal j)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var tmp = journalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(j));
        File.Move(tmp, journalPath, overwrite: true);
    }

    /// <summary>A rename only: MoveFileEx without MOVEFILE_COPY_ALLOWED fails across volumes instead of copying.</summary>
    static bool Rename(string from, string to) => MoveFileExW(from, to, 0);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool MoveFileExW(string existing, string newName, uint flags);
}
