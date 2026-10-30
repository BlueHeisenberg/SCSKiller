namespace SCSKiller.Core.Carved;

public sealed partial record PeVersionInfo
{
    public static partial PeVersionInfo GetVersionInfo(string fileName) => ReadManaged(fileName);
}
