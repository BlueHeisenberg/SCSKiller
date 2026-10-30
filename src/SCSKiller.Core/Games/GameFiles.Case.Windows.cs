namespace SCSKiller.Core.Games;

public static partial class GameFiles
{
    public static partial string Find(string root, string rel) => Under(root, rel);

    public static partial IEnumerable<string> FindAll(string root, string rel) => [Under(root, rel)];

    public static partial string Rel(string root, string path, string found, string rel) => Rel(root, path);

    public static partial string ChangeExtension(string path, string extension) => Path.ChangeExtension(path, extension);

    public static partial IEnumerable<string> EnumerateFiles(string dir, string pattern, SearchOption option) => Directory.EnumerateFiles(dir, pattern, option);

    public static partial IEnumerable<string> EnumerateFiles(string dir, string pattern, EnumerationOptions options) => Directory.EnumerateFiles(dir, pattern, options);

    public static partial IEnumerable<string> EnumerateDirectories(string dir, string pattern, SearchOption option) => Directory.EnumerateDirectories(dir, pattern, option);

    public static partial IEnumerable<string> EnumerateDirectories(string dir, string pattern, EnumerationOptions options) => Directory.EnumerateDirectories(dir, pattern, options);

    public static partial IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo dir, string pattern, SearchOption option) => dir.EnumerateFiles(pattern, option);

    public static partial IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo dir, string pattern, EnumerationOptions options) => dir.EnumerateFiles(pattern, options);

    public static partial IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo dir, string pattern, EnumerationOptions options) => dir.EnumerateDirectories(pattern, options);

    internal static partial IEnumerable<FileSystemInfo>? MarkerEntries(DirectoryInfo dir, int left, Func<bool> over) => dir.EnumerateFileSystemInfos("*", AllNames);
}
