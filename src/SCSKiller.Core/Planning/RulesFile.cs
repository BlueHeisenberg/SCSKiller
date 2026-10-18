using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Planning;

/// <summary>The planning rules: which root-signature rule and ray tracing collection rule an engine gets, and their data
/// (plan-rules.json). Embedded in Core; a signed copy served at /v1/content/plan-rules.json, or a newer one on a channel the
/// account reads (<see cref="FetchAsync"/>, <see cref="ContentTrust"/>), replaces embedded rules by id and adds others. A rule selects construction shapes the app implements, never describes a
/// new one: each lists the capabilities it uses (<see cref="Capabilities"/>), and one needing another, or failing
/// validation, is dropped alone, never the file.</summary>
public sealed class RulesFile
{
    public const string Name = "plan-rules";
    public const string ContentPath = "v1/content/plan-rules.json";

    /// <summary>rs.desc.v1: fixed descriptions chosen by stage set; rs.desc.v2: their <see cref="StagesDesc.BindsNothing"/> and
    /// <see cref="StagesDesc.Dxbc"/>, and <see cref="RuleMatch.Games"/>; rs.stock.v1: stock Unreal's construction with its
    /// parameters; rs.select.v1: a construction shape in code (<see cref="RsShapes"/>); stagesets.v1: an allow-list of stage
    /// sets; rt.collection.v1: a ray tracing collection rule (<see cref="RtShapes"/>); rt.noconfig.v1: collections without a state
    /// object config (<see cref="RtRule.Config"/>); rs.re.v1: RE Engine's construction with its parameters (<see cref="ReRule"/>);
    /// rs.re.v2: its <see cref="ReRule.DenyMesh"/>, <see cref="ReRule.CbvUavAbove"/> and <see cref="ReRule.BindlessSpaces"/>;
    /// rs.samplers.v1: static samplers as raw descriptions; rs.stock.v2: <see cref="StockRule.DenyMesh"/>; rt.nv.v1: <see cref="RtRule.Nv"/>;
    /// amd.ags.v1: <see cref="PlanRule.Ags"/>; nv.aftermath.v1: <see cref="PlanRule.Aftermath"/>.</summary>
    public static readonly IReadOnlySet<string> Capabilities = new HashSet<string> { "rs.desc.v1", "rs.desc.v2", "rs.stock.v1", "rs.select.v1", "stagesets.v1", "rt.collection.v1", "rt.noconfig.v1", "rs.re.v1", "rs.re.v2", "rs.samplers.v1", "rs.stock.v2", "rt.nv.v1", "amd.ags.v1", "nv.aftermath.v1" };

    /// <summary>ff7: <see cref="RootSig.BuildUe"/>; bindless / spaceBindless: the UE 4 forks' bindless tables; avalancheSrvs:
    /// SRV tables of 128 when a shader carries Avalanche's bindless marker (<see cref="RootSig.MaxSrvsFor"/>); dagor /
    /// dagorCbvRanges: built from each shader's header (<see cref="Dagor.DagorRootSig"/>), constant buffers as root CBVs or in
    /// tables.</summary>
    public static readonly IReadOnlySet<string> RsShapes = new HashSet<string> { "ff7", "bindless", "spaceBindless", "avalancheSrvs", "dagor", "dagorCbvRanges" };

    /// <summary>avalancheGlobal: Avalanche's fork's global root signature when the libraries carry its marker
    /// (<see cref="RtCollections.GlobalFor"/>); ue5Shape: only for libraries of 5.1's binding shape
    /// (<see cref="RtCollections.Ue5ShapeMismatch"/>); red3HitGroups: REDengine 3's material hit groups
    /// (<see cref="RedEngine.RedRayTracing"/>); soulsHitGroups: Elden Ring's material pairs (<see cref="FromSoft.SoulsRayTracing"/>).</summary>
    public static readonly IReadOnlySet<string> RtShapes = new HashSet<string> { "avalancheGlobal", "ue5Shape", "red3HitGroups", "soulsHitGroups" };

