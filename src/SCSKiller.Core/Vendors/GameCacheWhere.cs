using System.Globalization;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Vendors;

/// <summary>Where a Steam game's driver reads its shader cache on Linux, learned from the game's process while it plays: its
/// environment and the cache files it holds open. Steam's Shader Pre-caching gives the game Steam's folder for it (NVIDIA:
/// __GL_SHADER_DISK_CACHE_PATH, APP_NAME and READ_ONLY_APP_NAME; Mesa: MESA_SHADER_CACHE_DIR). With it off the game gets
/// none of them (measured on NVIDIA: only __GL_SHADER_DISK_CACHE_SIZE and SKIP_CLEANUP), and the driver keeps the cache in
/// its default folder. <see cref="Steam"/>: Steam's folder. <see cref="Set"/>: on NVIDIA, the file set the game's process
/// writes and reads whole, its real path without .toc or .bin, under the cap <see cref="Size"/>. <see cref="Off"/>: why the
/// compile can't be put where the game reads.</summary>
public sealed record GameCacheWhere(bool Steam = false, string? Set = null, long Size = 0, bool SkipCleanup = false, string? Off = null)
{
    public static string FileOf(string gameDir) => Path.Combine(gameDir, "cache-where.json");

    /// <summary>The driver's cap without __GL_SHADER_DISK_CACHE_SIZE (NVIDIA's default, about 1 GiB, assumed).</summary>
    public const long NvidiaDefaultSize = 1L << 30;

    const string SteamAppName = "steamapp_shader_cache";

    /// <summary>What NVIDIA's driver in a game's process reads: Steam's folder <paramref name="steamNvidia"/> (its nvidiav1),
    /// else the one set of &lt;folder&gt;/GLCache/&lt;driver&gt;/&lt;gpu&gt;/&lt;name&gt; it holds open that isn't read-only.
    /// <paramref name="real"/> resolves a path's links, null when it doesn't exist. Null while that can't be told yet: the
    /// driver opens its files once the game renders.</summary>
    public static GameCacheWhere? Nvidia(IReadOnlyDictionary<string, string> env, IEnumerable<string> open, string steamNvidia, Func<string, string?> real)
    {
        if (env.GetValueOrDefault("__GL_SHADER_DISK_CACHE") is "0" or "false") return new(Off: "the game's NVIDIA shader cache is turned off");
        var path = env.GetValueOrDefault("__GL_SHADER_DISK_CACHE_PATH") is { Length: > 0 } p ? real(p) : null;
        var app = env.GetValueOrDefault("__GL_SHADER_DISK_CACHE_APP_NAME") is { Length: > 0 } a ? a : null;
        if (path != null && app == SteamAppName && path == real(steamNvidia)) return new(Steam: true);
        var readOnly = (env.GetValueOrDefault("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var sets = open.Where(f => f.EndsWith(".toc", StringComparison.Ordinal) || f.EndsWith(".bin", StringComparison.Ordinal)).Select(f => f[..^4])
            .Where(s => IsSet(s, path, app) && !readOnly.Contains(Path.GetFileName(s))).Distinct(StringComparer.Ordinal).ToList();
        if (sets.Count != 1) return sets.Count == 0 ? null : new(Off: $"the game holds several NVIDIA cache files open ({string.Join(", ", sets.Select(Path.GetFileName))})");
        var size = long.TryParse(env.GetValueOrDefault("__GL_SHADER_DISK_CACHE_SIZE"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : NvidiaDefaultSize;
        return new(Set: sets[0], Size: size, SkipCleanup: env.GetValueOrDefault("__GL_SHADER_DISK_CACHE_SKIP_CLEANUP") is "1" or "true");
    }

    /// <summary>&lt;folder&gt;/GLCache/&lt;32 hex&gt;/&lt;16 hex&gt;/&lt;name&gt;, in <paramref name="folder"/> when the game names one. The
    /// name is <paramref name="app"/> and the process's n when the game names one, else the driver's own: 16 hex digits,
    /// a hash of the process image's file name plus n (measured: wine64-preloader's n = 0 is 92f74448a56b9fe6, n = 4 ends in a).</summary>
    static bool IsSet(string set, string? folder, string? app)
    {
        var parts = set.Split('/');
        if (parts.Length < 5 || parts[^4] != "GLCache" || !IsHex(parts[^3], 32) || !IsHex(parts[^2], 16)) return false;
        var name = parts[^1];
        var digits = name.Length - name.AsSpan().TrimEnd("0123456789").Length;
        return (folder == null || string.Join('/', parts[..^4]) == folder) && (app == null ? IsHex(name, 16) : digits > 0 && name[..^digits] == app);
    }

    static bool IsHex(string name, int length) => name.Length == length && name.All(char.IsAsciiHexDigitLower);

    /// <summary>What Mesa in a game's process reads: Steam's folder for the game <paramref name="steamFolder"/> in Mesa's
    /// single-file form, else nothing SCSKiller places. Mesa's default cache is shared by every app and evicts past its size.</summary>
    public static GameCacheWhere Mesa(IReadOnlyDictionary<string, string> env, string steamFolder, Func<string, string?> real) =>
        env.GetValueOrDefault("MESA_SHADER_CACHE_DIR") is { Length: > 0 } dir && real(dir) is { } at && at == real(steamFolder)
            && env.GetValueOrDefault("MESA_DISK_CACHE_SINGLE_FILE")?.ToLowerInvariant() is "1" or "true" or "yes" or "y"
            ? new(Steam: true) : new(Off: "Mesa's cache for the game isn't Steam's folder for it");

    public static GameCacheWhere? Load(string gameDir)
    {
        try { return File.Exists(FileOf(gameDir)) ? JsonSerializer.Deserialize<GameCacheWhere>(File.ReadAllBytes(FileOf(gameDir)), AppStore.Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string gameDir) => AppStore.WriteAtomic(FileOf(gameDir), JsonSerializer.SerializeToUtf8Bytes(this, AppStore.Json), ownerOnly: true);
}
