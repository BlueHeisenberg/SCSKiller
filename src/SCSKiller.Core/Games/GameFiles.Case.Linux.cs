using System.IO.Enumeration;

namespace SCSKiller.Core.Games;

public static partial class GameFiles
{
    public static partial string Find(string root, string rel)
    {
        var exact = Under(root, rel);
        if (Path.Exists(exact) || Path.IsPathRooted(rel.Replace('\\', '/'))) return exact;
        var path = root;
        var segments = rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (Entry(path, segments[i], folder: i < segments.Length - 1) is not { } name) return exact;
            path = Path.Combine(path, name);
        }
        return path;
    }

    public static partial string Rel(string root, string path, string found, string rel)
    {
        if (!path.StartsWith(found, StringComparison.Ordinal) || path.Length > found.Length && path[found.Length] != '/') return Rel(root, path);
        var dir = found;
        foreach (var _ in rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)) dir = Path.GetDirectoryName(dir) ?? "";
        return Rel(root, Under(dir, rel) + path[found.Length..]);
    }

    public static partial string ChangeExtension(string path, string extension) =>
        Path.ChangeExtension(path, extension) is var p && Path.GetDirectoryName(p) is { Length: > 0 } dir ? Find(dir, Path.GetFileName(p)) : p;

    public static partial IEnumerable<string> FindAll(string root, string rel)
    {
        if (Path.IsPathRooted(rel.Replace('\\', '/'))) return [Under(root, rel)];
        List<string> paths = [root];
        var segments = rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var (segment, folder) = (segments[i], i < segments.Length - 1);
            paths = [.. paths.SelectMany(p => Entries(p, segment, folder).Select(n => Path.Combine(p, n)))];
        }
        return paths;
    }

    static string? Entry(string dir, string name, bool folder) =>
        name is "." or ".." || (folder ? Directory.Exists(Path.Combine(dir, name)) : Path.Exists(Path.Combine(dir, name))) ? name : Entries(dir, name, folder).FirstOrDefault();

    // the entries named so but for case: the one spelled as given first, the others in ordinal order
    static List<string> Entries(string dir, string name, bool folder)
    {
        if (name is "." or "..") return [name];
        try
        {
            var all = new FileSystemEnumerable<string>(dir, (ref FileSystemEntry e) => e.FileName.ToString(), AllNames)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => (!folder || e.IsDirectory) && e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase),
            }.Order(StringComparer.Ordinal).ToList();
            return all.Remove(name) ? [name, .. all] : all;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    public static partial IEnumerable<string> EnumerateFiles(string dir, string pattern, SearchOption option) => EnumerateFiles(dir, pattern, Compatible(option));

    public static partial IEnumerable<string> EnumerateFiles(string dir, string pattern, EnumerationOptions options) =>
        Walk(dir, pattern, options, static (d, p, o) => Directory.EnumerateFiles(d, p, o), FileName);

    public static partial IEnumerable<string> EnumerateDirectories(string dir, string pattern, SearchOption option) => EnumerateDirectories(dir, pattern, Compatible(option));

    public static partial IEnumerable<string> EnumerateDirectories(string dir, string pattern, EnumerationOptions options) =>
        Walk(dir, pattern, options, static (d, p, o) => Directory.EnumerateDirectories(d, p, o), FileName);

    public static partial IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo dir, string pattern, SearchOption option) => EnumerateFiles(dir, pattern, Compatible(option));

    public static partial IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo dir, string pattern, EnumerationOptions options) =>
        Walk(dir.FullName, pattern, options, static (d, p, o) => new DirectoryInfo(d).EnumerateFiles(p, o), f => f.Name);

    public static partial IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo dir, string pattern, EnumerationOptions options) =>
        Walk(dir.FullName, pattern, options, static (d, p, o) => new DirectoryInfo(d).EnumerateDirectories(p, o), f => f.Name);

    internal static partial IEnumerable<FileSystemInfo>? MarkerEntries(DirectoryInfo dir, int left, Func<bool> over)
    {
        var read = new List<FileSystemInfo>();
        foreach (var e in dir.EnumerateFileSystemInfos("*", AllNames))
        {
            if (read.Count > left || over()) return null;
            read.Add(e);
        }
        return InNtfsOrder(read, e => e.Name);
    }

    // EnumerationOptions.Compatible, the options of the SearchOption forms: Win32 matching, nothing skipped, errors thrown
    static EnumerationOptions Compatible(SearchOption option) => new()
    {
        MatchType = MatchType.Win32, AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = option == SearchOption.AllDirectories,
    };

    /// <summary>One folder's matches in NTFS's order, then its subfolders' (queued in that order, links to folders left out),
    /// as FileSystemEnumerator walks on Windows. The root is listed on the call: .NET opens it there.</summary>
    static IEnumerable<T> Walk<T>(string dir, string pattern, EnumerationOptions options, Func<string, string, EnumerationOptions, IEnumerable<T>> list, Func<T, string> name)
    {
        var flat = new EnumerationOptions
        {
            AttributesToSkip = options.AttributesToSkip, BufferSize = options.BufferSize, IgnoreInaccessible = options.IgnoreInaccessible,
            MatchCasing = options.MatchCasing == MatchCasing.PlatformDefault ? MatchCasing.CaseInsensitive : options.MatchCasing,
            MatchType = options.MatchType, ReturnSpecialDirectories = options.ReturnSpecialDirectories,
        };
        if (!options.RecurseSubdirectories) return InNtfsOrder(list(dir, pattern, flat), name);
        // .NET moves a pattern's folder part into the directory before it recurses
        if (Path.GetDirectoryName(pattern.AsSpan()) is { Length: > 0 } sub)
            (dir, pattern) = (Path.Join(dir.AsSpan(), sub), pattern[(sub.Length + 1)..]);
        var first = InNtfsOrder(list(dir, pattern, flat), name);
        var pending = new Queue<(string Dir, int Depth, Above? Above)>();
        var above = Id(dir) is { } id ? new Above(id, null) : null;
        if (options.MaxRecursionDepth > 0)
            foreach (var s in Subfolders(dir, options)) pending.Enqueue((s, options.MaxRecursionDepth - 1, above));
        return Deeper(first, pending, pattern, options, flat, list, name);
    }

    static IEnumerable<T> Deeper<T>(List<T> first, Queue<(string Dir, int Depth, Above? Above)> pending, string pattern, EnumerationOptions options,
        EnumerationOptions flat, Func<string, string, EnumerationOptions, IEnumerable<T>> list, Func<T, string> name)
    {
        foreach (var x in first) yield return x;
        while (pending.TryDequeue(out var next))
        {
            // a link back into a folder above it: Windows follows a junction loop until the path is too long
            var id = Id(next.Dir);
            if (id is { } own && next.Above?.Has(own) == true) continue;
            List<T> found;
            List<string> subs;
            try
            {
                found = InNtfsOrder(list(next.Dir, pattern, flat), name);
                subs = next.Depth > 0 ? Subfolders(next.Dir, options) : [];
            }
            catch (DirectoryNotFoundException) { continue; }   // gone since its parent was listed: .NET passes over it too
            var above = id is { } here ? new Above(here, next.Above) : next.Above;
            foreach (var s in subs) pending.Enqueue((s, next.Depth - 1, above));
            foreach (var x in found) yield return x;
        }
    }

    // the folders .NET recurses into: those its options don't skip, "." and ".." aside. Never a link: a Wine prefix's dosdevices/z: is /
    static List<string> Subfolders(string dir, EnumerationOptions options) => InNtfsOrder(new DirectoryInfo(dir).EnumerateDirectories("*",
        new EnumerationOptions { AttributesToSkip = options.AttributesToSkip, BufferSize = options.BufferSize, IgnoreInaccessible = options.IgnoreInaccessible })
        .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Select(d => d.FullName), FileName);

    static (uint, uint, ulong)? Id(string dir) => App.Libc.Stat(dir) is { } s ? (s.DevMajor, s.DevMinor, s.Inode) : null;

    /// <summary>A folder's device and inode, and the folders above it on the walk's way to it.</summary>
    sealed record Above((uint, uint, ulong) Id, Above? Parent)
    {
        public bool Has((uint, uint, ulong) id)
        {
            for (var a = this; a != null; a = a.Parent)
                if (a.Id == id) return true;
            return false;
        }
    }

    static string FileName(string path) => Path.GetFileName(path);

    static List<T> InNtfsOrder<T>(IEnumerable<T> entries, Func<T, string> name) => entries.OrderBy(name, NtfsOrder).ToList();

    // NTFS keeps a folder's names in upper-cased ordinal order; two names it can't hold together keep an ordinal order
    static readonly IComparer<string> NtfsOrder = Comparer<string>.Create((a, b) =>
        StringComparer.OrdinalIgnoreCase.Compare(a, b) is var order and not 0 ? order : StringComparer.Ordinal.Compare(a, b));
}
