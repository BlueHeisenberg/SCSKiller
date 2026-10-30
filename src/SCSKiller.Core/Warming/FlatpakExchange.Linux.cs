using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Warming;

/// <summary>One warm process's folder for a game of the Flatpak Steam (<see cref="ProtonWarmHost"/>): flatpak/&lt;random&gt;/
/// in the data folder, made for that process alone and never used again, and the only folder of Core's the Steam's sandbox
/// is given. The sandbox can change the Proton, runtime and prefix the warm runs on, so what it leaves there is taken as
/// hostile: it may hold handles on the folder after the process, race Core, and put links, FIFOs or devices anywhere in it.
/// Core copies the warm's inputs in (a reflink, else a copy) before the process starts and creates the files it writes
/// itself (O_EXCL, never through a link): out, err, the launcher's log, alive and stop, at the folder's root. It reads what
/// the process left only through handles opened from the folder's own handle down without following a link, a regular file
/// with no other name alone and up to a set length, and copies that into its own folders before anything reads it. The
/// folder is locked while in use, and removed through the same handles once its outputs are out.</summary>
sealed class FlatpakExchange : IDisposable
{
    /// <summary>The data folder's folder of exchanges.</summary>
    public const string Folder = "flatpak";

    public const string Bin = "bin", Work = "work", Cache = "cache", StopName = "stop";
    const string OutName = "warm.out", ErrName = "warm.err", LogName = "launcher.log", AliveName = "alive";

    /// <summary>The warm's outputs in a stage folder that Core reads (<see cref="CopyOutputs"/>).</summary>
    static readonly HashSet<string> OutputNames = new(["scskiller.log", "scskiller_creates.csv", "scskiller_warm_times.csv", Recordings.FailedFile], StringComparer.Ordinal);

    /// <summary>The most one output may hold, and all of a process's together.</summary>
    const long OutputMax = 256L << 20, OutputsMax = 1L << 30;

    /// <summary>The most a set's .toc may hold: its records are a few dozen bytes each.</summary>
    const long TocMax = 256L << 20;

    /// <summary>The most GLCache folders a process's sets come out of besides those Core's sets went into: a process's driver
    /// makes one.</summary>
    const int BucketMax = 4;

    /// <summary>What a copy that isn't a reflink leaves free on the data folder's disk at least.</summary>
    const long FreeReserve = 1L << 30;

    /// <summary>How many names one walk looks at, and how deep it goes.</summary>
    const int EntryMax = 100_000, DepthMax = 16;

    const string CopySuffix = ".scskiller-copy";

    readonly SafeFileHandle _root, _alive;
    readonly Dictionary<string, (long Toc, long Bin)> _setsIn = new(StringComparer.Ordinal);   // "<32 hex>/<16 hex>/<n>" -> Core's set copied in
    readonly Dictionary<string, long> _vkd3dIn = new(StringComparer.OrdinalIgnoreCase);       // vkd3d-proton's files copied in, by name
    readonly Dictionary<string, string> _stages = new(StringComparer.Ordinal);                 // the warm's stage folder -> its copy's name in the work folder
    bool _disposed;

    /// <summary>The folder, as Core and the sandbox both name it.</summary>
    public string Path { get; }

    public string WorkPath => System.IO.Path.Combine(Path, Work);

    public string OutPath => System.IO.Path.Combine(Path, OutName);

    public string ErrPath => System.IO.Path.Combine(Path, ErrName);

    public string StopPath => System.IO.Path.Combine(Path, StopName);

    public string AlivePath => System.IO.Path.Combine(Path, AliveName);

    public string LauncherLogPath => System.IO.Path.Combine(Path, LogName);

    /// <summary>The cache folder the warm's driver writes (<see cref="Wine.CacheEnvironment"/>'s root).</summary>
    public string CachePath => System.IO.Path.Combine(Path, Cache);

    /// <summary>Read handles on the files Core made for the warm to write: its out and err, and the launcher's log.</summary>
    public SafeFileHandle Out { get; }

    public SafeFileHandle Err { get; }

    public SafeFileHandle LauncherLog { get; }

    FlatpakExchange(string path, SafeFileHandle root, SafeFileHandle alive, SafeFileHandle o, SafeFileHandle e, SafeFileHandle log) =>
        (Path, _root, _alive, Out, Err, LauncherLog) = (path, root, alive, o, e, log);

