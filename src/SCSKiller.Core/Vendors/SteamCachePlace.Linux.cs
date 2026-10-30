using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using FileId = (uint DevMajor, uint DevMinor, ulong Inode);
using Record = SCSKiller.Core.Vendors.GlCacheFile.Record;

namespace SCSKiller.Core.Vendors;

/// <summary><see cref="Unmerged"/>: a steamapp_shader_cache&lt;n&gt; that isn't SCSKiller's own slot as it wrote it waits for
/// Steam's merge at the game's exit.
/// <see cref="NotGiven"/>: the game doesn't read Steam's folder, and the compile can't go whole where it reads.</summary>
enum SteamPlaceState { Nothing, Placed, UpToDate, Removed, GameRunning, SteamBusy, OtherBucket, NextCompile, NoSpace, Failed, Unmerged, NotGiven }

/// <summary>One of the <see cref="SteamTarget"/> files as a placement left it: the warm's entries in it, and Steam's before
/// them. <paramref name="SteamOwns"/>: a file of the warm's alone that Steam has, left as it is.</summary>
sealed record SteamFilePlaced(string Name, long Pipelines, long Bytes, long SteamPipelines = 0, long SteamBytes = 0, bool SteamOwns = false);

/// <summary>What <see cref="SteamCachePlace"/> did, or why it waits. <paramref name="Leaf"/>: the driver's and the GPU's
/// folder names. <paramref name="Left"/>: the warm's pipelines that fit no file, and their bytes.
/// <paramref name="Vkd3dWaits"/>: vkd3d-proton's cache waits for the game, a process or free space, to be looked at again.</summary>
sealed record SteamPlaceResult(SteamPlaceState State, string? Leaf = null, IReadOnlyList<SteamFilePlaced>? Files = null, long Left = 0,
    long LeftBytes = 0, string? Vkd3d = null, bool Vkd3dWaits = false, string? Why = null)
{
    [JsonIgnore] public long Pipelines => Files?.Sum(f => f.Pipelines) ?? 0;
    [JsonIgnore] public long Bytes => Files?.Sum(f => f.Bytes) ?? 0;
    [JsonIgnore] public bool SteamOwnsOne => Files?.Any(f => f.SteamOwns) == true;
}

/// <summary>Puts the warm's NVIDIA cache where a Steam game reads it under the environment Steam gives every game, so it needs
/// no launch options. Steam points the driver at &lt;library&gt;/steamapps/shadercache/&lt;app&gt;/nvidiav1 (<paramref name="steamDir"/>
/// holds it), where the game writes steamapp_shader_cache&lt;n&gt; and first reads steam_shader_cache, then
/// steamapp_merged_shader_cache, read only (measured). The warm's sets of app name <paramref name="appName"/> without a
/// dictionary (<see cref="GlCacheNoDict"/>), in the game's data folder <paramref name="gameDir"/>, go into
/// <see cref="Targets"/> in the folder named like the warm's: entries in set then .toc order, the first of each key, each
/// as Coding says, the .tocs rebuilt. A <see cref="SteamTarget.SteamFirst"/> file keeps Steam's entries first, in order and
/// byte for byte, its dictionary entry included, and SCSKiller's in it are known by key and checksum. A file of the warm's
/// alone that neither <see cref="ManifestFile"/> nor the folder's owner note lists is Steam's, left alone. Steam's merge
/// after a game exits rewrites the merged file, the new entries in front and the others kept in order (measured), so the
/// warm's stay there. What fits no file isn't placed. vkd3d-proton's cache goes into the game's working folder
/// (<see cref="Vkd3dDir"/>), where vkd3d-proton keeps it without VKD3D_SHADER_CACHE_PATH, as files it merges at the game's
/// next start. Files are written under temp names nobody reads, then take their names in steps that check the file each
/// name held, with the folder locked, never while the game runs, a process holds a file of the folder or Steam merges there.
/// Each folder is opened from the library's steamapps folder or <see cref="Vkd3dRoot"/> down without following a link,
/// but for a link of this user's to a folder of this user's at shadercache or the app's folder. Every file that changes
/// names is under a write lease meanwhile, and a journal lets the next run finish or undo a pair of files a crash left
/// half renamed, or keeps them all and refuses when it can't. With no <see cref="Targets"/> (Mesa, whose warm writes the file
/// Steam points the game at itself) only vkd3d-proton's cache is placed and taken out.</summary>
sealed partial class SteamCachePlace(string gameDir, string appName, string steamDir)
{
    /// <summary>The warm's folder below GLCache, "&lt;driver&gt;/&lt;gpu&gt;"; null, or one without the warm's files: the newest.</summary>
    public string? Leaf { get; init; }

    /// <summary>Steam's config/config.vdf, whose ShaderCacheManager names the driver's and GPU's folders Steam keeps; null:
    /// not checked.</summary>
    public string? SteamConfig { get; init; }

    /// <summary>The warm's vkd3d-proton cache, vkd3d-proton.&lt;exe&gt;.cache (its .write beside it); null: none placed.</summary>
    public string? Vkd3dSource { get; init; }

    /// <summary>The game's Windows working folder, where vkd3d-proton keeps vkd3d-proton.cache.</summary>
    public string? Vkd3dDir { get; init; }

    /// <summary>The folder <see cref="Vkd3dDir"/> is opened from: the game's install folder, or a non-Steam game's start folder
    /// itself. Each file placed keeps it, to be taken out from it. Null: vkd3d-proton's cache isn't placed.</summary>
    public string? Vkd3dRoot { get; init; }

    /// <summary>The NVIDIA files the warm's sets go into; none: vkd3d-proton's cache alone.</summary>
    public IReadOnlyList<SteamTarget> Targets { get; init; } = NvidiaLinux.SteamTargets;

    public SteamEntryCoding Coding { get; init; } = NvidiaLinux.SteamCoding;

    /// <summary>The warm's set numbers the game read (<see cref="Wine.WarmFiles"/>).</summary>
    public int Files { get; init; } = Wine.WarmFiles;

    /// <summary>The warm was made under another driver than the one installed, whose folder the game no longer reads.</summary>
    public bool WarmForOtherDriver { get; init; }

    /// <summary>Whether the game runs, its launch by Steam included.</summary>
    public Func<bool> Running { get; init; } = () => false;

    /// <summary>Whether a process other than this one has one of the files open: null when a process of this user whose open
    /// files can't be read might. Tests: a stand-in.</summary>
    public Func<IReadOnlyList<string>, bool?> Held { get; init; } = paths => AppCacheFiles.ProcessesUsing(paths) is var (holding, unknown)
        && holding.Any(p => p != (nuint)Environment.ProcessId) ? true : unknown ? null : false;

    /// <summary>This user's id, which a link Steam's folders may hold at shadercache or the app's folder, and the folder it
    /// leads to, must have. Tests: a stand-in.</summary>
    public Func<uint> UserId { get; init; } = () => Libc.UserId;

    /// <summary>The bytes free on the file system of a folder. Tests: a stand-in.</summary>
    public Func<string, long> FreeSpace { get; init; } = path => new DriveInfo(path).AvailableFreeSpace;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>What must stay free beyond the files a placement writes.</summary>
    public long Margin { get; init; } = 1L << 30;

    public Action<string>? Log { get; init; }

    /// <summary>Called once a placement starts writing Steam's files: a look that finds them up to date or waits writes none.</summary>
    public Action? Writing { get; init; }

    /// <summary>Tests: called with a step's name each time Commit, Undo or Recover renamed or deleted a file.</summary>
    internal Action<string>? AfterStep { get; init; }

    void Step(string what) => AfterStep?.Invoke(what);

    /// <summary>Steam's shadercache folder of the game's app, whose nvidiav1 folder Steam points the driver at.</summary>
    public string SteamDir => steamDir;

    /// <summary>The folder whose GLCache the game's driver reads: Steam's nvidiav1, or the one the game was seen reading
    /// outside Steam's (<see cref="Outside"/>).</summary>
    public string NvidiaRoot { get; init; } = Path.Combine(steamDir, "nvidiav1");

    /// <summary>The targets are files the game writes in a driver folder outside Steam's (its default one), which other apps'
    /// files share: only the targets' holders block, nothing merges there, and the warm's entries all go in or none of
    /// SCSKiller's stay (<see cref="SteamPlaceState.NotGiven"/>).</summary>
    public bool Outside { get; init; }

    /// <summary>The "&lt;driver&gt;/&lt;gpu&gt;" folder the game was seen reading; null: not known.</summary>
    public string? ReadLeaf { get; init; }

    /// <summary>Steam runs in the Flatpak sandbox, which can change any folder below its own (this one,
    /// ~/.var/app/com.valvesoftware.Steam) and of the libraries it reaches: every folder is opened from it, or from / for one
    /// elsewhere, without following a link, and nothing goes outside Steam's folder. Null for a Steam outside a sandbox.</summary>
    public string? SandboxHome { get; init; }

    /// <summary>What was placed and where, read as the proof that a file in Steam's folder is SCSKiller's.</summary>
    public string ManifestFile => ManifestOf(gameDir);

    public static string ManifestOf(string gameDir) => Path.Combine(gameDir, "steam-place.json");

    /// <summary>SCSKiller's entries in a file of Steam's, each <see cref="Placed"/> in 24 bytes.</summary>
    string OursFile(string name) => Path.Combine(gameDir, $"steam-place.{name}.ours");

    /// <summary>What SCSKiller's own files in Steam's folder and the game's working folder start with.</summary>
    public const string OwnPrefix = ".scskiller-";

    const string TempPrefix = OwnPrefix + "place-";
    const string NoteName = OwnPrefix + "owner.json";
    const string JournalName = OwnPrefix + "journal.json";
    const string HalfRenamed = "a run that was cut short left Steam's files half renamed: left as it is";
    const string MergeTemp = "steamapp_merged_tmp_shader_cache";   // Steam's merge writes it, then renames it over the merged file
    const int EPerm = 1, ENoEnt = 2, EAgain = 11, EAccess = 13, EExist = 17, EInval = 22, ENoLck = 37, ENoSys = 38;
    const long MaxEntry = 256L << 20;   // 128 times the largest entry measured (the 2 MiB dictionary): a size past it is a broken file's
    const long MaxToc = 1L << 30;   // a .toc this run wrote, read back: 32M records, 128 bytes of .bin each at CacheFileIndexed

    string Config => string.Join(';', Targets.Select(t => $"{t.Name}:{t.Limit}:{(t.Slot ? $"slot:{t.Headroom}" : t.SteamFirst ? "steam" : "ours")}"
        + (t.WithSlot > 0 ? $":{t.WithSlot}" : ""))) + $";{Coding}";

    /// <summary>Places what isn't up to date: the warm's NVIDIA sets, then vkd3d-proton's cache. Never throws for I/O: the
    /// result says why it failed.</summary>
    public SteamPlaceResult Place(CancellationToken ct = default) => Locked(() => PlaceLocked(ct), "putting the cache in Steam's folder");

    /// <summary>Takes out what <see cref="Place"/> put there: its own files where they're still what it wrote, its entries
    /// from Steam's files, Steam's kept in order, and vkd3d-proton's files still as it wrote them. Then forgets them, except
    /// entries in a file that can't be rewritten.</summary>
    public SteamPlaceResult Remove(CancellationToken ct = default) => Locked(() => RemoveLocked(ct), "taking the cache out of Steam's folder");