    static readonly Dictionary<string, Stage> StageNames = new()
    {
        ["VS"] = Stage.Vertex, ["PS"] = Stage.Pixel, ["DS"] = Stage.Domain, ["HS"] = Stage.Hull, ["GS"] = Stage.Geometry,
        ["CS"] = Stage.Compute, ["AS"] = Stage.Amplification, ["MS"] = Stage.Mesh,
    };

    internal static Stage StageOf(string name) => StageNames[name];

    sealed record Doc(JsonElement[] Rules, string? About = null);

    public IReadOnlyList<PlanRule> Rules { get; }
    /// <summary>The rules left out, as "id: why".</summary>
    public IReadOnlyList<string> Dropped { get; }

    RulesFile(IReadOnlyList<PlanRule> rules, IReadOnlyList<string> dropped) => (Rules, Dropped) = (rules, dropped);

    /// <summary>The rules of a document within <see cref="ContentFile.MaxBytes"/> of the right shape; null when it isn't one.</summary>
    public static RulesFile? TryParse(string json)
    {
        if (ContentFile.Read<Doc>(json) is not { } doc || doc.About != null && !ContentFile.Text(doc.About, 2000)) return null;
        var (rules, dropped) = (new List<PlanRule>(), new List<string>());
        foreach (var e in doc.Rules)
        {
            var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : "?";
            var why = Unusable(e);
            var r = why == null ? ContentFile.Read<PlanRule>(e) : null;
            why ??= r == null ? "malformed" : Invalid(r) ?? (rules.Any(x => x.Id == r.Id) ? "a second rule with this id" : null);
            if (why == null) rules.Add(r!);
            else dropped.Add($"{id}: {why}");
        }
        return new(rules, dropped);
    }

