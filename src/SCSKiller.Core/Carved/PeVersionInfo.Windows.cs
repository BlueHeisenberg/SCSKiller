using System.Diagnostics;

namespace SCSKiller.Core.Carved;

public sealed partial record PeVersionInfo
{
    public static partial PeVersionInfo GetVersionInfo(string fileName)
    {
        var v = FileVersionInfo.GetVersionInfo(fileName);
        return new(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart, v.ProductName, v.FileDescription, v.OriginalFilename);
    }
}
