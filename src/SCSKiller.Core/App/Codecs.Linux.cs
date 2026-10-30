using CUE4Parse.Compression;

namespace SCSKiller.Core.App;

public static partial class Codecs
{
    public static readonly IReadOnlyDictionary<string, string> Pinned = new Dictionary<string, string>
    {
        ["liboodle-data-shared.so"] = "f21d70299bbe873f63e8aef778b30f4dee6dec1fc09140c185caf1f8d153f771",   // OodleUE 2026-06-04-1357, gcc-x64-release
        ["libz-ng.so"] = "583a6e04521b0406e2940f4ec61353f03bcf244c10f54038c352f300ab7bbb19",                // Zlib-ng.NET 1.0.0
    };

    public const string OodleUrl = "https://github.com/WorkingRobot/OodleUE/releases/download/2026-06-04-1357/gcc-x64-release.zip";
    // not CUE4Parse's ZlibHelper.DOWNLOAD_URL, which is always the Windows DLL
    public const string ZlibUrl = "https://github.com/NotOfficer/Zlib-ng.NET/releases/download/1.0.0/libz-ng.so.gz";

    static string OodleZipEntry => "lib/" + OodleHelper.OodleFileName;
}