    static string? Unusable(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("requires", out var req) || req.ValueKind != JsonValueKind.Array || req.GetArrayLength() == 0) return "no requires";
        foreach (var c in req.EnumerateArray())
            if (c.ValueKind != JsonValueKind.String || !Capabilities.Contains(c.GetString()!)) return $"needs {c}";
        return null;
    }

    /// <summary>Why a rule can't be used; null when it can. Every description is serialized by the runtime here.</summary>
    static string? Invalid(PlanRule r)
    {
        if (r.Id is not { Length: > 0 and <= 64 } || !r.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')) return "bad id";
        bool Needs(string cap) => r.Requires.Contains(cap);
        var m = r.Match;
        if (!ContentFile.Text(m.Family, 64) || (m.From == null) != (m.To == null)
            || m.From != null && (Ver(m.From) is not { } from || Ver(m.To!) is not { } to || from > to)
            || m.Fork != null && !(m.Fork is { Length: > 0 and <= 64 } && m.Fork.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' '))
            || m.Games != null && (!Needs("rs.desc.v2") || m.Games.Length is 0 or > 16 || !m.Games.All(g => ContentFile.Text(g, 64) && g.Contains(':'))))
            return "bad match";
        if (r.RootSignature == null && r.Rt == null && r.Ags == null && r.Aftermath == null) return "no rule";
        if (r.Ags is { } ags && (!Needs("amd.ags.v1") || r.RootSignature != null || r.Rt != null
                || ags.Profiles?.All(p => ContentFile.Text(p.Key, 64) && p.Value is { Length: 8 } && p.Value.All(char.IsAsciiHexDigit)) == false
                || ags.Proven?.All(a => ContentFile.Text(a, 64)) == false))
            return "bad ags";
        if (r.Aftermath is { } am && (!Needs("nv.aftermath.v1") || r.RootSignature != null || r.Rt != null || r.Ags != null || am.Count == 0
                || !am.All(p => ContentFile.Text(p.Key, 64) && p.Key.Contains(':') && p.Value is { Version: > 0, Features: > 0 })))
            return "bad aftermath";
        if (r.StageSets != null && (!Needs("stagesets.v1") || r.StageSets.Length == 0 || !r.StageSets.All(s => Stages(s)))) return "bad stageSets";
        if (r.RootSignature is { } rs)
        {
            if (rs.Samplers != null && (!Needs("rs.samplers.v1") || rs.StaticSamplers != null || rs.Samplers.Length is 0 or > 2032 || rs.Samplers.Any(x => x is not { Length: 13 })))
                return "bad samplers";
            var samplers = RootSig.StaticSamplers(r);
            if (rs.Select != null && (!Needs("rs.select.v1") || !rs.Select.All(s => s != null && RsShapes.Contains(s)))) return "bad select";
            var shapes = rs.Select?.Count(s => s is "ff7" or "dagor" or "dagorCbvRanges") ?? 0;
            if (rs.ByStages != null)
            {
                if (!Needs("rs.desc.v1") || rs.Stock != null || rs.Select != null || rs.Re != null || rs.ByStages.Length == 0) return "bad byStages";
                if (rs.ByStages.All(b => b?.Desc == null)) return "bad byStages";
                foreach (var b in rs.ByStages)
                {
                    if (b == null || b.Any != null && !Stages(b.Any)) return "bad byStages";
                    if (b.Predicated && (!Needs("rs.desc.v2") || b.Desc == null)) return "bad byStages";
                    if (b.Desc != null && Unserializable(b.Desc, samplers) is { } why) return $"a description doesn't serialize ({why})";
                }
                bool Described(IEnumerable<string> set) => rs.ByStages.Any(b => !b.Predicated && (b.Any == null || b.Any.Intersect(set).Any()));
                if (r.StageSets is { } sets ? !sets.All(Described) : !rs.ByStages.Any(b => !b.Predicated && b.Any == null)) return "bad byStages: a stage set with no description";
            }
            else if (rs.Re is { } re)
            {
                if (!Needs("rs.re.v1") || rs.Stock != null || rs.Select != null || re.CbvBase > 14 || re.ComputeUavs is 0 or > 1_000_000
                        || (!re.DenyMesh || re.CbvUavAbove || re.BindlessSpaces > 0) && !Needs("rs.re.v2") || re.BindlessSpaces > 16
                        || !Sizes(re.CbvSizes) || !Sizes(re.SrvSizes) || !Sizes(re.UavSizes))
                    return "bad re";
                foreach (var stages in new[] { new[] { Stage.Compute }, [Stage.Vertex, Stage.Pixel] })
                    try { RootSig.Serialize(RootSig.BuildRe(re, stages, re.CbvSizes[^1], re.SrvSizes[^1], re.UavSizes[^1]), samplers); }
                    catch (RootSig.SerializeException e) { return $"a description doesn't serialize ({e.Message})"; }
            }
            else if (shapes > 1 || (shapes == 1) == (rs.Stock != null)) return "no single construction";
            if (rs.Stock is { } p && (!Needs("rs.stock.v1") || !Stages(p.Stages) || p.Stages.Contains("CS") || !Stages(p.UavStages, empty: true)
                    || p.MaxSrvs is 0 or > 1_000_000 || p.MaxSamplers is 0 or > 2048 || p.DenyMesh && !Needs("rs.stock.v2")))
                return "bad stock";
        }
        if (r.Rt is { } rt)
        {
            if (!Needs("rt.collection.v1") || !ContentFile.Text(rt.Note, 300) || rt.Select != null && !rt.Select.All(s => s != null && RtShapes.Contains(s))) return "bad rt";
            if (!rt.Config && (!Needs("rt.noconfig.v1") || rt.Flags != 0)) return "bad rt: config";
            if (rt.Nv != null && !Needs("rt.nv.v1")) return "bad rt: nv";
            var shaped = rt.Select?.Any(s => s is "red3HitGroups" or "soulsHitGroups") == true;
            if (rt.Local != null && Unserializable(rt.Local, []) is { } l) return $"a description doesn't serialize ({l})";
            if (rt.Global == null && !shaped) return "bad rt: no global root signature";
            if (rt.Global != null && Unserializable(rt.Global, RtSamplers(rt)) is { } g) return $"a description doesn't serialize ({g})";
        }
        return null;
    }

    static bool Sizes(uint[]? s) => s is { Length: > 0 } && s[0] > 0 && s[^1] <= 1_000_000 && s.Zip(s.Skip(1)).All(p => p.First < p.Second);

    static bool Stages(string[]? names, bool empty = false) => names != null && (empty || names.Length > 0) && names.All(n => n != null && StageNames.ContainsKey(n)) && names.Distinct().Count() == names.Length;

    static Version? Ver(string s) => s.Count(c => c == '.') == 1 && Version.TryParse(s, out var v) ? v : null;

    /// <summary>A row the serializer reads within its bounds: a table (0, vis, then 5 values per range, or one range and an
    /// explicit offset), root constants (1) or a root CBV/SRV/UAV (2-4), each with 5 values.</summary>
    static bool Row(uint[]? r) => r is { Length: >= 2 } && r[1] <= 7 && r[0] switch
    {
        0 => r.Length >= 7 && ((r.Length - 2) % 5 == 0 || r.Length == 8),
        1 or 2 or 3 or 4 => r.Length == 5,
        _ => false,
    };

    static string? Unserializable(DescData d, byte[] samplers)
    {
        if (d.Rows == null || !d.Rows.All(Row)) return "a malformed row";
        try { RootSig.Serialize(d.ToDesc(), samplers); return null; }
        catch (RootSig.SerializeException e) { return e.Message; }
    }

    internal static byte[] RtSamplers(RtRule rt) => rt.GlobalSamplers is { } s ? RootSig.UeSamplers(s.Register, s.Space) : [];

    // set once by the type initializer: Current starts as this instance, and a lazy ??= can parse twice under threads
    public static RulesFile Embedded { get; } = TryParse(ContentFile.Embedded("SCSKiller.Core.Planning.plan-rules.json"))!;

    /// <summary>The rules in use: the embedded ones, or <see cref="WithServer"/>'s.</summary>
    public static RulesFile Current { get; set; } = Embedded;

    /// <summary>The server's rules, then each embedded rule whose id none of them has: where ranges overlap, the server's match first.</summary>
    public static RulesFile WithServer(RulesFile server) =>
        new([.. server.Rules, .. Embedded.Rules.Where(e => server.Rules.All(s => s.Id != e.Id))], server.Dropped);

    /// <summary>The root-signature rule matching the engine: one naming its fork first, then file order.</summary>
    public PlanRule? RuleFor(EngineInfo e) => First(e, r => r.RootSignature != null);

    /// <summary>The ray tracing collection rule matching the engine (a plan without recorded collections uses it).</summary>
    public PlanRule? RtFor(EngineInfo e) => First(e, r => r.Rt != null);

    PlanRule? First(EngineInfo e, Func<PlanRule, bool> kind) =>
        Rules.Where(r => kind(r) && Matches(r.Match, e)).OrderBy(r => r.Match.Fork == null).FirstOrDefault();

    static bool Matches(RuleMatch m, EngineInfo e)
    {
        if (m.Family != e.Family || m.Fork != null && m.Fork != e.Fork || m.Games != null && !m.Games.Any(g => g == (g.Contains('@') ? e.Game : e.Game?.Split('@')[0]))) return false;
        if (m.From == null) return true;
        if (!Version.TryParse(e.Version, out var v)) return false;
        var mm = new Version(v.Major, v.Minor);
        return Ver(m.From)! <= mm && mm <= Ver(m.To!)!;
    }

    static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    /// <summary>The effective rules of a game: a hash of the rules matching its engine, re-serialized (a copy that differs
    /// only in layout hashes the same). A plan records it, and a change re-plans the game like a newer planner.</summary>
    public string Hash(EngineInfo e) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { RuleFor(e), RtFor(e) }.OfType<PlanRule>().DistinctBy(r => r.Id).OrderBy(r => r.Id, StringComparer.Ordinal), Canonical))))[..32];

    /// <summary>The newest kept rules among stable's and those of <paramref name="channels"/> (<see cref="Channels"/>; null: not
    /// known yet, every copy kept), <see cref="ContentTrust.Cached"/>.</summary>
    public static RulesFile? Cached(string file, ContentTrust trust, IReadOnlyCollection<string>? channels) => trust.Cached(Name, TryParse, file, channels);

    /// <summary>The channels besides stable whose rules an entitlement reads: alpha (or internal) reads alpha and beta, beta
    /// reads beta. Stable, <see cref="ContentPath"/>, is read by everyone.</summary>
    public static string[] Channels(IReadOnlyCollection<string>? ent) =>
        ent == null ? []
        : ent.Contains(UpdateChannels.Alpha) || ent.Contains(UpdateChannels.Internal) ? [UpdateChannels.Alpha, UpdateChannels.Beta]
        : ent.Contains(UpdateChannels.Beta) ? [UpdateChannels.Beta] : [];

    /// <summary>The newest rules that verify among stable's copy and, with an access <paramref name="token"/>, those of the
    /// <paramref name="channels"/> the account reads (<see cref="Channels"/>; null: not known yet), each channel's kept beside
    /// <paramref name="cacheFile"/> (<see cref="ContentTrust.FetchAsync"/>). Null when no channel read has any.</summary>
    public static Task<RulesFile?> FetchAsync(string cacheFile, ContentTrust trust, RouteFailover? routes = null, string? token = null,
        IReadOnlyCollection<string>? channels = null, TimeSpan? budget = null) =>
        trust.FetchAsync(Name, TryParse, cacheFile, channels, token, routes, budget);
}

