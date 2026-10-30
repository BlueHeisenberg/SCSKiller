using System.Text.RegularExpressions;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Unreal;

/// <summary>Which graphics API an Unreal game runs on (EngineInfo.GraphicsApi). SM5 shaders serve both DX11 and DX12, so
/// the shader libraries alone don't tell; this follows the engine's own choice (WindowsDynamicRHI.cpp):
///   1. command line -dx11/-d3d11, -dx12/-d3d12, -vulkan (here: the user's Steam launch options, then the game's Steam
///      launch menu: one API offered decides it; several (DirectX 12 / DirectX 11 entries) leave it to the game's last log,
///      else to Steam's default entry when that runs DX12;
///   2. the user's GameUserSettings.ini: [D3DRHIPreference] PreferredRHI (5.1+) or bUseD3D12InGame, or a fork's own
///      PreferredGraphicsAPI (Gearbox). UE4 honours it only when the project doesn't set DefaultGraphicsRHI explicitly;
///   3. [/Script/WindowsTargetPlatform.WindowsTargetSettings] DefaultGraphicsRHI over the Engine ini hierarchy, from the
///      engine's BaseEngine.ini (a fork may set it there: Dead Island 2) to the user's saved Engine.ini; _Default or unset =
///      the engine default: UE4 DX11, UE5 DX12. A WinGDK build (the Xbox app's PC builds) reads its own target's
///      [/Script/WinGDKPlatformEditor.WinGDKTargetSettings] over the WinGDK hierarchy instead (<see cref="WinGdkEngineIni"/>):
///      the engine's BaseWinGDKEngine.ini sets DX12 there. Without it the game's last log decides, else UE5's DX12 is a
///      guess and UE4 offers both.
/// A fork whose own WindowsDynamicRHI.cpp starts DX12 unless -dx11/-d3d11 is given (<see cref="Dx12Forks"/>) skips 2 and 3.
/// Values: "D3D12" / "D3D11" / "Vulkan", suffixed " (launch option)", " (user setting)" or " (last run)" when that decided
/// it rather than the project default; "D3D11 or D3D12" when it can't be decided: the project defaults to DX11 but ships
/// DX12-only (SM6) shaders or ray tracing (DX12 is then a launch or in-game choice) and the game's last log doesn't say, the Steam launch menu offers
/// several and the game's last log doesn't say, or the config is unreadable (encrypted) and the log doesn't say. SM6-only
/// shader libraries mean D3D12 whatever the config says.</summary>
public static class UnrealRhi
{
    public const string Ambiguous = "D3D11 or D3D12";

    /// <summary>Forks whose engine starts DX12 unless the command line forces another RHI; their config takes no part.
    /// FF7 Remake (4.18): read from its exe's PlatformCreateDynamicRHI.</summary>
    static readonly string[] Dx12Forks = ["GAME_FinalFantasy7Remake"];

    /// <summary>Config files inside the paks that take part, lowest priority first ({P} = the project folder).</summary>
    static readonly string[] EngineIni = ["Engine/Config/BaseEngine.ini", "Engine/Config/Windows/BaseWindowsEngine.ini", "Engine/Platforms/Windows/Config/BaseWindowsEngine.ini",
        ProjectEngineIni, "Engine/Config/Windows/WindowsEngine.ini", "Engine/Platforms/Windows/Config/WindowsEngine.ini", "{P}/Config/Windows/WindowsEngine.ini",
        "{P}/Platforms/Windows/Config/WindowsEngine.ini"];
    const string ProjectEngineIni = "{P}/Config/DefaultEngine.ini";

    /// <summary>A WinGDK build's Engine ini hierarchy, lowest priority first.</summary>
    static readonly string[] WinGdkEngineIni = ["Engine/Config/BaseEngine.ini", "Engine/Platforms/WinGDK/Config/BaseWinGDKEngine.ini", ProjectEngineIni,
        "Engine/Platforms/WinGDK/Config/WinGDKEngine.ini", "{P}/Config/WinGDK/WinGDKEngine.ini", "{P}/Platforms/WinGDK/Config/WinGDKEngine.ini"];

    /// <summary>The exe is a WinGDK build's (…\Binaries\WinGDK\).</summary>
    public static bool IsWinGdk(string exePath) => Path.GetFileName(Path.GetDirectoryName(exePath))?.Equals("WinGDK", StringComparison.OrdinalIgnoreCase) == true;

    static readonly string[] UserSettingsIni = ["{P}/Config/DefaultGameUserSettings.ini", "{P}/Config/Windows/WindowsGameUserSettings.ini"];

    static readonly string[] DeviceProfilesIni = ["Engine/Config/BaseDeviceProfiles.ini", "Engine/Config/Windows/WindowsDeviceProfiles.ini",
        "Engine/Platforms/Windows/Config/WindowsDeviceProfiles.ini", "{P}/Config/DefaultDeviceProfiles.ini", "{P}/Config/Windows/WindowsDeviceProfiles.ini",
        "{P}/Platforms/Windows/Config/WindowsDeviceProfiles.ini"];

    static readonly string[] ConsoleVariablesIni = ["Engine/Config/ConsoleVariables.ini", "{P}/Config/ConsoleVariables.ini"];

