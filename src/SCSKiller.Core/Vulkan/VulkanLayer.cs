using System.Globalization;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

/// <summary>The Vulkan recorder: our build of Fossilize's capture layer (VkLayer_scskiller.dll, proxy/vklayer.cpp). Per game a
/// copy of the dll (beside the exe instead for a risk session on Windows) and its manifest in the data folder's
/// vulkan-layer\&lt;key&gt;\ (<see cref="Key"/>). A Steam game's is an implicit layer that only a Steam launch of the game enables
/// (SteamAppId=&lt;appid&gt;, a non-Steam game's SteamGameId, <see cref="EnableVariable"/>), registered on Windows as a value of HKCU's ImplicitLayers (the manifest's path, 0), on Linux as
/// the manifest's copy under its own name in the loader's per-user implicit_layer.d (<see cref="ImplicitDir"/>). Any other
/// game's (Windows) is an entry of SCSKiller's loader settings file (<see cref="SettingsName"/>), which turns the layer on
/// for the game's exe path alone: no other process ever finds it. The layer records only an exe the recorder's admission
/// admits (its signed scskiller.ini beside the exe), into the recorder folder's vulkan\&lt;exe file key&gt;\: the archives
/// (<see cref="VulkanPlan.InboxPattern"/>) and scskiller_creates.csv with #api,vulkan, the driver-cache variables the game ran
/// with (#cache,NAME=value) and its instance and device create infos (#vkinstance, #vkdevice; scskiller_layer.hpp).</summary>
public static partial class VulkanLayer
{
    public const string Folder = "vulkan-layer";

    /// <summary>The loader settings file in <see cref="Folder"/> (Windows): the loader takes a file only by this name.</summary>
    public const string SettingsName = "vk_loader_settings.json";

    /// <summary>HKCU's key the loader reads implicit layers from (Windows). Replaceable for tests, which never touch the real one.</summary>
    internal static string RegistryKey { get; set; } = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";

    /// <summary>The loader's per-user implicit layer folder (Linux): $XDG_DATA_HOME/vulkan/implicit_layer.d, ~/.local/share's
    /// without it. Replaceable for tests, which never touch the real one.</summary>
    internal static string ImplicitDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vulkan", "implicit_layer.d");

    /// <summary>The Steam app id of a Steam game, or of a non-Steam game added to Steam (<see cref="SteamShortcuts"/>, its high bit
    /// set); null for any other game, whose key is <see cref="Key"/>'s.</summary>
    public static string? AppId(Game g) =>
        g.Store == Store.Steam && g.Id.StartsWith("steam:", StringComparison.Ordinal) && g.Id[6..] is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? id
        : SteamShortcuts.AppId(g.Id)?.ToString(CultureInfo.InvariantCulture);

