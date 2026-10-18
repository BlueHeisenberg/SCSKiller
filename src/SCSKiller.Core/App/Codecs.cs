using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using CUE4Parse.Compression;

namespace SCSKiller.Core.App;

/// <summary>CUE4Parse's native codecs live in %LOCALAPPDATA%\SCSKiller\codecs,
/// not next to the exe: every update replaces the install folder, and public packages don't ship Oodle. A copy next to the
/// exe (a zip or dev build) seeds it; otherwise it is downloaded there from its GitHub release. A file is loaded
/// only if its SHA-256 is the pinned one: a new build of either DLL comes with a CUE4Parse update and a new pin.</summary>
public static class Codecs
{
    public static readonly IReadOnlyDictionary<string, string> Pinned = new Dictionary<string, string>
    {
        ["oodle-data-shared.dll"] = "cba19529d0a3b5ec9c630e95652af01e123ae29a34a8a5f7507f5bcf23d9e82b",   // OodleUE 2026-06-04-1357, clang-cl-x64-release
        ["zlib-ng2.dll"] = "454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2",           // Zlib-ng.NET 1.0.0
    };

    public static string Dir => DirOverride ?? Path.Combine(AppStore.DefaultDir, "codecs");
    // the tests' build-owned cache: they never read the app's data folder
    internal static string? DirOverride;
    static readonly Lock Gate = new();

    /// <summary>The pinned Oodle build runs AVX2, BMI2 and MOVBE code with no CPU check, which kills the process on a CPU
    /// without them. Without it CUE4Parse decodes Oodle data in managed code.</summary>
    public static bool NativeOodle => Avx2.IsSupported && Bmi2.X64.IsSupported;

    public const string NoNativeOodle = "This CPU lacks AVX2 and BMI2, which the Oodle codec needs.";

    public const string NoOodleDll = "Couldn't download the Oodle codec. Check your antivirus or firewall.";

    public const string OodleUrl = "https://github.com/WorkingRobot/OodleUE/releases/download/2026-06-04-1357/clang-cl-x64-release.zip";

    /// <summary>The line a user sees for an error a missing codec caused (<see cref="NoOodleDll"/>, <see cref="NoNativeOodle"/>:
    /// the details are its inner exception), else null.</summary>
    public static string? Reason(Exception e)
    {
        while (e is AggregateException { InnerExceptions.Count: 1 } a) e = a.InnerExceptions[0];
        return e.Message is NoOodleDll or NoNativeOodle ? e.Message : null;
    }

    /// <summary>Where a codec that couldn't be loaded is reported, with why (the app's log).</summary>
    public static IProgress<string>? Log { get; set; }

    /// <summary>Native Oodle couldn't be loaded on a CPU that runs it: Oodle data decodes in managed code, or not at all.</summary>
    public static bool OodleMissing { get { lock (Gate) return Failed.Contains(Path.Combine(Dir, OodleHelper.OodleFileName)); } }

    static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);   // paths

    // the system proxy: CUE4Parse's own download client bypasses it
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 256 << 20 };

    /// <summary>Loads Oodle (only where <see cref="NativeOodle"/>), and zlib-ng with <paramref name="zlib"/>; a no-op once
    /// loaded. Never throws: without a codec CUE4Parse decodes in managed code. Two readers may start at once (the lock:
    /// they'd race on the same download).</summary>
    public static void Load(bool zlib = true)
    {
        lock (Gate)
        {
            if (NativeOodle && OodleHelper.Instance == null) TryLoad(OodleHelper.OodleFileName, DownloadOodle, p => OodleHelper.Initialize(p), Log);
            if (zlib && ZlibHelper.Instance == null) TryLoad(ZlibHelper.DllName, DownloadZlib, p => ZlibHelper.Initialize(p), Log);
        }
    }

    /// <summary>False when <paramref name="name"/> can't be had or loaded, logged once.
    /// ponytail: tried once per process, retry on a later scan if transient network failures turn out to matter.</summary>
    internal static bool TryLoad(string name, Func<string, bool> download, Action<string> init, IProgress<string>? log, string? dir = null, string? seedDir = null)
    {
        var path = Path.Combine(dir ?? Dir, name);
        lock (Gate)
        {
            if (Failed.Contains(path)) return false;
            try
            {
                var file = Ensure(name, download, dir, seedDir);
                using (Hold(file, name)) init(file);   // CUE4Parse loads by path: the held file can't be swapped or deleted (and re-fetched unpinned) under it
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Failed.Add(path);
                log?.Report($"codec {name} not loaded, decoding in managed code: {e}");
                return false;
            }
        }
    }

    public static bool DownloadOodle(string path)
    {
        using var zip = new ZipArchive(Get(OodleUrl));
        using var dll = (zip.GetEntry("bin/" + OodleHelper.OodleFileName) ?? throw new InvalidDataException($"{OodleUrl} has no {OodleHelper.OodleFileName}")).Open();
        Save(dll, path);
        return true;
    }

    public static bool DownloadZlib(string path)
    {
        using var dll = new GZipStream(Get(ZlibHelper.DOWNLOAD_URL), CompressionMode.Decompress);
        Save(dll, path);
        return true;
    }

    static MemoryStream Get(string url)
    {
        try { return new MemoryStream(Http.GetByteArrayAsync(url).GetAwaiter().GetResult()); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new IOException($"downloading {url} failed: {e.Message}", e); }
    }

    static void Save(Stream from, string path)
    {
        using var to = File.Create(path);
        from.CopyTo(to);
    }

    /// <summary>A pinned copy of <paramref name="name"/> in <paramref name="dir"/> (default <see cref="Dir"/>): the one there,
    /// else the one in <paramref name="seedDir"/> (default: next to the exe), else <paramref name="download"/>ed. Throws
    /// when the download fails or none matches its pin; a mismatching file is deleted, never loaded.</summary>
    public static string Ensure(string name, Func<string, bool> download, string? dir = null, string? seedDir = null)
    {
        dir ??= Dir;
        var path = Path.Combine(dir, name);
        if (Verified(path, name)) return path;
        Directory.CreateDirectory(dir);
        try { File.Delete(path); }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && Verified(path, name)) { return path; }   // another instance put it there and holds it
        var seed = Path.Combine(seedDir ?? AppContext.BaseDirectory, name);
        foreach (var stale in Directory.EnumerateFiles(dir, name + ".*.download"))   // left by a killed download
            try { if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-1)) File.Delete(stale); } catch (IOException) { }
        var tmp = $"{path}.{Guid.NewGuid():N}.download";   // per attempt: only bytes this attempt verified ever take the loaded name
        try
        {
            if (seed != path && Verified(seed, name)) File.Copy(seed, tmp, overwrite: true);
            else download(tmp);
            if (!Verified(tmp, name)) throw new InvalidDataException($"{name} couldn't be downloaded, or isn't the expected build (SHA-256 mismatch)");
            try { File.Move(tmp, path, overwrite: true); }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && Verified(path, name)) { }
            return path;
        }
        finally { File.Delete(tmp); }
    }

    /// <summary><paramref name="path"/> open with writes and deletes denied, if it has its pin.</summary>
    static FileStream Hold(string path, string name)
    {
        var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Pinned.TryGetValue(name, out var sha) && Convert.ToHexStringLower(SHA256.HashData(f)) == sha) return f;
        f.Dispose();
        throw new InvalidDataException($"{name} changed after it was verified");
    }

    static bool Verified(string path, string name) => File.Exists(path) && Pinned.TryGetValue(name, out var sha)
        && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == sha;
}