/// <summary>A rule of plan-rules.json: what it matches, its root-signature rule and stage-set allow-list, its ray tracing
/// collection rule. <paramref name="Verified"/>: confirmed by a real game whatever confirmed-engines.json says.
/// <paramref name="Ags"/>: AMD AGS app names for every game, whatever the match (<see cref="Vendors.AmdAppCache.AgsKey"/>); unset, it is
/// left out of <see cref="RulesFile.Hash"/>'s serialization. <paramref name="Aftermath"/>: how each game (by id) starts NVIDIA
/// Aftermath, whatever the match (<see cref="Vendors.NvAftermath.Of"/>), left out the same way.</summary>
public sealed record PlanRule(string Id, string[] Requires, RuleMatch Match, RootSigRule? RootSignature = null, string[][]? StageSets = null,
    RtRule? Rt = null, bool Verified = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] AgsRule? Ags = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Dictionary<string, Vendors.AftermathInit>? Aftermath = null);

/// <summary>Driver app profiles matched on the AGS app name, exact case: app name -> DxcCache key, 8 hex digits. Proven: app
/// names whose game's own process was seen holding the key they give, registered before the game has been seen.</summary>
public sealed record AgsRule(Dictionary<string, string>? Profiles = null, string[]? Proven = null);

/// <summary>An engine family; an inclusive "major.minor" range of its versions (none: any version); a fork (none: any); the
/// games (<see cref="EngineInfo.Game"/>, none: any) whose own roots the rule describes, each a store id ("steam:1895880": any
/// build) or a store id and build ("steam:1895880@12345": a patch leaves the rule until its build is added). Unset, it is left out of
/// <see cref="RulesFile.Hash"/>'s serialization, so the rules without it hash as before it existed.</summary>
public sealed record RuleMatch(string Family, string? From = null, string? To = null, string? Fork = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string[]? Games = null);