    SteamPlaceResult Locked(Func<SteamPlaceResult> run, string what)
    {
        AppStore.PathGate gate;
        try { gate = new AppStore.PathGate(ManifestFile, TimeSpan.Zero); }
        catch (IOException e) when (IoErrors.HeldElsewhere(e)) { return new(SteamPlaceState.SteamBusy, Why: "another SCSKiller process is at it"); }
        using (gate)
            try { return run(); }
            catch (Refused e)
            {
                if (!e.Once || Tell(e.Message)) Log?.Invoke($"{what}: {e.Message}");
                return new(e.State, e.Leaf, Why: e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException
                or OverflowException or OutOfMemoryException)
            {
                Log?.Invoke($"{what} failed: {e.Message}");
                return new(SteamPlaceState.Failed, Why: e.Message);
            }
    }

    /// <summary>The first time this process sees <paramref name="message"/> for the game: a refusal that stays logs once.</summary>
    bool Tell(string message)
    {
        lock (Told) return Told.Add($"{ManifestFile}\n{message}");
    }

    static readonly HashSet<string> Told = [];

    SteamPlaceResult PlaceLocked(CancellationToken ct)
    {
        if (Targets.Count == 0) return PlaceVkd3dAlone();
        if (Targets.Any(t => t.Limit > NvidiaLinux.CacheFileIndexed || t.Limit <= GlCacheFile.HeaderSize || t.WithSlot > NvidiaLinux.CacheFileIndexed
                || t.WithSlot < 0 || t.Headroom < 0 || t.Slot && t.SteamFirst) || Targets.Count(t => t.Slot) > 1)
            throw new NotSupportedException("a file's limit is past what its .toc indexes");
        if (Coding is not (SteamEntryCoding.Stored or SteamEntryCoding.LeafDictionary)) throw new NotSupportedException($"entries can't be written {Coding}");
        // nothing of the game's or Steam's is read while the game plays
        if (Running()) throw new Refused(SteamPlaceState.GameRunning, "the game is running");
        var m = LoadManifest();
        if (Unfinished()) throw new Refused(SteamPlaceState.NextCompile, "rewriting the warm's files without a dictionary was cut short");
        if (WarmLeaf() is not var (leaf, names)) return new(SteamPlaceState.Nothing, Why: "the warm left no files");
        if (WarmForOtherDriver) throw new Refused(SteamPlaceState.NextCompile, "the compile was made under another driver", leaf);
        if (SteamBuckets() is { } buckets && !buckets.Contains(leaf))
            throw new Refused(SteamPlaceState.OtherBucket, $"Steam's current NVIDIA folders aren't {leaf}", leaf);
        if (ReadLeaf != null && ReadLeaf != leaf) throw new Refused(SteamPlaceState.NextCompile, $"the game reads {ReadLeaf}, the compile is for {leaf}", leaf);
        var target = Path.Combine(NvidiaRoot, "GLCache", leaf);
        using var sets = WarmSets.Read(Path.Combine(NvidiaLinuxAppCache.CacheDir(gameDir), leaf), names);
        if (sets.All.Count == 0) return new(SteamPlaceState.Nothing, leaf, Why: "none of the warm's files is without a dictionary");
        var sources = sets.Fingerprint;
        SteamPlaceResult nv;
        if (m != null && UpToDate(m, target, sources)) nv = (m.Last ?? new(SteamPlaceState.Placed, leaf)) with { State = SteamPlaceState.UpToDate };
        else
        {
            try { nv = PlaceCoded(m, target, leaf, sets, ct); }
            catch (Refused e) when (e.State == SteamPlaceState.NotGiven && m != null)
            {
                RemoveLocked(ct);
                throw;
            }
            m = LoadManifest()!;
        }
        var vk = TryPlaceVkd3d(m);
        var result = nv with { Vkd3d = vk.What, Vkd3dWaits = vk.Waits };
        if (nv.State == SteamPlaceState.Placed || vk.Placed) Log?.Invoke(Line(result));
        return result;
    }

    /// <summary><see cref="PlaceVkd3d"/>, its refusal or failure said in what it returns. Failed: why it failed.</summary>
    (string? What, bool Waits, bool Placed, string? Failed) TryPlaceVkd3d(Manifest m)
    {
        try
        {
            var (what, waits, placed) = PlaceVkd3d(m);
            return (what, waits, placed, null);
        }
        catch (Refused e) { return ($"{Vkd3dDir}: {e.Message}", true, false, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException or OutOfMemoryException)
        {
            Log?.Invoke($"placing vkd3d-proton's cache in {Vkd3dDir} failed: {e.Message}");
            return ($"{Vkd3dDir}: couldn't place vkd3d-proton's cache", false, false, e.Message);
        }
    }

    /// <summary>Without <see cref="Targets"/>: the game reads the warm's cache where it is, and vkd3d-proton's is placed, its
    /// failure the placement's. An NVIDIA placement the manifest lists stays as it is.</summary>
    SteamPlaceResult PlaceVkd3dAlone()
    {
        if (WarmForOtherDriver) throw new Refused(SteamPlaceState.NextCompile, "the compile was made under another driver");
        var vk = TryPlaceVkd3d(LoadManifest() ?? new Manifest());
        if (vk.Placed) Log?.Invoke($"vkd3d-proton: {vk.What}");
        var state = vk.Placed ? SteamPlaceState.Placed : vk.Failed != null ? SteamPlaceState.Failed : SteamPlaceState.UpToDate;
        return new(state, Vkd3d: vk.What, Vkd3dWaits: vk.Waits, Why: vk.Failed);
    }

    /// <summary>A rewrite <see cref="GlCacheNoDict"/> committed and didn't settle: its files may be both old and new.</summary>
    bool Unfinished()
    {
        var work = Path.Combine(gameDir, "proton-cache", "nodict");
        return Directory.Exists(work) && Directory.EnumerateFiles(work, "*.commit", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Any();
    }

    /// <summary>The warm's folder (<see cref="Leaf"/>, else the one whose sets were written last) and its sets' names.</summary>
    (string Leaf, List<string> Names)? WarmLeaf()
    {
        var root = NvidiaLinuxAppCache.CacheDir(gameDir);
        if (!IsFolder(root)) return null;
        var leaves = (from a in Folders(root, 32) from b in Folders(a.FullName, 16) select $"{a.Name}/{b.Name}").ToList();
        (string, List<string>)? Of(string leaf) => SetNames(Path.Combine(root, leaf)) is { Count: > 0 } n ? (leaf, n) : null;
        if (Leaf != null && leaves.Contains(Leaf) && Of(Leaf) is { } given) return given;
        return leaves.Select(Of).OfType<(string, List<string>)>()
            .OrderByDescending(l => l.Item2.Max(n => File.GetLastWriteTimeUtc(Path.Combine(root, l.Item1, n + ".bin")))).Cast<(string, List<string>)?>().FirstOrDefault();
    }

    /// <summary>The warm's sets in the folder that the game reads (&lt;app name&gt;&lt;n&gt;, n under <see cref="Files"/>), in number order.</summary>
    List<string> SetNames(string dir) => [.. Enumerable.Range(0, Files).Select(n => appName + n.ToString(CultureInfo.InvariantCulture))
        .Where(n => IsFile(Path.Combine(dir, n + ".toc")) && IsFile(Path.Combine(dir, n + ".bin")))];

    /// <summary>The warm's sets without a dictionary, read and open; a set with one, or that doesn't read, left out with why.</summary>
    sealed class WarmSets : IDisposable
    {
        public List<Set> All { get; } = [];
        public List<(string Name, string Why)> Skipped { get; } = [];
        public string Fingerprint => string.Join(';', All.Select(s => $"{s.Name}:{s.TocLength}:{s.BinLength}:{s.Written}"));

        public sealed record Set(string Name, byte[] Header, List<Record> Records, long TocLength, long BinLength, long Written, SafeFileHandle Bin);

        public static WarmSets Read(string dir, List<string> names)
        {
            var sets = new WarmSets();
            try
            {
                foreach (var name in names)
                {
                    string toc = Path.Combine(dir, name + ".toc"), bin = Path.Combine(dir, name + ".bin");
                    var bytes = File.ReadAllBytes(toc);
                    var t = GlCacheFile.ReadToc(bytes, out var why);
                    var h = File.OpenHandle(bin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var header = new byte[GlCacheFile.HeaderSize];
                    long length;
                    try
                    {
                        length = RandomAccess.GetLength(h);
                        why ??= t!.Value.Records.Any(r => r.IsDictionary) ? "it holds a dictionary"
                            : RandomAccess.Read(h, header, 0) != header.Length || !header.AsSpan().SequenceEqual(t.Value.Header) ? "its .bin header isn't its .toc's"
                            : sets.All.Count > 0 && !header.AsSpan().SequenceEqual(sets.All[0].Header) ? $"its header isn't {sets.All[0].Name}'s"
                            : t.Value.Records.Any(r => r.Offset < GlCacheFile.HeaderSize || r.Offset + r.Length > length) ? "an entry lies outside its .bin"
                            : t.Value.Records.Any(r => r.Size > MaxEntry) ? "an entry's size is past 256 MiB"
                            : null;
                    }
                    catch
                    {
                        h.Dispose();
                        throw;
                    }
                    if (why != null)
                    {
                        h.Dispose();
                        sets.Skipped.Add((name, why));
                        continue;
                    }
                    sets.All.Add(new(name, header, t!.Value.Records, bytes.Length, length, File.GetLastWriteTimeUtc(bin).Ticks, h));
                }
                return sets;
            }
            catch
            {
                sets.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var s in All) s.Bin.Dispose();
        }
    }

    /// <summary>The driver's and GPU's folders Steam keeps, "&lt;driver&gt;/&lt;gpu&gt;", from config.vdf's ShaderCacheManager
    /// (CurrentBucketDriver "L2:&lt;driver&gt;;...", CurrentBucketGPU "&lt;gpu&gt;;..." pair by place); Steam prunes the others.
    /// Null when it names none or can't be read.</summary>
    HashSet<string>? SteamBuckets()
    {
        if (SteamConfig == null || !File.Exists(SteamConfig)) return null;
        string? text;
        try { text = SteamSource.Small(SteamConfig); }   // a FIFO there is never opened
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return text == null ? null : Buckets(SteamSource.KeyValues(text));
    }

    internal static HashSet<string>? Buckets(Dictionary<string, object> vdf)
    {
        static Dictionary<string, object>? Find(Dictionary<string, object> o, int depth) =>
            o.GetValueOrDefault("ShaderCacheManager") as Dictionary<string, object>
            ?? (depth < 8 ? o.Values.OfType<Dictionary<string, object>>().Select(c => Find(c, depth + 1)).FirstOrDefault(c => c != null) : null);
        if (Find(vdf, 0) is not { } manager || manager.GetValueOrDefault("HasCurrentBucket") as string != "1"
            || manager.GetValueOrDefault("CurrentBucketDriver") is not string drivers || manager.GetValueOrDefault("CurrentBucketGPU") is not string gpus) return null;
        var pairs = drivers.Split(';').Zip(gpus.Split(';'))
            .Where(p => p.First.StartsWith("L2:", StringComparison.Ordinal)).Select(p => $"{p.First[3..]}/{p.Second}").ToHashSet(StringComparer.Ordinal);
        return pairs.Count > 0 ? pairs : null;
    }

    /// <summary>A target file as the folder has it: its headers, records and identity when it reads whole.</summary>
    sealed class Existing : IDisposable
    {
        public bool Exists { get; private init; }
        public string? Broken { get; private init; }   // why its entries can't be copied as they are
        public bool TocRead { get; private init; }     // Records are its .toc's
        public byte[] TocHeader { get; private init; } = [];
        public byte[] BinHeader { get; private init; } = [];
        public List<Record> Records { get; private init; } = [];
        public long TocLength { get; private init; } = -1;
        public long BinLength { get; private init; } = -1;
        public string TocHash { get; private init; } = "";
        public Libc.FileStat? TocStat { get; private init; }
        public Libc.FileStat? BinStat { get; private init; }
        public Ino? TocId { get; private init; }
        public Ino? BinId { get; private init; }
        public UnixFileMode Mode { get; private init; }
        public SafeFileHandle? Bin { get; private init; }

        /// <summary>The pair <paramref name="name"/> in the folder <paramref name="dir"/>, each file opened where its name is,
        /// never through a link: a link, or anything but a regular file, at a name is a file that doesn't read.
        /// <paramref name="walk"/>: a .bin past 4 GiB is read by walking its entries (<see cref="Walk"/>).</summary>
        public static Existing Read(string dir, string name, bool walk = false)
        {
            var toc = Libc.OpenEntry(Path.Combine(dir, name + ".toc"));
            using var tocFile = toc?.File;
            var bin = Libc.OpenEntry(Path.Combine(dir, name + ".bin"));
            if (toc == null && bin == null) return new();
            var h = bin?.File;
            try
            {
                string? why = null;
                var bytes = tocFile != null ? ReadAll(tocFile) : null;
                var t = bytes != null ? GlCacheFile.ReadToc(bytes, out why, v4: true) : null;
                var header = new byte[GlCacheFile.HeaderSize];
                var length = h != null ? RandomAccess.GetLength(h) : -1;
                var records = t?.Records;
                if (tocFile == null || h == null)
                {
                    why = "it isn't a .toc and a .bin";
                    h?.Dispose();
                    h = null;
                }
                else
                {
                    why ??= RandomAccess.Read(h, header, 0) != header.Length || !GlCacheFile.IsHeader(header, v4: true) ? "its .bin header isn't NVIDIA's"
                        : length > NvidiaLinux.CacheFileIndexed ? (walk ? null : "its .bin is past what its .toc indexes")   // Steam's merge wraps offsets there (steam-for-linux #13622)
                        : t!.Value.Records.Any(r => r.Offset < GlCacheFile.HeaderSize || r.Offset > length || r.Length > length - r.Offset) ? "an entry lies outside its .bin"
                        : t.Value.Records.Any(r => r.Size > MaxEntry) ? "an entry's size is past 256 MiB"
                        : Overlap(t.Value.Records) ? "its .toc names an entry twice or entries that overlap"
                        : null;
                    if (why == null && length > NvidiaLinux.CacheFileIndexed) records = Walk(h, t!.Value.Records, length, out why) ?? records;
                }
                return new()
                {
                    Exists = true, Broken = why, TocRead = t != null, TocHeader = t?.Header ?? [], BinHeader = header, Records = records ?? [],
                    TocLength = bytes?.Length ?? -1, BinLength = length, TocHash = bytes != null ? Convert.ToHexString(SHA256.HashData(bytes)) : "",
                    TocStat = toc?.Stat, BinStat = bin?.Stat, TocId = tocFile != null ? Ino.Of(tocFile) : null, BinId = h != null ? Ino.Of(h) : null,
                    Mode = h == null || OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(h), Bin = h,
                };
            }
            catch
            {
                h?.Dispose();
                throw;
            }
        }

        static byte[] ReadAll(SafeFileHandle h)
        {
            var bytes = new byte[RandomAccess.GetLength(h)];
            var at = 0;
            for (int n; at < bytes.Length && (n = RandomAccess.Read(h, bytes.AsSpan(at), at)) > 0;) at += n;
            return at == bytes.Length ? bytes : bytes[..at];
        }

        /// <summary>The length of the regular file at <paramref name="path"/>, -1 for none, and the identity of what is there,
        /// a link not followed.</summary>
        static (Libc.FileStat? Stat, long Length) Look(string path)
        {
            var e = Libc.OpenEntry(path);
            using var h = e?.File;
            return (e?.Stat, h != null ? RandomAccess.GetLength(h) : -1);
        }

        /// <summary>Two records name the same bytes: a .toc that names an entry again and again would copy it each time.</summary>
        static bool Overlap(List<Record> records)
        {
            for (var i = 1; i < records.Count; i++)
                if (records[i].Offset < records[i - 1].Offset)
                {
                    var spans = records.Select(r => (At: (long)r.Offset, End: r.Offset + r.Length)).ToArray();
                    Array.Sort(spans);
                    return spans.Skip(1).Where((r, j) => r.At < spans[j].End).Any();
                }
                else if (records[i].Offset < records[i - 1].Offset + records[i - 1].Length) return true;
            return false;
        }

        /// <summary>The files are still the ones read: the same inodes, change times and lengths.</summary>
        public bool Unchanged(string dir, string name) =>
            Look(Path.Combine(dir, name + ".toc")) == (TocStat, TocLength) && Look(Path.Combine(dir, name + ".bin")) == (BinStat, BinLength);

        /// <summary>Each record's entry checksum, read from the entry's header, which must be the one the record names; null,
        /// with why, when one isn't.</summary>
        public ulong[]? Sums(out string? why)
        {
            var sums = new ulong[Records.Count];
            var head = new byte[GlCacheFile.EntryHeaderSize];
            for (var i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.Offset > BinLength - head.Length || RandomAccess.Read(Bin!, head, r.Offset) != head.Length || BinaryPrimitives.ReadUInt32LittleEndian(head) != GlCacheFile.EntryMagic
                    || !head.AsSpan(4, 16).SequenceEqual(r.Key) || BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(28)) != r.Size)
                {
                    why = "an entry isn't the one its .toc names";
                    return null;
                }
                sums[i] = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20));
            }
            why = null;
            return sums;
        }

        /// <summary>Both files are there, read or not.</summary>
        public bool Whole => TocStat != null && BinStat != null;

        /// <summary>Of <paramref name="ours"/>, those the file may still hold while it is left as it is: of keys its .toc
        /// names, all of them when its .toc doesn't read or a file of the pair is missing.</summary>
        public HashSet<Placed> Still(HashSet<Placed> ours)
        {
            if (!Whole || !TocRead) return [.. ours];
            var keys = Records.Select(r => Key.Of(r.Key)).ToHashSet();
            return [.. ours.Where(p => keys.Contains(p.Key))];
        }

        public void Dispose() => Bin?.Dispose();
    }

    /// <summary>Which records hold SCSKiller's entries: every record of a key and checksum of <paramref name="ours"/>, each
    /// copy of it, which holds the same bytes.</summary>
    static bool[] OursAt(List<Record> records, ulong[] sums, HashSet<Placed> ours) =>
        [.. records.Select((r, i) => ours.Contains(new Placed(Key.Of(r.Key), sums[i])))];

    /// <summary>Why nothing may be written in the folder now; null when it may. A process that may hold a file without
    /// showing it is for the write leases to find.</summary>
    (SteamPlaceState, string)? Blocked(string dir, bool held = true)
    {
        if (Running()) return (SteamPlaceState.GameRunning, "the game is running");
        if (!Directory.Exists(dir)) return null;
        var files = Directory.EnumerateFiles(dir).Where(f => Path.GetFileName(f) is var n && !n.StartsWith(OwnPrefix, StringComparison.Ordinal)
            && (!Outside || Targets.Any(t => n == t.Name + ".toc" || n == t.Name + ".bin"))).ToList();
        if (files.Any(f => Path.GetFileName(f).StartsWith(MergeTemp, StringComparison.Ordinal))) return (SteamPlaceState.SteamBusy, "Steam is merging the game's cache");
        if (held && files.Count > 0 && Held(files) == true) return (SteamPlaceState.SteamBusy, "a process has a file of the game's cache open");
        return null;
    }

    void Check(string dir, string? leaf, bool held = true)
    {
        if (Blocked(dir, held) is var (state, why)) throw new Refused(state, why, leaf);
    }