    /// <summary>A name no exchange had: 128 random bits.</summary>
    public static string NewName() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>The path of the exchange <paramref name="name"/> of <paramref name="dataDir"/>.</summary>
    public static string PathOf(string dataDir, string name) => System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDir), Folder, name);

    /// <summary>Makes the exchange <paramref name="name"/> (<see cref="NewName"/>) in <paramref name="dataDir"/>, locked, with
    /// its bin, work and cache folders (nvidia and vkd3d in it), its out, err, launcher log and alive files, and the stop file
    /// too when <paramref name="stopped"/>. Throws when the name is taken or anything is in the way.</summary>
    public static FlatpakExchange Make(string dataDir, string name, bool stopped)
    {
        var path = PathOf(dataDir, name);
        using var parent = Libc.OpenBelow(System.IO.Path.GetFullPath(dataDir), [Folder], make: true);
        if (Libc.Owner(parent)?.Uid != Libc.UserId) throw new IOException($"{System.IO.Path.GetDirectoryName(path)} isn't this user's");
        if (Libc.MakeFolderAt(parent, name) is var made and not 0) throw new IOException($"{path}: {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(made)}");
        var root = Libc.OpenFolderAt(parent, name) ?? throw new IOException($"{path} went missing as it was made");
        var folders = new List<SafeFileHandle>();
        var files = new List<SafeFileHandle>();
        try
        {
            if (!Libc.TryLock(root)) throw new IOException($"{path} is locked");
            foreach (var sub in new[] { Bin, Work, Cache })
                folders.Add(Libc.OpenFolderAt(root, sub, make: true) ?? throw new IOException($"{path}: {sub} can't be made"));
            foreach (var sub in new[] { "nvidia", "vkd3d" })
                folders.Add(Libc.OpenFolderAt(folders[2], sub, make: true) ?? throw new IOException($"{path}: {Cache}/{sub} can't be made"));
            foreach (var file in new[] { AliveName, OutName, ErrName, LogName })
                files.Add(Libc.CreateAt(root, file, Libc.OwnerReadWrite) ?? throw new IOException($"{path}: {file} is there already"));
            if (stopped) Libc.CreateAt(root, StopName, Libc.OwnerReadWrite)?.Dispose();
            return new FlatpakExchange(path, root, files[0], files[1], files[2], files[3]);
        }
        catch
        {
            foreach (var h in files) h.Dispose();
            root.Dispose();
            throw;
        }
        finally
        {
            foreach (var h in folders) h.Dispose();
        }
    }

    /// <summary>Copies Core's file <paramref name="from"/> to <paramref name="to"/> (a path under the exchange, '/' between
    /// folders), making its folders: before the process starts, when nothing else has the folder.</summary>
    public void CopyIn(string from, string to)
    {
        var parts = to.Split('/');
        var dir = _root;
        var opened = new List<SafeFileHandle>();
        try
        {
            foreach (var part in parts[..^1])
            {
                dir = Libc.OpenFolderAt(dir, part, make: true) ?? throw new IOException($"{Path}/{to}: {part} isn't a folder");
                opened.Add(dir);
            }
            using var src = File.OpenHandle(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = Libc.CreateAt(dir, parts[^1], Libc.OwnerReadWrite) ?? throw new IOException($"{Path}/{to} is there already");
            if (Libc.Clone(dst, src) != 0) Copy(src, dst, RandomAccess.GetLength(src), System.IO.Path.GetDirectoryName(Path)!);
        }
        finally
        {
            foreach (var h in opened) h.Dispose();
        }
    }

    /// <summary>Copies Core's folder <paramref name="from"/> to <paramref name="to"/> (as <see cref="CopyIn"/>), its files and
    /// folders, links left out.</summary>
    public void CopyTreeIn(string from, string to)
    {
        var copied = 0;
        foreach (var f in new DirectoryInfo(from).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = DepthMax }))
        {
            if (++copied > EntryMax) throw new IOException($"{from} holds more files than a compile copies");
            CopyIn(f.FullName, $"{to}/{System.IO.Path.GetRelativePath(from, f.FullName)}");
        }
    }

    /// <summary>A new read handle on Core's own file <paramref name="file"/> of the exchange (<see cref="Out"/>, <see cref="Err"/>,
    /// <see cref="LauncherLog"/>), whatever name the sandbox gave it since: /proc's link of an open file names that file.</summary>
    public static SafeFileHandle Reopen(SafeFileHandle file) =>
        File.OpenHandle($"/proc/self/fd/{file.DangerousGetHandle()}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Writes the stop file at the root, never through a link. Anything there but a file or a folder (a link, which
    /// the warm would follow) goes first, and the root gets its 0700 back when the sandbox took Core's access to it. Null once
    /// a stop is there, else why none could be put there.</summary>
    public string? Stop()
    {
        var why = $"{StopName} stayed taken";
        for (var tries = 0; tries < 3; tries++)
        {
            try
            {
                using var made = Libc.CreateAt(_root, StopName, Libc.OwnerReadWrite);
                if (made != null) return null;
            }
            catch (IOException e)
            {
                why = e.Message;
                Libc.Chmod(_root, Libc.ModeOwnerOnly);
                continue;
            }
            if (Libc.EntryAt(_root, StopName) is { Type: 0x8000 or Libc.ModeDirectory }) return null;
            if (Libc.UnlinkAt(_root, StopName, folder: false) is var errno and not (0 or Libc.NoEntry))
            {
                why = $"{StopName}: {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(errno)}";
                if (errno is Libc.AccessDenied or Libc.NotPermitted) Libc.Chmod(_root, Libc.ModeOwnerOnly);
            }
        }
        return why;
    }

    /// <summary>Sets the alive file's time through Core's own handle on it.</summary>
    public void Touch() => Libc.TouchHandle(_alive);

    /// <summary>Copies Core's sets of <paramref name="appName"/> from each folder of <paramref name="glCache"/> (the game's
    /// GLCache) in: the driver gives a process the lowest number no process holds and adds to that set, so the warm's
    /// processes add to the sets they would in Core's folder.</summary>
    public void CopySetsIn(string glCache, string appName)
    {
        if (!Directory.Exists(glCache)) return;
        foreach (var a in Folders(glCache, 32))
            foreach (var b in Folders(a.FullName, 16))
                foreach (var n in b.EnumerateFiles("*.toc", NoLinks).Select(f => Number(f.Name, appName)).OfType<int>().Order())
                {
                    var name = appName + n.ToString(CultureInfo.InvariantCulture);
                    string toc = System.IO.Path.Combine(b.FullName, name + ".toc"), bin = System.IO.Path.ChangeExtension(toc, ".bin");
                    if (!IsFile(toc) || !IsFile(bin)) continue;
                    var to = $"{Cache}/nvidia/GLCache/{a.Name}/{b.Name}/{name}";
                    CopyIn(bin, to + ".bin");
                    CopyIn(toc, to + ".toc");
                    _setsIn[$"{a.Name}/{b.Name}/{name}"] = (new FileInfo(toc).Length, new FileInfo(bin).Length);
                }
    }

    /// <summary>Copies vkd3d-proton's files of exe file <paramref name="exe"/> from Core's folder <paramref name="vkd3d"/> in:
    /// vkd3d-proton reads them at its start and merges its new entries in.</summary>
    public void CopyVkd3dIn(string vkd3d, string exe)
    {
        foreach (var name in Vkd3dNames(exe))
            if (System.IO.Path.Combine(vkd3d, name) is var file && IsFile(file))
            {
                CopyIn(file, $"{Cache}/vkd3d/{name}");
                _vkd3dIn[name] = new FileInfo(file).Length;
            }
    }

    static string[] Vkd3dNames(string exe) => [$"vkd3d-proton.{exe}.cache", $"vkd3d-proton.{exe}.cache.write"];

    /// <summary>The .bin files of the warm's sets of <paramref name="appName"/> in the exchange's NVIDIA folder, by their path,
    /// with their length and inode: regular files alone, the rest isn't the driver's.</summary>
    public Dictionary<string, (long Length, ulong Inode)> Bins(string appName)
    {
        var bins = new Dictionary<string, (long, ulong)>(StringComparer.Ordinal);
        Sets(appName, (dir, a, b, n) =>
        {
            if (Libc.EntryAt(dir, appName + n + ".bin") is { Type: 0x8000 } e) bins[System.IO.Path.Combine(CachePath, "nvidia", "GLCache", a, b, appName + n + ".bin")] = (e.Length, e.Inode);
        });
        return bins;
    }

    /// <summary>The bytes of the regular files in the exchange's NVIDIA folder.</summary>
    public long NvidiaBytes()
    {
        long sum = 0;
        var entries = EntryMax;
        using var nvidia = OpenBelow([Cache, "nvidia"]);
        if (nvidia != null) Walk(nvidia, 0, ref entries, (dir, name) => sum += Libc.EntryAt(dir, name) is { Type: 0x8000 } e ? e.Length : 0);
        return sum;
    }

    /// <summary>The copy in the work folder (<paramref name="workDir"/>) of the warm's stage folder <paramref name="stage"/> (a
    /// path in the exchange's work folder): its own name unless an earlier process's copy took it. Null for any other path.</summary>
    public string? StageCopy(string workDir, string stage)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(stage));
        var dir = System.IO.Path.GetDirectoryName(full);
        if (dir != WorkPath && dir != Libc.RealPath(WorkPath) || System.IO.Path.GetFileName(full) is not { } name || !IsStage(name)) return null;
        return System.IO.Path.Combine(workDir, CopyName(workDir, name));
    }

    string CopyName(string workDir, string stage)
    {
        lock (_stages)
        {
            if (_stages.TryGetValue(stage, out var copy)) return copy;
            copy = stage;
            for (var n = 2; System.IO.Path.Exists(System.IO.Path.Combine(workDir, copy)); n++) copy = $"{stage}-{n.ToString(CultureInfo.InvariantCulture)}";
            return _stages[stage] = copy;
        }
    }

    static bool IsStage(string name) => name.StartsWith("stage-", StringComparison.Ordinal) && name.Length <= 64;

    /// <summary>Copies the warm's outputs (<see cref="OutputNames"/>) out of each stage folder of the exchange's work folder
    /// into its copy in <paramref name="workDir"/> (<see cref="StageCopy"/>), at the same place: regular files with no other
    /// name alone, each up to <see cref="OutputMax"/> and all up to <see cref="OutputsMax"/>. <paramref name="left"/> gets
    /// each one left out.</summary>
    public void CopyOutputs(string workDir, Action<string> left)
    {
        using var work = Libc.OpenFolderAt(_root, Work);
        if (work == null) return;
        var (budget, entries) = (OutputsMax, EntryMax);
        foreach (var name in Names(work, ref entries).Where(IsStage))
        {
            using var stage = Libc.OpenFolderAt(work, name);
            if (stage != null) CopyTree(stage, System.IO.Path.Combine(workDir, CopyName(workDir, name)), 0, ref budget, ref entries, left);
        }
    }

    void CopyTree(SafeFileHandle dir, string to, int depth, ref long budget, ref int entries, Action<string> left)
    {
        foreach (var name in Names(dir, ref entries))
        {
            var e = Libc.EntryAt(dir, name);
            if (e is { Type: Libc.ModeDirectory })
            {
                using var sub = depth < DepthMax ? Libc.OpenFolderAt(dir, name) : null;
                if (sub != null) CopyTree(sub, System.IO.Path.Combine(to, name), depth + 1, ref budget, ref entries, left);
                continue;
            }
            if (!OutputNames.Contains(name)) continue;
            if (Libc.OpenFileAt(dir, name) is not var (file, length))
            {
                left($"{name}: not a regular file with one name, left in the sandbox's folder");
                continue;
            }
            using (file)
            {
                if (length > OutputMax || length > budget)
                {
                    left($"{name}: {Format.Bytes(length)}, more than a compile's log may hold, left in the sandbox's folder");
                    continue;
                }
                Directory.CreateDirectory(to);
                using var dst = new FileStream(System.IO.Path.Combine(to, name), new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.ReadWrite, UnixCreateMode = Libc.OwnerReadWrite });
                dst.SetLength(0);
                budget -= Copy(file, dst.SafeFileHandle, length, to);
            }
        }
    }

    /// <summary>Copies each of the warm's sets of <paramref name="appName"/> the process left in the exchange's NVIDIA folder
    /// into the game's GLCache folder <paramref name="glCache"/>, in the folder named the same: a set Core copied in
    /// (<see cref="CopySetsIn"/>) over Core's own when its lengths changed, any other under the lowest number below
    /// <paramref name="files"/> that Core's folder has free. A .bin up to <paramref name="max"/>, a .toc up to
    /// <see cref="TocMax"/>, both regular files with no other name, from the folders Core's sets went into and
    /// <see cref="BucketMax"/> more, all within <paramref name="budget"/>: what Core's folder may still take. Each set is
    /// counted at its files' length or what they take on disk, the larger, less what the files it replaces hold, before
    /// it's copied. Each goes under a temp name first, then the .bin takes its name before the .toc. <paramref name="left"/>
    /// gets each set left out.</summary>
    public void CopySetsOut(string glCache, string appName, int files, long max, long budget, Action<string> left)
    {
        var sets = new List<(string A, string B, int N)>();
        var buckets = _setsIn.Keys.Select(k => k[..k.LastIndexOf('/')]).ToHashSet(StringComparer.Ordinal);
        var (more, over) = (0, new HashSet<string>(StringComparer.Ordinal));
        Sets(appName, (_, a, b, n) =>
        {
            if (n >= files) return;
            var bucket = $"{a}/{b}";
            if (!buckets.Contains(bucket))
            {
                if (more == BucketMax)
                {
                    over.Add(bucket);
                    return;
                }
                buckets.Add(bucket);
                more++;
            }
            sets.Add((a, b, n));
        });
        if (over.Count > 0) left($"{over.Count} more of NVIDIA's cache folders than a compile makes, left in the sandbox's folder");
        foreach (var (a, b, n) in sets)
        {
            var name = appName + n.ToString(CultureInfo.InvariantCulture);
            using var dir = OpenBelow([Cache, "nvidia", "GLCache", a, b]);
            if (dir == null) continue;
            var bin = Libc.OpenFileAt(dir, name + ".bin");
            var toc = Libc.OpenFileAt(dir, name + ".toc");
            string? into = null;
            long reserved = 0;   // taken off the room left while the copy is in flight, returned if it doesn't land
            try
            {
                if (bin is not var (binFile, binLength) || toc is not var (tocFile, tocLength))
                {
                    left($"{name}: not a pair of regular files with one name, left in the sandbox's folder");
                    continue;
                }
                var copied = _setsIn.TryGetValue($"{a}/{b}/{name}", out var was) ? was : ((long Toc, long Bin)?)null;
                if (copied == (tocLength, binLength)) continue;
                if (binLength > max || tocLength > TocMax)
                {
                    left($"{name}: {Format.Bytes(binLength + tocLength)}, more than a cache file may hold, left in the sandbox's folder");
                    continue;
                }
                var target = System.IO.Path.Combine(glCache, a, b);
                // both temp files are written while the files they replace still exist, so the folder holds their full size
                // on top of the room already used: that full size is reserved now, and the replaced files are credited back
                // only once the rename frees them
                var full = Taken(binFile, binLength) + Taken(tocFile, tocLength);
                if (full > budget)
                {
                    left($"{name}: {Format.Bytes(binLength + tocLength)}, more than NVIDIA's cache folder has room for, left in the sandbox's folder");
                    continue;
                }
                budget -= full;
                reserved = full;
                Directory.CreateDirectory(target);
                foreach (var stale in new DirectoryInfo(target).EnumerateFiles("*" + CopySuffix)) stale.Delete();   // a copy cut short
                int? number = copied != null ? n : Enumerable.Range(0, files).Cast<int?>().FirstOrDefault(m => !Exists(target, appName + m + ".toc") && !Exists(target, appName + m + ".bin"));
                if (number is not { } m)
                {
                    left($"{name}: no free file number in {target}, left in the sandbox's folder");
                    continue;
                }
                into = System.IO.Path.Combine(target, appName + m.ToString(CultureInfo.InvariantCulture));
                var replaced = copied != null ? Length(target, appName + m + ".bin") + Length(target, appName + m + ".toc") : 0;
                foreach (var (file, length, ext) in new[] { (binFile, binLength, ".bin"), (tocFile, tocLength, ".toc") })
                {
                    var temp = into + ext + CopySuffix;
                    using (var dst = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, UnixCreateMode = Libc.OwnerReadWrite }))
                        if (Libc.Clone(dst.SafeFileHandle, file) != 0) Copy(file, dst.SafeFileHandle, length, target);
                        else if (RandomAccess.GetLength(dst.SafeFileHandle) > length) throw new IOException($"{name}{ext} grew while it was copied");
                }
                foreach (var ext in new[] { ".bin", ".toc" })
                    if (copied != null) File.Move(into + ext + CopySuffix, into + ext, overwrite: true);
                    else if (Libc.RenameNew(into + ext + CopySuffix, into + ext) is var errno and not 0)
                        throw new IOException($"naming {System.IO.Path.GetFileName(into + ext)} failed: {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(errno)}");
                budget += replaced;   // the replace freed them: only the net stays reserved
                reserved = 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                budget += reserved;   // nothing landed
                left($"{name}: copying it out failed ({e.Message}), left in the sandbox's folder");
                if (into != null)
                    foreach (var ext in new[] { ".bin", ".toc" }) File.Delete(into + ext + CopySuffix);
            }
            finally
            {
                bin?.File.Dispose();
                toc?.File.Dispose();
            }
        }
    }

    /// <summary>Copies vkd3d-proton's files of exe file <paramref name="exe"/> the process left in the exchange back over Core's
    /// in <paramref name="vkd3d"/> when their lengths changed, up to <paramref name="max"/> each, and removes Core's of one it
    /// copied in that vkd3d-proton removed (its merge). <paramref name="left"/> gets each one left out.</summary>
    public void CopyVkd3dOut(string vkd3d, string exe, long max, Action<string> left)
    {
        using var dir = OpenBelow([Cache, "vkd3d"]);
        if (dir == null) return;
        foreach (var name in Vkd3dNames(exe))
        {
            var into = System.IO.Path.Combine(vkd3d, name);
            var temp = into + CopySuffix;
            try
            {
                if (Libc.EntryAt(dir, name) == null)
                {
                    if (_vkd3dIn.ContainsKey(name) && IsFile(into)) File.Delete(into);
                    continue;
                }
                if (Libc.OpenFileAt(dir, name) is not var (file, length))
                {
                    left($"{name}: not a regular file with one name, left in the sandbox's folder");
                    continue;
                }
                using (file)
                {
                    if (_vkd3dIn.TryGetValue(name, out var was) && was == length) continue;
                    if (length > max)
                    {
                        left($"{name}: {Format.Bytes(length)}, more than vkd3d-proton's cache may hold, left in the sandbox's folder");
                        continue;
                    }
                    Directory.CreateDirectory(vkd3d);
                    File.Delete(temp);
                    using (var dst = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, UnixCreateMode = Libc.OwnerReadWrite }))
                        if (Libc.Clone(dst.SafeFileHandle, file) != 0) Copy(file, dst.SafeFileHandle, length, vkd3d);
                        else if (RandomAccess.GetLength(dst.SafeFileHandle) > max) throw new IOException($"{name} grew past what vkd3d-proton's cache may hold");
                    File.Move(temp, into, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left($"{name}: copying it out failed ({e.Message}), left in the sandbox's folder");
                File.Delete(temp);
            }
        }
    }

    /// <summary>Each of the warm's sets of <paramref name="appName"/> in the exchange's GLCache folder, by its folder's handle,
    /// the folders' names and its number, as <see cref="NvidiaLinuxAppCache"/> lays them out.</summary>
    void Sets(string appName, Action<SafeFileHandle, string, string, int> set)
    {
        var entries = EntryMax;
        using var root = OpenBelow([Cache, "nvidia", "GLCache"]);
        if (root == null) return;
        foreach (var a in Names(root, ref entries).Where(n => IsHex(n, 32)))
        {
            using var da = Libc.OpenFolderAt(root, a);
            if (da == null) continue;
            foreach (var b in Names(da, ref entries).Where(n => IsHex(n, 16)))
            {
                using var db = Libc.OpenFolderAt(da, b);
                if (db == null) continue;
                foreach (var n in Names(db, ref entries).Select(f => Number(f, appName)).OfType<int>().Distinct().Order()) set(db, a, b, n);
            }
        }
    }

    /// <summary>The set number of a file named &lt;app name&gt;&lt;n&gt;.toc or .bin, as <see cref="GlCacheNoDict"/> reads it; null for any other.</summary>
    static int? Number(string file, string appName)
    {
        if (!file.StartsWith(appName, StringComparison.Ordinal) || !(file.EndsWith(".toc", StringComparison.Ordinal) || file.EndsWith(".bin", StringComparison.Ordinal))) return null;
        var digits = file[appName.Length..^4];
        return digits.Length is > 0 and <= 4 && digits.All(char.IsAsciiDigit) && (digits.Length == 1 || digits[0] != '0') ? int.Parse(digits, CultureInfo.InvariantCulture) : null;
    }

    static bool IsHex(string name, int length) => name.Length == length && name.All(char.IsAsciiHexDigitLower);

    /// <summary>The folder <paramref name="below"/> names under the exchange's root, opened without a link; null when it isn't one.</summary>
    SafeFileHandle? OpenBelow(string[] below)
    {
        var dir = _root;
        SafeFileHandle? opened = null;
        foreach (var name in below)
        {
            var next = Libc.OpenFolderAt(dir, name);
            opened?.Dispose();
            if (next == null) return null;
            dir = opened = next;
        }
        return opened;
    }

    /// <summary>The names in the open folder, at most <paramref name="entries"/> more of the walk's.</summary>
    static List<string> Names(SafeFileHandle dir, ref int entries)
    {
        if (entries <= 0) return [];
        var names = Directory.EnumerateFileSystemEntries($"/proc/self/fd/{dir.DangerousGetHandle()}").Select(System.IO.Path.GetFileName).OfType<string>().Take(entries).ToList();
        entries -= names.Count;
        return names;
    }

    /// <summary>Calls <paramref name="entry"/> on every name below the open folder that isn't a folder, folders opened without a link.</summary>
    static void Walk(SafeFileHandle dir, int depth, ref int entries, Action<SafeFileHandle, string> entry)
    {
        foreach (var name in Names(dir, ref entries))
        {
            if (Libc.EntryAt(dir, name) is not { Type: Libc.ModeDirectory })
            {
                entry(dir, name);
                continue;
            }
            using var sub = depth < DepthMax ? Libc.OpenFolderAt(dir, name) : null;
            if (sub != null) Walk(sub, depth + 1, ref entries, entry);
        }
    }

    /// <summary>Copies up to <paramref name="length"/> bytes of <paramref name="from"/> into <paramref name="to"/>, checking
    /// before that the disk of <paramref name="near"/> keeps <see cref="FreeReserve"/> free. Returns the bytes copied.</summary>
    static long Copy(SafeFileHandle from, SafeFileHandle to, long length, string near)
    {
        if (new DriveInfo(near).AvailableFreeSpace - length < FreeReserve) throw new IOException($"the disk of {near} has no room for {Format.Bytes(length)} more");
        var buffer = new byte[1 << 20];
        long at = 0;
        for (int n; at < length && (n = RandomAccess.Read(from, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - at)), at)) > 0; at += n)
            RandomAccess.Write(to, buffer.AsSpan(0, n), at);
        return at;
    }

    static IEnumerable<DirectoryInfo> Folders(string path, int hex) =>
        new DirectoryInfo(path).EnumerateDirectories("*", NoLinks).Where(d => IsHex(d.Name, hex));

    static bool IsFile(string path) => new FileInfo(path) is { Exists: true } f && !f.Attributes.HasFlag(FileAttributes.ReparsePoint);

    static bool Exists(string dir, string name) => Libc.Lstat(System.IO.Path.Combine(dir, name)) != null;

    /// <summary>What the file opened to be copied takes at most once copied: its length, or the disk it takes when more.</summary>
    static long Taken(SafeFileHandle file, long length) => Math.Max(length, Libc.Allocated(file) ?? length);

    /// <summary>The length of Core's own file <paramref name="name"/> in <paramref name="dir"/>; 0 when there's none.</summary>
    static long Length(string dir, string name) => new FileInfo(System.IO.Path.Combine(dir, name)) is { Exists: true } f ? f.Length : 0;

    static readonly EnumerationOptions NoLinks = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Removes the exchange, through its own handles: nothing below it is followed, whatever the sandbox put there.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var h in new[] { _alive, Out, Err, LauncherLog }) h.Dispose();
        try
        {
            var entries = EntryMax;
            Libc.Chmod(_root, Libc.ModeOwnerOnly);
            Remove(_root, 0, ref entries);
            using var parent = Libc.OpenBelow(System.IO.Path.GetDirectoryName(Path)!, [], make: false);
            Libc.UnlinkAt(parent, System.IO.Path.GetFileName(Path), folder: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // what's left goes with the next sweep
        finally { _root.Dispose(); }
    }

    /// <summary>Removes what the open folder holds, each folder below given its 0700 back first: the sandbox may have taken
    /// Core's access to it.</summary>
    static void Remove(SafeFileHandle dir, int depth, ref int entries)
    {
        foreach (var name in Names(dir, ref entries))
        {
            if (Libc.EntryAt(dir, name) is { Type: Libc.ModeDirectory } && depth < DepthMax)
            {
                Libc.ChmodFolderAt(dir, name, Libc.ModeOwnerOnly);
                using (var sub = Libc.OpenFolderAt(dir, name))
                    if (sub != null) Remove(sub, depth + 1, ref entries);
                Libc.UnlinkAt(dir, name, folder: true);
            }
            else Libc.UnlinkAt(dir, name, folder: false);
        }
    }

    /// <summary>Removes the exchanges of <paramref name="dataDir"/> that no Core holds (a Core that exited left them), made
    /// over an hour ago: a lock taken means none holds it.</summary>
    public static void Sweep(string dataDir, Action<string>? log)
    {
        SafeFileHandle parent;
        try { parent = Libc.OpenBelow(System.IO.Path.GetFullPath(dataDir), [Folder], make: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        using (parent)
        {
            var entries = EntryMax;
            foreach (var name in Names(parent, ref entries))
            {
                var path = System.IO.Path.Combine(dataDir, Folder, name);
                if (Libc.StatNoFollow(path) is not { } st || DateTimeOffset.FromUnixTimeSeconds(st.ChangedSeconds) > DateTimeOffset.UtcNow.AddHours(-1)) continue;
                Libc.ChmodFolderAt(parent, name, Libc.ModeOwnerOnly);
                using var dir = Libc.OpenFolderAt(parent, name);
                if (dir == null)
                {
                    Libc.UnlinkAt(parent, name, folder: false);
                    continue;
                }
                if (!Libc.TryLock(dir)) continue;
                var budget = EntryMax;
                Remove(dir, 0, ref budget);
                if (Libc.UnlinkAt(parent, name, folder: true) == 0) log?.Invoke($"removed {path}, a compile's folder an SCSKiller that has exited left");
            }
        }
    }
}

/// <summary>The sandbox a Flatpak Steam game's warm process runs in, told from this process's /proc, which the sandbox
/// can't change: the pid namespace of the child of the launcher Core started that runs in another (bwrap's), which every
/// process of the sandbox and of the subsandboxes flatpak-portal starts for it with its pids is in. A process in any other
/// is never the warm's, whatever its environment, name or image. A pidfd on that child keeps the namespace, and so its
/// number, from going to another while this lives.</summary>
sealed class FlatpakSandbox : IDisposable
{
    readonly int _launcher;
    readonly SafeFileHandle? _launcherFd;
    readonly ulong? _outer = ProcessTree.PidNamespace(Environment.ProcessId);
    readonly Lock _gate = new();
    (SafeFileHandle Fd, ulong Ns)? _init;
    long _looked;   // when the sandbox was last looked for, in ticks
    bool _disposed;

    /// <param name="launcher">the launcher, just started: its pidfd is had while its pid can't be another's (Core hasn't
    /// reaped it)</param>
    public FlatpakSandbox(Process launcher)
    {
        _launcher = launcher.Id;
        var fd = Libc.PidFd(_launcher, out var error);
        if (fd != null && launcher.HasExited)
        {
            fd.Dispose();
            (fd, error) = (null, Libc.NoSuchProcess);
        }
        (_launcherFd, Error) = (fd, error);
    }

    /// <summary>The errno pidfd_open failed with on the launcher, 0 when <see cref="Ready"/>.</summary>
    public int Error { get; }

    /// <summary>The launcher's pidfd was had: without it the sandbox can't be told from the host or ended, so the launch is
    /// refused rather than run one that can't be stopped.</summary>
    public bool Ready => _launcherFd != null;

    /// <summary>The sandbox is fully gone: the launcher has exited and the pid namespace's first process (whose death ends the
    /// namespace) is gone, or that process never came up. Its handles stay meaningful until then, past any faked exit.</summary>
    public bool Gone
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return true;
                var running = _launcherFd != null && Libc.Signal(_launcherFd, 0);
                _init ??= Find();   // while the launcher runs: its child is found only through it
                return !running && (_init is not { } i || !Libc.Signal(i.Fd, 0));
            }
        }
    }

    /// <summary>The host's pid namespace, the launcher's: flatpak's own bwrap and xdg-dbus-proxy run in it.</summary>
    public ulong? Outer => _outer;

    /// <summary>The sandbox's pid namespace, once its first process runs; looked for at most every 100 ms till then.</summary>
    public ulong? Namespace
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return null;
                if (_init is { } i) return i.Ns;
                if (Environment.TickCount64 - _looked < 100) return null;
                _looked = Environment.TickCount64;
                return (_init = Find())?.Ns;
            }
        }
    }

    /// <summary>The process is in the sandbox.</summary>
    public bool Holds(int pid) => Namespace is { } ns && ProcessTree.PidNamespace(pid) == ns;

    /// <summary>The launcher's child in another pid namespace, through a pidfd had before it's checked again; the launcher
    /// still unreaped after, so its pid was its own.</summary>
    (SafeFileHandle, ulong)? Find()
    {
        if (_launcherFd == null || _outer is not { } outer) return null;
        foreach (var pid in ProcessTree.Pids())
        {
            if (ProcessTree.Parent(pid) != _launcher || ProcessTree.PidNamespace(pid) is not { } ns || ns == outer) continue;
            var fd = Libc.PidFd(pid, out _);
            if (fd == null) continue;
            if (ProcessTree.Parent(pid) == _launcher && ProcessTree.PidNamespace(pid) == ns && Libc.Signal(_launcherFd, 0)) return (fd, ns);
            fd.Dispose();
        }
        return null;
    }

    /// <summary>Ends the sandbox, through pidfds alone: the launcher stopped first so it starts nothing more, then its child
    /// in the sandbox's namespace, whose end ends every process in it, whether or not the launcher still runs, then the
    /// launcher. False when no kill landed.</summary>
    public bool End()
    {
        lock (_gate)
        {
            if (_disposed || _launcherFd == null) return false;
            Libc.Signal(_launcherFd, Libc.SigStop);
            _init ??= Find();
            var landed = _init is { } i && Libc.Signal(i.Fd, Libc.SigKill);
            return Libc.Signal(_launcherFd, Libc.SigKill) | landed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _init?.Fd.Dispose();
            _launcherFd?.Dispose();
        }
    }
}