/// <summary>One construction: <paramref name="Stock"/> (with <paramref name="Select"/>ed fork shapes), the "ff7" shape, fixed
/// descriptions by stage set (<paramref name="ByStages"/>: the first whose <see cref="StagesDesc.Any"/> stage the pipeline
/// has, or that names none), or RE Engine's (<paramref name="Re"/>). Static samplers: UE's six (<paramref name="StaticSamplers"/>),
/// or <paramref name="Samplers"/>, each a D3D12_STATIC_SAMPLER_DESC as 13 values.</summary>
public sealed record RootSigRule(StockRule? Stock = null, string[]? Select = null, StagesDesc[]? ByStages = null, SamplerSet? StaticSamplers = null,
    ReRule? Re = null, uint[][]? Samplers = null);

/// <summary>RE Engine's parameters (<see cref="RootSig.BuildRe"/>): the first CBV register of its tables (those below are root
/// CBVs), the sizes its CBV, SRV and pixel shader UAV tables come in, its compute UAV table, the directly-indexed heap flag,
/// denying the mesh and amplification stages (RE Village's flags don't), CBV and UAV tables sized above what the pipeline
/// binds (as SRV tables always are), and the bindless tables after the rest: one per space 1 up to
/// <paramref name="BindlessSpaces"/>, an unbounded SRV range t0 visible to every stage (RE Village: 8).</summary>
public sealed record ReRule(uint CbvBase, uint[] CbvSizes, uint[] SrvSizes, uint[] UavSizes, uint ComputeUavs, bool HeapIndexed = false,
    bool DenyMesh = true, bool CbvUavAbove = false, uint BindlessSpaces = 0);