    /// <summary>The game id of a layer's app id (<see cref="AppId"/>).</summary>
    public static string AppGameId(string appId) =>
        uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var app) && SteamShortcuts.IsShortcut(app)
            ? SteamShortcuts.IdOf(app) : "steam:" + appId;

    /// <summary>The variable Steam sets to this value in the game's launch, which enables its layer: SteamAppId=&lt;app id&gt;; a
    /// non-Steam game's SteamGameId=&lt;its 64-bit game id&gt; (<see cref="SteamShortcuts.GameId"/>), as SteamAppId is no
    /// shortcut's own.</summary>
    public static (string Name, string Value) EnableVariable(string appId) =>
        uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var app) && SteamShortcuts.IsShortcut(app)
            ? ("SteamGameId", SteamShortcuts.GameId(app).ToString(CultureInfo.InvariantCulture)) : ("SteamAppId", appId);

    /// <summary>The game's layer key: a Steam game's app id, on Windows any other game's "g" and 15 hex digits of its id's
    /// SHA-256 (never all digits). Null off Windows for a game that isn't Steam's: its exe runs under Wine there, so the
    /// loader settings file can't name it.</summary>
    public static string? Key(Game g) => AppId(g) ?? (OperatingSystem.IsWindows()
        ? "g" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(g.Id)))[..15] : null);

    static bool IsSteamKey(string key) => key.All(char.IsAsciiDigit);

    public static string LayerName(string appId) => "VK_LAYER_SCSKILLER_" + appId;

    public static string ManifestName(string appId) => $"VkLayer_scskiller_{appId}.json";

    /// <summary>The layer's inbox for an exe: the recorder folder's vulkan\&lt;file key&gt;.</summary>
    public static string Inbox(string recorderDir, string fileKey) => Path.Combine(recorderDir, "vulkan", fileKey);

    /// <summary>The manifest: the layer enabled only where <see cref="EnableVariable"/> is the game's, disabled by
    /// DISABLE_VK_LAYER_SCSKILLER_&lt;appid&gt;=1 (an implicit layer needs one).</summary>
    public static string Manifest(string appId, string dllPath)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("file_format_version", "1.0.0");
            w.WriteStartObject("layer");
            w.WriteString("name", LayerName(appId));
            w.WriteString("type", "GLOBAL");
            w.WriteString("library_path", dllPath);
            w.WriteString("api_version", "1.4.303");
            w.WriteString("implementation_version", "1");
            w.WriteString("description", "SCSKiller's Vulkan recorder");
            w.WriteStartObject("functions");
            w.WriteString("vkGetInstanceProcAddr", "VK_LAYER_fossilize_GetInstanceProcAddr");
            w.WriteString("vkGetDeviceProcAddr", "VK_LAYER_fossilize_GetDeviceProcAddr");
            w.WriteEndObject();
            w.WriteStartObject("enable_environment");
            var (name, value) = EnableVariable(appId);
            w.WriteString(name, value);
            w.WriteEndObject();
            w.WriteStartObject("disable_environment");
            w.WriteString("DISABLE_" + LayerName(appId), "1");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>The registrations of a manifest of this key that isn't <paramref name="manifest"/>: another SCSKiller copy's,
    /// whose layer would record the same process too. For a key that isn't Steam's, every loader settings file registered
    /// that isn't this data folder's: the loader reads only the first.</summary>
    public static partial List<string> Others(string key, string manifest);

    /// <summary>The manifest is registered, as it is now, for <paramref name="g"/>'s exe when the key isn't Steam's.</summary>
    public static partial bool Registered(string manifest, Game? g = null);

    /// <summary>Registers the manifest, for <paramref name="g"/>'s exe when the key isn't Steam's; never in place of another
    /// copy's registration.</summary>
    public static partial void Register(string manifest, Game? g = null);

    /// <summary>Takes the manifest's registration out alone: every other one stays as it is.</summary>
    public static partial void Unregister(string manifest);

    /// <summary>Takes out every registration of a manifest under <paramref name="folder"/> (a data folder's layers); returns them.</summary>
    public static partial List<string> UnregisterUnder(string folder);

    /// <summary>The registrations of a manifest under <paramref name="folder"/> (a data folder's layers), each as that manifest's path.</summary>
    public static partial List<string> Under(string folder);

    /// <summary>The app id in a manifest's name (<see cref="ManifestName"/>); null when the name isn't a Steam game's.</summary>
    public static string? ManifestAppId(string manifest) => ManifestKey(manifest) is { } k && IsSteamKey(k) ? k : null;

    /// <summary>The key in a manifest's name (<see cref="ManifestName"/>); null when the name isn't one.</summary>
    public static string? ManifestKey(string manifest) =>
        Path.GetFileName(manifest) is var n && n.StartsWith("VkLayer_scskiller_", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)
        && n[18..^5] is { Length: > 0 } k && (k.All(char.IsAsciiDigit) || k.Length == 16 && k[0] == 'g' && k[1..].All(char.IsAsciiHexDigitLower)) ? k : null;

    /// <summary>The game a registered manifest is for: a Steam game's from its name, any other's from its settings entry.
    /// Null when neither says.</summary>
    public static string? GameId(string manifest) => ManifestAppId(manifest) is { } app ? AppGameId(app) : SettingsGame(manifest);

    private static partial string? SettingsGame(string manifest);

    /// <summary>Why the recorder of a game that isn't Steam's can't load from its exe's folder: the Vulkan loader there (the
    /// exe's own vulkan-1.dll, else the system's) predates loader settings files (1.3.255). Null: it can, or can't be told.</summary>
    public static partial string? LoaderProblem(string exe);

    /// <summary>The driver-cache variables of the csv's last session (its #cache lines after the last #session): what Steam
    /// redirected the game's Vulkan cache to, which the warm replays under. Null: no session, or none redirected. Read beside
    /// the running game's layer, which holds the csv open to append; IOException when it can't be read, which says nothing.</summary>
    public static Dictionary<string, string>? CacheEnv(string csv)
    {
        Dictionary<string, string>? last = null;
        bool inSession = false;
        using var reader = new StreamReader(new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        for (string? line; (line = reader.ReadLine()) != null;)
            if (line.StartsWith("#session,", StringComparison.Ordinal)) (last, inSession) = (null, true);
            else if (inSession && line.StartsWith("#cache,", StringComparison.Ordinal) && line.IndexOf('=', 7) is var eq and > 7 && CacheVariable(line[7..eq])
                     && line.Length < 4096 && !line.Any(char.IsControl))   // a NUL would end the variable in the warm's environment block and start another
                (last ??= new(StringComparer.OrdinalIgnoreCase))[line[7..eq]] = line[(eq + 1)..];
        return last;
    }

    /// <summary>The device of the csv's last session (its last #vkdevice line after the last #session) as the replayer reads it
    /// (<see cref="VulkanPlan.DeviceVariable"/>): "&lt;extensions&gt;,,&lt;features&gt;", each name and struct checked, the queues
    /// left out. Null: none, or a line that isn't the layer's. IOException as <see cref="CacheEnv"/>.</summary>
    public static string? Device(string csv)
    {
        string? last = null;
        bool inSession = false;
        using var reader = new StreamReader(new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        for (string? line; (line = reader.ReadLine()) != null;)
            if (line.StartsWith("#session,", StringComparison.Ordinal)) (last, inSession) = (null, true);
            else if (inSession && line.StartsWith("#vkdevice,", StringComparison.Ordinal)) last = DeviceOf(line[10..]);
        return last;
    }

    /// <summary>A #vkdevice line's fields as <see cref="Device"/> passes them on; null when a name or a struct isn't one the layer
    /// writes: the csv is the game process's to write.</summary>
    static string? DeviceOf(string fields)
    {
        if (fields.Length > 16384 || fields.Split(',') is not [var exts, _, var features]) return null;
        var names = exts.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var structs = features.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return names.All(n => n.StartsWith("VK_", StringComparison.Ordinal) && n.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            && structs.All(f => f.Split(':') is [var type, var bits] && type.Length is > 0 and < 11 && type.All(char.IsAsciiDigit)
                && (bits == "?" || bits.Length > 0 && bits.All(c => c is '0' or '1')))
            ? $"{string.Join(' ', names)},,{string.Join(' ', structs)}" : null;
    }

    /// <summary>The variables a #cache line may carry, as the layer writes them: the csv is the game process's to write, so
    /// any other name (a layer path, PATH) never reaches the warm.</summary>
    static bool CacheVariable(string name) => name.ToUpperInvariant() is var n && n.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
        && (n.StartsWith("__GL_SHADER_DISK_CACHE", StringComparison.Ordinal) || n.StartsWith("AMD_VK_PIPELINE_CACHE_", StringComparison.Ordinal)
            || n == "AMD_VK_USE_PIPELINE_CACHE");
}