/// <summary>Lowers the priority of the systemd scopes a Flatpak Steam game's warm runs in: flatpak-portal starts the game's
/// processes in a scope of their own, which the launcher's nice and ionice never reach (measured). As
/// <see cref="ProtonLaunch.Command"/>'s scope: CPUWeight=idle (1 before systemd 251) and IOWeight=1 for idle, CPUWeight=10 for
/// below normal, set at runtime through <paramref name="systemctl"/> on each scope once, and never one that would raise a
/// weight. Only a scope of the Flatpak Steam's (app-flatpak-com.valvesoftware.Steam-&lt;n&gt;.scope) that holds a process
/// of <paramref name="sandbox"/>'s and every one of whose host-namespace processes is tied to this launch
/// (<see cref="Stranger"/>, <see cref="FlatpakSandbox"/>), up to <see cref="ScopeMax"/> scopes.</summary>
sealed class FlatpakScopes(string systemctl, int systemdVersion, WarmPriority priority, FlatpakSandbox sandbox, Action<string>? log)
{
    const int Tries = 40, ScopeMax = 8;
    readonly Dictionary<string, int> _scopes = new(StringComparer.Ordinal);   // cgroup -> looks left, 0 once done
    readonly HashSet<string> _over = new(StringComparer.Ordinal);             // scopes past the cap, left as they are

