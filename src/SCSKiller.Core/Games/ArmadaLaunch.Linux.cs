using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Games;

/// <summary>Armada (an arm64 handheld distribution) launches its Steam games through its own wrapper, named in each game's
/// launch options (<c>/usr/libexec/armada/armada-game-launch %command%</c>), which picks the game's Turnip build from
/// Armada's game tweaks: the variables it adds, as its main() adds them, for the warm to run on the same driver build.
/// Mesa keys its cache on the driver library (its build id, else the file's time: none of Armada's builds has an id), so a
/// warm on another Turnip build fills entries the game never reads.
/// Read from Armada's own files, as armada_game_tweaks.py and armada_turnip.py read them: the tweaks are
/// /usr/share/armada/game-tweaks.json with /etc/armada/game-tweaks.json over it, a game's settings over the global ones
/// (its env merged with the global env); its env applies first, then its turnipDriver unless VK_DRIVER_FILES or
/// VK_ICD_FILENAMES is set by then: "stable" or none is the system's Turnip (nothing set), a system variant
/// /usr/share/armada/turnip/&lt;name&gt;/icd.&lt;arch&gt;.json, a user one (user:&lt;name&gt;) the arm64 glibc build in
/// ~/.local/share/armada/turnip/&lt;name&gt;/, through a manifest Armada writes in ~/.cache/armada-turnip at launch (written
/// here the same way when it isn't there yet). A pick that resolves to nothing is the system's Turnip, as Armada falls back.</summary>
public static partial class ArmadaLaunch
{
    public const string Wrapper = "/usr/libexec/armada/armada-game-launch";
    const string DefaultDriver = "stable", UserPrefix = "user:", Library = "libvulkan_freedreno.so";
    // the guest rootfs's Turnip, for x86 games under FEX: the native loader skips them (armada-game-launch's GUEST_TURNIP_MANIFESTS)
    static readonly string[] GuestManifests = ["/usr/share/vulkan/icd.d/freedreno_icd.x86_64.json", "/usr/share/vulkan/icd.d/freedreno_icd.i686.json"];

    [GeneratedRegex("^[^/:.][^/:]{0,99}$")]
    private static partial Regex SafeFolderName();

    /// <summary>The launch options run the game through Armada's wrapper.</summary>
    public static bool Wraps(string? options) => options?.Contains(Wrapper, StringComparison.Ordinal) == true;

    /// <summary>What the wrapper adds to a game's environment that SCSKiller reads: the env of its tweaks, then the Turnip
    /// driver (VK_DRIVER_FILES and VK_ICD_FILENAMES) unless the game's own variables <paramref name="own"/> or that env set
    /// one. <paramref name="root"/> stands for / and <paramref name="home"/> for the home folder (tests).</summary>
    public static List<(string Name, string Value)> Variables(uint appId, IReadOnlyList<(string Name, string Value)> own, string root = "/",
        string? home = null, string? cacheHome = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        cacheHome ??= Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { } c && Path.IsPathFullyQualified(c) ? c : Path.Combine(home, ".cache");
        var settings = Settings(root, appId);
        var vars = new List<(string Name, string Value)>();
        if (settings["env"] is JsonObject env)
            foreach (var (name, value) in env)
                if (value is JsonValue v && v.GetValueKind() is not JsonValueKind.Null && Scalar(v) is { } s) vars.Add((name, s));
        bool Set(string name) => own.Any(v => v.Name == name) || vars.Any(v => v.Name == name);
        if (Set("VK_DRIVER_FILES") || Set("VK_ICD_FILENAMES")) return vars;
        if ((settings["turnipDriver"] as JsonValue)?.GetValueKind() is not JsonValueKind.String) return vars;
        var driver = settings["turnipDriver"]!.GetValue<string>();
        if (driver is "" or DefaultDriver || Manifests(driver, root, home, cacheHome) is not { } manifests) return vars;
        var files = string.Join(':', manifests);
        vars.Add(("VK_DRIVER_FILES", files));
        vars.Add(("VK_ICD_FILENAMES", files));
        return vars;
    }

