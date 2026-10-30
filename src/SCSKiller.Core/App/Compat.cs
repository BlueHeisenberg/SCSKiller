using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SCSKiller.Core.App;

/// <summary>The player's answer in a compatibility report, in the popup's order.</summary>
public enum CompatChoice { Gone, Better, NoHelp, Worse, NoStart, NotCompiled, NoStutterBefore }

/// <summary>What SCSKiller did for the game, as the report states it.</summary>
public enum CompatStatus { Compiled, Partly, NotSupported, Failed, NotCompiled }

/// <summary>The similar-systems line on a game page (<see cref="CompatFeed"/>).</summary>
public enum SimilarSystems { Helped, NoHelp, Warning }

/// <summary>POST /v1/compat's body, the whole of what a compatibility report sends, as snake_case JSON (<see cref="Json"/>).
/// Sent only when the user presses Send rating. A null member is sent as null: not known on this PC. From Linux
/// <see cref="WindowsBuild"/> is null and <see cref="Linux"/> adds os, os_build and proton_version.</summary>
public sealed record CompatReport(string GameId, string StoreBuildKey, CompatChoice Choice, bool FrameGen, bool Upscaler, bool Mods,
    string? CpuModel, string? GpuModel, string GpuVendor, string? DriverVersion, string? WindowsBuild, string AppVersion, CompatCompile Compile)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The system of a report from Linux. Null from Windows, whose body names no system but its build.</summary>
    [JsonIgnore] public LinuxSystem? Linux { get; init; }

    public byte[] Body()
    {
        if (Linux is not { } linux) return JsonSerializer.SerializeToUtf8Bytes(this, Json);
        var o = JsonSerializer.SerializeToNode(this, Json)!.AsObject();
        o["os"] = "linux";
        o["os_build"] = linux.Kernel;
        o["proton_version"] = linux.Proton;
        return JsonSerializer.SerializeToUtf8Bytes(o, Json);
    }

    /// <summary>What the rating popup says a report sends.</summary>
    public static string WhatIsSent => OperatingSystem.IsWindows()
        ? "Sent with the rating: your CPU model, GPU model, driver version, Windows version, and what SCSKiller compiled for this game. Nothing else."
        : "Sent with the rating: your CPU model, GPU model, driver version, Linux kernel version, the game's Proton version, and what SCSKiller compiled for this game. Nothing else.";

    /// <summary>Why the game page's Rate this compile is off for <paramref name="s"/> on the GPU vendor <paramref name="vendor"/>
    /// (<see cref="ScsKiller.PackGpu"/>; the server takes "nvidia" and "amd" only); null when it is on: a store build compiled and played at least once since, or whose
    /// last compile failed or was refused. <paramref name="linuxRefused"/>: this start's server refused a report from Linux
    /// (<see cref="Sharing.RefusesLinux"/>).</summary>
    public static string? WhyNot(GameState s, string? vendor, bool linuxRefused = false) =>
        linuxRefused ? Sharing.LinuxRefused
        // a game the user added has an id made from a path on this PC: never sent
        : s.Game.Store == Store.Manual || s.Game.Version == null ? NoBuild
        : vendor is not ("nvidia" or "amd") ? "Ratings are for NVIDIA and AMD GPUs."
        : s.CompileFailed ? null   // "could not be compiled" needs no launch
        : s.WarmedAt == null ? "Compile the game and play it once to rate how it ran."
        : !(s.PlayedAt > s.WarmedAt) ? "Play the game once since it compiled to rate how it ran."
        : null;

    public const string NoBuild = "SCSKiller can't tell which version of this game is installed.";

    /// <summary>The store build key uploads use too (game id, "@", the store's build id).</summary>
    public static string BuildKey(Game g) => $"{g.Id}@{g.Version}";

    /// <summary>"10.0.26300": Windows' major, minor and build number only.</summary>
    public static string ThisWindows => Environment.OSVersion.Version is var v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>The problem report's system line: "Windows: 10.0.26300", or from Linux "Linux: 7.2, Proton GE-Proton11-7".</summary>
    public static string SystemLine(LinuxSystem? linux) =>
        linux == null ? $"Windows: {ThisWindows}" : $"Linux: {linux.Kernel}, Proton {linux.Proton ?? "unknown"}";

    public static CompatCompile CompileOf(GameState s, bool maximumPlans) => new(
        s.Status == GameStatus.Unsupported && (s.WarmedAt != null || s.CompileFailed) ? CompatStatus.NotSupported
        : s.CompileFailed ? CompatStatus.Failed
        : s.WarmedAt == null ? CompatStatus.NotCompiled
        : ScsKiller.IsPartlyCompiled(s) ? CompatStatus.Partly : CompatStatus.Compiled,
        ScsKiller.ShownPercent(s), s.Plan?.Recorded > 0, s.MaximumForGame ?? maximumPlans);
}