    /// <summary>Lowers the scopes of <paramref name="members"/> not done yet.</summary>
    public void Lower(IEnumerable<int> members)
    {
        foreach (var pid in members)
        {
            if (Of(pid) is not { } cgroup || _over.Contains(cgroup)) continue;
            if (!_scopes.TryGetValue(cgroup, out var left))
            {
                if (_scopes.Count >= ScopeMax)
                {
                    // a rejected scope isn't counted, so one past the cap can't let the next one through
                    if (_over.Add(cgroup)) log?.Invoke($"the compile's priority wasn't lowered in its scope {Path.GetFileName(cgroup)}: its sandbox has more than {ScopeMax} scopes");
                    continue;
                }
                left = Tries;
            }
            if (left == 0) continue;
            var unit = Path.GetFileName(cgroup);
            if (Stranger(cgroup) is { } other)
            {
                // a subsandbox's processes join the sandbox's namespace as they start: looked at again
                if ((_scopes[cgroup] = left - 1) == 0) log?.Invoke($"the compile's priority wasn't lowered: its scope {unit} holds {other}");
                continue;
            }
            _scopes[cgroup] = 0;
            if (Set(unit, cgroup) is { } why) log?.Invoke($"the compile's priority wasn't lowered: {why}");
        }
    }

    /// <summary>The cgroup of the Flatpak Steam's scope the process is in; null for any other.</summary>
    internal static string? Of(int pid)
    {
        try
        {
            var line = File.ReadLines($"/proc/{pid}/cgroup").FirstOrDefault(l => l.StartsWith("0::/", StringComparison.Ordinal));
            return line != null && IsScope(Path.GetFileName(line[3..])) ? line[3..] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool IsScope(string unit) =>
        unit.StartsWith("app-flatpak-" + ProtonLaunch.FlatpakApp + "-", StringComparison.Ordinal) && unit.EndsWith(".scope", StringComparison.Ordinal)
        && unit[("app-flatpak-" + ProtonLaunch.FlatpakApp + "-").Length..^".scope".Length] is { Length: > 0 and <= 20 } n && n.All(char.IsAsciiDigit);

    /// <summary>Why the cgroup isn't this launch's sandbox: a process in a pid namespace but the sandbox's and the host's, a
    /// host-namespace process not tied to this launch, or none in the sandbox's; null when every process is tied. The
    /// host-namespace plumbing flatpak keeps in the warm's scope (the sandbox's bwrap monitor and its D-Bus proxy) is tied
    /// by the host's /proc, which the sandbox can't forge: its subtree reaches a process of the sandbox namespace, or shares
    /// an open pipe or socket with one. An unrelated host process, tied to neither, keeps the scope from being lowered.</summary>
    string? Stranger(string cgroup)
    {
        if (sandbox.Namespace is not { } ns || sandbox.Outer is not { } outer) return "a sandbox that isn't known yet";
        List<int> scope;
        try { scope = [.. File.ReadLines($"/sys/fs/cgroup{cgroup}/cgroup.procs").Select(l => int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : -1).Where(p => p > 0)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"processes that can't be read ({e.Message})"; }
        var (parent, pidns) = (new Dictionary<int, int>(), new Dictionary<int, ulong?>());
        var nPipes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in ProcessTree.Pids())
        {
            if (ProcessTree.Parent(p) is { } pp) parent[p] = pp;
            var their = ProcessTree.PidNamespace(p);
            pidns[p] = their;
            if (their == ns) nPipes.UnionWith(Pipes(p));
        }
        var children = new Dictionary<int, List<int>>();
        foreach (var (c, pp) in parent) (children.TryGetValue(pp, out var l) ? l : children[pp] = []).Add(c);
        // the process, or a descendant of it in the host's /proc, is in the sandbox namespace or shares a pipe or socket with it
        bool Tied(int root)
        {
            var (seen, stack) = (new HashSet<int>(), new Stack<int>([root]));
            while (stack.TryPop(out var p))
            {
                if (!seen.Add(p)) continue;
                if (pidns.GetValueOrDefault(p) == ns || Pipes(p).Overlaps(nPipes)) return true;
                if (children.TryGetValue(p, out var cs)) foreach (var c in cs) stack.Push(c);
            }
            return false;
        }
        var ours = false;
        foreach (var pid in scope)
        {
            var their = pidns.TryGetValue(pid, out var v) ? v : ProcessTree.PidNamespace(pid);
            if (their == ns) ours = true;
            else if (their == outer) { if (!Tied(pid)) return $"a process not of this compile (pid {pid})"; }
            else if (their != null || ProcessTree.Lives(pid)) return $"another sandbox's process (pid {pid})";
        }
        return ours ? null : "no process of the compile's sandbox";
    }

    /// <summary>The pipe and socket endpoints a process has open (pipe:[inode], socket:[inode]); empty when they can't be read.</summary>
    static HashSet<string> Pipes(int pid)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
                if (new FileInfo(e).LinkTarget is { } t && (t.StartsWith("pipe:", StringComparison.Ordinal) || t.StartsWith("socket:", StringComparison.Ordinal))) set.Add(t);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return set;
    }

    string? Set(string unit, string cgroup)
    {
        var dir = $"/sys/fs/cgroup{cgroup}";
        var idle = Read(dir, "cpu.idle") == "1";
        var cpu = Weight(Read(dir, "cpu.weight"));
        var io = Weight(Read(dir, "io.weight")?.Split('\n')[0] is ['d', 'e', 'f', 'a', 'u', 'l', 't', ' ', .. var w] ? w : null);
        // a weight the scope has at or below the one set stays: a sandbox's scope is never raised
        var props = new List<string>();
        if (priority == WarmPriority.Idle)
        {
            if (!idle && systemdVersion >= 251) props.Add("CPUWeight=idle");
            else if (!idle && cpu > 1) props.Add("CPUWeight=1");
            if (io > 1) props.Add("IOWeight=1");
        }
        else if (!idle && cpu > 10) props.Add("CPUWeight=10");
        if (props.Count == 0) return null;
        try
        {
            var psi = new ProcessStartInfo(systemctl) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--user", "set-property", "--runtime", unit }.Concat(props)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "systemctl didn't start";
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                p.Kill();
                return "systemctl took too long";
            }
            return p.ExitCode == 0 ? null : $"systemctl failed ({err.Result.Trim()})";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return e.Message; }
    }

    /// <summary>The cgroup's file <paramref name="name"/>, trimmed; null when it has none (its controller is off).</summary>
    static string? Read(string dir, string name)
    {
        try { return File.ReadAllText(Path.Combine(dir, name)).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A weight cgroup's file gives; the default (100) when there's none.</summary>
    static int Weight(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : 100;
}
