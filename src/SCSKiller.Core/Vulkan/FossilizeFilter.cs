using System.Text.Json;

namespace SCSKiller.Core.Vulkan;

/// <summary>An application info entry (tag 0): the application and engine the game named at vkCreateInstance. The entry's
/// hash names the application and the device features it enabled, as an unbucketed archive's blob links name it.</summary>
public sealed record FozApp(ulong Hash, string? Name, string? Engine, uint Version, uint EngineVersion)
{
    /// <summary>The entry's payload read; null when it isn't an application info.</summary>
    public static FozApp? Parse(ulong hash, byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload.AsMemory(0, Array.IndexOf(payload, (byte)0) is var nul and >= 0 ? nul : payload.Length));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("applicationInfo", out var a) || a.ValueKind != JsonValueKind.Object) return null;
            return new(hash, Text(a, "applicationName"), Text(a, "engineName"), Number(a, "applicationVersion"), Number(a, "engineVersion"));
        }
        catch (JsonException) { return null; }

        static string? Text(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static uint Number(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var u) ? u : 0;
    }
}

/// <summary>Fossilize's application info filter (fossilize_application_filter.cpp), which Steam hands its layer as
/// fossilize_engine_filters.json, as far as it names a bucket: the rule of the application's name or its engine's says
/// which parts of the application info, the GPU's vendor id and the device features the game enabled make the bucket's
/// hash (get_bucket_hash). The features aren't in the archive whole, so <see cref="Matches"/> tries every combination.</summary>
public sealed class FossilizeFilter
{
    public const string FileName = "fossilize_engine_filters.json";
    const int MaxFile = 1 << 20;

    sealed record Rule(string[] Variants, string[] FeatureVariants, uint[] AppDeltas, uint[] EngineDeltas)
    {
        public bool Buckets => Variants.Length + FeatureVariants.Length > 0;
        public bool Known => Variants.Concat(FeatureVariants).All(KnownVariants.Contains);
        public bool NamesVendor => Variants.Concat(FeatureVariants).Contains("VendorID");
    }

    static readonly HashSet<string> KnownVariants = new(StringComparer.Ordinal)
    {
        "VendorID", "MutableDescriptorType", "BindlessUBO", "BufferDeviceAddress", "ApplicationVersion", "EngineVersion",
        "ApplicationVersionMajor", "ApplicationVersionMinor", "ApplicationVersionPatch", "EngineVersionMajor", "EngineVersionMinor",
        "EngineVersionPatch", "ApplicationName", "EngineName", "FragmentShadingRate", "DynamicRendering", "DescriptorBuffer", "DescriptorHeap",
    };

    /// <summary>PCI vendor ids a bucket of another GPU is told by: AMD, NVIDIA, Intel, ARM, Qualcomm, Imagination, Apple,
    /// Broadcom, Microsoft, Mesa's software devices.</summary>
    static readonly uint[] Vendors = [0x1002, 0x10DE, 0x8086, 0x13B5, 0x5143, 0x1010, 0x106B, 0x14E4, 0x1414, 0x10005];

    readonly Dictionary<string, Rule> _apps = new(StringComparer.Ordinal), _engines = new(StringComparer.Ordinal);
    Rule _default = new([], [], [], []);

    /// <summary>The device features a bucket hash may name, each enabled or not.</summary>
    public readonly record struct Features(bool BindlessUbo, bool Mutable, bool BufferDeviceAddress, bool FsrAttachment, bool FsrPipeline,
        bool FsrPrimitive, bool DynamicRendering, bool DescriptorBuffer, bool DescriptorBufferPush, bool DescriptorHeap)
    {
        public static IEnumerable<Features> All() => Enumerable.Range(0, 1 << 10).Select(b => new Features((b & 1) != 0, (b & 2) != 0, (b & 4) != 0,
            (b & 8) != 0, (b & 16) != 0, (b & 32) != 0, (b & 64) != 0, (b & 128) != 0, (b & 256) != 0, (b & 512) != 0));
    }