    /// <summary>The game's merged tweaks: the global ones with the game's over them, unless its entry is off.</summary>
    static JsonObject Settings(string root, uint appId)
    {
        var defaults = Read(Path.Combine(root, "usr/share/armada/game-tweaks.json"));
        var overrides = Read(Path.Combine(root, "etc/armada/game-tweaks.json"));
        var global = defaults["global"] as JsonObject is { } g ? (JsonObject)g.DeepClone() : [];
        if (overrides["global"] is JsonObject og)
            foreach (var (k, v) in og) global[k] = v?.DeepClone();
        var games = overrides["games"] is JsonObject ? overrides["games"] : defaults["games"];
        if ((games as JsonObject)?[appId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject game
            || game["enabled"] is JsonValue e && e.GetValueKind() == JsonValueKind.False) return global;
        var globalEnv = global["env"] as JsonObject;
        foreach (var (k, v) in game) global[k] = v?.DeepClone();
        if (globalEnv != null && game["env"] is JsonObject gameEnv)
        {
            var env = new JsonObject();
            foreach (var (k, v) in globalEnv) env[k] = v?.DeepClone();
            foreach (var (k, v) in gameEnv) env[k] = v?.DeepClone();
            foreach (var k in env.Where(p => p.Value is null || p.Value.GetValueKind() == JsonValueKind.Null).Select(p => p.Key).ToList()) env.Remove(k);
            global["env"] = env;
        }
        return global;
    }

    static JsonObject Read(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    // Python's str() of a JSON scalar, as os.environ[str(key)] = str(value) sets it
    static string? Scalar(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };

    /// <summary>The manifests the wrapper sets for <paramref name="driver"/>; null when it resolves to none.</summary>
    static List<string>? Manifests(string driver, string root, string home, string cacheHome)
    {
        if (!driver.StartsWith(UserPrefix, StringComparison.Ordinal))
        {
            var dir = Path.Combine(root, "usr/share/armada/turnip", driver);
            if (!SafeFolderName().IsMatch(driver) || !File.Exists(Path.Combine(dir, "icd.aarch64.json")) || !File.Exists(Path.Combine(dir, "aarch64", Library)))
                return null;
            return [.. new[] { "aarch64", "x86_64", "i686" }.Select(a => Path.Combine("/usr/share/armada/turnip", driver, $"icd.{a}.json"))];
        }
        var name = driver[UserPrefix.Length..];
        var library = Path.Combine(home, ".local/share/armada/turnip", name, Library);
        if (!SafeFolderName().IsMatch(name) || !Arm64Glibc(library)) return null;
        var manifest = Path.Combine(cacheHome, "armada-turnip", name + ".json");
        var body = JsonSerializer.Serialize(new { file_format_version = "1.0.1", ICD = new { library_path = library, api_version = "1.4.0" } },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
        try
        {
            if (!File.Exists(manifest) || !SameDriver(manifest, library))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                var tmp = $"{manifest}.{Environment.ProcessId}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
                File.WriteAllText(tmp, body, new UTF8Encoding(false));
                File.Move(tmp, manifest, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return [manifest, .. GuestManifests];
    }

    static bool SameDriver(string manifest, string library)
    {
        try { return (JsonNode.Parse(File.ReadAllText(manifest))?["ICD"]?["library_path"] as JsonValue)?.GetValue<string>() == library; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    // armada_turnip.is_arm64_glibc: an ELF64 aarch64 file naming libc.so.6 (an Android build has the same file name)
    static bool Arm64Glibc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64 || info.Length > 256L << 20) return false;
            var d = File.ReadAllBytes(path);
            return d[0] == 0x7F && d[1] == (byte)'E' && d[2] == (byte)'L' && d[3] == (byte)'F' && d[4] == 2 && d[18] == 0xB7 && d[19] == 0
                && d.AsSpan().IndexOf("libc.so.6\0"u8) >= 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
