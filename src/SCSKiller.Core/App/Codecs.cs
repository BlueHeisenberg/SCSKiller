using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using CUE4Parse.Compression;

namespace SCSKiller.Core.App;

/// <summary>CUE4Parse's native codecs live in %LOCALAPPDATA%\SCSKiller\codecs,
/// not next to the exe: every update replaces the install folder, and public packages don't ship Oodle. A copy next to the
/// exe (a zip or dev build) seeds it; otherwise CUE4Parse downloads it there from its GitHub release. A file is loaded
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
    public static bool NativeOodle => OperatingSystem.IsWindows() && Avx2.IsSupported && Bmi2.X64.IsSupported;

    public const string NoNativeOodle = "this CPU lacks AVX2/BMI2, which the Oodle library needs";

    /// <summary>Loads Oodle (only where <see cref="NativeOodle"/>), and zlib-ng with <paramref name="zlib"/>; a no-op once
    /// loaded. Two readers may start at once (the lock: they'd race on the same download).</summary>
    public static void Load(bool zlib = true)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (NativeOodle && OodleHelper.Instance == null) OodleHelper.Initialize(Ensure(OodleHelper.OodleFileName, DownloadOodle));
            if (zlib && ZlibHelper.Instance == null) ZlibHelper.Initialize(Ensure(ZlibHelper.DllName, p => ZlibHelper.DownloadDll(p, null!)));
        }
    }

    public static bool DownloadOodle(string path)
    {
        var got = path;
        if (!OodleHelper.DownloadOodleDll(ref got)) return false;
        if (got != path) File.Move(got, path, overwrite: true);
        return true;
    }

    /// <summary>A pinned copy of <paramref name="name"/> in <paramref name="dir"/> (default <see cref="Dir"/>): the one there,
    /// else the one in <paramref name="seedDir"/> (default: next to the exe), else <paramref name="download"/>ed. Throws
    /// <see cref="InvalidDataException"/> when none matches its pin; a mismatching file is deleted, never loaded.</summary>
    public static string Ensure(string name, Func<string, bool> download, string? dir = null, string? seedDir = null)
    {
        dir ??= Dir;
        var path = Path.Combine(dir, name);
        if (Verified(path)) return path;
        Directory.CreateDirectory(dir);
        File.Delete(path);
        var seed = Path.Combine(seedDir ?? AppContext.BaseDirectory, name);
        if (seed != path && Verified(seed)) File.Copy(seed, path);
        else download(path);
        if (Verified(path)) return path;
        File.Delete(path);
        throw new InvalidDataException($"{name} couldn't be downloaded, or isn't the expected build (SHA-256 mismatch)");
    }

    static bool Verified(string path) => File.Exists(path) && Pinned.TryGetValue(Path.GetFileName(path), out var sha)
        && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == sha;
}