/// <summary>Stock Unreal's parameters (<see cref="RootSig"/>'s BuildStock): its stage list in priority order, MAX_SRVS and
/// MAX_SAMPLERS, the stages with a UAV table, tables only for the resource types a stage binds, the input-layout flag only
/// for a VS reading vertex input, MAX_SRVS raised to what the shaders bind, UE 5's AGS UAV, root constants, diagnostic UAV and
/// directly-indexed heap flags, 5.8's NVIDIA extension UAV table, the mesh and amplification stages denied on every root
/// signature whatever the stages list (<paramref name="DenyMesh"/>: Kuro's 4.26 fork). Unset, it is left out of <see cref="RulesFile.Hash"/>'s serialization.</summary>
public sealed record StockRule(string[] Stages, uint MaxSrvs, uint MaxSamplers, string[] UavStages, bool TablesWhenUsed = false,
    bool LayoutNeedsVertexInput = false, bool GrowSrvs = false, bool Ags = false, bool RootConstants = false, bool Diagnostic = false, bool HeapFlags = false,
    bool NvExtension = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DenyMesh = false);

/// <summary>An entry of <see cref="RootSigRule.ByStages"/>; without a description the stages it names have none. It applies
/// only when every shader of the pipeline declares no resource (<paramref name="BindsNothing"/>) and when one is shader
/// model 5 or older, DXBC (<paramref name="Dxbc"/>). Both, unset, are left out of <see cref="RulesFile.Hash"/>'s serialization.</summary>
public sealed record StagesDesc(DescData? Desc = null, string[]? Any = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool BindsNothing = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Dxbc = false)
{
    internal bool Predicated => BindsNothing || Dxbc;
}

/// <summary>A <see cref="RootSig.Desc"/> as data.</summary>
public sealed record DescData(uint Flags, uint[][] Rows, bool AppendRanges = false, bool Version10 = false, bool RangeOffsets = false)
{
    public RootSig.Desc ToDesc() => new(Flags, [.. Rows], AppendRanges, Version10, RangeOffsets);
}

/// <summary>UE's six static samplers from this register, in this space.</summary>
public sealed record SamplerSet(uint Register, uint Space);

/// <summary>A collection rule (<see cref="RtCollections.Rule"/>'s fields) with its global root signature and samplers, and one
/// local root signature for every library (none: UE's per library, <see cref="RtCollections.LocalRs"/>); or a hit group
/// shape it selects, whose construction carries its own. <paramref name="Config"/> false: the collections have no state
/// object config subobject (its flags unused). <paramref name="Nv"/>: the NVAPI extension slot the game sets around every
/// collection it creates, used when a recording's NVAPI records don't show one; unset, it is left out of <see cref="RulesFile.Hash"/>'s serialization.</summary>
public sealed record RtRule(string Note, uint Flags = 0, uint Depth = 0, uint Payload = 0, uint Attributes = 0, DescData? Global = null,
    SamplerSet? GlobalSamplers = null, DescData? Local = null, string[]? Select = null, bool Config = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] NvSlot? Nv = null);

/// <summary>An NVAPI shader extension slot: a UAV register and space (NvAPI_D3D12_SetNvShaderExtnSlotSpace).</summary>
public sealed record NvSlot(uint Slot, uint Space);