    /// <summary>Steam's filter beside its client; null when it's missing or isn't one this reads.</summary>
    public static FossilizeFilter? Read(string steamRoot)
    {
        try
        {
            using var f = Games.GameFiles.OpenRegular(Path.Combine(steamRoot, FileName));
            if (f == null || f.Length > MaxFile) return null;
            using var text = new StreamReader(f);
            return Parse(text.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The filter's rules; null for text that isn't a filter of a version this reads (Fossilize then buckets nothing).</summary>
    public static FossilizeFilter? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.String
                || asset.GetString() != "FossilizeApplicationInfoFilter" || !root.TryGetProperty("version", out var v) || !v.TryGetInt32(out var version) || version > 2)
                return null;
            var filter = new FossilizeFilter();
            if (!Rules(root, "applicationFilters", filter._apps) || !Rules(root, "engineFilters", filter._engines)) return null;
            var defaults = Names(root, "defaultBucketVariantDependencies");
            var defaultFeatures = Names(root, "defaultBucketVariantFeatureDependencies");
            if (defaults == null || defaultFeatures == null) return null;
            filter._default = new(defaults, defaultFeatures, [], []);
            return filter;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }

        static bool Rules(JsonElement root, string name, Dictionary<string, Rule> into)
        {
            if (!root.TryGetProperty(name, out var rules)) return true;
            if (rules.ValueKind != JsonValueKind.Object) return false;
            foreach (var r in rules.EnumerateObject())
            {
                if (r.Value.ValueKind != JsonValueKind.Object) return false;
                var (variants, features) = (Names(r.Value, "bucketVariantDependencies"), Names(r.Value, "bucketVariantFeatureDependencies"));
                var (app, engine) = (Versions(r.Value, "applicationVersionBucketDeltas"), Versions(r.Value, "engineVersionBucketDeltas"));
                if (variants == null || features == null || app == null || engine == null) return false;
                into[r.Name] = new(variants, features, app, engine);
            }
            return true;
        }

        static string[]? Names(JsonElement o, string name) =>
            !o.TryGetProperty(name, out var a) ? [] : a.ValueKind != JsonValueKind.Array || a.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String) ? null
                : [.. a.EnumerateArray().Select(x => x.GetString()!)];

