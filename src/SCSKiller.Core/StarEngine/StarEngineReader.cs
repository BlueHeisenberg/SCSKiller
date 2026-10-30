namespace SCSKiller.Core.StarEngine;

/// <summary>Star Citizen (CIG's StarEngine): &lt;channel&gt;\Bin64\StarCitizen.exe with Data.p4k in the channel folder (LIVE, PTU,
/// EPTU, each a game of its own). Nothing in it is read: the install has anti-cheat, and a Vulkan game plans from its
/// recording alone. Which renderer the player set isn't read yet, so Vulkan, the default, is a guess the player can change.</summary>
public sealed class StarEngineReader : IEngineReader
{
    public const string Family = "StarEngine";
    public const string Exe = "StarCitizen.exe";

    /// <summary>The channel folder's name (LIVE, PTU) when <paramref name="exe"/> is Star Citizen's; null otherwise.</summary>
    public static string? Channel(string exe)
    {
        if (!Path.GetFileName(exe).Equals(Exe, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(exe) is not { } bin
            || !Path.GetFileName(bin).Equals("Bin64", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(bin) is not { } channel) return null;
        return File.Exists(Path.Combine(channel, "Data.p4k")) ? Path.GetFileName(channel) : null;
    }

    public EngineInfo? Detect(Game game) => Channel(game.ExePath) == null ? null
        : new EngineInfo(Family, "", null, "Vulkan", false, null, ApiGuessed: true, OffersVulkan: true);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) =>
        new(ShaderIndex.EmptyContentHash, [], new Dictionary<string, ShaderInfo>(), []);

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
}
