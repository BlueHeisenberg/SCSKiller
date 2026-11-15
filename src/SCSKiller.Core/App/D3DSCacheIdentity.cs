using System.Runtime.InteropServices;
using System.Text;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>An app_id row's identity, raw as the runtime writes it; no identity is null, null, 0, 0.</summary>
public sealed record AppIdentity(string? Name, string? Engine, long Version, long EngineVersion)
{
    public static readonly AppIdentity None = new(null, null, 0, 0);

    /// <summary>Values a db and a report take: no identity, or a name (may be empty) and an engine (may be empty or none) of at
    /// most 256 UTF-16 units with no control character or lone surrogate, and versions not negative.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Valid => Version >= 0 && EngineVersion >= 0
        && (Name == null ? Engine == null && Version == 0 && EngineVersion == 0 : (Engine == null || Text(Engine)) && Text(Name));

    static readonly UTF8Encoding Strict = new(false, true);

    static bool Text(string s)
    {
        if (s.Length > 256 || s.Any(char.IsControl)) return false;
        try { Strict.GetByteCount(s); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal string Set => $"app_name = {Q(Name)}, engine_name = {Q(Engine)}, app_version = {Version}, engine_version = {EngineVersion}";

    internal string Is => $"app_name is {Q(Name)} and engine_name is {Q(Engine)} and app_version is {Version} and engine_version is {EngineVersion}";

    static string Q(string? s) => s == null ? "null" : "'" + s.Replace("'", "''") + "'";
}

/// <summary>An outcome report's Advanced Shader Delivery block: a delivery registered for the game, the size of its delivered
/// dbs in MB, the first adapter family they were registered for, and whether one was compiled by this driver (null: not known).</summary>
public sealed record AsdReport(bool Present, long SizeMb, string? GpuFamily, bool? CompilerMatch)
{
    public static AsdReport Of(D3DSCache.Delivery? d, string? compiler)
    {
        if (d == null) return new(false, 0, null, null);
        var fits = D3DSCache.Fits(d, compiler);
        var family = d.Families?.Where(f => f is { Length: > 0 and <= 64 } && f.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).FirstOrDefault();
        return new(true, Math.Min(d.Bytes / (1024 * 1024), 10_000_000), family, fits);
    }
}

/// <summary>What the registry read found for an exe (<see cref="Kind"/>): no registration, one, registrations that differ, or a
/// key it couldn't read; <see cref="Registered"/>: the identities of the exe's registrations it could read.</summary>
public sealed record RegistryRead(string Kind, AppIdentity[] Registered)
{
    public const string None = "none", One = "registered", Differ = "differ", Unreadable = "unreadable";

    public bool Is(string kind, IEnumerable<AppIdentity>? registered) => Kind == kind && Registered.ToHashSet().SetEquals(registered ?? []);
}

/// <summary>What the fold stamps into a game's adapter db and why (<see cref="Source"/>): the registration's identity
/// ("registry"), a plan rule's ("rule", <see cref="Rule"/> its id), nothing ("none": the db goes in with what it has), or
/// the dbs stay out ("refused": registrations differ and no rule says which one the runtime takes).</summary>
public sealed record IdentityStamp(string Source, AppIdentity? Stamped, RegistryRead Read, string? Rule = null)
{
    public const string Registry = "registry", ByRule = "rule", Nothing = "none", Refused = "refused";
}

public static partial class D3DSCache
{
    /// <summary>The identity the fold gives <paramref name="exe"/>'s adapter db (the path its dbs name; an Xbox game's
    /// <paramref name="package"/> family): a rule of <paramref name="rules"/> made for exactly this <paramref name="read"/>
    /// first, matched on the exe's final path as the registry is, else the registration's, else none.</summary>
    public static IdentityStamp Decide(RegistryRead read, string exe, string? package, RulesFile rules)
    {
        // unreadable: measured, the runtime writes no identity then, so nothing is stamped and no rule applies
        if (OperatingSystem.IsWindows() && read.Kind != RegistryRead.Unreadable && rules.Identities.Count > 0 && Games.GameFiles.Identity(exe)?.Final is { } final
            && rules.IdentityFor(final, package, read) is { } r) return new(IdentityStamp.ByRule, r.Writes, read, r.Id);
        return read.Kind switch
        {
            RegistryRead.One => new(IdentityStamp.Registry, read.Registered[0], read),
            RegistryRead.Differ => new(IdentityStamp.Refused, null, read),
            RegistryRead.Unreadable when read.Registered.Length > 1 => new(IdentityStamp.Refused, null, read),
            _ => new(IdentityStamp.Nothing, null, read),
        };
    }

    /// <summary>The compiler (<see cref="CompilerSql"/>'s string) of the first adapter db in <paramref name="dir"/>, one of our
    /// staged warm's; null when none or unreadable.</summary>
    public static string? CompilerOf(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*_VEN_*.dxcache").Order(StringComparer.Ordinal).Select(Compiler).FirstOrDefault(c => c is { Length: > 0 }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether a delivered db of <paramref name="d"/> was compiled by <paramref name="compiler"/> (this driver's, from our own
    /// warm): true when one was, false when every one could be read and none was, null when that isn't known (no compiler yet, a
    /// db with a WAL, one that can't be read). Read as it is on disk: Gaming Services' file, never written next to.</summary>
    public static bool? Fits(Delivery d, string? compiler)
    {
        if (compiler == null || d.Psdbs.Count == 0) return null;
        var unknown = false;
        foreach (var p in d.Psdbs)
        {
            string? of;
            try
            {
                var f = new FileInfo(p);
                var key = $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
                string? c = null;
                of = !f.Exists || File.Exists(p + "-wal") && new FileInfo(p + "-wal").Length > 0 ? null
                    : PsdbCompilers.TryGetValue(key, out var known) ? known
                    : Query(f.FullName, string.Format(CompilerSql, "main"), st => c = Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0))) ? PsdbCompilers[key] = c ?? ""
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { of = null; }
            if (of is { Length: > 0 } && of == compiler) return true;
            unknown |= of is not { Length: > 0 };
        }
        return unknown ? null : false;
    }

    // delivered db (path, length, write time) -> its compiler, kept once read
    // ponytail: never pruned, a few entries per delivered game
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PsdbCompilers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one adapter db of a game's folder; null when none or more than one.</summary>
    public static string? AdapterDb(string dir)
    {
        try { return Directory.GetFiles(dir, "*_VEN_*.dxcache") is [var db] ? db : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A query for <see cref="PostExit"/>: each row it returns goes to <see cref="Row"/>.</summary>
    public readonly record struct DbQuery(string Sql, Action<DbRow> Row);

    public readonly struct DbRow
    {
        readonly nint st;
        internal DbRow(nint st) => this.st = st;
        public string? Text(int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(st, column));
        public long Long(int column) => sqlite3_column_int64(st, column);
        public unsafe byte[] Blob(int column) => sqlite3_column_bytes(st, column) is var n and > 0 ? new ReadOnlySpan<byte>((void*)sqlite3_column_blob(st, column), n).ToArray() : [];
    }

    /// <summary>The app_id rows' identities into <paramref name="into"/>.</summary>
    public static DbQuery AppIdQuery(ICollection<AppIdentity> into) =>
        new("select app_name, engine_name, app_version, engine_version from app_id", r => into.Add(new(r.Text(0), r.Text(1), r.Long(2), r.Long(3))));

    /// <summary>A game's db read after the game exited, only once its processes are gone: opened once, read-only, as the runtime
    /// left it with its WAL applied (the runtime keeps one after the exit, its last writes in it), then each query run. A WAL
    /// is read only through the -shm the runtime left, mapped read-only: nothing is created, written or deleted next to the db.
    /// Per query whether it ran; null when the db wasn't read (no -shm beside a WAL, busy, unreadable).</summary>
    public static bool[]? PostExit(string db, params DbQuery[] queries)
    {
        try
        {
            var wal = File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0;
            if (wal && !File.Exists(db + "-shm")) return null;
            // readonly_shm: SQLITE_READONLY_CANTINIT where the -shm would need writing; immutable: no WAL to apply, no file opened beside it
            var uri = wal ? Immutable(db)[..^"?immutable=1".Length] + "?readonly_shm=1" : Immutable(db);
            var rc = sqlite3_open_v2(uri, out var h, 0x01 /* READONLY */ | 0x40 /* URI */, 0);
            try { return rc == 0 ? [.. queries.Select(q => Rows(h, q.Sql, st => q.Row(new DbRow(st))))] : null; }
            finally { sqlite3_close_v2(h); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