        static uint[]? Versions(JsonElement o, string name) =>
            !o.TryGetProperty(name, out var a) ? [] : a.ValueKind != JsonValueKind.Array || a.EnumerateArray().Any(x => !x.TryGetUInt32(out var u) || u == 0) ? null
                : [.. a.EnumerateArray().Select(x => x.GetUInt32())];
    }

    Rule? AppRule(FozApp app) => app.Name != null && _apps.TryGetValue(app.Name, out var r) ? r : null;
    Rule? EngineRule(FozApp app) => app.Engine != null && _engines.TryGetValue(app.Engine, out var r) ? r : null;

    /// <summary>The rules that make the application's hash: its name's and its engine's, else the default one.</summary>
    IEnumerable<Rule> Applying(FozApp app) =>
        new[] { AppRule(app), EngineRule(app) }.OfType<Rule>().DefaultIfEmpty(_default);

    /// <summary>needs_buckets: the application records into a bucket folder, not the top level.</summary>
    public bool Buckets(FozApp app) => Applying(app).Any(r => r.Buckets);

    /// <summary>get_bucket_hash for a GPU of <paramref name="vendorId"/> with these features enabled; null when a rule names a
    /// dependency this doesn't know (another Fossilize hashes it, this can't).</summary>
    public ulong? Hash(FozApp app, uint vendorId, Features features)
    {
        if (!Applying(app).All(r => r.Known)) return null;
        var (byApp, byEngine) = (AppRule(app), EngineRule(app));
        var h = new Hasher();
        h.U32(0);
        if (byApp != null) Variants(h, byApp, app, vendorId, features);
        h.U32(0);
        if (byEngine != null)
        {
            Variants(h, byEngine, app, vendorId, features);
            foreach (var d in byEngine.AppDeltas.Where(d => app.Version >= d)) h.U32(d ^ 0xabba);
            foreach (var d in byEngine.EngineDeltas.Where(d => app.EngineVersion >= d)) h.U32(d ^ 0xcafe);
        }
        if (byApp == null && byEngine == null) Variants(h, _default, app, vendorId, features);
        return h.Value;
    }

    /// <summary>Whether bucket <paramref name="bucket"/> of <paramref name="app"/> is a GPU's of <paramref name="vendorId"/>:
    /// true when some features give its hash (also when no rule names the vendor), false when only another known vendor's
    /// do, null when nothing does (a rule or a dependency this can't reproduce).</summary>
    public bool? Matches(ulong bucket, FozApp app, uint vendorId)
    {
        if (Gives(vendorId)) return true;
        return Applying(app).Any(r => r.NamesVendor) && Vendors.Where(v => v != vendorId).Any(Gives) ? false : null;

        bool Gives(uint vendor) => Features.All().Any(f => Hash(app, vendor, f) == bucket);
    }

    static void Variants(Hasher h, Rule rule, FozApp app, uint vendorId, Features f)
    {
        foreach (var v in rule.Variants) Variant(h, v, app, vendorId, f, feature: false);
        foreach (var v in rule.FeatureVariants) Variant(h, v, app, vendorId, f, feature: true);
    }

    /// <summary>hash_variant: a feature dependency adds nothing while the feature is off, so a new one keeps old hashes.</summary>
    static void Variant(Hasher h, string name, FozApp app, uint vendorId, Features f, bool feature)
    {
        switch (name)
        {
            case "VendorID": h.U32(vendorId); break;
            case "MutableDescriptorType": Flag(1000351000, f.Mutable); break;
            case "BufferDeviceAddress": Flag(1000257000, f.BufferDeviceAddress); break;
            case "DynamicRendering": Flag(1000044003, f.DynamicRendering); break;
            case "DescriptorHeap": Flag(1000135009, f.DescriptorHeap); break;
            case "BindlessUBO":
                var bindless = f.BindlessUbo;
                if (feature && bindless)
                {
                    h.U32(1000161001);
                    h.U32(10);
                }
                if (!feature || bindless) h.U32(bindless ? 1u : 0);
                break;
            case "FragmentShadingRate":
                var fsr = f.FsrAttachment || f.FsrPipeline || f.FsrPrimitive;
                if (feature && fsr) h.U32(1000226003);
                if (!feature || fsr)
                {
                    h.U32(f.FsrAttachment ? 1u : 0);
                    h.U32(f.FsrPipeline ? 1u : 0);
                    h.U32(f.FsrPrimitive ? 1u : 0);
                }
                break;
            case "DescriptorBuffer":
                var db = f.DescriptorBuffer || f.DescriptorBufferPush;
                if (feature && db) h.U32(1000316002);
                if (!feature || db)
                {
                    h.U32(f.DescriptorBuffer ? 1u : 0);
                    h.U32(f.DescriptorBufferPush ? 1u : 0);
                }
                break;
            case "ApplicationVersion": h.U32(app.Version); break;
            case "ApplicationVersionMajor": h.U32(app.Version >> 22); break;
            case "ApplicationVersionMinor": h.U32((app.Version >> 12) & 0x3FF); break;
            case "ApplicationVersionPatch": h.U32(app.Version & 0xFFF); break;
            case "EngineVersion": h.U32(app.EngineVersion); break;
            case "EngineVersionMajor": h.U32(app.EngineVersion >> 22); break;
            case "EngineVersionMinor": h.U32((app.EngineVersion >> 12) & 0x3FF); break;
            case "EngineVersionPatch": h.U32(app.EngineVersion & 0xFFF); break;
            case "ApplicationName": h.String(app.Name ?? ""); break;
            case "EngineName": h.String(app.Engine ?? ""); break;
        }

        void Flag(uint sType, bool on)
        {
            if (feature && on) h.U32(sType);
            if (!feature || on) h.U32(on ? 1u : 0);
        }
    }

    /// <summary>fossilize_hasher.hpp: FNV-1 over 32-bit words.</summary>
    sealed class Hasher
    {
        public ulong Value { get; private set; } = 0xcbf29ce484222325;

        public void U32(uint v) => Value = unchecked(Value * 0x100000001b3) ^ v;

        public void String(string s)
        {
            U32(0xff);
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) U32(b);
        }
    }
}