    /// <summary>A folder opened without following a link below its trusted root and named through its handle
    /// (<see cref="At"/>), so a folder above it swapped for a link meanwhile changes nothing. <see cref="Path"/>: its name for
    /// the manifest and the log.</summary>
    sealed class Folder(string path, SafeFileHandle handle) : IDisposable
    {
        public string Path { get; } = path;
        public SafeFileHandle Handle { get; } = handle;
        public string At { get; } = $"/proc/self/fd/{handle.DangerousGetHandle()}";
        public string this[string name] => System.IO.Path.Combine(At, name);
        public void Dispose() => Handle.Dispose();
    }

    /// <summary><paramref name="path"/> opened from <paramref name="root"/> down (from <see cref="SandboxHome"/> or / in a
    /// sandbox), made with <paramref name="make"/>; null when it is missing. IOException when it isn't below the root or a
    /// folder on the way is a link.</summary>
    Folder? OpenFolder(string path, string root, bool make)
    {
        var below = Path.GetRelativePath(root, path);
        if (below == ".." || below.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(below)) throw new IOException($"{path} isn't in {root}");
        if (SandboxHome is { } home) return OpenUnlinked(path, home, make);
        try { return new(path, Libc.OpenBelow(root, below == "." ? [] : below.Split('/'), make)); }
        catch (FileNotFoundException) when (!make) { return null; }
    }

    /// <summary><paramref name="path"/> opened from the sandbox's <paramref name="home"/> down when it is in it, else from /,
    /// without following any link; null when it is missing and not made.</summary>
    static Folder? OpenUnlinked(string path, string home, bool make)
    {
        var root = path.StartsWith(home + "/", StringComparison.Ordinal) ? home : "/";
        try { return new(path, Libc.OpenBelow(root, Path.GetRelativePath(root, path).Split('/', StringSplitOptions.RemoveEmptyEntries), make)); }
        catch (FileNotFoundException) when (!make) { return null; }
    }

    /// <summary>Steam's &lt;library&gt;/steamapps/shadercache/&lt;app&gt;/nvidiav1/GLCache/&lt;driver&gt;/&lt;gpu&gt; folder, opened from
    /// the library's steamapps folder down, or its GLCache folder with <paramref name="glCache"/>. shadercache and the app's
    /// folder may be links of this user's to a folder of this user's (a cache moved to another disk), no folder below them.
    /// Anything else in the way refuses, logged once.</summary>
    Folder? OpenLeaf(string target, bool make, bool glCache = false)
    {
        if (SandboxHome is { } home)
        {
            if (!target.StartsWith(steamDir + "/", StringComparison.Ordinal)) throw new Refused(SteamPlaceState.Failed, $"{target} isn't in the Flatpak Steam's folder: nothing is placed there") { Once = true };
            try { return OpenUnlinked(target, home, make); }
            catch (IOException e) { throw new Refused(SteamPlaceState.Failed, $"{e.Message}: nothing is placed in that library") { Once = true }; }
        }
        // a folder outside Steam's is the real path the game was seen reading: opened from / without a link, this user's
        if (!target.StartsWith(steamDir + "/", StringComparison.Ordinal))
            try
            {
                var outside = new Folder(target, Libc.OpenBelow("/", target.Split('/', StringSplitOptions.RemoveEmptyEntries), make));
                if (Libc.Owner(outside.Handle)?.Uid == UserId()) return outside;
                outside.Dispose();
                throw new IOException($"{target} isn't this user's");
            }
            catch (FileNotFoundException) when (!make) { return null; }
            catch (IOException e) { throw new Refused(SteamPlaceState.Failed, $"{e.Message}: nothing is placed there") { Once = true }; }
        var below = new List<string>();
        var root = target;
        for (var i = glCache ? 4 : 6; i > 0; i--)
        {
            below.Insert(0, Path.GetFileName(root));
            root = Path.GetDirectoryName(root)!;
        }
        try { return new(target, Libc.OpenBelow(root, below, make, links: 2, user: UserId())); }
        catch (FileNotFoundException) when (!make) { return null; }
        catch (IOException e) { throw new Refused(SteamPlaceState.Failed, $"{e.Message}: nothing is placed in that library") { Once = true }; }
    }

    /// <summary>A folder of the game's that holds vkd3d-proton's cache, opened from <paramref name="root"/> (default:
    /// <see cref="Vkd3dRoot"/>) down.</summary>
    Folder? OpenVkd3d(string dir, string? root = null) =>
        OpenFolder(dir, root ?? Vkd3dRoot ?? throw new IOException("the game's install folder isn't known"), make: false);

    /// <summary>Locks Steam's folder against another SCSKiller process, whatever its data folder, while it is open.</summary>
    static void Lock(Folder dir, string? leaf)
    {
        if (!Libc.TryLock(dir.Handle)) throw new Refused(SteamPlaceState.SteamBusy, "another SCSKiller process is at Steam's folder", leaf);
    }