    static readonly string[] ScalabilityIni = ["Engine/Config/BaseScalability.ini", "Engine/Config/Windows/BaseWindowsScalability.ini",
        "Engine/Config/Windows/WindowsScalability.ini", "Engine/Platforms/Windows/Config/WindowsScalability.ini", "{P}/Config/DefaultScalability.ini",
        "{P}/Config/Windows/WindowsScalability.ini", "{P}/Platforms/Windows/Config/WindowsScalability.ini"];

    /// <summary>Whether a pak file path is one of the config files <see cref="Resolve"/>, <see cref="RtPipelinesOff"/>,
    /// <see cref="RayTracingOff"/> or <see cref="MaxAnisotropy"/> reads.</summary>
    public static bool IsConfig(string path, string project) =>
        EngineIni.Concat(WinGdkEngineIni).Concat(UserSettingsIni).Concat(DeviceProfilesIni).Concat(ConsoleVariablesIni).Concat(ScalabilityIni).Any(p => Same(p, path, project));

    /// <summary>The engine's own scalability groups (sg.&lt;Group&gt;): it applies their [Group@Level] sections itself.</summary>
    static readonly string[] EngineGroups = ["ResolutionQuality", "ViewDistanceQuality", "AntiAliasingQuality", "ShadowQuality", "GlobalIlluminationQuality",
        "ReflectionQuality", "PostProcessQuality", "TextureQuality", "EffectsQuality", "FoliageQuality", "ShadingQuality", "LandscapeQuality"];