/// <summary>A report's system on Linux: the kernel's major and minor numbers (<paramref name="Kernel"/>, "7.2", nothing
/// after them) and the build name of the Proton the game last ran on (<paramref name="Proton"/>, "GE-Proton11-7"; null: not
/// known).</summary>
public sealed record LinuxSystem(string Kernel, string? Proton)
{
    public static string ThisKernel => Environment.OSVersion.Version is var v ? $"{v.Major}.{v.Minor}" : "";
}

/// <summary><see cref="CompatReport.Compile"/>: the compile's status, the share of the game's pipelines it covered (null:
/// not known), whether it compiled recorded pipelines, and whether in Maximum mode.</summary>
public sealed record CompatCompile(CompatStatus Status, int? CoveragePct, bool Recorded, bool Maximum);

/// <summary>A report sent from this PC: the choice and when. Kept per store build in games\&lt;id&gt;\compat-sent.json.</summary>
public sealed record CompatSent(CompatChoice Choice, DateTimeOffset At)
{
    public const string FileName = "compat-sent.json";

    public static CompatSent? For(string gameDir, string buildKey) =>
        Community.Read<Dictionary<string, CompatSent>>(Path.Combine(gameDir, FileName))?.GetValueOrDefault(buildKey);

    public static void Save(string gameDir, string buildKey, CompatSent sent)
    {
        var path = Path.Combine(gameDir, FileName);
        var all = Community.Read<Dictionary<string, CompatSent>>(path) ?? [];
        all[buildKey] = sent;
        Directory.CreateDirectory(gameDir);
        Community.Write(path, all);
    }
}

/// <summary>The public compatibility aggregate, compat.json at /v1/content/: per game and system class (GPU vendor, and
/// family or null for the whole vendor) the device count and the share of each choice, plus the table that names a GPU's
/// family. Signed with <see cref="ContentTrust.CompatKeys"/> only, one copy, no channels; strict: an unknown member or a
/// value out of range refuses the whole file. Advisory: it only ever sets the similar-systems line.</summary>
public sealed class CompatFeed
{
    public const string Name = "compat", FileName = Name + ".json", ContentPath = "v1/content/" + FileName;
    public const int MaxBytes = 1 << 20;

    /// <summary>The similar-systems thresholds, checked in this order: a warning at this share of "worse", not "does not
    /// start" (only the recorder stops a game starting, never the compile); else "helped" or "did not help" at <see cref="Mostly"/>.</summary>
    public const double Warn = 0.20, Mostly = 0.60;
    const double Rounding = 1e-9;   // the shares come rounded to 0.05: 0.15 + 0.05 must reach 0.20

    static readonly string[] Vendors = ["nvidia", "amd"];

    public sealed record Shares(double Gone, double Better, [property: JsonPropertyName("no_help")] double NoHelp, double Worse,
        [property: JsonPropertyName("no_start")] double NoStart, [property: JsonPropertyName("not_compiled")] double NotCompiled,
        [property: JsonPropertyName("no_stutter_before")] double NoStutterBefore)
    {
        internal double[] All => [Gone, Better, NoHelp, Worse, NoStart, NotCompiled, NoStutterBefore];
    }

    sealed record Class(string Vendor, string? Family, int Devices, Shares Shares);
    sealed record Entry(string[] Ids, Class[] Classes);
    sealed record Family(string Vendor, [property: JsonPropertyName("family")] string Name, string[] Match);
    sealed record Doc(string Generated, [property: JsonPropertyName("min_devices")] int MinDevices, Family[] Families, Entry[] Games);