    /// <summary>Removes the temp files a run cut short left: under the folder's lock, none is in use. While a journal is
    /// there, every one of them stays: a rename that couldn't be put back may have left another process's file at one.</summary>
    static void CleanTemps(string dir)
    {
        if (!Directory.Exists(dir) || Journaled(dir)) return;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles(TempPrefix + "*")) f.Delete();
    }

    /// <summary>Deletes what this run left under its temp names, known by identity: its own files and those it swapped out of
    /// their names. A file another process put at a name while it was swapped stays, and every one stays while a journal is there.</summary>
    void CleanUp(Folder dir, HashSet<FileId> known)
    {
        try
        {
            if (Journaled(dir.At)) return;
            foreach (var f in new DirectoryInfo(dir.At).EnumerateFiles(TempPrefix + "*"))
                if (Libc.Stat(f.FullName) is not { } s) continue;
                else if (known.Contains(Id(s))) f.Delete();
                else Log?.Invoke($"{Path.Combine(dir.Path, f.Name)} isn't a file this run wrote or replaced: left as it is");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Invoke($"temp files in {dir.Path} stay until the next placement: {e.Message}"); }
    }

    static bool Journaled(string dir) => Libc.Lstat(Path.Combine(dir, JournalName)) != null;

    static List<Change> ReadJournal(string path) =>
        JsonSerializer.Deserialize<List<Change>>(ReadSmall(path) ?? throw new FileNotFoundException(path), AppStore.Json) is { } changes
            && changes.All(c => c is { Name.Length: > 0 } && !c.Name.StartsWith('.') && !c.Name.Contains('/')) ? changes
            : throw new JsonException("it names no file of the folder");

    /// <summary>A file SCSKiller keeps in Steam's folder, which a sandboxed Steam can change: read only when it is a regular
    /// file, a link not followed and a FIFO never opened, up to <paramref name="max"/>. Null when nothing is there.</summary>
    static byte[]? ReadSmall(string path, long max = 16 << 20)
    {
        if (Libc.OpenEntry(path) is not { } e) return null;
        using var h = e.File ?? throw new IOException($"{Path.GetFileName(path)} isn't a regular file");
        var length = RandomAccess.GetLength(h);
        if (length > max) throw new IOException($"{Path.GetFileName(path)} is past {Format.Bytes(max)}");
        var bytes = new byte[length];
        var at = 0;
        for (int n; at < bytes.Length && (n = RandomAccess.Read(h, bytes.AsSpan(at), at)) > 0;) at += n;
        return bytes[..at];
    }

    static FileId Id(Libc.FileStat s) => (s.DevMajor, s.DevMinor, s.Inode);

    /// <summary>The path holds the file <paramref name="was"/> describes: the same inode, as a rename changes its change time.</summary>
    static bool Same(Libc.FileStat? now, Libc.FileStat was) => now is { } n && Id(n) == Id(was);

    /// <summary>One target file as a placement writes it: Steam's entries it keeps, then the warm's.</summary>
    sealed class Out(SteamTarget target, Existing existing)
    {
        public SteamTarget Target { get; } = target;
        public Existing Existing { get; } = existing;
        public bool SteamOwns { get; set; }
        public bool Kept { get; set; }   // left as it is: Steam's, or a file of Steam's that can't be copied
        public bool Ours { get; set; }   // the folder's file is one SCSKiller wrote
        public HashSet<Placed> KeptOurs { get; set; } = [];   // SCSKiller's entries in a file left as it is
        public List<Record> Steam { get; } = [];
        public List<Record> Mine { get; } = [];   // SCSKiller's earlier entries in a file of Steam's
        public List<(int Set, Record Record)> Warm { get; } = [];
        public List<Placed> WarmPlaced { get; } = [];   // the warm's entries as written
        public List<(int Set, Record Record)> Skipped { get; } = [];   // the warm's entries left out: they don't check or don't decode
        public Ino? TocWritten { get; set; }   // the temp files as written
        public Ino? BinWritten { get; set; }
        public (Libc.FileStat Toc, Libc.FileStat Bin)? Named { get; set; }   // the files written, at their names while Commit held them
        public long End { get; set; } = GlCacheFile.HeaderSize;
        public long SteamBytes => Steam.Sum(r => r.Length);
        public long? Written { get; set; }   // SCSKiller's bytes as encoded
        public long WarmBytes => Written ?? Warm.Sum(w => w.Record.Length);
        public bool Dict { get; set; }       // it holds the leaf's dictionary
        public bool DictOurs { get; set; }   // SCSKiller wrote that dictionary entry in it
        public List<(SafeFileHandle Bin, Record Record)> Carried { get; } = [];   // dictionaries SCSKiller recorded, copied in from where the leaf holds them
        public int Count => Steam.Count + Carried.Count + Warm.Count + (DictOurs ? 1 : 0);

        /// <summary>Written anew: it gets the warm's entries, or loses some of SCSKiller's earlier ones.</summary>
        public bool Write { get; set; }

        /// <summary>It came out the same as it is, byte for byte: it keeps its name and isn't written.</summary>
        public bool Same { get; set; }
    }

    /// <param name="stored">why the leaf's dictionary can't be had: the entries go in stored, as the warm has them</param>
    SteamPlaceResult PlaceNvidia(Manifest? m, string target, string leaf, WarmSets sets, CancellationToken ct, NoDictionary? stored = null)
    {
        var coding = stored != null ? SteamEntryCoding.Stored : Coding;
        Check(target, leaf);
        if (m != null && m.Target != target && m.Files.Count > 0 && Directory.Exists(m.Target))
            try
            {
                foreach (var s in RemoveFrom(m, m.Target, ct).Stayed)
                    Log?.Invoke(s.Line(Path.Combine(m.Target, s.File.Name)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Refused)
            {
                if (e is not Refused { Once: true } || Tell(e.Message)) Log?.Invoke($"what was placed in {m.Target} stays there: {e.Message}");
            }
        using var dir = OpenLeaf(target, make: true)!;
        Lock(dir, leaf);
        var same = m != null && m.Target == target;
        var known = new HashSet<FileId>();
        var outs = new List<Out>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var files = new LeafFiles(dir, outs);
        try
        {
            Check(dir.At, leaf);
            Recover(dir, leaf);
            CleanTemps(dir.At);
            // with the game's own slot in play, a steamapp_shader_cache<k> that comes or goes meanwhile is refused at commit
            if (SlotTarget != null) files.Watch();
            var note = ReadNote(dir.At);
            var recorded = RecordedHashes(m, note, dir.Path);
            if (same && Unmerged(dir, m!) is { } wait) throw new Refused(SteamPlaceState.Unmerged, wait, leaf);
            var slot = same ? SlotOf(m!) : null;
            foreach (var t in Filled(slot)) outs.Add(new Out(t, Existing.Read(dir.At, t.Name, walk: t.SteamFirst)));
            var steamKeys = new HashSet<Key>();
            foreach (var o in outs)
            {
                var was = same ? m!.Files.FirstOrDefault(f => f.Name == o.Target.Name) : null;
                var e = o.Existing;
                if (!o.Target.SteamFirst)
                {
                    o.Ours = e.Exists && (was?.Ours.Any(p => p.Matches(e)) == true || NoteOwns(note, o.Target.Name, e));
                    if (e.Exists && !o.Ours)
                    {
                        (o.SteamOwns, o.Kept) = (true, true);
                        steamKeys.UnionWith(e.Records.Select(r => Key.Of(r.Key)));
                    }
                    continue;
                }
                var ours = was is { Keys: > 0 } ? ReadOurs(o.Target.Name) : [];
                if (same) ours.UnionWith(FoldedOurs(m!));
                string? bad = null;
                var sums = e is { Exists: true, Broken: null } ? e.Sums(out bad) : [];
                if ((e.Broken ?? bad) is { } why)
                {
                    (o.Kept, o.KeptOurs) = (true, e.Still(ours));
                    steamKeys.UnionWith(e.Records.Select(r => Key.Of(r.Key)).Except(o.KeptOurs.Select(p => p.Key)));
                    Log?.Invoke($"{o.Target.Name}: Steam's file stays as it is, {why}");
                    continue;
                }
                var mine = OursAt(e.Records, sums!, ours);
                if (coding == SteamEntryCoding.Stored) KeepDictionary(o.Target.Name, e, mine, recorded);
                o.Steam.AddRange(e.Records.Where((_, i) => !mine[i]));
                o.Mine.AddRange(e.Records.Where((_, i) => mine[i]));
                o.End = GlCacheFile.HeaderSize + o.SteamBytes;
                if (o.End > NvidiaLinux.CacheFileIndexed)
                {
                    (o.Kept, o.KeptOurs) = (true, e.Still(ours));
                    o.Steam.Clear();
                    o.Mine.Clear();
                    steamKeys.UnionWith(e.Records.Select(r => Key.Of(r.Key)).Except(o.KeptOurs.Select(p => p.Key)));
                    Log?.Invoke($"{o.Target.Name}: Steam's file stays as it is, Steam's and the game's entries alone are past what its .toc indexes");
                    continue;
                }
                steamKeys.UnionWith(o.Steam.Select(r => Key.Of(r.Key)));
                o.Write = o.Steam.Count < e.Records.Count;   // it holds some of SCSKiller's earlier entries
            }
            var carried = new List<FileEntry>();
            if (!same) NoteSlots(note, dir, carried);
            else if (SlotOut(m!, slot, dir, outs, note, carried) is { } own) outs.Add(own);
            // the dictionary a slot that stays has its entries encoded with is one SCSKiller placed, whatever is recorded
            var framed = SlotFramed(dir, carried.Select(f => f.Name));
            recorded.UnionWith(PlacedByFrames(outs, framed, RecordedIds(m, note, dir.Path)));
            if (coding == SteamEntryCoding.Stored) foreach (var o in outs) KeepFirst(o, recorded);
            // the first copy of each key, in set then .toc order, less what Steam has and the slots that stay as they are:
            // Steam's fold keeps the first copy of a key, and those fold in front of the merged file
            var seen = new HashSet<Key>(steamKeys);
            foreach (var f in carried) seen.UnionWith(CarriedOurs(f).Select(p => p.Key));
            var warm = new List<(int Set, Record Record)>();
            for (var i = 0; i < sets.All.Count; i++)
                foreach (var r in sets.All[i].Records)
                    if (seen.Add(Key.Of(r.Key))) warm.Add((i, r));
            var at = 0;
            foreach (var o in outs.Where(o => !o.Kept))
            {
                for (; at < warm.Count && o.End + warm[at].Record.Length <= o.Target.Limit; at++)
                {
                    o.Warm.Add(warm[at]);
                    o.End += warm[at].Record.Length;
                }
                o.Write |= o.Warm.Count > 0 || o.Ours;
            }
            var left = warm.Skip(at).ToList();
            if (Outside && coding == SteamEntryCoding.Stored) AllIn(left, leaf);
            if (coding == SteamEntryCoding.Stored) KeepRecorded(files, outs, removal: false, recorded);
            var need = outs.Where(o => o.Write && o.Count > 0).Sum(o => o.End + GlCacheFile.HeaderSize + (long)GlCacheFile.RecordSize4 * o.Count);
            if (FreeSpace(target) is var free && free < need + Margin)
                throw new Refused(SteamPlaceState.NoSpace, $"{Format.Bytes(need)} to write and {Format.Bytes(free)} free", leaf);
            Writing?.Invoke();
            foreach (var (name, why) in sets.Skipped) Log?.Invoke($"{name}: not put in Steam's folder, {why}");
            DictPlaced? coded = null;
            if (coding == SteamEntryCoding.LeafDictionary) (left, coded) = PlaceEncoded(m, note, dir, leaf, outs, files, recorded, framed, sets, warm, known, ct);
            else foreach (var o in outs.Where(o => o.Write && o.Count > 0)) WriteTemp(dir.At, o, sets, known, ct);
            if (Outside) AllIn(left, leaf);
            Check(dir.At, leaf);
            if (outs.FirstOrDefault(o => !o.Existing.Unchanged(dir.At, o.Target.Name)) is { } changed)
                throw new Refused(SteamPlaceState.SteamBusy, $"{changed.Target.Name} changed meanwhile", leaf);
            if (SlotTarget != null) files.Check(leaf);
            var result = new SteamPlaceResult(SteamPlaceState.Placed, leaf,
                [.. outs.Select(o => new SteamFilePlaced(o.Target.Name, o.Warm.Count, o.WarmBytes, o.Steam.Count, o.SteamBytes, o.SteamOwns))],
                left.Count, left.Sum(w => w.Record.Length));
            Install(m, dir, outs, SlotTarget != null ? files : null, sets.Fingerprint, result, note, known, leaf, coding, coded, stored?.Missing, carried);
            LogSkipped(leaf, outs, sets);
            if (outs.Where(o => o.Same).Select(o => o.Target.Name).ToList() is { Count: > 0 } kept)
                Log?.Invoke($"{leaf}: {string.Join(", ", kept)} not written again, the same entries in the same order");
            if (slot != null) Log?.Invoke(Replaced(leaf, outs, carried, clock.Elapsed));
            return result;
        }
        catch (Refused e) when (e.Leaf == null) { throw new Refused(e.State, e.Message, leaf) { Once = e.Once }; }
        finally
        {
            foreach (var o in outs) o.Existing.Dispose();
            CleanUp(dir, known);
        }
    }

    void AllIn(List<(int Set, Record Record)> left, string leaf)
    {
        if (left.Count > 0)
            throw new Refused(SteamPlaceState.NotGiven, $"{left.Count:N0} pipelines ({Format.Bytes(left.Sum(w => w.Record.Length))}) don't fit {string.Join(", ", Targets.Select(t => t.Name))}", leaf);
    }

    /// <summary>The placement's log line: the folder, what each file holds, what didn't fit.</summary>
    internal static string Line(SteamPlaceResult r) =>
        $"{r.Leaf}: " + string.Join(", ", (r.Files ?? []).Select(f => f.SteamOwns ? $"{f.Name} is Steam's, left as it is"
            : $"{f.Name} {f.Pipelines:N0} pipelines ({Format.Bytes(f.Bytes)})" + (f.SteamPipelines > 0 ? $" after Steam's {f.SteamPipelines:N0} ({Format.Bytes(f.SteamBytes)})" : "")))
        + $", {r.Left:N0} pipelines ({Format.Bytes(r.LeftBytes)}) don't fit" + (r.Vkd3d != null ? $", vkd3d-proton: {r.Vkd3d}" : "");

    /// <summary>Writes the file's temp .bin and .toc, synced: Steam's entries then the warm's, each checked against its
    /// record as it is copied, and the dictionaries carried (<see cref="Out.Carried"/>) first in a file of SCSKiller's, after
    /// Steam's in one of Steam's. Only the warm's entries count against the file's limit, all of them against what a .toc indexes.
    /// One of the warm's that doesn't check is left out (<see cref="Out.Skipped"/>), and a file left with no entries gets no
    /// temp files, nor one that comes out the same as it is (<see cref="Out.Same"/>).</summary>
    void WriteTemp(string dir, Out o, WarmSets? sets, HashSet<FileId> known, CancellationToken ct)
    {
        if (o.End > NvidiaLinux.CacheFileIndexed) throw new InvalidDataException($"{o.Target.Name} would come out {o.End} bytes, past what its .toc indexes");
        var steamHeaders = o.Target.SteamFirst && o.Existing is { Exists: true, Broken: null };
        var (tocHeader, binHeader) = steamHeaders ? (o.Existing.TocHeader, o.Existing.BinHeader) : (sets!.All[0].Header, sets.All[0].Header);
        var mode = o.Existing.Exists ? o.Existing.Mode : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var buffer = new byte[1 << 16];
        using var s = new Sink(o, () => Create(Path.Combine(dir, TempPrefix + o.Target.Name + ".bin"), mode, known), tocHeader, binHeader, mode);
        Refused Unkept() => new(SteamPlaceState.Failed, $"SCSKiller's dictionary entry doesn't check, {o.Target.Name} can't keep it: left as it is");
        bool Copy(SafeFileHandle from, Record r, bool warm, bool carried = false)
        {
            if (r.Length > buffer.Length) buffer = new byte[Math.Max(r.Length, 2L * buffer.Length)];
            var entry = buffer.AsSpan(0, (int)r.Length);
            for (var read = 0; read < entry.Length;)
            {
                var n = RandomAccess.Read(from, entry[read..], r.Offset + read);
                if (n == 0 && warm) return false;
                if (n == 0) throw carried ? Unkept() : new Refused(SteamPlaceState.SteamBusy, $"Steam's {o.Target.Name} ends inside an entry");
                read += n;
            }
            // the warm's reject log has one of its own that doesn't check: the others still go in
            if (warm && !GlCacheFile.Checks(entry, r)) return false;
            if (carried && !GlCacheFile.Checks(entry, r)) throw Unkept();
            // Steam's entries were checked before: one that isn't the same now changed meanwhile
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != GlCacheFile.EntryMagic || !entry[4..20].SequenceEqual(r.Key)
                || BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]) != r.Size)
                throw new Refused(SteamPlaceState.SteamBusy, $"an entry of Steam's {o.Target.Name} isn't the one its .toc names");
            if (warm) o.WarmPlaced.Add(new(Key.Of(r.Key), BinaryPrimitives.ReadUInt64LittleEndian(entry[20..])));
            s.Add(entry);
            return true;
        }
        var n = 0;
        var steam = o.Steam.Select(r => (Bin: o.Existing.Bin!, Record: r, Carried: false));
        var carried = o.Carried.Select(c => (c.Bin, c.Record, Carried: true));
        foreach (var (from, r, ours) in o.Target.SteamFirst ? steam.Concat(carried) : carried.Concat(steam))
        {
            if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
            Copy(from, r, warm: false, ours);
        }
        foreach (var (set, r) in o.Warm)
        {
            if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
            if (!Copy(sets!.All[set].Bin, r, warm: true)) o.Skipped.Add((set, r));
        }
        if (o.Skipped.Count > 0)
        {
            o.Warm.RemoveAll(o.Skipped.Contains);
            o.End -= o.Skipped.Sum(w => w.Record.Length);
            if (o.Count == 0) return;   // Commit moves the file aside: the temp .bin goes with the run's
        }
        if (s.At != o.End || o.Warm.Count > 0 && s.At > o.Target.Limit) throw new InvalidDataException($"{o.Target.Name} came out {s.At} bytes, not {o.End}");
        s.Close(Path.Combine(dir, TempPrefix + o.Target.Name + ".toc"), known);
        if (o.Same) o.Write = false;
    }

    /// <summary>A new file at <paramref name="path"/>, known as this run's, read back through its own handle.</summary>
    static FileStream Create(string path, UnixFileMode mode, HashSet<FileId> known)
    {
        var f = OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException()
            : new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, BufferSize = 1 << 20, UnixCreateMode = mode });
        if (Libc.Stat(f.SafeFileHandle) is { } s) known.Add(Id(s));
        return f;
    }

    /// <summary>Gives the temp files their names (<see cref="Commit"/>), with the manifest and the owner note counting both
    /// the earlier files and these as SCSKiller's until all are in place.</summary>
    void Install(Manifest? m, Folder dir, List<Out> outs, LeafFiles? files, string sources, SteamPlaceResult result, OwnerNote? note, HashSet<FileId> known,
        string leaf, SteamEntryCoding coding = SteamEntryCoding.Stored, DictPlaced? coded = null, string? noDictionary = null, List<FileEntry>? carried = null)
    {
        var same = m != null && m.Target == dir.Path;
        var now = new Manifest
        {
            Target = dir.Path, Config = Config, Sources = sources, At = Clock(), Last = result, Vkd3d = m?.Vkd3d ?? [], Vkd3dSources = m?.Vkd3dSources, Dictionary = coded,
            Coded = coding, NoDictionary = noDictionary, Slot = same ? m!.Slot : null, PastHeadroom = same && m!.PastHeadroom,
            Measured = outs.FirstOrDefault(o => o.Target.SteamFirst) is { Kept: false } merged ? merged.SteamBytes : same ? m!.Measured : null,
        };
        // a dictionary SCSKiller placed stays in the leaf: recorded until it is the one the entries are encoded with again
        var held = HeldDictionary(m, note, dir, outs)?.D;
        now.WasDictionary = new[] { held, same ? m!.WasDictionary : null }.FirstOrDefault(d => d != null && d.Hash != coded?.Hash);
        var during = now.Copy();
        during.Pending = true;
        // until the files have their names, they and the game's entries may hold the dictionary SCSKiller's files held
        during.WasDictionary = held ?? now.WasDictionary;
        var newOurs = new Dictionary<string, HashSet<Placed>>();
        foreach (var o in outs)
        {
            var name = o.Target.Name;
            var was = same ? m!.Files.FirstOrDefault(f => f.Name == name) : null;
            var ours = newOurs[name] = !Keyed(o.Target) ? [] : o.Kept ? o.KeptOurs : [.. o.WarmPlaced];
            var print = o.Target.SteamFirst || o.Count == 0 ? null : o.Same ? Print.Of(o.Existing) : o.Write ? Print.Of(dir[TempPrefix + name]) : null;
            var entry = new FileEntry
            {
                Name = name, SteamFirst = o.Target.SteamFirst, SteamOwns = o.SteamOwns, Ours = print != null ? [print] : [], Keys = ours.Count,
                Broken = o.Kept && ours.Count > 0 ? Print.Of(o.Existing) : null, Bin = !o.Write ? Math.Max(o.Existing.BinLength, 0) : o.Count > 0 ? o.End : 0,
                Dictionary = o.Dict, Slot = o.Target.Slot,
            };
            now.Files.Add(entry);
            var both = Keyed(o.Target) ? ours.Union(was is { Keys: > 0 } ? ReadOurs(name) : []).ToHashSet() : [];
            // either file may be at the name once the run is cut short: a slot carried from here counts the longer, and is
            // either pair by identity, as a print alone is a copy's too
            during.Files.Add(new()
            {
                Name = name, SteamFirst = entry.SteamFirst, SteamOwns = entry.SteamOwns, Ours = o.Ours ? [.. entry.Ours, Print.Of(o.Existing)] : entry.Ours,
                Keys = both.Count, Broken = entry.Broken, Bin = Math.Max(o.Existing.BinLength, entry.Bin), Dictionary = entry.Dictionary, Slot = entry.Slot,
                Sides = !o.Target.Slot ? null
                    : [.. new[] { o.Ours ? SlotPair.Of(o.Existing.TocId, o.Existing.BinId) : null, SlotPair.Of(o.TocWritten, o.BinWritten) }.OfType<SlotPair>()],
            });
            if (Keyed(o.Target)) WriteOurs(name, both);
        }
        foreach (var f in carried ?? [])
        {
            if (f.Found is { } found) WriteOurs(f.Name, found);
            now.Files.Add(f);
            during.Files.Add(f);
        }
        // a slot Steam folded into the merged file: its entries there stay SCSKiller's until the merged file is rewritten
        if (same) during.Files.AddRange(m!.Files.Where(f => f.Slot && !during.Files.Any(d => d.Name == f.Name)));
        SaveManifest(during);
        WriteNote(dir.At, note, during);
        Commit(dir, outs, known, leaf, files);
        if (outs.Where(o => o.Write && o.Count > 0 && o.Named == null).Select(o => o.Target.Name).ToList() is { Count: > 0 } lost)
        {
            // another process put its own files at a name this run gave its files to: they stay as they are, and a slot
            // among them isn't SCSKiller's; the rest stays pending
            during.Files.RemoveAll(f => f.Slot && lost.Contains(f.Name));
            foreach (var name in lost.Where(n => SlotNumber(n) != null)) File.Delete(OursFile(name));
            SaveManifest(during);
            WriteNote(dir.At, note, during);
            throw new Refused(SteamPlaceState.SteamBusy, $"{string.Join(", ", lost)} changed as it took its name: left as it is", leaf);
        }
        foreach (var o in outs.Where(o => Keyed(o.Target))) WriteOurs(o.Target.Name, newOurs[o.Target.Name]);
        Committed(m, now, outs);
        SaveManifest(now);
        WriteNote(dir.At, note, now);
        ForgetDictionaries([.. new[] { coded, now.WasDictionary }.OfType<DictPlaced>().Select(d => DictFile(LeafOf(dir.Path), d.Hash))]);
    }

    /// <summary>Gives each written file's temp .bin, then its .toc, its name, and moves aside the files of one left empty, the
    /// .toc first: a .toc never names entries its .bin lacks. Every file that changes names, old and new, is held
    /// (<see cref="Hold"/>) from before the journal is written: one a process has open stops it, and one a process opens
    /// meanwhile, looked at after each rename, settles each pair (<see cref="Settle"/>) before any lease goes. The journal,
    /// synced first, lets <see cref="Recover"/> settle a pair a crash left half renamed. It goes once the names are synced,
    /// and stays, with every file it lists, when a pair can't be settled. Once every file is held, the leaf's other files
    /// (<paramref name="files"/>) must still be the ones a removal, or a placement with the game's own slot, read or watched.</summary>
    void Commit(Folder dir, List<Out> outs, HashSet<FileId> known, string? leaf, LeafFiles? files = null)
    {
        var changes = outs.Where(o => o.Write).ToList();
        if (changes.Count == 0) return;
        Check(dir.At, leaf, held: false);
        var holds = new List<HeldFile>();
        var journal = new List<Change>();
        var gave = new HashSet<FileId>();
        var journaled = false;
        try
        {
            Ino? Keep(string path, string name, Func<SafeFileHandle, bool> same)
            {
                var held = Hold(path, same, name);
                holds.Add(held);
                return held.Id;
            }
            foreach (var o in changes)
            {
                var (e, toc, bin) = (o.Existing, o.Target.Name + ".toc", o.Target.Name + ".bin");
                e.Dispose();   // the kernel leases a file only while no other handle has it open, this process's included
                // a write keeps the inode and changes the change time
                Ino? Old(string name, Libc.FileStat? was, long length) => was is { } w ? Keep(dir[name], name, h => Libc.Stat(h) == w && RandomAccess.GetLength(h) == length) : null;
                Ino? New(string name, Ino? written) => written is { } id ? Keep(dir[TempPrefix + name], name, h => Ino.Of(h) == id) : null;
                journal.Add(new(o.Target.Name, Old(toc, e.TocStat, e.TocLength), Old(bin, e.BinStat, e.BinLength), New(toc, o.TocWritten), New(bin, o.BinWritten)));
            }
            files?.Check(leaf);
            AppStore.WriteAtomic(dir[JournalName], JsonSerializer.SerializeToUtf8Bytes(journal, AppStore.Json), ownerOnly: true);
            Libc.SyncFolder(dir.At);
            journaled = true;
            Opened(holds, leaf);
            foreach (var o in changes)
            {
                string toc = dir[o.Target.Name + ".toc"], bin = dir[o.Target.Name + ".bin"];
                if (o.Count == 0)
                {
                    foreach (var (path, was) in new[] { (toc, o.Existing.TocStat), (bin, o.Existing.BinStat) })
                        if (was is { } w && Aside(path, w) is { })
                        {
                            known.Add(Id(w));
                            Step($"aside {Path.GetFileName(path)}");
                            Opened(holds, leaf);
                        }
                    continue;
                }
                foreach (var (to, was, coming) in new[] { (bin, o.Existing.BinStat, o.BinWritten), (toc, o.Existing.TocStat, o.TocWritten) })
                {
                    gave.Add(Id(Swap(dir[TempPrefix + Path.GetFileName(to)], to, was, known, coming).Ours!.Value));
                    Step($"swap {Path.GetFileName(to)}");
                    Opened(holds, leaf);
                }
            }
            // what each name holds once all are renamed, read while every file is still held: the file this run wrote,
            // or Out.Named stays null, and the names stay as they are
            foreach (var o in changes.Where(o => o.Count > 0))
            {
                var (toc, bin) = (Libc.StatNoFollow(dir[o.Target.Name + ".toc"]), Libc.StatNoFollow(dir[o.Target.Name + ".bin"]));
                o.Named = o.TocWritten?.Is(toc) == true && o.BinWritten?.Is(bin) == true ? (toc!.Value, bin!.Value) : null;
            }
            Opened(holds, leaf);
        }
        catch when (journaled)
        {
            // the last pair first, each left as a pair that belongs together while every lease still holds
            var settled = true;
            for (var i = journal.Count - 1; i >= 0; i--) settled &= Settle(dir, journal[i], back: true, holds, gave, known, out _) != null;
            if (!settled || !Synced(dir)) throw Stuck(leaf, "what it renamed can't all be put back or finished");
            File.Delete(dir[JournalName]);
            Step("delete journal");
            throw;
        }
        finally
        {
            foreach (var h in holds) h.Dispose();
        }
        Libc.SyncFolder(dir.At);
        File.Delete(dir[JournalName]);
        Step("delete journal");
    }

    /// <summary>Leaves the pair <paramref name="c"/> as one that belongs together: its old files at their names
    /// (<paramref name="back"/>) or its new ones, but the side whose file this run gave its name to and a process opened,
    /// and the other side when the first can't be finished. Returns the side, true for the old, and whether a file moved;
    /// null, logged, when neither can be proved: the names stay as they are.</summary>
    bool? Settle(Folder dir, Change c, bool back, List<HeldFile> holds, HashSet<FileId> gave, HashSet<FileId> known, out bool moved)
    {
        // a process that opened a file once it had its name reads the pair that file belongs to
        bool Opened(bool old) => PairOf(c).Select(f => old ? f.Old : f.New).OfType<Ino>().Any(id => gave.Contains(id.Node) && holds.Any(h => h.Id.Node == id.Node && h.Broken));
        moved = false;
        var (side, why) = (back, "");
        if (Opened(old: true) && Opened(old: false))
        {
            Log?.Invoke($"{c.Name}: a process opened an old file and a new one once each had its name: the names stay as they are");
            return null;
        }
        if (Opened(old: !side)) (side, why) = (!side, "a process opened one of them once it had its name");
        if (!Toward(dir, c, side, holds, gave, known, ref moved))
        {
            if (Opened(old: side) || !Toward(dir, c, !side, holds, gave, known, ref moved))
            {
                Log?.Invoke($"{c.Name}: neither its old files nor its new ones can all have their names: they stay as they are");
                return null;
            }
            (side, why) = (!side, "the other way couldn't be finished");
        }
        if (side != back) Log?.Invoke($"{c.Name}: {(side ? "its old files have their names again" : "its new files have their names")}, as {why}");
        return side;
    }

    /// <summary>A pair's .toc and .bin: what each name held before and is to hold after.</summary>
    static (string File, Ino? Old, Ino? New)[] PairOf(Change c) => [(c.Name + ".toc", c.OldToc, c.NewToc), (c.Name + ".bin", c.OldBin, c.NewBin)];

    /// <summary>Where a file of a pair is: at its name, or at the temp name a swap or a move aside gave it.</summary>
    static string? Find(Folder dir, Ino id, string file) => new[] { file, TempPrefix + file, TempPrefix + "old-" + file }.FirstOrDefault(n => id.Is(Libc.Stat(dir[n])));

    /// <summary>Gives each name of the pair <paramref name="c"/> its old file (<paramref name="back"/>, the .toc first) or its
    /// new one (the .bin first), each found by identity and held, once every name is checked to hold a file of the pair or
    /// none. Going back, a name another process gave a newer file keeps it. Each rename is checked after it
    /// (<see cref="Swap"/>, <see cref="Aside"/>). False, logged, when one can't be done.</summary>
    bool Toward(Folder dir, Change c, bool back, List<HeldFile> holds, HashSet<FileId> gave, HashSet<FileId> known, ref bool moved)
    {
        var steps = new List<(string File, Ino? Want, Libc.FileStat? Now)>();
        var files = PairOf(c);
        if (!back) Array.Reverse(files);
        foreach (var (file, old, @new) in files)
        {
            var want = back ? old : @new;
            var now = Libc.Stat(dir[file]);
            if (want?.Is(now) ?? now == null) continue;
            if (now is { } n && old?.Is(n) != true && @new?.Is(n) != true)
            {
                if (back) continue;
                Log?.Invoke($"{file} isn't a file of the pair: it stays as it is");
                return false;
            }
            if (want != null && (Find(dir, want, file) == null || !holds.Any(h => h.Id == want))) return false;
            steps.Add((file, want, now));
        }
        try
        {
            foreach (var (file, want, now) in steps)
            {
                if (want == null)
                {
                    if (Aside(dir[file], now!.Value) == null) throw new Refused(SteamPlaceState.SteamBusy, $"{file} went meanwhile");
                    known.Add(Id(now.Value));
                }
                else
                {
                    Swap(dir[Find(dir, want, file) ?? throw new Refused(SteamPlaceState.SteamBusy, $"{file} went meanwhile")], dir[file], now, known, want);
                    gave.Add(want.Node);
                }
                moved = true;
                Step($"{(back ? "back" : "forward")} {file}");
            }
            return true;
        }
        catch (Exception e) when (e is Refused or IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"{c.Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Refuses once a process opened one of the files held since they were.</summary>
    static void Opened(List<HeldFile> holds, string? leaf)
    {
        if (holds.FirstOrDefault(h => h.Broken) is { } opened) throw new Refused(SteamPlaceState.SteamBusy, $"a process opened {opened.Name} meanwhile", leaf);
    }

    /// <summary>Puts back what <paramref name="named"/> renamed, the last first, unless a process opened a file it gave a name
    /// to: then that file and all still to go back stay as they are. False when anything stays.</summary>
    bool Undo(List<Named> named, List<HeldFile> holds)
    {
        for (var i = named.Count - 1; i >= 0; i--)
        {
            if (named.FirstOrDefault(n => n.Ours is { } o && holds.Any(h => h.Id.Node == Id(o) && h.Broken)) is { } opened)
            {
                Log?.Invoke($"a process opened {Path.GetFileName(opened.To)} once it had that name: the names stay as they are");
                return false;
            }
            var done = Back(named[i]);
            Step($"back {Path.GetFileName(named[i].To)}");
            if (!done) return false;
        }
        return true;
    }

    bool Synced(Folder dir)
    {
        try
        {
            Libc.SyncFolder(dir.At);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"{dir.Path} can't be synced: {e.Message}");
            return false;
        }
    }

    /// <summary>Files left half renamed, which their journal keeps with every file it lists until a run settles them.</summary>
    static Refused Stuck(string? leaf, string why) => new(SteamPlaceState.Failed, $"{HalfRenamed} ({why})", leaf) { Once = true };

    /// <summary>A file of the folder held open: under a write lease, which an open by another process breaks, or unleased on
    /// a file system without leases.</summary>
    sealed class HeldFile(SafeFileHandle file, string name, Ino id, bool leased) : IDisposable
    {
        public SafeFileHandle File { get; } = file;
        public string Name { get; } = name;
        public Ino Id { get; } = id;
        public bool Broken => leased && !AppCacheFiles.Leased(File);
        public void Dispose() => File.Dispose();
    }

    /// <summary>The file at <paramref name="path"/>, held while <paramref name="same"/> finds it still the file meant, under a
    /// write lease: the kernel grants it only while no other handle has the file open or mapped, and an open meanwhile breaks
    /// it and waits for its release. Unleased only on a file system without leases where every process of this user was read
    /// and none has the file open. Either way the file is looked at again once held, as a writer may have changed it and
    /// closed it before. <paramref name="name"/>: the file's name for a refusal.</summary>
    HeldFile Hold(string path, Func<SafeFileHandle, bool> same, string? name = null)
    {
        name ??= Path.GetFileName(path);
        SafeFileHandle h;
        try { h = Libc.OpenNoFollow(path); }
        catch (IOException) { throw new Refused(SteamPlaceState.SteamBusy, $"{name} changed or was opened meanwhile"); }
        try
        {
            if (!same(h)) throw new Refused(SteamPlaceState.SteamBusy, $"{name} changed meanwhile");
            var leased = AppCacheFiles.Lease(h) switch
            {
                0 => true,
                EAgain => throw new Refused(SteamPlaceState.SteamBusy, $"a process has {name} open"),
                // only a file system without leases may lean on Held: a lease refused for rights can hide another user's process
                EInval or ENoLck when Held([path]) == false => false,
                EInval or ENoLck or EAccess or EPerm => throw new Refused(SteamPlaceState.SteamBusy, $"a process may have {name} open"),
                var errno => throw new IOException($"{name} can't be leased: {Marshal.GetPInvokeErrorMessage(errno)}"),
            };
            var id = Ino.Of(h);
            if (!same(h)) throw new Refused(SteamPlaceState.SteamBusy, $"{name} changed meanwhile");
            if (leased ? !AppCacheFiles.Leased(h) : Held([path]) != false) throw new Refused(SteamPlaceState.SteamBusy, $"a process opened {name} meanwhile");
            return new(h, name, id, leased);
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>Our file at <see cref="Temp"/> given the name <see cref="To"/>, and the file <see cref="Was"/> that name held,
    /// moved to <see cref="Old"/>. Without <see cref="Ours"/>: the file was only moved aside.</summary>
    sealed record Named(string? Temp, string To, Libc.FileStat? Ours, Libc.FileStat? Was, string? Old);

    /// <summary>Gives our temp file, <paramref name="coming"/> when given, the name <paramref name="to"/>.
    /// <paramref name="was"/>: the file that name held when it was read, swapped out to the temp's name in the same step and
    /// checked there; null: it held none. A file another process put at the name meanwhile is swapped back, and refused.</summary>
    Named Swap(string temp, string to, Libc.FileStat? was, HashSet<FileId> known, Ino? coming = null)
    {
        var name = Path.GetFileName(to);
        var ours = Libc.Stat(temp) ?? throw new IOException($"{Path.GetFileName(temp)} is gone");
        if (coming?.Is(ours) == false) throw new Refused(SteamPlaceState.SteamBusy, $"{Path.GetFileName(temp)} changed meanwhile");
        known.Add(Id(ours));
        if (was is not { } w)
        {
            if (Libc.RenameNew(temp, to) is var created and not 0) throw Naming(created, name);
            return new(temp, to, ours, null, null);
        }
        known.Add(Id(w));
        switch (Exchanged(temp, to, w, ours))
        {
            case 0: return new(temp, to, ours, w, temp);
            case -1: throw new Refused(SteamPlaceState.SteamBusy, $"{name} changed meanwhile");
            case EInval or ENoSys:   // a file system without RENAME_EXCHANGE: the old file moved aside and checked first
                var old = Aside(to, w) ?? throw new Refused(SteamPlaceState.SteamBusy, $"{name} went meanwhile");
                if (Libc.RenameNew(temp, to) is var named and not 0)
                {
                    Libc.RenameNew(old, to);   // a newer file at the name keeps it: the old one goes with the temp files
                    throw Naming(named, name);
                }
                return new(temp, to, ours, w, old);
            case var errno: throw new IOException($"naming {name} failed: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
    }

    /// <summary>Undoes <see cref="Swap"/> or a move aside while the names still hold what it left there, each rename checked
    /// after it at both names; otherwise they stay as they are, and false.</summary>
    bool Back(Named n)
    {
        bool Twice(Libc.FileStat ours, Libc.FileStat was)
        {
            if (!Moved(n.To, n.Temp!, ours)) return false;
            if (Moved(n.Old!, n.To, was)) return true;
            Moved(n.Temp!, n.To, ours);   // the name isn't left empty
            return false;
        }
        var done = (n.Ours is not { } o || Same(Libc.Stat(n.To), o)) && (n.Was is not { } w || Same(Libc.Stat(n.Old!), w))
            && (n.Ours is not { } ours ? Moved(n.Old!, n.To, n.Was!.Value)
                : n.Was is not { } was ? Moved(n.To, n.Temp!, ours)
                : n.Old == n.Temp ? Exchanged(n.Temp!, n.To, ours, was) == 0
                : Twice(ours, was));
        if (!done) Log?.Invoke($"{Path.GetFileName(n.To)} changed meanwhile: it stays as it is");
        return done;
    }

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/>, never over a file, and checks the file that moved is
    /// <paramref name="file"/>: one another process put at <paramref name="from"/> meanwhile goes back.</summary>
    bool Moved(string from, string to, Libc.FileStat file)
    {
        if (Libc.RenameNew(from, to) != 0) return false;
        var now = Libc.Stat(to);
        if (Same(now, file)) return true;
        if (now is not { } other || Libc.RenameNew(to, from) != 0 || !Same(Libc.Stat(from), other))
            Log?.Invoke($"{Path.GetFileName(from)} changed twice meanwhile: what {Path.GetFileName(to)} holds stays");
        return false;
    }

    /// <summary>Swaps what the names <paramref name="a"/> and <paramref name="b"/> hold, then checks <paramref name="a"/>
    /// holds <paramref name="toA"/> and <paramref name="b"/> <paramref name="toB"/>. Only <paramref name="b"/> holding
    /// <paramref name="toB"/> proves the swap moved a file another process put at <paramref name="b"/> before it, and
    /// swapping again, its own undo, puts that file back, logged when that can't be proved. Anything else, a file put at
    /// <paramref name="b"/> after the swap included, stays as it is. 0 when they hold, -1 when they don't, else the errno of
    /// a swap not done.</summary>
    int Exchanged(string a, string b, Libc.FileStat toA, Libc.FileStat toB)
    {
        if (Libc.Exchange(a, b) is var errno and not 0) return errno;
        var (atA, atB) = (Libc.Stat(a), Libc.Stat(b));
        if (Same(atA, toA) && Same(atB, toB)) return 0;
        if (Same(atA, toA) || !Same(atB, toB))
            Log?.Invoke($"{Path.GetFileName(b)} changed meanwhile: what the names hold stays");
        else if (atA is not { } x || Libc.Exchange(a, b) != 0 || !Same(Libc.Stat(a), toB) || !Same(Libc.Stat(b), x))
            Log?.Invoke($"{Path.GetFileName(b)} changed twice meanwhile: what {Path.GetFileName(a)} holds stays");
        return -1;
    }

    /// <summary>Moves the file at <paramref name="path"/> to a temp name, never over a file, if it is still
    /// <paramref name="was"/>; null when it is gone. A file another process put there meanwhile goes back, refused.</summary>
    string? Aside(string path, Libc.FileStat was)
    {
        var name = Path.GetFileName(path);
        var aside = Path.Combine(Path.GetDirectoryName(path)!, TempPrefix + "old-" + name);
        switch (Libc.RenameNew(path, aside))
        {
            case 0: break;
            case ENoEnt: return null;
            case var errno: throw Naming(errno, Path.GetFileName(aside));
        }
        if (Same(Libc.Stat(aside), was)) return aside;
        if (Libc.RenameNew(aside, path) != 0) Log?.Invoke($"{name} changed twice meanwhile: what {Path.GetFileName(aside)} holds stays");
        throw new Refused(SteamPlaceState.SteamBusy, $"{name} changed meanwhile");
    }

    /// <summary>Deletes the file <paramref name="name"/> of the folder while it is still <paramref name="was"/> and
    /// <paramref name="length"/> bytes, under a write lease: one a process opens meanwhile goes back.</summary>
    void Take(Folder dir, string name, Libc.FileStat was, long length)
    {
        using var held = Hold(dir[name], h => Libc.Stat(h) == was && RandomAccess.GetLength(h) == length);
        if (Aside(dir[name], was) is not { } aside) return;
        if (!held.Broken)
        {
            File.Delete(aside);
            return;
        }
        if (Libc.RenameNew(aside, dir[name]) != 0) Log?.Invoke($"{name} changed meanwhile: what {Path.GetFileName(aside)} holds stays");
        throw new Refused(SteamPlaceState.SteamBusy, $"a process opened {name} meanwhile");
    }

    /// <summary>A pair of files changing names, by identity: what each name held before and is to hold after, null for none.</summary>
    sealed record Change(string Name, Ino? OldToc, Ino? OldBin, Ino? NewToc, Ino? NewBin);

    /// <summary>A file's identity in a journal: its inode, which a rename keeps, and its length and write time, which a write
    /// changes. Not its change time, which each rename changes.</summary>
    sealed record Ino(uint Major, uint Minor, ulong Inode, long Length, long Written)
    {
        public static Ino Of(SafeFileHandle h) => Libc.Stat(h) is { } s ? new(s.DevMajor, s.DevMinor, s.Inode, RandomAccess.GetLength(h), File.GetLastWriteTimeUtc(h).Ticks)
            : throw new IOException("a file's identity can't be read");

        public FileId Node => (Major, Minor, Inode);

        /// <summary>The file <paramref name="s"/> describes has this inode, whatever it holds now.</summary>
        public bool Is(Libc.FileStat? s) => s is { } v && Id(v) == Node;
    }

    /// <summary>A slot's .toc and .bin by <see cref="Ino"/>: what a pending manifest knows them by, as a rename keeps it.</summary>
    sealed record SlotPair(Ino Toc, Ino Bin)
    {
        public static SlotPair? Of(Ino? toc, Ino? bin) => toc != null && bin != null ? new(toc, bin) : null;
    }

    /// <summary>Settles each pair of files the journal of a run cut short lists (<see cref="Settle"/>): back to the files the
    /// names held before when all of them are still in the folder, else on to the new ones, and the other way when that
    /// can't be finished. Each file it renames or deletes is held as <see cref="Commit"/> holds them, while it is the one the
    /// journal lists. The other side's files go (<see cref="Delete"/>), then the journal. A journal that doesn't read, a
    /// name holding a file it doesn't list, a pair it can't settle or a file it can't delete as held keeps the journal and
    /// every temp file, refused while the leases still hold, until a run settles them.</summary>
    void Recover(Folder dir, string? leaf)
    {
        var path = dir[JournalName];
        if (Libc.Lstat(path) == null) return;
        List<Change> changes;
        try { changes = ReadJournal(path); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { throw Stuck(leaf, "its journal doesn't read"); }
        Libc.FileStat? At(string name) => Libc.Stat(dir[name]);
        var holds = new List<HeldFile>();
        var gave = new HashSet<FileId>();
        var others = new List<(string File, Ino Id)>();
        var done = new List<string>();
        Exception? stuck = null;
        try
        {
            foreach (var c in changes)
                try
                {
                    var pair = PairOf(c);
                    if (pair.FirstOrDefault(f => At(f.File) is { } now && f.Old?.Is(now) != true && f.New?.Is(now) != true) is { File: { } odd })
                        throw Stuck(leaf, $"{odd} isn't a file its journal lists");
                    bool Has(Ino? id, string file) => id?.Is(At(file)) ?? At(file) == null;
                    var back = pair.All(f => Has(f.Old, f.File))
                        || !pair.All(f => Has(f.New, f.File)) && pair.All(f => f.Old is not { } id || Find(dir, id, f.File) != null);
                    if (!back && !pair.All(f => f.New is not { } id || Find(dir, id, f.File) != null))
                        throw Stuck(leaf, "neither all its old files nor all its new ones are there");
                    foreach (var (file, old, @new) in pair)
                        foreach (var id in new[] { old, @new }.OfType<Ino>())
                            if (Find(dir, id, file) is { } at) holds.Add(Hold(dir[at], h => Ino.Of(h) == id, file));
                    if (Settle(dir, c, back, holds, gave, [], out var moved) is not { } side) throw Stuck(leaf, $"{c.Name} can't be put back or finished");
                    foreach (var (file, old, @new) in pair)
                        if ((side ? @new : old) is { } other) others.Add((file, other));
                    if (moved) done.Add($"{c.Name}: what a run cut short left half renamed was {(side ? "put back" : "finished")}");
                }
                catch (Exception e) when (e is Refused or IOException or UnauthorizedAccessException) { stuck ??= e; }
            if (stuck != null) throw stuck is Refused { Once: true } ? stuck : Stuck(leaf, stuck.Message);
            if (!Synced(dir)) throw Stuck(leaf, "its folder can't be synced");
            // the other side's files, found at their temp names, then each deleted as held, before the journal goes
            foreach (var (file, id, at) in others.Select(o => (o.File, o.Id, At: Find(dir, o.Id, o.File))).ToList())
                if (at != null && at != file && !Delete(dir, at, file, holds.FirstOrDefault(h => h.Id == id)))
                    throw Stuck(leaf, $"the {file} it replaced can't be deleted as the file it held");
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Stuck(leaf, e.Message); }
            Step("delete journal");
        }
        finally
        {
            foreach (var h in holds) h.Dispose();
            foreach (var line in done) Log?.Invoke(line);
        }
    }

    /// <summary>Deletes the file <paramref name="held"/> holds, at the temp name <paramref name="at"/>: moved to a fresh name
    /// first, and unlinked there once that name is checked to hold the held file, its lease intact; otherwise moved back.
    /// False when it stays.</summary>
    bool Delete(Folder dir, string at, string file, HeldFile? held)
    {
        if (held == null) return false;
        var gone = $"{TempPrefix}gone-{Guid.NewGuid():N}";
        if (Libc.RenameNew(dir[at], dir[gone]) != 0) return false;
        if (held.Id.Is(Libc.Stat(dir[gone])) && !held.Broken)
            try
            {
                File.Delete(dir[gone]);
                Step($"delete {file}");
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke($"{gone} stays: {e.Message}");
                return false;
            }
        if (Libc.RenameNew(dir[gone], dir[at]) != 0) Log?.Invoke($"{at} changed meanwhile: what {gone} holds stays");
        return false;
    }

    static Exception Naming(int errno, string name) => errno == EExist ? new Refused(SteamPlaceState.SteamBusy, $"{name} appeared meanwhile")
        : new IOException($"naming {name} failed: {Marshal.GetPInvokeErrorMessage(errno)}");

    /// <summary>What was placed is still there, from these sets, under these targets: Steam's files' .tocs and entry headers are read,
    /// through the folder opened as a placement opens it (<see cref="OpenLeaf"/>) and <see cref="Existing.Read"/>. A folder
    /// it refuses isn't up to date.</summary>
    bool UpToDate(Manifest m, string target, string sources)
    {
        if (m.Pending || m.Target != target || m.Config != Config || m.Sources != sources) return false;
        Folder? leaf;
        try { leaf = OpenLeaf(target, make: false); }
        catch (Refused) { return false; }
        using var dir = leaf;
        if (dir == null || Journaled(dir.At) || !SlotUpToDate(m, dir)) return false;
        foreach (var t in Filled(SlotOf(m)))
        {
            if (m.Files.FirstOrDefault(f => f.Name == t.Name) is not { } f) return false;
            using var e = Existing.Read(dir.At, t.Name);
            if (!t.SteamFirst)
            {
                if (f.SteamOwns ? !e.Exists || f.Ours.Any(p => p.Matches(e)) : f.Ours.Count == 0 ? e.Exists : !f.Ours.Any(p => p.Matches(e))) return false;
                continue;
            }
            // Steam's merge drops entries, or the cache was cleared: what didn't fit may now
            if (m.Last is { Left: > 0 } && Math.Max(e.BinLength, 0) < f.Bin) return false;
            // a session since the slot was left empty: what it wrote decides whether the slot is filled again
            if (m.PastHeadroom && e.BinLength != f.Bin) return false;
            if (f.Keys == 0) continue;
            if (f.Broken is { } kept)
            {
                if (!kept.Matches(e)) return false;
                continue;
            }
            // Steam's merge grows the file: SCSKiller's entries then make room
            if (e.BinLength < 0 || e.BinLength > t.Limit || !e.TocRead || e.Sums(out _) is not { } sums) return false;
            // the game's copy of a key folds in front and replaces SCSKiller's
            var held = e.Records.Select((r, i) => new Placed(Key.Of(r.Key), sums[i])).ToHashSet();
            if (!ReadOurs(t.Name).All(held.Contains)) return false;
        }
        return DictionaryUpToDate(m, dir);
    }

    /// <summary>SCSKiller's entries in a file of Steam's that stayed there: the file can't be rewritten. Without
    /// <see cref="Why"/>: a file of its pair is missing, so which entries it holds can't be read.</summary>
    sealed record Stay(FileEntry File, HashSet<Placed> Ours, Print Print, string? Why)
    {
        public string Line(string name) => Why == null ? $"{name} can't be read, SCSKiller's entries stay listed"
            : Ours.Count == 0 ? $"{name} stays as it is, {Why}" : $"{Ours.Count(p => p.Key.B != DictTail):N0} pipelines stay in {name}, {Why}";
    }

    SteamPlaceResult RemoveLocked(CancellationToken ct)
    {
        if (Running()) throw new Refused(SteamPlaceState.GameRunning, "the game is running");
        var m = LoadManifest();
        if (Targets.Count == 0) return RemoveVkd3dAlone(m);
        if (m == null) return RemoveByNotes(ct);
        var leaf = m.Target.Length > 0 ? LeafOf(m.Target) : null;
        var (files, stayed) = m.Files.Count > 0 && Directory.Exists(m.Target) ? RemoveFrom(m, m.Target, ct) : ([], []);
        var vkd3d = RemoveVkd3d(m);
        var line = $"taken out of Steam's folder: " + string.Join(", ", files.Select(f => $"{f.Name} {f.Pipelines:N0} pipelines ({Format.Bytes(f.Bytes)})")
            .DefaultIfEmpty("nothing of SCSKiller's was left there")) + (vkd3d != null ? $", vkd3d: {vkd3d}" : "");
        foreach (var f in m.Files.Where(f => Keyed(f) && !stayed.Any(s => s.File == f))) File.Delete(OursFile(f.Name));
        if (stayed.Count == 0)
        {
            File.Delete(ManifestFile);
            ForgetDictionaries();
            Log?.Invoke(line);
            return new(SteamPlaceState.Removed, leaf, files, Vkd3d: vkd3d);
        }
        var why = string.Join(", ", stayed.Select(s => s.Line(s.File.Name)));
        var failed = new SteamPlaceResult(stayed.All(s => s.File.Slot) ? SteamPlaceState.Unmerged : SteamPlaceState.Failed, leaf, files, Vkd3d: vkd3d, Why: why);
        foreach (var s in stayed)
        {
            WriteOurs(s.File.Name, s.Ours);
            (s.File.Keys, s.File.Broken) = (s.Ours.Count, s.Print);
        }
        // Steam folds a slot left in the folder into the merged file: the next removal takes its entries out of that
        m.Files = [.. stayed.Select(s => s.File), .. stayed.Any(s => s.File.Slot) ? m.Files.Where(f => f.SteamFirst && !stayed.Any(s => s.File == f)).Select(f => f.Emptied()) : []];
        m.Vkd3d = [];
        (m.Pending, m.Vkd3dSources, m.Last) = (false, null, failed);
        SaveManifest(m);
        Log?.Invoke($"{line}, {why}");
        return failed;
    }

    /// <summary>Without <see cref="Targets"/>: vkd3d-proton's files placed, still as written, go, and nothing else. An NVIDIA
    /// placement the manifest lists stays listed.</summary>
    SteamPlaceResult RemoveVkd3dAlone(Manifest? m)
    {
        if (m == null) return new(SteamPlaceState.Nothing);
        var vkd3d = RemoveVkd3d(m);
        (m.Vkd3d, m.Vkd3dSources) = ([], null);
        if (m.Target.Length == 0 && m.Files.Count == 0) File.Delete(ManifestFile);
        else SaveManifest(m);
        Log?.Invoke($"vkd3d-proton: {vkd3d ?? "nothing of SCSKiller's was placed"}");
        return new(SteamPlaceState.Removed, Vkd3d: vkd3d);
    }

    static string LeafOf(string target) => $"{Path.GetFileName(Path.GetDirectoryName(target))}/{Path.GetFileName(target)}";

    /// <summary>Without the manifest: the files of the warm's alone that an owner note of this SCSKiller in Steam's folders
    /// lists, still as written. SCSKiller's entries in Steam's merged file stay, as only the manifest's folder lists them.</summary>
    SteamPlaceResult RemoveByNotes(CancellationToken ct)
    {
        var root = Path.Combine(NvidiaRoot, "GLCache");
        List<(string Target, OwnerNote Note)> owned;
        using (var glCache = OpenLeaf(root, make: false, glCache: true))
            owned = glCache == null ? [] : [.. from a in Folders(glCache.At, 32) from b in Folders(a.FullName, 16) let note = ReadNote(b.FullName) where note != null && Owns(note)
                select (Path.Combine(root, a.Name, b.Name), note)];
        if (owned.Count == 0) return new(SteamPlaceState.Nothing);
        var gone = new List<SteamFilePlaced>();
        var stayed = new List<string>();
        var slotsOnly = true;
        foreach (var (target, note) in owned)
        {
            var m = new Manifest
            {
                Target = target,
                // Steam's merged file too, which may have to keep SCSKiller's dictionary; a slot by its print and identity
                Files = [.. Targets.Where(t => !t.Slot && (t.SteamFirst || note.Files.ContainsKey(t.Name)))
                    .Select(t => new FileEntry { Name = t.Name, SteamFirst = t.SteamFirst, Ours = t.SteamFirst ? [] : note.Files[t.Name] }),
                    .. note.Files.Keys.Select(n => NoteSlot(note, n)).OfType<FileEntry>()],
            };
            var (went, stays) = RemoveFrom(m, target, ct);
            gone.AddRange(went);
            stayed.AddRange(stays.Select(s => s.Line(Path.Combine(LeafOf(target), s.File.Name))));
            slotsOnly &= stays.All(s => s.File.Slot);
        }
        var line = "taken out of Steam's folder by its owner note: " + string.Join(", ", gone.Select(f => $"{f.Name} {f.Pipelines:N0} pipelines ({Format.Bytes(f.Bytes)})")
            .DefaultIfEmpty("nothing of SCSKiller's was left there"));
        if (stayed.Count == 0)
        {
            Log?.Invoke(line);
            return new(SteamPlaceState.Removed, LeafOf(owned[^1].Target), gone);
        }
        var why = string.Join(", ", stayed);
        Log?.Invoke($"{line}, {why}");
        return new(slotsOnly ? SteamPlaceState.Unmerged : SteamPlaceState.Failed, LeafOf(owned[^1].Target), gone, Why: why);
    }

    /// <summary>Takes SCSKiller's files and entries out of <paramref name="target"/> as <paramref name="m"/> lists them:
    /// each of its own files that is still what it wrote, and its entries from Steam's files, Steam's kept byte for byte and
    /// in order. Returns what went from each, and what stayed in a file of Steam's that can't be rewritten.</summary>
    (List<SteamFilePlaced> Gone, List<Stay> Stayed) RemoveFrom(Manifest m, string target, CancellationToken ct)
    {
        Check(target, null);
        using var dir = OpenLeaf(target, make: false);
        if (dir == null) return ([], []);
        Lock(dir, null);
        var gone = new List<SteamFilePlaced>();
        var stayed = new List<Stay>();
        var known = new HashSet<FileId>();
        var outs = new List<Out>();
        using var files = new LeafFiles(dir, outs);
        try
        {
            Check(dir.At, null);
            Recover(dir, null);
            CleanTemps(dir.At);
            if (SlotTarget != null) files.Watch();
            var note = ReadNote(dir.At);
            var recorded = RecordedHashes(m, note, dir.Path);
            foreach (var f in m.Files)
            {
                var o = new Out(new SteamTarget(f.Name, NvidiaLinux.CacheFileIndexed, f.SteamFirst), Existing.Read(dir.At, f.Name, walk: f.SteamFirst));
                outs.Add(o);
                var e = o.Existing;
                if (!e.Exists) continue;
                if (!f.SteamFirst)
                {
                    if (f.SteamOwns) continue;
                    if (f.Slot ? !SlotOurs(f, e) : !f.Ours.Any(p => p.Matches(e)))
                    {
                        // a slot that isn't as SCSKiller wrote it stays, and is SCSKiller's to wait for until Steam folds it
                        if (f.Slot) stayed.Add(f.Keys > 0 ? new(f, ReadOurs(f.Name), Print.Of(e), "the game added to it, Steam folds it into the merged file at the game's exit")
                            : new(f, [], Print.Of(e), (f.Unproven ? "SCSKiller's note doesn't record its identity" : "it isn't as SCSKiller wrote it")
                                + ", Steam folds it into the merged file at the game's exit"));
                        continue;
                    }
                    (o.Ours, o.Write) = (true, true);
                    gone.Add(new(f.Name, e.Records.Count(r => !r.IsDictionary), e.BinLength - GlCacheFile.HeaderSize));
                    continue;
                }
                var ours = f.Keys > 0 ? ReadOurs(f.Name) : [];
                ours.UnionWith(FoldedOurs(m));
                if (ours.Count == 0) continue;
                string? bad = null;
                var sums = e.Broken == null ? e.Sums(out bad) : null;
                if ((e.Broken ?? bad) is { } why)
                {
                    if (e.Still(ours) is { Count: > 0 } still) stayed.Add(new(f, still, Print.Of(e), e.Whole ? why : null));
                    continue;
                }
                var mine = OursAt(e.Records, sums!, ours);
                KeepDictionary(f.Name, e, mine, recorded);
                o.Steam.AddRange(e.Records.Where((_, i) => !mine[i]));
                o.Mine.AddRange(e.Records.Where((_, i) => mine[i]));
                o.End = GlCacheFile.HeaderSize + o.SteamBytes;
                if (o.End > NvidiaLinux.CacheFileIndexed)
                {
                    o.Steam.Clear();
                    o.Mine.Clear();
                    if (e.Still(ours) is { Count: > 0 } still) stayed.Add(new(f, still, Print.Of(e), "Steam's and the game's entries alone are past what its .toc indexes"));
                    continue;
                }
                o.Write = o.Steam.Count < e.Records.Count;
                if (o.Write) gone.Add(new(f.Name, e.Records.Where((r, i) => mine[i] && !r.IsDictionary).Count(), e.Records.Sum(r => r.Length) - o.SteamBytes, o.Steam.Count, o.SteamBytes));
            }
            // the dictionary SCSKiller's slots have their entries encoded with is one it placed, whatever is recorded
            recorded.UnionWith(PlacedByFrames(outs, SlotFramed(dir, m.Files.Where(f => f.Slot).Select(f => f.Name)), RecordedIds(m, note, dir.Path)));
            KeepRecorded(files, outs, removal: true, recorded);
            var need = outs.Where(o => o.Write && o.Count > 0).Sum(o => o.End + GlCacheFile.HeaderSize + (long)GlCacheFile.RecordSize4 * o.Count);
            if (need > 0 && FreeSpace(target) is var free && free < need + Margin)
                throw new Refused(SteamPlaceState.NoSpace, $"{Format.Bytes(need)} to write and {Format.Bytes(free)} free");
            foreach (var o in outs.Where(o => o.Write && o.Count > 0)) WriteTemp(dir.At, o, null, known, ct);
            Check(dir.At, null);
            if (outs.FirstOrDefault(o => !o.Existing.Unchanged(dir.At, o.Target.Name)) is { } changed)
                throw new Refused(SteamPlaceState.SteamBusy, $"{changed.Target.Name} changed meanwhile");
            files.Check(null);
            Commit(dir, outs, known, null, files);
            // the note keeps the slots that stay, so a later removal still knows them
            WriteNote(dir.At, note, new Manifest { Files = [.. stayed.Where(s => s.File.Slot).Select(s => s.File)] });
            return (gone, stayed);
        }
        finally
        {
            foreach (var o in outs) o.Existing.Dispose();
            CleanUp(dir, known);
        }
    }

    /// <summary>The note in Steam's folder of the SCSKiller whose manifest is <see cref="Owner"/>: the prints of its files of
    /// the warm's alone there, so a lost manifest doesn't leave them as Steam's.</summary>
    sealed class OwnerNote
    {
        public string Owner { get; set; } = "";
        public Dictionary<string, List<Print>> Files { get; set; } = [];
        public uint? DictionaryId { get; set; }
        public string? DictionaryHash { get; set; }
        public uint? WasDictionaryId { get; set; }   // one SCSKiller placed before, which stays in the leaf
        public string? WasDictionaryHash { get; set; }
        public Dictionary<string, SlotId> Slots { get; set; } = [];   // each slot's identity as placed
    }

    /// <summary>A slot's files as placed: their identities and lengths, which a copy doesn't have.</summary>
    sealed record SlotId(Libc.FileStat TocStat, Libc.FileStat BinStat, long Toc, long Bin);

    OwnerNote? ReadNote(string target)
    {
        var path = Path.Combine(target, NoteName);
        try { return ReadSmall(path) is { } bytes ? JsonSerializer.Deserialize<OwnerNote>(bytes, AppStore.Json) : null; }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log?.Invoke($"Steam's folder's {NoteName} doesn't read, written anew: {e.Message}");
            return null;
        }
    }

    /// <summary>The note is this SCSKiller's, or that of one whose manifest is gone.</summary>
    bool Owns(OwnerNote note) => note.Owner == ManifestFile || !File.Exists(note.Owner);

    bool NoteOwns(OwnerNote? note, string name, Existing e) => note != null && Owns(note) && note.Files.GetValueOrDefault(name)?.Any(p => p.Matches(e)) == true;

    /// <summary>Writes the folder's note as <paramref name="m"/> lists SCSKiller's files of the warm's alone, deleted once it
    /// lists none. A note of another SCSKiller whose manifest is there stays.</summary>
    void WriteNote(string target, OwnerNote? was, Manifest m)
    {
        if (was != null && !Owns(was)) return;
        var path = Path.Combine(target, NoteName);
        var files = m.Files.Where(f => !f.SteamFirst && f.Ours.Count > 0).ToDictionary(f => f.Name, f => f.Ours);
        if (files.Count == 0) File.Delete(path);
        else AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new OwnerNote
        {
            Owner = ManifestFile, Files = files, DictionaryId = m.Dictionary?.Id, DictionaryHash = m.Dictionary?.Hash,
            WasDictionaryId = m.WasDictionary?.Id, WasDictionaryHash = m.WasDictionary?.Hash,
            Slots = m.Files.Where(f => f.Slot && f.Ours.Count > 0 && !f.Unproven && f is { TocStat: not null, BinStat: not null })
                .ToDictionary(f => f.Name, f => new SlotId(f.TocStat!.Value, f.BinStat!.Value, f.Toc, f.Bin)),
        }, AppStore.Json), ownerOnly: true);   // its temp name is created O_EXCL: a link planted there is never written through
    }

    internal const string Vkd3dCache = "vkd3d-proton.cache", Vkd3dWrite = "vkd3d-proton.cache.write";
    const int Vkd3dHeader = 48, Vkd3dEntryHeader = 24;   // vkd3d-proton's stream archive: a header, then entries of 24 bytes and their data, 8-aligned

    /// <summary>Places the warm's vkd3d-proton cache when it changed since the last placement. In a folder with neither of
    /// the game's files: the warm's .cache and .write under the game's names. With the game's .cache alone: the warm's,
    /// merged, as the game's .write, whose new keys vkd3d-proton adds to its .cache at the game's next start. With the game's
    /// .write, which vkd3d-proton hasn't merged yet: nothing until it has. Its files placed in another folder, still as
    /// written, go, also with nothing to place. Returns what it did or why it waits.</summary>
    (string? What, bool Waits, bool Placed) PlaceVkd3d(Manifest m)
    {
        if (Vkd3dRoot == null) return (null, false, false);
        if (Vkd3dSource == null || Vkd3dDir == null) return TakeBackVkd3d(m);
        var sources = new[] { Vkd3dSource, Vkd3dSource + ".write" }.Where(IsFile).ToList();
        if (sources.Count == 0) return TakeBackVkd3d(m);
        if (Running()) return ($"{Vkd3dDir} waits for the game to close", true, false);
        var fingerprint = Vkd3dDir + ";" + string.Join(';', sources.Select(s => $"{Path.GetFileName(s)}:{new FileInfo(s).Length}:{File.GetLastWriteTimeUtc(s).Ticks}"));
        string cache = Path.Combine(Vkd3dDir, Vkd3dCache), write = Path.Combine(Vkd3dDir, Vkd3dWrite);
        // vkd3d-proton merges the .write into the .cache at the game's start: either one is what was placed
        if (m.Vkd3dSources == fingerprint && (File.Exists(cache) || File.Exists(write))) return ($"{Vkd3dDir} is up to date", false, false);
        using var dir = OpenVkd3d(Vkd3dDir);
        if (dir == null) return ($"{Vkd3dDir} is missing", false, false);
        if (Held([dir[Vkd3dCache], dir[Vkd3dWrite], .. m.Vkd3d.Select(v => v.Path)]) == true) return ($"a process has {Vkd3dDir}'s vkd3d-proton cache open", true, false);
        var placedWrite = m.Vkd3d.FirstOrDefault(v => v.Path == write);
        var hasWrite = File.Exists(dir[Vkd3dWrite]);
        var ourWrite = hasWrite ? placedWrite?.Still(dir[Vkd3dWrite]) : null;
        if (hasWrite && ourWrite == null) return ($"{Vkd3dDir}: the game's {Vkd3dWrite} waits for its next start", true, false);
        var size = sources.Sum(s => new FileInfo(s).Length);
        if (FreeSpace(Vkd3dDir) is var free && free < size + Margin) return ($"{Vkd3dDir}: {Format.Bytes(size)} to write and {Format.Bytes(free)} free", true, false);
        CleanTemps(dir.At);
        var known = new HashSet<FileId>();
        var placed = new List<Vkd3dFile>();
        // printed through its own handle before it had a name: what holds the name now is SCSKiller's only if it is that file
        void Keep(string name, Made made)
        {
            if (Same(Libc.Stat(dir[name]), made.Stat)) placed.Add(new(Path.Combine(Vkd3dDir, name), made.Length, made.Hash, Vkd3dRoot));
            else Log?.Invoke($"{Path.Combine(Vkd3dDir, name)} changed as it took its name: the game's");
        }
        string what;
        try
        {
            if (!File.Exists(dir[Vkd3dCache]) && !hasWrite)
            {
                try
                {
                    foreach (var s in sources)
                    {
                        var name = s.EndsWith(".write", StringComparison.Ordinal) ? Vkd3dWrite : Vkd3dCache;
                        var made = Copy(s, dir[TempPrefix + name], known);
                        if (Running()) throw new Refused(SteamPlaceState.GameRunning, "the game started");
                        if (Libc.RenameNew(dir[TempPrefix + name], dir[name]) is var errno and not 0) throw Naming(errno, name);
                        Keep(name, made);
                    }
                }
                catch when (placed.Count > 0)
                {
                    m.Vkd3d = [.. m.Vkd3d.Where(v => placed.All(p => p.Path != v.Path)), .. placed];   // what took its name stays SCSKiller's
                    SaveManifest(m);
                    throw;
                }
                what = $"placed {string.Join(" and ", placed.Select(p => Path.GetFileName(p.Path)))} in {Vkd3dDir}";
            }
            else
            {
                var temp = dir[TempPrefix + "vkd3d"];
                var made = sources.Count == 1 ? Copy(sources[0], temp, known) : Merge(sources[0], sources[1], temp, known);
                if (Running()) throw new Refused(SteamPlaceState.GameRunning, "the game started");
                if (ourWrite is { } stat)
                {
                    // the .write placed before and ours, held as Commit holds Steam's files
                    using HeldFile old = Hold(dir[Vkd3dWrite], h => Libc.Stat(h) == stat && RandomAccess.GetLength(h) == placedWrite!.Length),
                        ours = Hold(temp, h => Libc.Stat(h) == made.Stat, Vkd3dWrite);
                    var named = Swap(temp, dir[Vkd3dWrite], stat, known);
                    if (old.Broken || ours.Broken)
                    {
                        Undo([named], [old, ours]);
                        throw new Refused(SteamPlaceState.SteamBusy, $"a process opened {Vkd3dWrite} meanwhile");
                    }
                }
                else if (Libc.RenameNew(temp, dir[Vkd3dWrite]) is var errno and not 0) throw Naming(errno, Vkd3dWrite);
                Keep(Vkd3dWrite, made);
                what = $"placed {Vkd3dWrite} in {Vkd3dDir} for the game to merge at its next start";
            }
            Libc.SyncFolder(dir.At);
        }
        finally { CleanUp(dir, known); }
        m.Vkd3d = [.. m.Vkd3d.Where(v => placed.All(p => p.Path != v.Path)), .. placed];
        m.Vkd3dSources = fingerprint;
        SaveManifest(m);
        foreach (var v in m.Vkd3d.Except(placed).ToList())
        {
            var name = Path.GetFileName(v.Path);
            if (Path.GetDirectoryName(v.Path) == Vkd3dDir)
            {
                if (v.Still(dir[name]) == null) m.Vkd3d.Remove(v);   // the game merged or replaced it: its own now
                continue;
            }
            TakeElsewhere(m, v);
        }
        SaveManifest(m);
        return (what, false, true);
    }

    /// <summary>With nothing to place: takes out the vkd3d-proton files placed in a folder other than <see cref="Vkd3dDir"/>
    /// (any, on the Vulkan route), still as written, unless the game runs or a process holds one.</summary>
    (string? What, bool Waits, bool Placed) TakeBackVkd3d(Manifest m)
    {
        var elsewhere = m.Vkd3d.Where(v => Path.GetDirectoryName(v.Path) != Vkd3dDir).ToList();
        if (elsewhere.Count == 0) return (null, false, false);
        if (Running()) return ($"{string.Join(" and ", elsewhere.Select(v => v.Path))} wait for the game to close", true, false);
        if (Held([.. elsewhere.Select(v => v.Path)]) == true) return ($"a process has {string.Join(" or ", elsewhere.Select(v => v.Path))} open", true, false);
        var taken = new List<string>();
        foreach (var v in elsewhere)
            if (TakeElsewhere(m, v)) taken.Add(v.Path);
        SaveManifest(m);
        var stay = m.Vkd3d.Intersect(elsewhere).Any();
        return (taken.Count > 0 ? $"deleted {string.Join(" and ", taken)}" : stay ? "the vkd3d-proton cache placed before stays" : null, stay, taken.Count > 0);
    }

    /// <summary>Takes <paramref name="v"/>, placed in a folder that isn't <see cref="Vkd3dDir"/>, out of it while still as written,
    /// and out of the manifest; true when it deleted it.</summary>
    bool TakeElsewhere(Manifest m, Vkd3dFile v)
    {
        var name = Path.GetFileName(v.Path);
        try
        {
            using var other = OpenVkd3d(Path.GetDirectoryName(v.Path)!, v.Root);
            var taken = false;
            if (other != null && v.Still(other[name]) is { } s)
            {
                CleanTemps(other.At);
                Take(other, name, s, v.Length);
                taken = true;
            }
            m.Vkd3d.Remove(v);
            return taken;
        }
        catch (Exception e) when (e is Refused or IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"{v.Path} stays: {e.Message}");
            return false;
        }
    }

    /// <summary>Deletes each vkd3d-proton file placed that is still as it was written; returns what it did.</summary>
    string? RemoveVkd3d(Manifest m)
    {
        if (m.Vkd3d.Count == 0) return null;
        if (Running()) throw new Refused(SteamPlaceState.GameRunning, "the game is running");
        var deleted = new List<string>();
        foreach (var v in m.Vkd3d)
        {
            if (!File.Exists(v.Path)) continue;
            using var dir = OpenVkd3d(Path.GetDirectoryName(v.Path)!, v.Root);
            var name = Path.GetFileName(v.Path);
            if (dir == null || v.Still(dir[name]) is not { } s) continue;
            if (Held([dir[name]]) == true) throw new Refused(SteamPlaceState.GameRunning, $"{v.Path} is open in another process");
            CleanTemps(dir.At);
            Take(dir, name, s, v.Length);
            deleted.Add(v.Path);
        }
        return deleted.Count == 0 ? "the game's vkd3d-proton cache has merged what was placed, left as it is" : $"deleted {string.Join(" and ", deleted)}";
    }

    /// <summary>A file written, printed through its own handle: its length, SHA-256 and identity.</summary>
    sealed record Made(long Length, string Hash, Libc.FileStat Stat)
    {
        public static Made Of(FileStream f)
        {
            f.Flush(flushToDisk: true);
            var (length, hash) = Fingerprint(f.SafeFileHandle);
            return new(length, hash, Libc.Stat(f.SafeFileHandle) ?? throw new IOException($"{f.Name} can't be read"));
        }
    }

    /// <summary>The file's length and SHA-256, read through <paramref name="h"/>.</summary>
    static (long Length, string Hash) Fingerprint(SafeFileHandle h, long max = long.MaxValue)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long at = 0;
        for (int n; at < max && (n = RandomAccess.Read(h, buffer.AsSpan(0, (int)Math.Min(buffer.Length, max - at)), at)) > 0; at += n) sha.AppendData(buffer, 0, n);
        return (at, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>A synced copy at <paramref name="to"/>, sharing <paramref name="from"/>'s extents where the file system can.</summary>
    static Made Copy(string from, string to, HashSet<FileId> known)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        using var dst = Create(to, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, known);
        if (Libc.Clone(dst.SafeFileHandle, src.SafeFileHandle) != 0) src.CopyTo(dst, 1 << 20);
        return Made.Of(dst);
    }

    /// <summary>One archive of the warm's .cache and .write, as vkd3d-proton merges them: the .cache's whole entries, then the
    /// .write's whose hash and type it lacks. Archives of another header (another vkd3d-proton or driver): the newer alone.</summary>
    static Made Merge(string cache, string write, string to, HashSet<FileId> known)
    {
        using var a = File.OpenHandle(cache, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = File.OpenHandle(write, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] ha = new byte[Vkd3dHeader], hb = new byte[Vkd3dHeader];
        if (RandomAccess.Read(a, ha, 0) != Vkd3dHeader || RandomAccess.Read(b, hb, 0) != Vkd3dHeader || !ha.AsSpan().SequenceEqual(hb))
            return Copy(File.GetLastWriteTimeUtc(write) > File.GetLastWriteTimeUtc(cache) ? write : cache, to, known);
        using var dst = Create(to, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, known);
        dst.Write(ha);
        var seen = new HashSet<(ulong, uint)>();
        var buffer = new byte[1 << 16];
        Span<byte> head = stackalloc byte[Vkd3dEntryHeader];
        foreach (var (h, keep) in new[] { (a, false), (b, true) })
        {
            var length = RandomAccess.GetLength(h);
            for (long at = Vkd3dHeader; at + Vkd3dEntryHeader <= length;)
            {
                if (RandomAccess.Read(h, head, at) != Vkd3dEntryHeader) break;
                var size = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
                var whole = Vkd3dEntryHeader + ((size + 7L) & ~7L);
                if (size > MaxEntry || at + whole > length) break;   // a sliced or broken entry ends what vkd3d-proton reads too
                if (seen.Add((BinaryPrimitives.ReadUInt64LittleEndian(head), BinaryPrimitives.ReadUInt32LittleEndian(head[20..]))) || !keep)
                {
                    if (whole > buffer.Length) buffer = new byte[whole];
                    if (RandomAccess.Read(h, buffer.AsSpan(0, (int)whole), at) != whole) break;
                    dst.Write(buffer, 0, (int)whole);
                }
                at += whole;
            }
        }
        return Made.Of(dst);
    }

    /// <summary>What a placement wrote: the leaf it wrote into, under which targets, from which of the warm's sets; each
    /// file's own prints (<see cref="Print"/>) or the count of SCSKiller's entries in a file of Steam's (listed in its
    /// <see cref="OursFile"/>); the vkd3d-proton files it placed. <see cref="Pending"/>: written before the files take their
    /// names, so the earlier files and the new ones both count as SCSKiller's until it is written again.</summary>
    sealed class Manifest
    {
        public string Target { get; set; } = "";
        public string Config { get; set; } = "";
        public string Sources { get; set; } = "";
        public bool Pending { get; set; }
        public DateTimeOffset At { get; set; }
        public List<FileEntry> Files { get; set; } = [];
        public string? Vkd3dSources { get; set; }
        public List<Vkd3dFile> Vkd3d { get; set; } = [];
        public SteamPlaceResult? Last { get; set; }
        public DictPlaced? Dictionary { get; set; }
        public DictPlaced? WasDictionary { get; set; }   // one SCSKiller placed before, which stays in the leaf: while Pending, the one its files held
        public SteamEntryCoding Coded { get; set; }   // Stored also when the leaf's dictionary couldn't be had (NoDictionary)
        public string? NoDictionary { get; set; }
        public SlotSeen? Slot { get; set; }
        public bool PastHeadroom { get; set; }   // a session wrote more than the slot's headroom: the slot stays empty until one that didn't
        public long? Measured { get; set; }   // Steam's and the game's bytes in the merged file as last read, which a session's are measured from

        public Manifest Copy() => new()
        {
            Target = Target, Config = Config, Sources = Sources, Pending = Pending, At = At, Vkd3dSources = Vkd3dSources, Vkd3d = [.. Vkd3d], Last = Last, Dictionary = Dictionary,
            WasDictionary = WasDictionary, Coded = Coded, NoDictionary = NoDictionary, Slot = Slot, PastHeadroom = PastHeadroom, Measured = Measured,
        };
    }

    /// <summary>A target file as a placement left it. <see cref="Broken"/>: a file of Steam's left as it is, holding
    /// SCSKiller's entries, up to date until it changes.</summary>
    sealed class FileEntry
    {
        public string Name { get; set; } = "";
        public bool SteamFirst { get; set; }
        public bool SteamOwns { get; set; }
        public List<Print> Ours { get; set; } = [];
        public int Keys { get; set; }
        public Print? Broken { get; set; }
        public long Bin { get; set; }   // the .bin's length as placed
        public bool Dictionary { get; set; }   // it holds the leaf's dictionary SCSKiller's entries are encoded with
        public bool Slot { get; set; }   // the game's own slot: its entries, listed in its OursFile, end up in the merged file
        public long Toc { get; set; }    // the slot's .toc length as placed
        public Libc.FileStat? TocStat { get; set; }   // the slot's files as placed: the game's appends change their change time
        public Libc.FileStat? BinStat { get; set; }
        public List<SlotPair>? Sides { get; set; }   // while pending: the slot's pair as read and as written, either may be at the names
        [JsonIgnore] public bool Unproven { get; init; }   // a slot the owner note lists without its identity: never SCSKiller's
        [JsonIgnore] public HashSet<Placed>? Found { get; set; }   // a slot's entries read from it, to be listed as its keys

        public FileEntry Emptied() => new() { Name = Name, SteamFirst = SteamFirst, SteamOwns = SteamOwns, Bin = Bin, Dictionary = Dictionary };
    }

    /// <summary>A file set as it was: its .toc's and .bin's lengths and its .toc's SHA-256.</summary>
    sealed record Print(long Toc, long Bin, string TocHash)
    {
        public static Print Of(string set)
        {
            var toc = ReadSmall(set + ".toc", MaxToc) ?? throw new FileNotFoundException(set + ".toc");
            using var bin = Libc.OpenEntry(set + ".bin")?.File ?? throw new IOException($"{Path.GetFileName(set)}.bin isn't a regular file");
            return new(toc.Length, RandomAccess.GetLength(bin), Convert.ToHexString(SHA256.HashData(toc)));
        }

        public static Print Of(Existing e) => new(e.TocLength, e.BinLength, e.TocHash);

        public bool Matches(Existing e) => e.Exists && e.TocLength == Toc && e.BinLength == Bin && e.TocHash == TocHash;
    }

    /// <summary>A vkd3d-proton file placed: its path, length and SHA-256, and the folder it was opened from
    /// (<see cref="Vkd3dRoot"/>; null: the current one).</summary>
    sealed record Vkd3dFile(string Path, long Length, string Hash, string? Root = null)
    {
        /// <summary>The file's identity, read at <paramref name="at"/> without following a link, while it is still as written
        /// and the same before and after its bytes are read; null once it isn't.</summary>
        public Libc.FileStat? Still(string at)
        {
            try
            {
                using var h = Libc.OpenNoFollow(at);
                var was = Libc.Stat(h);
                if (RandomAccess.GetLength(h) != Length) return null;
                var (length, hash) = Fingerprint(h, Length + 1);   // a file still growing is never hashed to its end
                return was != null && Libc.Stat(h) == was && length == Length && hash == Hash ? was : null;
            }
            catch (IOException) { return null; }
        }
    }

    Manifest? LoadManifest() => Load(ManifestFile);

    static Manifest? Load(string file) => File.Exists(file) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(file), AppStore.Json) : null;

    /// <summary>The vkd3d-proton files a placement of the game whose data folder is <paramref name="gameDir"/> wrote: a file
    /// of that name elsewhere is the game's own.</summary>
    public static IReadOnlySet<string> Vkd3dPlaced(string gameDir)
    {
        try { return Load(ManifestOf(gameDir))?.Vkd3d.Select(v => v.Path).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new HashSet<string>(); }
    }

    void SaveManifest(Manifest m) => AppStore.WriteAtomic(ManifestFile, JsonSerializer.SerializeToUtf8Bytes(m, AppStore.Json), ownerOnly: true);

    /// <summary>The last result a placement recorded, for a status before this process placed anything; null without one.</summary>
    public SteamPlaceResult? Last()
    {
        try { return LoadManifest() is { Pending: false } m ? m.Last : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    HashSet<Placed> ReadOurs(string name)
    {
        var path = OursFile(name);
        if (!File.Exists(path))
        {
            Log?.Invoke($"{path} is missing: SCSKiller's entries in {name} count as Steam's");
            return [];
        }
        var bytes = File.ReadAllBytes(path);
        var ours = new HashSet<Placed>(bytes.Length / Placed.Size);
        for (var at = 0; at + Placed.Size <= bytes.Length; at += Placed.Size) ours.Add(Placed.Of(bytes.AsSpan(at, Placed.Size)));
        return ours;
    }

    void WriteOurs(string name, IReadOnlyCollection<Placed> ours)
    {
        var bytes = new byte[ours.Count * Placed.Size];
        var at = 0;
        foreach (var p in ours)
        {
            p.Write(bytes.AsSpan(at, Placed.Size));
            at += Placed.Size;
        }
        AppStore.WriteAtomic(OursFile(name), bytes, ownerOnly: true);
    }

    /// <summary>A cache key, the 16 bytes a .toc record starts with.</summary>
    readonly record struct Key(ulong A, ulong B)
    {
        public static Key Of(ReadOnlySpan<byte> k) => new(BinaryPrimitives.ReadUInt64LittleEndian(k), BinaryPrimitives.ReadUInt64LittleEndian(k[8..]));

        public void Write(Span<byte> to)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(to, A);
            BinaryPrimitives.WriteUInt64LittleEndian(to[8..], B);
        }
    }

    /// <summary>An entry SCSKiller placed: its key and its checksum (bytes 20-28 of the entry), which tell it from the game's
    /// own entry of that key.</summary>
    readonly record struct Placed(Key Key, ulong Sum)
    {
        public const int Size = 24;

        public static Placed Of(ReadOnlySpan<byte> b) => new(Key.Of(b), BinaryPrimitives.ReadUInt64LittleEndian(b[16..]));

        public void Write(Span<byte> to)
        {
            Key.Write(to);
            BinaryPrimitives.WriteUInt64LittleEndian(to[16..], Sum);
        }
    }

    static IEnumerable<DirectoryInfo> Folders(string path, int hex) =>
        new DirectoryInfo(path).EnumerateDirectories("*", NoLinks).Where(d => d.Name.Length == hex && d.Name.All(char.IsAsciiHexDigitLower));

    static bool IsFolder(string path) => new DirectoryInfo(path) is { Exists: true } d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint);

    static bool IsFile(string path) => new FileInfo(path) is { Exists: true } f && !f.Attributes.HasFlag(FileAttributes.ReparsePoint);

    static readonly EnumerationOptions NoLinks = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Why nothing was placed or taken out, and the state that says it.</summary>
    sealed class Refused(SteamPlaceState state, string why, string? leaf = null) : Exception(why)
    {
        public SteamPlaceState State { get; } = state;
        public string? Leaf { get; } = leaf;
        public bool Once { get; init; }   // it stays until someone acts: logged once
    }
}