    /// <summary>The player's r.MaxAnisotropy, which a fork's static samplers may follow (texture filtering, Planning.SamplerVariants);
    /// null when the config doesn't decide it: nothing sets it, a value isn't a number, the launch options name it, or a
    /// game-specific [Group@Level] group sets it at a level its saved settings don't name. Lowest priority first (ECVF_SetBy):
    ///   1. the engine's scalability groups ([TextureQuality@N] over the Scalability ini hierarchy and the user's Scalability.ini),
    ///      at the level the GameUserSettings [ScalabilityGroups] save, else the device profile's sg value, else 3 (Epic);
    ///   2. [/Script/Engine.RendererSettings] over the Engine ini hierarchy (project setting);
    ///   3. [SystemSettings] and [ConsoleVariables] over the Engine ini hierarchy and the user's Engine.ini;
    ///   4. the active device profile's CVars (dp.Override, else WindowsNoEditor for UE4 or Windows for UE5), its base profiles below it;
    ///   5. a game's own groups: [Group@Level] sections outside the Scalability inis, or of a group no sg cvar names, at
    ///      the level its saved settings name (Group=..._LEVEL). The game applies them itself, after startup.</summary>
    public static int? MaxAnisotropy(int engineMajor, IReadOnlyDictionary<string, string> configs, string project, string? userDir, string launch = "")
    {
        const string Key = "r.MaxAnisotropy";
        if (Regex.IsMatch(launch, @"r\.MaxAnisotropy(?![.\w])", RegexOptions.IgnoreCase)) return null;
        var user = UserConfig(userDir);
        string User(string file) => user != null && File.Exists(Path.Combine(user, file)) ? File.ReadAllText(Path.Combine(user, file)).TrimStart('﻿') : "";
        var engine = Ordered(EngineIni, configs, project).Append(User("Engine.ini")).ToList();
        var scalability = Ordered(ScalabilityIni, configs, project).Append(User("Scalability.ini")).ToList();
        var settings = Ordered(UserSettingsIni, configs, project).Append(User("GameUserSettings.ini")).ToList();
        var profile = ProfileCVars(Ordered(DeviceProfilesIni, configs, project).Append(User("DeviceProfiles.ini")).ToList(),
            Last(engine, "SystemSettings", "dp.Override") ?? Last(engine, "ConsoleVariables", "dp.Override") ?? (engineMajor >= 5 ? "Windows" : "WindowsNoEditor"));
        var sg = settings.SelectMany(Entries).Where(e => e.Section.Equals("ScalabilityGroups", StringComparison.OrdinalIgnoreCase) && e.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase))
            .Concat(profile.Where(c => c.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase)).Select(c => ("", c.Key, c.Value)))
            .Select(e => e.Key[3..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ByEngine(string group) => EngineGroups.Contains(group, StringComparer.OrdinalIgnoreCase) || sg.Contains(group);

        string? value = null;   // "?": not decidable
        string? Pick(string? higher) => value = higher ?? value;
        // the device profile's sg values are only the defaults the saved settings override
        foreach (var group in Groups(scalability, Key).Where(ByEngine))
            Pick(Int(Last(settings, "ScalabilityGroups", "sg." + group) ?? profile.GetValueOrDefault("sg." + group) ?? "3") is not { } n ? "?"
                : Last(scalability, $"{group}@{(Math.Clamp(n, 0, 4) == 4 ? "Cine" : n)}", Key));   // the top level's section is @Cine
        Pick(Last(engine, Renderer, Key));
        Pick(Last(engine, "SystemSettings", Key) is { } s && Last(engine, "ConsoleVariables", Key) is { } c && s != c ? "?"
            : Last(engine, "SystemSettings", Key) ?? Last(engine, "ConsoleVariables", Key));
        Pick(profile.GetValueOrDefault(Key));
        // ConsoleVariables.ini outranks the device profile, but a Shipping build never reads it (DISABLE_CHEAT_CVARS)
        var own = settings.Concat(scalability).Concat(engine).ToList();
        foreach (var group in Groups(settings.Concat(engine), Key).Concat(Groups(scalability, Key).Where(g => !ByEngine(g))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var levels = own.SelectMany(Entries).Select(e => e.Section).Where(x => x.StartsWith(group + "@", StringComparison.OrdinalIgnoreCase))
                .Select(x => x[(group.Length + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var saved = settings.SelectMany(Entries).Where(e => e.Key.Equals(group, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value).LastOrDefault();
            var level = saved == null ? null : levels.FirstOrDefault(l => saved.Equals(l, StringComparison.OrdinalIgnoreCase) || saved.EndsWith("_" + l, StringComparison.OrdinalIgnoreCase));
            if (level == null) return null;   // the game applies it: no order among its groups makes another one's value sure
            Pick(Last(own, $"{group}@{level}", Key));
        }
        return value == "?" ? null : Int(value);
    }

    /// <summary>A cvar's integer as the engine reads it (Atoi: the leading digits).</summary>
    static int? Int(string? v) => v != null && Regex.Match(v, @"^\s*-?\d+") is { Success: true } m && int.TryParse(m.Value, out var n) ? n : null;

    /// <summary>The groups whose [Group@Level] sections set <paramref name="key"/>.</summary>
    static IEnumerable<string> Groups(IEnumerable<string> inis, string key) =>
        inis.SelectMany(Entries).Where(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Section.IndexOf('@') > 0)
            .Select(e => e.Section[..e.Section.IndexOf('@')]).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>(section, key, value) of each key line; array prefixes (+ - . !) kept on the key.</summary>
    static IEnumerable<(string Section, string Key, string Value)> Entries(string ini)
    {
        var section = "";
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
            var eq = line.IndexOf('=');
            if (eq > 0 && line[0] != ';') yield return (section, line[..eq].Trim(), line[(eq + 1)..].Trim().Trim('"'));
        }
    }

    /// <summary>The CVars a device profile sets, its base profiles' (BaseProfileName) overridden by its own. CVars array
    /// entries: "+"/"." add, "-" removes the same entry, "!" clears, over the files in order.</summary>
    static Dictionary<string, string> ProfileCVars(List<string> files, string name)
    {
        var chain = new List<string>();
        for (var n = name; n.Length > 0 && !chain.Contains(n, StringComparer.OrdinalIgnoreCase); n = Last(files, $"{n} DeviceProfile", "BaseProfileName") ?? "") chain.Add(n);
        var cvars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in Enumerable.Reverse(chain))
        {
            var entries = new List<string>();
            foreach (var (_, key, value) in files.SelectMany(Entries).Where(e => e.Section.Equals($"{n} DeviceProfile", StringComparison.OrdinalIgnoreCase)))
            {
                if (!key.TrimStart('+', '.', '-', '!').Trim().Equals("CVars", StringComparison.OrdinalIgnoreCase)) continue;
                if (key[0] == '!') entries.Clear();
                else if (key[0] == '-') entries.Remove(value);
                else entries.Add(value);
            }
            foreach (var e in entries)
                if (e.IndexOf('=') is > 0 and var eq) cvars[e[..eq].Trim()] = e[(eq + 1)..].Trim();
        }
        return cvars;
    }

    /// <summary>Ray tracing is off in the game (DRAGON BALL: Sparking! ZERO): [/Script/Engine.RendererSettings] r.RayTracing,
    /// the last of the Engine ini hierarchy (base, project default, Windows platform layers) and the user's Engine.ini, is
    /// off, and nothing that outranks it sets r.RayTracing to anything else: [SystemSettings] or [ConsoleVariables] of any
    /// of those files, ConsoleVariables.ini, a device profile's CVars, the user's DeviceProfiles.ini, the launch options. The
    /// cvar is read once at startup and gates all ray tracing. Unset is not off: a false off would leave ray tracing unplanned.</summary>
    public static bool RayTracingOff(IReadOnlyDictionary<string, string> configs, string project, string? userDir = null, string launch = "")
    {
        var user = UserConfig(userDir);
        string User(string file) => user != null && File.Exists(Path.Combine(user, file)) ? File.ReadAllText(Path.Combine(user, file)) : "";
        var engine = Ordered(EngineIni, configs, project).Append(User("Engine.ini")).ToList();
        if (Last(engine, Renderer, "r.RayTracing") is not { } v || !IsOff(v)) return false;
        if (Regex.IsMatch(launch, @"r\.RayTracing(?![.\w])", RegexOptions.IgnoreCase)) return false;
        var outranking = engine.Concat(Ordered(ConsoleVariablesIni, configs, project)).Concat(Ordered(DeviceProfilesIni, configs, project)).Append(User("DeviceProfiles.ini"));
        return outranking.All(text => RayTracingCVars(text).All(IsOff));
    }

    const string Renderer = "/Script/Engine.RendererSettings";

    /// <summary>The values r.RayTracing is given outside [/Script/Engine.RendererSettings]: as a key, or in a CVars entry.</summary>
    static IEnumerable<string> RayTracingCVars(string ini)
    {
        string? section = null;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0 || line[0] == ';') continue;
            var key = line[..eq].TrimStart('+', '.', '-', '!').Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (key.Equals("CVars", StringComparison.OrdinalIgnoreCase) && value.IndexOf('=') is > 0 and var ceq)
                (key, value) = (value[..ceq].Trim(), value[(ceq + 1)..].Trim());
            else if (section?.Equals(Renderer, StringComparison.OrdinalIgnoreCase) == true) continue;
            if (key.Equals("r.RayTracing", StringComparison.OrdinalIgnoreCase)) yield return value;
        }
    }

    /// <summary>The Windows device profile sets r.RayTracing.AllowPipeline=0: the game never builds a ray tracing state
    /// object, only inline ray tracing (SILENT HILL: Townfall). CVars array entries: "+"/"." add, "-" removes the same
    /// entry, "!" clears; the last file wins. The Windows profile is the root of the PC profiles: no base to follow.</summary>
    public static bool RtPipelinesOff(IReadOnlyDictionary<string, string> configs, string project)
    {
        string? v = null;
        foreach (var text in Ordered(DeviceProfilesIni, configs, project))
        {
            var inWindows = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inWindows = line.Equals("[Windows DeviceProfile]", StringComparison.OrdinalIgnoreCase); continue; }
                var eq = line.IndexOf('=');
                if (!inWindows || eq <= 0 || !line[..eq].TrimStart('+', '.', '-', '!').Trim().Equals("CVars", StringComparison.OrdinalIgnoreCase)) continue;
                if (line[0] == '!') { v = null; continue; }
                var cvar = line[(eq + 1)..].Trim().Trim('"');
                var ceq = cvar.IndexOf('=');
                if (ceq <= 0 || !cvar[..ceq].Trim().Equals("r.RayTracing.AllowPipeline", StringComparison.OrdinalIgnoreCase)) continue;
                var value = cvar[(ceq + 1)..].Trim();
                if (line[0] != '-') v = value;
                else if (v == value) v = null;
            }
        }
        return v == "0";
    }

    /// <summary>NVIDIA Aftermath with shader debug info: r.GPUCrashDebugging.Aftermath.DumpShaderDebugInfo or .TrackAll (all
    /// feature flags) on in the Engine ini hierarchy, and r.GPUCrashDebugging.Aftermath not off (RHICoreNvidiaAftermath.cpp
    /// defaults it to 1). Aftermath's GenerateShaderDebugInfo with crash dumps puts every pipeline under another NVIDIA cache
    /// key, so a warm's entries never serve the game. The generic r.GPUCrashDebugging takes no part: false only defers to
    /// the Aftermath cvar (UE::RHI::ShouldEnableGPUCrashFeature), and games set it false in BaseEngine.ini. The launch options
    /// override both as that function and InitializeBeforeDeviceCreation read them. The cvars count from [ConsoleVariables],
    /// else [SystemSettings], else any [/Script/...] section, of the Engine ini hierarchy (<paramref name="configs"/>, which
    /// may hold the install's loose files, <see cref="WithLoose"/>) and the user's Engine.ini and GameUserSettings.ini.
    /// ponytail: a device profile that turns it off isn't read.</summary>
    public static bool AftermathShaderDebug(IReadOnlyDictionary<string, string> configs, string project, string launch = "", string? userDir = null) =>
        Aftermath(configs, project, launch, userDir).ShaderDebug;

    /// <summary>The config or the launch options turn NVIDIA Aftermath off (<see cref="AftermathShaderDebug"/>'s sources).</summary>
    public static bool AftermathOff(IReadOnlyDictionary<string, string> configs, string project, string launch = "", string? userDir = null) =>
        !Aftermath(configs, project, launch, userDir).On;

    static (bool On, bool ShaderDebug) Aftermath(IReadOnlyDictionary<string, string> configs, string project, string launch, string? userDir)
    {
        var user = UserConfig(userDir);
        string User(string file) => user != null && File.Exists(Path.Combine(user, file)) ? File.ReadAllText(Path.Combine(user, file)).TrimStart('﻿') : "";
        var engine = Ordered(EngineIni, configs, project).Append(User("Engine.ini")).Append(User("GameUserSettings.ini")).ToList();
        string? CVar(string key) => Last(engine, "ConsoleVariables", key) ?? Last(engine, "SystemSettings", key)
            ?? Last(engine, s => s.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase), key);
        bool On(string key) => CVar(key) is { } v && IsOn(v);
        bool Param(string name) => Regex.IsMatch(launch, $@"(?:^|\s)-{name}(?![=\w])", RegexOptions.IgnoreCase);
        var aftermath = !Param("nogpucrashdebugging") && (Param("gpucrashdebugging") || !(CVar("r.GPUCrashDebugging.Aftermath") is { } v && IsOff(v)));
        if (Regex.Match(launch, @"(?:^|\s)-nvaftermath=(\d+)", RegexOptions.IgnoreCase) is { Success: true } m) aftermath = m.Groups[1].Value.Trim('0').Length > 0;
        else if (Param("nvaftermath")) aftermath = true;
        return (aftermath, aftermath && (On("r.GPUCrashDebugging.Aftermath.DumpShaderDebugInfo") || On("r.GPUCrashDebugging.Aftermath.TrackAll")
            || Param("nvAftermathDumpShaderDebugInfo") || Param("nvaftermathall")));
    }

    /// <summary><paramref name="configs"/> with the Engine ini hierarchy's files the paks don't hold read from the install
    /// under <paramref name="root"/> (the folder above the project's): the engine reads a loose file the paks lack.</summary>
    internal static IReadOnlyDictionary<string, string> WithLoose(IReadOnlyDictionary<string, string> configs, string root, string project)
    {
        Dictionary<string, string>? all = null;
        foreach (var rel in EngineIni.Select(p => p.Replace("{P}", project)).Where(p => !configs.Keys.Any(k => k.Equals(p, StringComparison.OrdinalIgnoreCase))))
            try
            {
                var file = GameFiles.Find(root, rel);
                if (File.Exists(file)) (all ??= new(configs, StringComparer.OrdinalIgnoreCase))[rel] = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return all ?? configs;
    }

    static bool IsOn(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);

    static bool IsOff(string v) => v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase);

    static bool Same(string pattern, string path, string project) => string.Equals(pattern.Replace("{P}", project), path, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Ordered(string[] order, IReadOnlyDictionary<string, string> configs, string project) =>
        order.SelectMany(p => configs.Where(c => Same(p, c.Key, project)).Select(c => c.Value.TrimStart('\uFEFF')));   // a BOM would hide the first section header

    /// <summary>(GraphicsApi, the evidence that decided it). <paramref name="configs"/>: pak path -> text of the files
    /// <see cref="IsConfig"/> selects; <paramref name="userDir"/>: the user's Saved folder or null; <paramref name="launch"/>:
    /// the command line the store adds; <paramref name="menu"/>, <paramref name="menuDefault"/>: <see cref="LaunchMenu"/>.</summary>
    public static (string Api, string Why) Resolve(int engineMajor, IReadOnlyCollection<string> platforms, IReadOnlyDictionary<string, string> configs,
        string project, string? userDir, string launch, IReadOnlyList<string>? menu = null, string? menuDefault = null, string? fork = null, bool winGdk = false) =>
        Resolve(engineMajor, platforms, configs, project, userDir, launch, out _, menu, menuDefault, fork, winGdk);

    /// <param name="engineDefault">the engine's default API for <paramref name="engineMajor"/> decided it: neither the game,
    /// its config nor the user's settings name one (step 3, DefaultGraphicsRHI unset or _Default)</param>
    /// <param name="winGdk">the exe is a WinGDK build's (<see cref="IsWinGdk"/>)</param>
    public static (string Api, string Why) Resolve(int engineMajor, IReadOnlyCollection<string> platforms, IReadOnlyDictionary<string, string> configs,
        string project, string? userDir, string launch, out bool engineDefault, IReadOnlyList<string>? menu = null, string? menuDefault = null, string? fork = null,
        bool winGdk = false)
    {
        engineDefault = false;
        bool sm5 = platforms.Contains("PCD3D_SM5"), sm6 = platforms.Contains("PCD3D_SM6");
        if (sm6 && !sm5) return ("D3D12", "only SM6 shaders, which run on DX12 only");
        if (Regex.Match(launch, @"(?:^|\s)-(dx11|d3d11|dx12|d3d12|vulkan)\b", RegexOptions.IgnoreCase) is { Success: true } cmd)
            return ($"{Api(cmd.Groups[1].Value)} (launch option)", $"launch option -{cmd.Groups[1].Value}");
        if (menu?.Any(e => MenuApi(e) != null) == true)
        {
            var bare = Resolve(engineMajor, platforms, configs, project, userDir, launch, out var bareDefault, fork: fork, winGdk: winGdk);   // what an entry without a flag runs
            var plain = bare.Api.Split(" (")[0];
            var offered = menu.Select(e => MenuApi(e) ?? plain).Distinct().ToList();
            if (offered.Count == 1)
                return menu.All(e => MenuApi(e) != null) ? ($"{offered[0]} (launch option)", $"the game's Steam launch menu passes {offered[0]}") : bare;
            if (LastRun(userDir) is { } run) return ($"{run.Api} (last run)", $"the game's Steam launch menu offers {string.Join(" / ", offered)}; the game's last log: {run.Line}");
            // a player who picks DX11 runs it, so only a DX12 default decides
            if (menuDefault != null && (MenuApi(menuDefault) ?? plain) == "D3D12")
            {
                if (MenuApi(menuDefault) != null) return ("D3D12 (launch option)", "the default entry of the game's Steam launch menu passes D3D12, no log");
                engineDefault = bareDefault;
                return bare;
            }
            return (Ambiguous, $"the game's Steam launch menu offers {string.Join(" / ", offered)}, no log");
        }
        if (fork != null && Dx12Forks.Contains(fork)) return ("D3D12", $"{fork} starts DX12 unless -dx11 or -d3d11 is given");

        var userConfig = UserConfig(userDir, winGdk);
        string? User(string file) => userConfig != null && File.Exists(Path.Combine(userConfig, file)) ? File.ReadAllText(Path.Combine(userConfig, file)) : null;
        var layers = winGdk ? WinGdkEngineIni : EngineIni;
        var engine = Ordered(layers, configs, project).Append(User("Engine.ini") ?? "").ToList();
        var settings = Ordered(UserSettingsIni, configs, project).Append(User("GameUserSettings.ini") ?? "").ToList();

        var def = Last(engine, winGdk ? "/Script/WinGDKPlatformEditor.WinGDKTargetSettings" : "/Script/WindowsTargetPlatform.WindowsTargetSettings", "DefaultGraphicsRHI");
        if (winGdk && def == null)   // the Windows target's setting doesn't apply to a WinGDK build
        {
            if (LastRun(userDir) is { } ran) return ($"{ran.Api} (last run)", $"no WinGDK DefaultGraphicsRHI; the game's last log: {ran.Line}");
            if (engineMajor >= 5)
            {
                engineDefault = true;
                return ("D3D12", "no WinGDK DefaultGraphicsRHI (UE5 default)");
            }
            return (Ambiguous, "no WinGDK DefaultGraphicsRHI, no log");
        }
        var explicitDefault = def is not (null or "DefaultGraphicsRHI_Default");
        var byProject = def switch
        {
            "DefaultGraphicsRHI_DX11" => "D3D11", "DefaultGraphicsRHI_DX12" => "D3D12", "DefaultGraphicsRHI_Vulkan" => "Vulkan",
            _ => engineMajor >= 5 || project.Equals("OakGame", StringComparison.OrdinalIgnoreCase) ? "D3D12" : "D3D11",   // Borderlands 3 ships with DX12 on
        };
        var why = def != null ? $"DefaultGraphicsRHI={def}" + (def == "DefaultGraphicsRHI_Default" ? $" (UE{engineMajor} default)" : "") : $"no DefaultGraphicsRHI (UE{engineMajor} default)";

        // the user's choice, if the engine honours it
        var pref = Last(settings, "D3DRHIPreference", "PreferredRHI") is { } p ? (Api(p), $"PreferredRHI={p}")
            : Last(settings, "*", "PreferredGraphicsAPI") is { } g ? (Api(g), $"PreferredGraphicsAPI={g}")
            : Last(settings, "D3DRHIPreference", "bUseD3D12InGame") is { } b && b.Equals("true", StringComparison.OrdinalIgnoreCase) ? ("D3D12", "bUseD3D12InGame=True")
            : ((string?)null, "");
        var honoured = pref.Item1 is { } api && api != "?" && (engineMajor >= 5 || !explicitDefault) ? pref.Item1 : null;
        if (honoured != null && honoured != byProject)
            return ($"{honoured} (user setting)", $"{pref.Item2} in the user's GameUserSettings.ini over {why}");

        if (!configs.Keys.Any(k => Same(ProjectEngineIni, k, project))) // the project's own config is unreadable (encrypted paks)
        {
            if (LastRun(userDir) is { } log) return ($"{log.Api} (last run)", $"project config unreadable; the game's last log: {log.Line}");
            return sm5 && sm6 ? (Ambiguous, "project config unreadable, both SM5 and SM6 shaders ship, no log")
                : (Ambiguous, $"project config unreadable, no log (UE{engineMajor} default would be {byProject})");
        }
        // a UE4 DX12 game may ship SM5 libraries only; ray tracing runs on DX12 only, so a project that ships it offers DX12
        var offers = byProject != "D3D11" ? null
            : sm6 ? $"{why}, but DX12-only (SM6) shaders ship: DX12 is a launch or in-game option"
            : Last(Ordered(layers, configs, project), "/Script/Engine.RendererSettings", "r.RayTracing") is { } rt && IsOn(rt)
                ? $"{why}, but the project ships ray tracing (r.RayTracing={rt}), which runs on DX12 only: DX12 is a launch or in-game option" : null;
        // the player's own choice of the default API outranks a log of an earlier run on the other one
        if (offers != null && honoured != null) return ($"{honoured} (user setting)", $"{pref.Item2} in the user's GameUserSettings.ini; {offers}");
        if (offers != null)
            return LastRun(userDir) is { } last ? ($"{last.Api} (last run)", $"{offers}; the game's last log: {last.Line}") : (Ambiguous, offers + ", no log");
        engineDefault = !explicitDefault && honoured == null;
        return (byProject, why + (pref.Item1 != null ? $"; user {pref.Item2} agrees or is ignored" : ""));
    }

    /// <summary>The API a launch-menu entry picks: a flag in its arguments, else "DirectX 12" or "DX12" in its description.</summary>
    static string? MenuApi(string entry) =>
        Regex.Match(entry, @"(?:^|\s)-(dx11|d3d11|dx12|d3d12|vulkan)\b|\b(?:DirectX|DX)\s*(1[12])\b", RegexOptions.IgnoreCase) is { Success: true } m
            ? Api(m.Groups[1].Success ? m.Groups[1].Value : "dx" + m.Groups[2].Value) : null;

    static string Api(string v) => v.Trim('"').ToLowerInvariant() switch
    {
        "dx11" or "d3d11" => "D3D11", "dx12" or "d3d12" => "D3D12", "vulkan" => "Vulkan", _ => "?",
    };

    /// <summary>Last value of section/key over ini texts in priority order; section "*" = any section.</summary>
    static string? Last(IEnumerable<string> inis, string section, string key) =>
        Last(inis, s => section == "*" || s.Equals(section, StringComparison.OrdinalIgnoreCase), key);

    /// <summary>Last value of key over ini texts in priority order, in the sections <paramref name="section"/> accepts.</summary>
    static string? Last(IEnumerable<string> inis, Func<string, bool> section, string key)
    {
        string? v = null;
        foreach (var text in inis)
        {
            string? cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { cur = line.Trim('[', ']'); continue; }
                var eq = line.IndexOf('=');
                if (cur != null && eq > 0 && section(cur)
                    && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) v = line[(eq + 1)..].Trim().Trim('"');
            }
        }
        return v;
    }

    /// <summary>The RHI the game's newest log (Saved\Logs, Saved\Crashes) says it started with.</summary>
    static (string Api, string Line)? LastRun(string? userDir)
    {
        if (userDir == null) return null;
        foreach (var log in Logs(userDir))
        {
            using var r = new StreamReader(new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (var (n, line) = (0, r.ReadLine()); line != null && n < 20000; n++, line = r.ReadLine())
                if (Regex.Match(line, @"LogRHI: Using (?:Default|Preferred|Forced) RHI: (D3D11|D3D12|Vulkan)|(LogD3D1[12])RHI:") is { Success: true } m)
                    return (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value == "LogD3D12" ? "D3D12" : "D3D11", $"{log.Name} {log.LastWriteTime:yyyy-MM-dd}: {line.Trim()}");
        }
        return null;
    }

    static IEnumerable<FileInfo> Logs(string userDir)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return new[] { "Logs", "Crashes" }.Select(d => Path.Combine(userDir, d)).Where(Directory.Exists)
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*.log", opts)).OrderByDescending(f => f.LastWriteTimeUtc);
    }

    /// <summary>What <see cref="Resolve"/> reads besides the game files, as a stamp that changes when any of it does: the
    /// user's Saved folder, its newest ini and log, the Steam launch options and launch menu.</summary>
    public static string Stamp(Game game, string project)
    {
        var (entries, def) = LaunchMenu(game);
        return string.Join('|', UserFiles(UserDir(game, project), IsWinGdk(game.ExePath)), LaunchOptions(game), string.Join(';', entries), def);
    }

    /// <summary>The user files <see cref="Resolve"/> and <see cref="MaxAnisotropy"/> read, each by presence, length and write
    /// time: the selected config folder's inis, and every log <see cref="LastRun"/> may read.</summary>
    internal static string UserFiles(string? userDir, bool winGdk = false)
    {
        if (userDir == null) return "";
        static string Of(FileInfo f) => f.Exists ? $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : $"{f.FullName}:-";
        var config = UserConfig(userDir, winGdk);
        var inis = config == null ? [] : new[] { "Engine.ini", "GameUserSettings.ini", "Scalability.ini", "DeviceProfiles.ini" }.Select(n => Of(new FileInfo(Path.Combine(config, n))));
        return string.Join('>', inis.Prepend(userDir).Concat(Logs(userDir).Select(Of)));
    }

    /// <summary>The newest Saved\Config\Windows* folder, a WinGDK build's Saved\Config\WinGDK: the one the game reads.</summary>
    static string? UserConfig(string? userDir, bool winGdk = false) =>
        userDir == null || !Directory.Exists(Path.Combine(userDir, "Config")) ? null
            : Directory.EnumerateDirectories(Path.Combine(userDir, "Config"), ConfigFolders(winGdk)).MaxBy(Directory.GetLastWriteTimeUtc);

    static string ConfigFolders(bool winGdk) => winGdk ? "WinGDK" : "Windows*";

    /// <summary>The user's Saved folder (…\Saved\Config\Windows*\, a WinGDK build's …\WinGDK\, holds Engine.ini and GameUserSettings.ini): under
    /// %LOCALAPPDATA%, Documents\My Games or Saved Games, named after the project, the exe, the install folder or the game,
    /// possibly under a company folder, "Saved" or "Saved_&lt;store&gt;_&lt;user&gt;". Newest wins. Read-only.</summary>
    public static string? UserDir(Game game, string project)
    {
        var names = new[] { project, ExeStem(game), Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')), game.Name }
            .Where(n => n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"), Path.Combine(home, "Saved Games") }
            .Where(Directory.Exists).ToList();
        var flat = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var folders = ConfigFolders(IsWinGdk(game.ExePath));
        return roots.SelectMany(r => names.Select(n => Path.Combine(r, n)).Concat(Directory.EnumerateDirectories(r, "*", flat).SelectMany(c => names.Select(n => Path.Combine(c, n)))))
            .Where(Directory.Exists).SelectMany(d => Directory.EnumerateDirectories(d, "Saved*", flat))
            .Where(s => Directory.Exists(Path.Combine(s, "Config")) && Directory.EnumerateDirectories(Path.Combine(s, "Config"), folders, flat).Any())
            .OrderByDescending(s => Directory.GetLastWriteTimeUtc(Path.Combine(s, "Config"))).FirstOrDefault();
    }

    static string ExeStem(Game game) => Regex.Replace(Path.GetFileNameWithoutExtension(game.ExePath), @"-Win(64|GDK)-Shipping$", "", RegexOptions.IgnoreCase);

    /// <summary>The Steam launch options the user set for the game ("" if none, or not a Steam game): the newest
    /// userdata\*\config\localconfig.vdf, the app's block; a non-Steam game's from Steam's shortcuts (<see cref="SteamShortcuts"/>).</summary>
    public static string LaunchOptions(Game game) => LaunchOptions(game, SteamRoot);

    internal static string LaunchOptions(Game game, string steamRoot)
    {
        if (SteamShortcuts.AppId(game.Id) is { } shortcut) return SteamShortcuts.Find(steamRoot, shortcut)?.LaunchOptions ?? "";
        if (SteamSource.AppId(game.Id) is not { } app) return "";
        var userdata = Path.Combine(steamRoot, "userdata");
        var vdf = Directory.Exists(userdata)
            ? Directory.EnumerateDirectories(userdata).Select(u => new FileInfo(Path.Combine(u, "config", "localconfig.vdf"))).Where(f => f.Exists).MaxBy(f => f.LastWriteTimeUtc)
            : null;
        if (vdf == null) return "";
        var text = File.ReadAllText(vdf.FullName);
        foreach (Match m in Regex.Matches(text, $@"""{app}""\s*\{{")) // the app id also keys other blocks
        {
            var end = m.Index + m.Length;
            for (var depth = 1; end < text.Length && depth > 0; end++) depth += text[end] == '{' ? 1 : text[end] == '}' ? -1 : 0;
            if (SteamSource.Values(text[m.Index..end], "LaunchOptions").FirstOrDefault() is { } options) return options;
        }
        return "";
    }

    /// <summary>The launch menu the game defines in Steam (appinfo.vdf config/launch): "arguments description" of each
    /// Windows entry that starts the game (its exe, or the root launcher named after it), a beta branch's own entries left
    /// out, and the one of type "default". Empty if none, or not a Steam game. Kept per appinfo.vdf stamp.</summary>
    public static (IReadOnlyList<string> Entries, string? Default) LaunchMenu(Game game)
    {
        if (SteamSource.AppId(game.Id) is not { } id) return ([], null);
        var f = new FileInfo(Path.Combine(SteamRoot, "appcache", "appinfo.vdf"));
        var stamp = f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";
        var key = $"{game.Id}|{game.InstallDir}|{game.ExePath}";
        lock (menus)
        {
            if (menuStamp != stamp) { menus.Clear(); menuStamp = stamp; }
            if (menus.TryGetValue(key, out var hit)) return hit;
        }
        var apps = SteamSource.Apps(f.FullName, new HashSet<uint> { id });
        var menu = LaunchMenu(apps?.GetValueOrDefault(id), game);
        if (apps != null) lock (menus) if (menuStamp == stamp) menus[key] = menu;   // a failed read is tried again
        return menu;
    }

    static readonly Dictionary<string, (IReadOnlyList<string>, string?)> menus = [];
    static string? menuStamp;

    internal static (IReadOnlyList<string> Entries, string? Default) LaunchMenu(Dictionary<string, object>? app, Game game)
    {
        if (app?.GetValueOrDefault("config") is not Dictionary<string, object> config || config.GetValueOrDefault("launch") is not Dictionary<string, object> launch) return ([], null);
        var install = Path.GetFullPath(game.InstallDir).TrimEnd('\\', '/');
        bool StartsTheGame(string exe)
        {
            var p = Path.GetFullPath(GameFiles.Under(install, exe));
            return p.Equals(Path.GetFullPath(game.ExePath), StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(p)!.Equals(install, StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(p).Equals(ExeStem(game), StringComparison.OrdinalIgnoreCase);
        }
        var entries = launch.Values.OfType<Dictionary<string, object>>()
            .Where(e => e.GetValueOrDefault("executable") is string exe && StartsTheGame(exe))
            .Where(e => e.GetValueOrDefault("config") is not Dictionary<string, object> c
                || !c.ContainsKey("BetaKey") && (c.GetValueOrDefault("oslist") is not string os || os.Contains("windows", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        static string Text(Dictionary<string, object> e) => $"{e.GetValueOrDefault("arguments")} {e.GetValueOrDefault("description")}";
        var def = entries.Where(e => "default".Equals(e.GetValueOrDefault("type") as string, StringComparison.OrdinalIgnoreCase)).Select(Text).FirstOrDefault();
        return (entries.Select(Text).ToList(), def);
    }

    static string SteamRoot => SteamSource.Root();
}
