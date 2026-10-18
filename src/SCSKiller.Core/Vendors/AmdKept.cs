using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Vendors;

/// <summary>One game's compiled AMD DirectX 12 cache files kept aside as hard links in <paramref name="keptDir"/>, so the data
/// outlives the driver's eviction past its cap (it deletes only its own name) and goes back as a link before the game runs.
/// A link is one file with two names: the driver keeps writing into the kept copy while both exist, and a put-back file
/// keeps its file id, creation time and size. A kept name counts only while it is the file this class linked (its id is in
/// <see cref="State.Links"/>): only those are ever deleted or put back. Nothing in DxcCache is ever deleted or overwritten.</summary>
public sealed class AmdKept(string dxcDir, string keptDir)
{
    public const string StateFile = "kept.json";

    /// <summary>The last put-back: when, whether the game had already started, the files no link could be made for, the
    /// driver the files were kept under, and the file id of each link made (name: id). Dropping: a drop left links that
    /// were in use; they are neither kept nor put back, and the next keep or drop deletes them first.</summary>
    public sealed record State(DateTimeOffset? PutBackAt = null, bool Late = false, int Unkept = 0, string? HeldBy = null, string? Driver = null,
        Dictionary<string, string>? Links = null, bool Dropping = false);

    public string Dir => keptDir;

