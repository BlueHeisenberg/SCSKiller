using CUE4Parse.Compression;

namespace SCSKiller.Core.App;

public static partial class Codecs
{
    public static readonly IReadOnlyDictionary<string, string> Pinned = new Dictionary<string, string>
    {
        ["oodle-data-shared.dll"] = "cba19529d0a3b5ec9c630e95652af01e123ae29a34a8a5f7507f5bcf23d9e82b",   // OodleUE 2026-06-04-1357, clang-cl-x64-release
        ["zlib-ng2.dll"] = "454be2f3d10f804ace577198401431db5e95d0286b59589bc28a40085388e7c2",           // Zlib-ng.NET 1.0.0
    };

    public const string OodleUrl = "https://github.com/WorkingRobot/OodleUE/releases/download/2026-06-04-1357/clang-cl-x64-release.zip";
    public const string ZlibUrl = "https://github.com/NotOfficer/Zlib-ng.NET/releases/download/1.0.0/zlib-ng2.dll.gz";

    static string OodleZipEntry => "bin/" + OodleHelper.OodleFileName;
}
