using SCSKiller.Core.App;

namespace SCSKiller.Core.BlackSpace;

/// <summary>The archive key of a BlackSpace game (<see cref="PazCrypto"/>), given by the user: kept only in
/// %LOCALAPPDATA%\SCSKiller\games\&lt;id&gt;\paz.key (hex), never in plans, logs or anything meant to be shared, like
/// <see cref="Unreal.UnrealKeys"/>' aes.key. Stored only once it opens the game's files.</summary>
public sealed class BlackSpaceKeys(string dataDir)
{
    public const int Size = 32;

    /// <summary>A scan reads the key while the user sets it: one at a time.</summary>
    static readonly Lock Gate = new();

    string KeyFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "paz.key");

    /// <summary>The stored key file's write time, "" if none: a key added or changed re-runs detection.</summary>
    public string KeyStamp(Game game) => File.Exists(KeyFile(game)) ? File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString() : "";

    /// <summary>The stored key, unchecked (the reader checks it against the game's files).</summary>
    public byte[]? Stored(Game game)
    {
        lock (Gate)
            try { return File.Exists(KeyFile(game)) ? Parse(File.ReadAllText(KeyFile(game))) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A key the user supplies (64 hex digits, one leading "0x" optional): stored if it <paramref name="opens"/>
    /// the game's files.</summary>
    public bool Set(Game game, string key, Func<byte[], bool> opens)
    {
        if (Parse(key) is not { } k || !opens(k)) return false;
        var file = KeyFile(game);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", Convert.ToHexString(k));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        return true;
    }

    internal static byte[]? Parse(string text)
    {
        var hex = text.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return hex.Length == 2 * Size && hex.All(char.IsAsciiHexDigit) ? Convert.FromHexString(hex) : null;
    }
}