    /// <summary>Links can be made: DxcCache is a real folder (not moved through a junction) on the same volume as
    /// <paramref name="keptRoot"/>, both by their final paths.</summary>
    public static bool Usable(string dxcDir, string keptRoot)
    {
        if (!Directory.Exists(dxcDir) || Redirected(dxcDir) || Games.GameFiles.FinalPath(dxcDir) is not { } dxc || Redirected(keptRoot)) return false;
        Directory.CreateDirectory(keptRoot);
        return Games.GameFiles.FinalPath(keptRoot) is { } kept && string.Equals(Path.GetPathRoot(dxc), Path.GetPathRoot(kept), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Links each of <paramref name="names"/> (DxcCache file names) whose key is one of <paramref name="keys"/> (the
    /// game's own) and that is there. A kept link whose DxcCache file is another file now (made again) is replaced by it.
    /// Links of other keys go. A file no link can be made for stays only in DxcCache, counted with whoever holds it open.</summary>
    public State Keep(IEnumerable<string> names, IReadOnlySet<string> keys, string? driver)
    {
        var was = Read();
        if (was.Dropping && !Drop()) return was;
        if (was.Dropping) was = new();
        if (Redirected(keptDir)) throw new IOException($"{keptDir} is reached through a link");
        Directory.CreateDirectory(keptDir);
        var links = Mine(was);
        int unkept = 0;
        string? heldBy = null;
        foreach (var name in links.Keys.Where(n => !keys.Contains(AmdAppCache.Key(n, true)!)).ToList())
            if (TryDelete(Path.Combine(keptDir, name))) links.Remove(name);
            else
            {
                unkept++;
                heldBy ??= Holder(Path.Combine(keptDir, name));
            }
        foreach (var name in names.Where(n => Named(n) && keys.Contains(AmdAppCache.Key(n, true)!)))
        {
            string from = Path.Combine(dxcDir, name), to = Path.Combine(keptDir, name);
            if (Id(from) is not { } now)
            {
                if (File.Exists(from)) unkept++;   // there, but its id can't be read
                continue;
            }
            var there = Id(to);
            if (there != null && !links.ContainsKey(name) || there == null && File.Exists(to)) { unkept++; continue; }   // not a link this class made: left alone
            if (there == now) continue;
            if ((there == null || TryDelete(to)) && CreateHardLinkW(to, from, 0)) { links[name] = now; continue; }
            var stuck = there != null && File.Exists(to);   // the old link, open without delete sharing
            if (there != null && !stuck) links.Remove(name);
            unkept++;
            heldBy ??= Holder(stuck ? to : from);
        }
        foreach (var (name, id) in was.Links ?? [])   // a recorded link whose id can't be read now stays on record, untouched
            if (!links.ContainsKey(name) && Named(name) && File.Exists(Path.Combine(keptDir, name)) && Id(Path.Combine(keptDir, name)) == null) links[name] = id;
        var state = was with { Unkept = unkept, HeldBy = heldBy, Driver = driver ?? was.Driver, Links = links };
        Write(state);
        return state;
    }

    /// <summary>Links every kept file missing from DxcCache back under its name (a name taken meanwhile stays as it is) and
    /// marks all of them just used, so the driver's next trim takes other files first. <paramref name="ours"/>: the keys
    /// still this game's alone. Returns how many went back; the state notes it when any did.</summary>
    public int PutBack(Func<string, bool> ours, bool late, DateTimeOffset now)
    {
        var back = 0;
        foreach (var name in Mine(Read()).Keys)
        {
            if (!ours(AmdAppCache.Key(name, true)!)) continue;
            string kept = Path.Combine(keptDir, name), there = Path.Combine(dxcDir, name);
            if (!File.Exists(there) && CreateHardLinkW(there, kept, 0)) back++;   // fails on a name that exists: never overwrites
            Touch(kept, now);
        }
        if (back > 0) Write(Read() with { PutBackAt = now, Late = late });
        return back;
    }

    /// <summary>Bytes of the kept files, and of those that only the kept copy holds (not the same file in DxcCache).</summary>
    public (long Bytes, long Frees) Size()
    {
        long bytes = 0, frees = 0;
        foreach (var (name, id) in Mine(Read()))
        {
            var kept = new FileInfo(Path.Combine(keptDir, name));
            if (!kept.Exists) continue;
            bytes += kept.Length;
            if (Id(Path.Combine(dxcDir, name)) != id) frees += kept.Length;
        }
        return (bytes, frees);
    }

    /// <summary>The kept link of <paramref name="name"/>'s file id; null when none is kept.</summary>
    public string? KeptId(string name) => Mine(Read()).GetValueOrDefault(name);

    public bool Any => Mine(Read()).Count > 0;

    public State Read()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>Deletes the kept links and the state, then the folder if nothing else is in it. DxcCache isn't touched.
    /// False when a link couldn't be deleted (open without delete sharing): it stays on record for the next drop.</summary>
    public bool Drop()
    {
        if (!Directory.Exists(keptDir) || Redirected(keptDir)) return true;
        var was = Read();
        foreach (var name in Owned(was).Keys) TryDelete(Path.Combine(keptDir, name));
        // a recorded name still there (in use, or its id unreadable) stays on record
        var left = (was.Links ?? []).Where(l => Named(l.Key) && File.Exists(Path.Combine(keptDir, l.Key))).ToDictionary(StringComparer.OrdinalIgnoreCase);
        if (left.Count == 0) TryDelete(StatePath);
        else Write(was with { Links = left, Dropping = true });
        try { Directory.Delete(keptDir); }
        catch (IOException) { }   // not empty: a file that isn't ours stays
        return left.Count == 0;
    }

    string StatePath => Path.Combine(keptDir, StateFile);

    /// <summary>A plain DxcCache file name of a D3D12 key: never a path, whatever the state file says.</summary>
    static bool Named(string name) => name == Path.GetFileName(name) && name.EndsWith(".parc", StringComparison.OrdinalIgnoreCase) && AmdAppCache.Key(name, true) != null;

    /// <summary>The recorded links that are still the file linked, unless a drop is pending.</summary>
    Dictionary<string, string> Mine(State s) => s.Dropping ? new(StringComparer.OrdinalIgnoreCase) : Owned(s);

    /// <summary>The recorded links that are still the file linked; none through a kept folder reached through a link.</summary>
    Dictionary<string, string> Owned(State s) => !Directory.Exists(keptDir) || Redirected(keptDir) || s.Links == null ? new(StringComparer.OrdinalIgnoreCase)
        : s.Links.Where(l => Named(l.Key) && Id(Path.Combine(keptDir, l.Key)) == l.Value).ToDictionary(StringComparer.OrdinalIgnoreCase);

    void Write(State s)
    {
        var tmp = StatePath + ".tmp";
        File.Delete(tmp);   // a name, never written through: it could be another file's link
        using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(f, s);
        File.Move(tmp, StatePath, overwrite: true);
    }

    static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The folder or one of its parents is a junction or symbolic link.</summary>
    static bool Redirected(string dir)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d != null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    /// <summary>The volume and file id (the same through every hard link); null when it can't be opened.</summary>
    public static string? Id(string path) => Games.GameFiles.Identity(path) is { Id: { } id } ? Convert.ToHexString(id) : null;

    static string? Holder(string path)
    {
        var pids = AppCacheFiles.ProcessesUsing(path);
        var exe = Warming.ProcessTree.Snapshot().FirstOrDefault(p => pids.Contains((nuint)p.Pid)).Exe;
        return exe == null ? null : exe.Equals("AMDRSSrcExt.exe", StringComparison.OrdinalIgnoreCase) ? "the Radeon overlay" : exe;
    }

    /// <summary>Last access only, through an attribute handle: no content is read and no other time changes.</summary>
    static void Touch(string path, DateTimeOffset now)
    {
        using var h = CreateFileW(path, 0x100 /* FILE_WRITE_ATTRIBUTES */, 7, 0, 3 /* OPEN_EXISTING */, 0, 0);
        if (h.IsInvalid) return;
        var at = now.UtcDateTime.ToFileTimeUtc();
        SetFileTime(h, 0, ref at, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateHardLinkW(string newName, string existing, nint security);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileTime(SafeFileHandle h, nint creation, ref long lastAccess, nint lastWrite);
}
