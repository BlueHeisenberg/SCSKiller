namespace SCSKiller.Core.App;

public static partial class FileTimes
{
    public static partial DateTime? CreatedUtc(string path) => File.GetCreationTimeUtc(path);

    public static partial DateTime? CreatedUtc(FileSystemInfo f) => f.CreationTimeUtc;
}