    readonly Doc doc;
    readonly Dictionary<string, Entry> byId = new(StringComparer.OrdinalIgnoreCase);

    CompatFeed(Doc d)
    {
        doc = d;
        foreach (var e in d.Games)
            foreach (var id in e.Ids) byId.TryAdd(id, e);
    }

    /// <summary>The aggregate, or null when the body isn't exactly its shape within the caps.</summary>
    public static CompatFeed? TryParse(string json) => ContentFile.Read<Doc>(json, MaxBytes) is { } d && Valid(d) ? new(d) : null;

    static bool Valid(Doc d) =>
        DateOnly.TryParseExact(d.Generated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        && d.MinDevices is >= 1 and <= 1_000_000
        && d.Families.Length <= 500 && d.Families.All(f => Vendors.Contains(f.Vendor) && IsFamily(f.Name)
            && f.Match is { Length: >= 1 and <= 64 } && f.Match.All(m => ContentFile.Text(m, 64)))
        && d.Games.Length <= 20_000 && d.Games.All(g => g.Ids is { Length: >= 1 and <= 8 } && g.Ids.All(Games.GameVerdicts.IsId)
            && g.Classes is { Length: >= 1 and <= 64 } && g.Classes.All(c => Vendors.Contains(c.Vendor) && (c.Family == null || IsFamily(c.Family))
                && c.Devices >= 0 && c.Shares.All.All(v => v is >= 0 and <= 1)));

    static bool IsFamily(string s) => s.Length is >= 1 and <= 40 && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>The family of <paramref name="gpuModel"/> on <paramref name="vendor"/>: the first entry of that vendor, in file
    /// order, one of whose match strings the model contains, ignoring case; null when none does.</summary>
    public string? FamilyOf(string vendor, string? gpuModel) => gpuModel == null ? null
        : doc.Families.FirstOrDefault(f => f.Vendor == vendor && f.Match.Any(m => gpuModel.Contains(m, StringComparison.OrdinalIgnoreCase)))?.Name;

    /// <summary>The line for the game on this GPU: its family's class, else the vendor's (family null), each only with at least
    /// min_devices devices; null when there is none or no threshold is reached.</summary>
    public SimilarSystems? For(Game game, string? vendor, string? gpuModel)
    {
        if (vendor == null || !byId.TryGetValue(game.Id, out var e)) return null;
        var family = FamilyOf(vendor, gpuModel);
        var row = (family == null ? null : e.Classes.FirstOrDefault(c => c.Vendor == vendor && c.Family == family && c.Devices >= doc.MinDevices))
            ?? e.Classes.FirstOrDefault(c => c.Vendor == vendor && c.Family == null && c.Devices >= doc.MinDevices);
        return row == null ? null : Verdict(row.Shares);
    }

    public static SimilarSystems? Verdict(Shares s) =>
        s.Worse >= Warn - Rounding ? SimilarSystems.Warning
        : s.Gone + s.Better >= Mostly - Rounding ? SimilarSystems.Helped
        : s.NoHelp + s.NotCompiled + s.NoStutterBefore >= Mostly - Rounding ? SimilarSystems.NoHelp
        : null;

    /// <summary>The kept copy at <paramref name="cacheFile"/> when it still verifies; null without one.</summary>
    public static CompatFeed? Cached(ContentTrust trust, string cacheFile)
    {
        try { return trust.Accept(Name, File.ReadAllText(cacheFile), TryParse); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The server's copy when it verifies, kept at <paramref name="cacheFile"/>; else the kept one. A 404 (the feed is
    /// off) deletes the kept copy and gives null. Never throws.</summary>
    public static async Task<CompatFeed?> FetchAsync(ContentTrust trust, string cacheFile, RouteFailover routes, TimeSpan? budget = null)
    {
        var (json, status) = await routes.GetContentStatusAsync(ContentPath, budget ?? TimeSpan.FromSeconds(3), MaxBytes);
        if (status == HttpStatusCode.NotFound)
        {
            try { File.Delete(cacheFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the next check deletes it
            return null;
        }
        if (json != null && trust.Accept(Name, json, TryParse) is { } fresh)
        {
            ContentFile.Keep(cacheFile, json);
            return fresh;
        }
        return Cached(trust, cacheFile);
    }
}
