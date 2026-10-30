using System.Text;
using System.Text.Json;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Vulkan;

/// <summary>A recorded Vulkan pipeline: its entry, whether it is a library (GPL or ray tracing), whether a shader of it
/// traces rays inline (RayQuery), and the names of the pipelines it is created from (its libraries, its base).</summary>
public sealed record VkPipeline(Foz.Entry Entry, bool Library, bool RayQuery, IReadOnlyList<string> Needs)
{
    public string Name => Entry.Name;
    /// <summary>Created with VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT.</summary>
    public bool DescriptorHeap { get; init; }
    /// <summary>The archive it is read from, of a plan of several (<see cref="FozSource"/>).</summary>
    public FozOrigin Origin { get; init; }
}

/// <summary>Where an archive of a Vulkan plan comes from, best first.</summary>
public enum FozOrigin
{
    /// <summary>SCSKiller's own recording.</summary>
    Ours,
    /// <summary>Steam's recording on this PC (<see cref="SteamPipelineCache"/>).</summary>
    SteamLocal,
    /// <summary>Valve's download: other players' recordings, whose shader modules the replayer validates as Steam's does.</summary>
    SteamDownload,
    /// <summary>The community database's entry for the build: other players' pipelines, without shader modules.</summary>
    Community,
}

/// <summary>An archive a Vulkan plan reads, never writes. Apps: of an unbucketed download, the applications whose pipelines
/// count (their application info hashes, as its blob links name them); null: every pipeline counts.</summary>
public sealed record FozSource(string Path, FozOrigin Origin)
{
    public IReadOnlySet<ulong>? Apps { get; init; }

    /// <summary>Recorded on this PC, where the game's driver already took its shader modules.</summary>
    public bool Trusted => Origin is FozOrigin.Ours or FozOrigin.SteamLocal;
}

/// <summary>A Vulkan game's plan: no game files are read, the driver key needs state SPIR-V doesn't carry. The recording
/// (games\&lt;id&gt;\recording.foz) is the plan: every recorded pipeline, every kind, in the order play first created
/// them; what complete warms created is left out at the warm (<see cref="LeaveOut"/>). A plan item is a 'V' record whose
/// payload is the entry's 40-char name, so its key (<see cref="Key"/>) is what compiled.keys and the warm's failed keys hold.</summary>
public static partial class VulkanPlan
{
    public const string RecordingFile = "recording.foz";
    /// <summary>The warm's input in its work folder: the recording's non-pipeline entries and the pipelines to replay.</summary>
    public const string WorkFile = "scskiller.foz";
    /// <summary>Beside <see cref="WorkFile"/>: a Fossilize validation whitelist naming its shader modules recorded on this PC, so
    /// the replayer's --spirv-val checks only Valve's download's (--on-disk-validation-whitelist &lt;work&gt;/scskiller_validated).</summary>
    public const string ValidatedFile = "scskiller_validated.foz";
    /// <summary>The recorder layer's archives in its inbox (<see cref="VulkanLayer.Inbox"/>): scskiller.&lt;hash&gt;.&lt;n&gt;.foz.</summary>
    public const string InboxPattern = "scskiller.*.foz";
    public const string Platform = "vulkan";
    public const char Tag = 'V';
    /// <summary><see cref="VendorCaps.VulkanKey"/> of a vendor that keys its Vulkan cache on the exe file name (NVIDIA's GLCache).</summary>
    public const string ExeNameKey = "exe-name";
    /// <summary><see cref="VendorCaps.VulkanKey"/> of a vendor whose Vulkan cache a variable keys on a name of its own, whatever
    /// the exe (NVIDIA on Linux, __GL_SHADER_DISK_CACHE_APP_NAME): the warm writes under its name and the game reads it
    /// (<see cref="Vendors.Wine.CacheEnvironment"/>).</summary>
    public const string AppNameKey = "app-name";
    /// <summary><see cref="VendorCaps.VulkanKey"/> of a vendor that keys its Vulkan cache on what it compiles and the driver build
    /// alone (Mesa): the replayer under the game's own cache variables fills the file the game reads.</summary>
    public const string ContentKey = "content";
    /// <summary><see cref="VendorCaps.VulkanKey"/> of a vendor whose Vulkan cache file the game's variables name (AMD on
    /// Windows, as Steam's shader pre-caching sets them) and whose entries are keyed on the device's enabled extensions: the
    /// replayer runs under the game's variables with the game's own device (<see cref="DeviceVariable"/>).</summary>
    public const string DeviceKey = "device";
    /// <summary>The replayer's variable holding the game's device as the recorder's #vkdevice line has it
    /// (<see cref="VulkanLayer.Device"/>): set, it enables exactly its extensions and features (third_party/fossilize).</summary>
    public const string DeviceVariable = "SCSKILLER_REPLAY_DEVICE";
    /// <summary>A plan on <see cref="DeviceKey"/> whose game's device isn't recorded yet: the warm can't reach its cache.</summary>
    public const string DeviceNeeded = "turn on recording and start the game once, so the compile uses its Vulkan device";
    /// <summary>A plan on <see cref="DeviceKey"/> whose game's last launch didn't use Steam's shader cache folder.</summary>
    public const string SteamCacheNeeded = "start the game once from Steam with Shader Pre-Caching on";
    /// <summary>A Steam game whose folder has Valve's download but no archive this PC recorded: the download's pipelines count
    /// only for the application a launch here records (<see cref="SteamPipelineCache.Sources"/>), so no recorder is needed.</summary>
    public const string StartOnce = "start it once from Steam, then it can compile from Steam's shader download";

    /// <summary>A recorded device (<see cref="VulkanLayer.Device"/>) as the driver gets it under Steam's Fossilize layer, which
    /// adds VK_EXT_pipeline_creation_cache_control and turns its feature on (measured: Detroit: Become Human's 10 extensions
    /// reach the driver as 11; Fossilize's layer/dispatch.cpp CreateDevice): the Vulkan 1.3 struct's bit, else the
    /// extension's struct, added when the game chains neither.</summary>
    public static string UnderSteamLayer(string device)
    {
        const string ext = "VK_EXT_pipeline_creation_cache_control";
        const string vk13 = "53:", own = "1000297000:";   // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES, ..._PIPELINE_CREATION_CACHE_CONTROL_FEATURES
        if (device.Split(',') is not [var exts, var queues, var features]) return device;
        var names = exts.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!names.Contains(ext)) names.Add(ext);
        var structs = features.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var at = structs.FindIndex(s => s.StartsWith(vk13, StringComparison.Ordinal) && s.Length > vk13.Length + 3);
        if (at >= 0) structs[at] = structs[at][..(vk13.Length + 3)] + "1" + structs[at][(vk13.Length + 4)..];   // pipelineCreationCacheControl is its 4th
        else if ((at = structs.FindIndex(s => s.StartsWith(own, StringComparison.Ordinal) && s.Length > own.Length)) >= 0) structs[at] = own + "1" + structs[at][(own.Length + 1)..];
        else structs.Add(own + "1");
        return $"{string.Join(' ', names)},{queues},{string.Join(' ', structs)}";
    }

    /// <summary>Where Steam's shader pre-caching points a vendor's Vulkan cache, below the app's shadercache folder
    /// (measured): the variable naming the folder, and that folder then the one the driver writes in. Null: none.</summary>
    public static (string Variable, string[] Tail)? SteamCache(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => ("__GL_SHADER_DISK_CACHE_PATH", ["nvidiav1", "GLCache"]),
        GpuVendor.Amd when OperatingSystem.IsWindows() => ("AMD_VK_PIPELINE_CACHE_PATH", ["AMDv1"]),
        _ => null,
    };

    /// <summary>The vendor's own Vulkan cache folder on Windows, where a game without a redirect keeps its cache. Null: none known.</summary>
    public static string? DefaultCache(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "GLCache"),
        GpuVendor.Amd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMD", "VkCache"),
        _ => null,
    };

    /// <summary>The folder the vendor's variable (<see cref="SteamCache"/>) names in <paramref name="env"/>: Steam exports every
    /// vendor's at once. Null: none.</summary>
    public static string? CachePath(IReadOnlyDictionary<string, string>? env, GpuVendor vendor) =>
        SteamCache(vendor) is ({ } name, _) && env?.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } path ? path : null;

    const ulong LibraryBit = 0x800;              // VK_PIPELINE_CREATE_LIBRARY_BIT_KHR
    const ulong DescriptorHeapBit = 1UL << 36;   // VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT
    const long Flags2SType = 1000470005;         // VK_STRUCTURE_TYPE_PIPELINE_CREATE_FLAGS_2_CREATE_INFO_KHR
    const uint RayQueryKhr = 4472, RayQueryProvisional = 6016;

    public static bool IsVulkan(EngineInfo e) => e.GraphicsApi.StartsWith("Vulkan", StringComparison.Ordinal);

    public static Rec Item(string name) => new(Tag, Encoding.ASCII.GetBytes(name));
    public static string Key(string name) => Item(name).Key;

    /// <summary>On a vendor whose Vulkan cache a staged warm reaches (<see cref="VendorCaps.VulkanKey"/>): ready with a
    /// recording that has a pipeline, or archives (<see cref="Recording.VulkanSources"/>) one of which has a pipeline to plan.</summary>
    public static PlanCheck Check(Recording? recording) =>
        (recording?.VulkanSources is { } sources ? sources.Any(s => s.Origin != FozOrigin.Community && HasPipeline(s)) : recording != null && File.Exists(recording.DbPath) && HasPipeline(recording.DbPath))
            ? new(Readiness.Ready, "planned from a recording")
            : DownloadOnly(recording) ? new(Readiness.NeedsRecording, StartOnce) : new(Readiness.NeedsRecording, Planner.Record);

    static bool DownloadOnly(Recording? recording) => recording?.VulkanSources is { } sources
        && sources.Any(s => s is { Origin: FozOrigin.SteamDownload, Apps: not null }) && !sources.Any(s => s.Origin == FozOrigin.SteamLocal);

    static bool HasPipeline(FozSource s)
    {
        try
        {
            using var f = SteamPipelineCache.Open(s.Path);
            return Foz.First(f, e => s.Apps is { } apps ? e.Tag == Foz.BlobLink && Link(f, e) is { } l && Foz.IsPipeline(l.Tag) && apps.Contains(l.App) : Foz.IsPipeline(e.Tag)) != null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    static bool HasPipeline(string foz)
    {
        try { return Foz.List(foz).Any(e => Foz.IsPipeline(e.Tag)); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Every pipeline of the archive in file order; one whose JSON doesn't parse is still listed (the replayer
    /// decides), as neither a library nor RayQuery, needing nothing.</summary>
    public static List<VkPipeline> Pipelines(string foz)
    {
        using var f = Foz.OpenShared(foz);
        return Pipelines([new Archive(new FozSource(foz, FozOrigin.Ours), f, Foz.List(f))]);
    }

    /// <summary>The pipelines of several archives, the best one first: a name two of them hold is the first one's, and what
    /// a pipeline is created from may be in any of them. An unbucketed download's pipelines that another application
    /// recorded (<see cref="FozSource.Apps"/>) aren't listed. An archive that can't be read is logged and left out.</summary>
    public static List<VkPipeline> Pipelines(IReadOnlyList<FozSource> sources, IProgress<string>? log = null)
    {
        var archives = OpenAll(sources, log);
        try { return Pipelines(archives); }
        finally { foreach (var a in archives) a.Dispose(); }
    }

    static List<VkPipeline> Pipelines(List<Archive> archives)
    {
        var modules = new Dictionary<ulong, (Archive A, Foz.Entry E)>();
        var byTag = new Dictionary<int, HashSet<string>>();
        foreach (var a in archives)
            foreach (var e in a.Entries)
                if (e.Tag == Foz.ShaderModule) modules.TryAdd(e.Hash, (a, e));
                else if (Foz.IsPipeline(e.Tag)) (byTag.TryGetValue(e.Tag, out var hashes) ? hashes : byTag[e.Tag] = new(StringComparer.Ordinal)).Add($"{e.Hash:x16}");
        var rayQuery = new Dictionary<ulong, bool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<VkPipeline>();
        foreach (var a in archives)
        {
            var linked = a.Source.Apps is { } apps ? Linked(a, apps) : null;
            foreach (var e in a.Entries.Where(e => Foz.IsPipeline(e.Tag) && linked?.Contains((e.Tag, e.Hash)) != false))
                if (seen.Add(e.Name)) list.Add(Parse(a.File, e, byTag[e.Tag], RayQuery) with { Origin = a.Source.Origin });
        }
        return list;

        // decoded once, from the best archive holding it, and only when a pipeline names it
        bool RayQuery(string h)
        {
            if (!Hex16(h, out var hash) || !modules.TryGetValue(hash, out var m)) return false;
            if (rayQuery.TryGetValue(hash, out var rq)) return rq;
            try { rq = UsesRayQuery(Foz.Payload(m.A.File, m.E)); }
            catch (Exception x) when (x is InvalidDataException or JsonException or FormatException or ArgumentException or KeyNotFoundException or InvalidOperationException) { rq = false; }
            return rayQuery[hash] = rq;
        }
    }

    /// <summary>A pipeline's entry read: whether it is a library, whether a shader of it traces rays inline, and the pipelines
    /// of its kind (<paramref name="kind"/>: their 16-hex hashes) it is created from.</summary>
    static VkPipeline Parse(Stream f, Foz.Entry e, HashSet<string> kind, Func<string, bool> rayQuery)
    {
        var (library, heap, rq, needs, needed) = (false, false, false, new List<string>(), new HashSet<string>(StringComparer.Ordinal));
        try
        {
            using var doc = JsonDocument.Parse(Foz.Payload(f, e));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var k in doc.RootElement.EnumerateObject())
                if (k.Value.ValueKind == JsonValueKind.Object && k.Value.TryGetProperty($"{e.Hash:x16}", out var info))
                {
                    library = Flags(info) is { } top && (top & LibraryBit) != 0;
                    Walk(info);
                }
        }
        catch (Exception x) when (x is InvalidDataException or JsonException or InvalidOperationException) { }   // InvalidOperationException: a value of another kind
        return new VkPipeline(e, library, rq, needs) { DescriptorHeap = heap };

        // every 16-hex string naming a pipeline of the same kind is one it is created from; a shader module's, its stage
        void Walk(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Object:
                    if (v.TryGetProperty("sType", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt64(out var s) && s == Flags2SType && Flags(v) is { } f2)
                        (library, heap) = (library || (f2 & LibraryBit) != 0, heap || (f2 & DescriptorHeapBit) != 0);
                    foreach (var p in v.EnumerateObject()) Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var x in v.EnumerateArray()) Walk(x);
                    break;
                case JsonValueKind.String when v.GetString() is { Length: 16 } h:
                    if (h != $"{e.Hash:x16}" && kind.Contains(h) && Foz.Name(e.Tag, Convert.ToUInt64(h, 16)) is var n && needed.Add(n)) needs.Add(n);
                    rq |= rayQuery(h);
                    break;
            }
        }

        static ulong? Flags(JsonElement o) => o.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Number && fl.TryGetUInt64(out var u) ? u : null;
    }

    /// <summary>Fossilize's way of naming an object: 16 lowercase hex digits.</summary>
    static bool Hex16(string s, out ulong hash)
    {
        hash = 0;
        return s.Length == 16 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) && ulong.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, null, out hash);
    }

    /// <summary>A shader module entry (JSON, NUL, varint-packed SPIR-V words; base64 "code" in older archives) declares
    /// the RayQuery capability.</summary>
    internal static bool UsesRayQuery(byte[] payload)
    {
        var nul = Array.IndexOf(payload, (byte)0);
        using var doc = JsonDocument.Parse(payload.AsMemory(0, nul < 0 ? payload.Length : nul));
        foreach (var m in doc.RootElement.GetProperty("shaderModules").EnumerateObject())
        {
            var o = m.Value;
            // a word takes a varint byte at least, a base64 char at most: a size past that is a lie
            if (o.GetProperty("codeSize").GetInt64() is var size && (size < 0 || size / 4 > payload.Length)) throw new InvalidDataException("a code size past the payload");
            var words = new uint[size / 4];
            if (o.TryGetProperty("varintOffset", out var off))
            {
                var at = nul + 1 + off.GetInt32();
                if (nul < 0 || !Varint(payload.AsSpan(at, o.GetProperty("varintSize").GetInt32()), words)) throw new InvalidDataException("bad varint SPIR-V");
            }
            else Buffer.BlockCopy(Convert.FromBase64String(o.GetProperty("code").GetString()!), 0, words, 0, words.Length * 4);
            for (var i = 5; i < words.Length;)
            {
                var (count, op) = ((int)(words[i] >> 16), words[i] & 0xFFFF);
                if (count == 0 || op == 14) break;   // OpMemoryModel: the capabilities are all before it
                if (op == 17 && i + 1 < words.Length && words[i + 1] is RayQueryKhr or RayQueryProvisional) return true;
                i += count;
            }
        }
        return false;
    }

    /// <summary>Fossilize's varint.cpp decode_varint: 7 bits a byte, low first, the high bit continues.</summary>
    static bool Varint(ReadOnlySpan<byte> b, uint[] words)
    {
        var at = 0;
        for (var i = 0; i < words.Length; i++)
        {
            uint w = 0;
            var shift = 0;
            do
            {
                if (at >= b.Length || shift >= 32) return false;
                w |= (uint)(b[at] & 0x7F) << shift;
                shift += 7;
            } while ((b[at++] & 0x80) != 0);
            words[i] = w;
        }
        return at == b.Length;
    }

    /// <summary>The plan: every recorded pipeline, ray tracing pipelines left out without <paramref name="rayTracing"/>.
    /// Where the game reads the warm's cache under another app name (<see cref="AppNameKey"/>), pipelines using descriptor
    /// heaps are left out and counted in <see cref="PlanStats.DescriptorHeap"/>: NVIDIA serves them from the game's own cache
    /// file alone (measured), so they are misses whatever the warm does. Where the warm's cache is capped
    /// (<see cref="VendorCaps.VulkanCacheCap"/>), each pipeline is charged <see cref="App.ScsKiller.DefaultBytesPerPso"/> with
    /// every pipeline it is created from (the work file holds them too), the best archives' first, and one that no longer
    /// fits is counted in <see cref="PlanStats.CacheBudgetLeftOut"/>. The stats count ray tracing like a D3D12 plan's: RT
    /// pipelines (libraries included) as state objects and as the game's ray tracing, every one replayed (none uncovered),
    /// and RayQuery pipelines as inline ray tracing.</summary>
    /// <param name="indexHash">the game's index (a DirectX game's under Proton: its plan goes stale with it)</param>
    public static Plan Build(Game game, Recording recording, VendorCaps caps, string outDir, bool rayTracing, string indexHash = ShaderIndex.EmptyContentHash)
    {
        var archives = recording.VulkanSources is { } sources ? OpenAll(sources, null) : One(recording.DbPath);
        try
        {
            var all = Pipelines(archives);
            var (planned, stats) = Select(all, Needs(archives, all), caps, rayTracing);
            var plan = new Plan(game.Id, indexHash, Platform, caps.Profile, stats, Path.Combine(outDir, "plan.bin"));
            PlanFile.Write(plan, planned.Select(p => Item(p.Name)));
            return plan;
        }
        finally { foreach (var a in archives) a.Dispose(); }
    }

    /// <summary>The names of the pipelines a plan of <paramref name="sources"/> replays and its counts, as <see cref="Build"/>
    /// plans them: what a DirectX plan under Proton adds from Steam's archives.</summary>
    public static (List<string> Names, PlanStats Stats) Select(IReadOnlyList<FozSource> sources, VendorCaps caps, bool rayTracing)
    {
        var archives = OpenAll(sources, null);
        try
        {
            var all = Pipelines(archives);
            var (planned, stats) = Select(all, Needs(archives, all), caps, rayTracing);
            return ([.. planned.Select(p => p.Name)], stats);
        }
        finally { foreach (var a in archives) a.Dispose(); }
    }

    static (List<VkPipeline> Planned, PlanStats Stats) Select(List<VkPipeline> all, Func<string, IReadOnlyList<string>?> needs, VendorCaps caps, bool rayTracing)
    {
        var planned = all.Where(p => rayTracing || p.Entry.Tag != Foz.RayTracing).ToList();
        var heap = caps.VulkanKey == AppNameKey ? planned.RemoveAll(p => p.DescriptorHeap) : 0;
        var rt = all.Where(p => p.Entry.Tag == Foz.RayTracing).ToList();
        var leftOut = all.Count - planned.Count;
        long? overCap = null;
        if (caps.VulkanCacheCap is { } cap)
        {
            var kept = WithinBudget(planned, needs, cap / (long)App.ScsKiller.DefaultBytesPerPso);
            overCap = planned.Count - kept.Count;
            planned = kept;
        }
        var stats = new PlanStats(planned.Count, 0, 0, 0, false, StageSets: all.Count, LeftOut: leftOut,
            RtStateObjects: rt.Count, RtLibraries: rt.Count, RtUncovered: 0, RtInline: all.Count(p => p.RayQuery), CacheBudgetLeftOut: overCap, DescriptorHeap: heap,
            FromDownload: planned.Count(p => p.Origin == FozOrigin.SteamDownload),
            FromCommunity: planned.Count(p => p.Origin == FozOrigin.Community) is var community and > 0 ? community : null);
        return (planned, stats);
    }

    /// <summary>Of <paramref name="planned"/>, in order, those that fit in <paramref name="room"/> pipelines, each charged with
    /// the pipelines it is created from (<paramref name="needs"/>) that no earlier one brought.</summary>
    static List<VkPipeline> WithinBudget(List<VkPipeline> planned, Func<string, IReadOnlyList<string>?> needs, long room)
    {
        var charged = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<VkPipeline>();
        foreach (var p in planned)
        {
            var adds = new HashSet<string>(StringComparer.Ordinal);
            var todo = new Stack<string>([p.Name]);
            while (todo.TryPop(out var n))
                if (!charged.Contains(n) && adds.Add(n) && needs(n) is { } from)
                    foreach (var need in from) todo.Push(need);
            if (charged.Count + adds.Count > room) continue;
            charged.UnionWith(adds);
            kept.Add(p);
        }
        return kept;
    }

    /// <summary>What a pipeline of <paramref name="archives"/> is created from, by name, as <see cref="Materialize(Plan, IReadOnlyList{FozSource}, string, IProgress{string}?)"/>
    /// resolves it: from the best archive holding it, whichever application recorded it (<see cref="FozSource.Apps"/>), so a
    /// chain through a pipeline the plan doesn't list is charged in full. Null for a name no archive has.</summary>
    static Func<string, IReadOnlyList<string>?> Needs(List<Archive> archives, List<VkPipeline> listed)
    {
        var known = listed.ToDictionary(p => p.Name, p => (IReadOnlyList<string>?)p.Needs, StringComparer.Ordinal);
        var byName = new Dictionary<string, (Archive A, Foz.Entry E)>(StringComparer.Ordinal);
        var kinds = new Dictionary<int, HashSet<string>>();
        foreach (var a in archives)
            foreach (var e in a.Entries.Where(e => Foz.IsPipeline(e.Tag)))
            {
                byName.TryAdd(e.Name, (a, e));
                (kinds.TryGetValue(e.Tag, out var hashes) ? hashes : kinds[e.Tag] = new(StringComparer.Ordinal)).Add($"{e.Hash:x16}");
            }
        return n => known.TryGetValue(n, out var k) ? k
            : byName.TryGetValue(n, out var x) ? known[n] = Parse(x.A.File, x.E, kinds[x.E.Tag], _ => false).Needs : null;
    }

    /// <summary>Writes the work folder's <see cref="WorkFile"/> and <see cref="ValidatedFile"/>: every non-pipeline entry of the recording (the replayer
    /// resolves modules, layouts and render passes by hash) with the plan's pipelines and the ones they are created from,
    /// in recording order; and <see cref="Planner.SkippedFile"/> with the plan's pipelines the recording no longer has.</summary>
    public static void Materialize(Plan plan, Recording recording, string workDir)
    {
        if (recording.VulkanSources is { } sources)
        {
            Materialize(plan, sources, workDir);
            return;
        }
        Directory.CreateDirectory(workDir);
        var wanted = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == Tag).Select(r => Encoding.ASCII.GetString(r.Payload)).ToHashSet(StringComparer.Ordinal);
        var pipelines = Pipelines(recording.DbPath);
        var keep = Closure(pipelines, wanted);
        Write(recording.DbPath, Path.Combine(workDir, WorkFile), e => !Foz.IsPipeline(e.Tag) || keep.Contains(e.Name));
        // SCSKiller's own recording: every module was recorded on this PC
        var modules = Foz.List(recording.DbPath).Where(e => e.Tag == Foz.ShaderModule).Select(e => e.Hash).ToList();
        WriteNew(Path.Combine(workDir, ValidatedFile), w =>
        {
            Foz.WriteMagic(w);
            foreach (var h in modules) Foz.AppendName(w, Foz.ShaderModule, h);
        });
        wanted.ExceptWith(pipelines.Select(p => p.Name));
        File.WriteAllText(Path.Combine(workDir, Planner.SkippedFile), $"{wanted.Count} 0");
    }

    /// <summary>The work folder of a plan of several archives: its <see cref="WorkFile"/> holds the plan's pipelines and
    /// everything they are created from, pipelines and objects (named by Fossilize's handle fields, <see cref="References"/>), each from the
    /// best archive that has it, and one application info, the best archive's first: the replayer creates its device from
    /// one, whichever its hash map lists first. A pipeline that needs what no archive has is left out and counted in
    /// <see cref="Planner.SkippedFile"/>, as one no archive has. <see cref="ValidatedFile"/> names the modules of this PC's
    /// archives. Steam's archives are only read, and each file is written new and renamed over its
    /// name, so a link there is replaced, never written through.</summary>
    public static void Materialize(Plan plan, IReadOnlyList<FozSource> sources, string workDir, IProgress<string>? log = null)
    {
        var (work, validated, skipped) = (Path.Combine(workDir, WorkFile), Path.Combine(workDir, ValidatedFile), Path.Combine(workDir, Planner.SkippedFile));
        if (new[] { work, validated, skipped }.Any(p => SteamPipelineCache.Owned(p)))
            throw new InvalidOperationException($"{workDir} is or links into Steam's folders, which SCSKiller never writes");
        Directory.CreateDirectory(workDir);
        var wanted = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == Tag).Select(r => Encoding.ASCII.GetString(r.Payload)).ToHashSet(StringComparer.Ordinal);
        var archives = OpenAll(sources, log);
        try
        {
            var byName = new Dictionary<string, (Archive A, Foz.Entry E)>(StringComparer.Ordinal);
            var byHash = new Dictionary<ulong, List<string>>();
            foreach (var a in archives)
                foreach (var e in a.Entries.Where(e => e.Tag is not (Foz.ApplicationInfo or Foz.BlobLink or Foz.BucketInfo)))
                    if (byName.TryAdd(e.Name, (a, e))) (byHash.TryGetValue(e.Hash, out var names) ? names : byHash[e.Hash] = []).Add(e.Name);

            // what the plan reaches, and what of it names something no archive has
            var refs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var broken = new HashSet<string>(StringComparer.Ordinal);
            var todo = new Stack<string>(wanted.Where(byName.ContainsKey));
            while (todo.TryPop(out var n))
            {
                if (refs.ContainsKey(n)) continue;
                var (named, missing) = References(byName[n].A.File, byName[n].E, byHash);
                refs[n] = named;
                if (missing) broken.Add(n);
                foreach (var r in named)
                {
                    (users.TryGetValue(r, out var u) ? u : users[r] = []).Add(n);
                    todo.Push(r);
                }
            }
            var spread = new Stack<string>(broken);
            while (spread.TryPop(out var n))
                foreach (var u in users.GetValueOrDefault(n) ?? [])
                    if (broken.Add(u)) spread.Push(u);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            todo = new Stack<string>(wanted.Where(n => refs.ContainsKey(n) && !broken.Contains(n)));
            while (todo.TryPop(out var n))
                if (keep.Add(n))
                    foreach (var r in refs[n]) todo.Push(r);

            WriteNew(work, dst =>
            {
                Foz.WriteMagic(dst);
                if (archives.SelectMany(a => a.Entries.Where(e => e.Tag == Foz.ApplicationInfo).Take(1).Select(e => (A: a, E: e))).FirstOrDefault() is { A: { } first } app)
                    Foz.Copy(first.File, app.E, dst);
                foreach (var a in archives)
                    foreach (var e in a.Entries.Where(e => keep.Contains(e.Name) && byName[e.Name].A == a)) Foz.Copy(a.File, e, dst);
            });
            var modules = keep.Select(n => byName[n]).Where(x => x.E.Tag == Foz.ShaderModule).ToList();
            WriteNew(validated, w =>
            {
                Foz.WriteMagic(w);
                foreach (var m in modules.Where(m => m.A.Source.Trusted)) Foz.AppendName(w, Foz.ShaderModule, m.E.Hash);
            });
            WriteNew(skipped, w => w.Write(Encoding.ASCII.GetBytes($"{wanted.Count(n => !keep.Contains(n))} 0")));
        }
        finally { foreach (var a in archives) a.Dispose(); }
    }

    /// <summary>Writes a new file beside <paramref name="path"/> and renames it over it: a link or a hard link at the name is
    /// replaced, never written through.</summary>
    static void WriteNew(string path, Action<Stream> write)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20)) write(f);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    /// <summary>Fossilize's fields that hold handles, by tag: a set layout's immutable samplers, a pipeline layout's set
    /// layouts, and a pipeline's layout, render pass, base pipeline, shader modules (in its stages) and libraries (in its
    /// pNext or library info). Samplers, shader modules and render passes name nothing.</summary>
    static readonly Dictionary<int, HashSet<string>> HandleFields = new()
    {
        [2] = ["immutableSamplers"],
        [3] = ["setLayouts"],
        [Foz.Graphics] = PipelineHandles(),
        [Foz.Compute] = PipelineHandles(),
        [Foz.RayTracing] = PipelineHandles(),
    };

    static HashSet<string> PipelineHandles() => new(StringComparer.Ordinal) { "layout", "renderPass", "basePipelineHandle", "module", "libraries" };

    /// <summary>The names an entry's handle fields hold (<see cref="HandleFields"/>, a null handle left out), and whether one
    /// of them names nothing in <paramref name="byHash"/>. JSON that doesn't parse names nothing (the replayer decides).</summary>
    static (HashSet<string> Names, bool Missing) References(Stream f, Foz.Entry e, Dictionary<ulong, List<string>> byHash)
    {
        var (names, missing) = (new HashSet<string>(StringComparer.Ordinal), false);
        if (!HandleFields.TryGetValue(e.Tag, out var fields)) return (names, missing);
        try
        {
            using var doc = JsonDocument.Parse(Foz.Payload(f, e));
            Walk(doc.RootElement);
        }
        catch (Exception x) when (x is InvalidDataException or JsonException) { }
        return (names, missing);

        void Walk(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Array)
                foreach (var x in v.EnumerateArray()) Walk(x);
            else if (v.ValueKind == JsonValueKind.Object)
                foreach (var p in v.EnumerateObject())
                    if (!fields.Contains(p.Name)) Walk(p.Value);
                    else if (p.Value.ValueKind == JsonValueKind.Array) foreach (var x in p.Value.EnumerateArray()) Handle(x);
                    else Handle(p.Value);
        }

        void Handle(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.String || !Hex16(v.GetString()!, out var h) || h == 0 || h == e.Hash) return;
            if (byHash.TryGetValue(h, out var found)) names.UnionWith(found);
            else missing = true;
        }
    }

    /// <summary>An archive a plan reads, open, and its entries.</summary>
    sealed class Archive(FozSource source, FileStream file, List<Foz.Entry> entries) : IDisposable
    {
        public FozSource Source => source;
        public FileStream File => file;
        public List<Foz.Entry> Entries => entries;
        public void Dispose() => file.Dispose();
    }

    /// <summary>The archives that can be read, in their order, each opened for reading only.</summary>
    /// <summary>SCSKiller's own recording, open: one that can't be read throws.</summary>
    static List<Archive> One(string foz)
    {
        var f = Foz.OpenShared(foz);
        try { return [new Archive(new FozSource(foz, FozOrigin.Ours), f, Foz.List(f))]; }
        catch { f.Dispose(); throw; }
    }

    static List<Archive> OpenAll(IReadOnlyList<FozSource> sources, IProgress<string>? log)
    {
        var list = new List<Archive>();
        foreach (var s in sources)
        {
            FileStream? f = null;
            try
            {
                f = SteamPipelineCache.Open(s.Path);
                list.Add(new(s, f, Foz.List(f)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                f?.Dispose();
                log?.Report($"{s.Path} can't be read: {e.Message}");
            }
        }
        return list;
    }

    /// <summary>The pipelines of an unbucketed archive that <paramref name="apps"/> recorded, by its blob links.</summary>
    static HashSet<(int, ulong)> Linked(Archive a, IReadOnlySet<ulong> apps)
    {
        var linked = new HashSet<(int, ulong)>();
        foreach (var e in a.Entries.Where(e => e.Tag == Foz.BlobLink))
            if (Link(a.File, e) is { } l && Foz.IsPipeline(l.Tag) && apps.Contains(l.App)) linked.Add((l.Tag, l.Hash));
        return linked;
    }

    /// <summary>A blob link entry ({"link":{"application":..,"tag":..,"hash":..}}): which application recorded which object.
    /// Null when it isn't one.</summary>
    static (ulong App, int Tag, ulong Hash)? Link(Stream f, Foz.Entry e)
    {
        try
        {
            using var doc = JsonDocument.Parse(Foz.Payload(f, e));
            return doc.RootElement.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.Object
                && l.TryGetProperty("application", out var app) && app.ValueKind == JsonValueKind.String && Hex16(app.GetString()!, out var a)
                && l.TryGetProperty("tag", out var tag) && tag.TryGetInt32(out var t)
                && l.TryGetProperty("hash", out var hash) && hash.ValueKind == JsonValueKind.String && Hex16(hash.GetString()!, out var h)
                ? (a, t, h) : null;
        }
        catch (Exception x) when (x is InvalidDataException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Rewrites a work folder's <see cref="WorkFile"/> without the pipelines <paramref name="compiled"/> holds (by
    /// <see cref="Key"/>), but those a kept one is created from. Returns how many were left out.</summary>
    public static long LeaveOut(string workFoz, IReadOnlySet<string> compiled)
    {
        var pipelines = Pipelines(workFoz);
        var keep = Closure(pipelines, pipelines.Where(p => !compiled.Contains(Key(p.Name))).Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
        var left = pipelines.Count - keep.Count;
        if (left == 0) return 0;
        var tmp = workFoz + ".tmp";
        try
        {
            Write(workFoz, tmp, e => !Foz.IsPipeline(e.Tag) || keep.Contains(e.Name));
            File.Move(tmp, workFoz, true);
        }
        finally { File.Delete(tmp); }
        return left;
    }

    /// <summary>The <see cref="Key"/>s of a warm's pipelines it created: every one in its <see cref="WorkFile"/> but those a
    /// <see cref="App.Recordings.FailedFile"/> names.</summary>
    public static HashSet<string> Created(string workFoz, IEnumerable<string> failed)
    {
        var notCreated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in failed.Where(File.Exists))
            foreach (var k in File.ReadAllBytes(f).Chunk(20).Where(c => c.Length == 20)) notCreated.Add(Hex(k));
        return [.. Foz.List(workFoz).Where(e => Foz.IsPipeline(e.Tag)).Select(e => Key(e.Name)).Where(k => !notCreated.Contains(k))];
    }

    /// <summary>The names of <paramref name="wanted"/> and every pipeline they are created from, transitively.</summary>
    static HashSet<string> Closure(List<VkPipeline> pipelines, HashSet<string> wanted)
    {
        var byName = pipelines.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>(wanted.Where(byName.ContainsKey));
        while (todo.TryPop(out var n))
            if (keep.Add(n))
                foreach (var d in byName[n].Needs) todo.Push(d);
        return keep;
    }

    static void Write(string from, string to, Func<Foz.Entry, bool> keep)
    {
        using var src = Foz.OpenShared(from);
        var entries = Foz.List(src);
        using var dst = new BufferedStream(File.Create(to), 1 << 20);
        Foz.WriteMagic(dst);
        foreach (var e in entries.Where(keep)) Foz.Copy(src, e, dst);
    }

    /// <summary>Deletes <paramref name="path"/>, the file <paramref name="inbox"/> has open, when nothing else has it open and it
    /// holds the <paramref name="merged"/> bytes.</summary>
    private static partial void DeleteMerged(FileStream inbox, string path, long merged, IProgress<string>? log);

    /// <summary>Merges every recorder archive in <paramref name="dir"/> into <paramref name="store"/> and deletes each one
    /// nothing has open any more (Fossilize never writes to a closed archive again), never one in Steam's folders
    /// (<see cref="SteamPipelineCache.Owned"/>). One still open is merged as far as
    /// it is whole. Each is staged first: what is checked is what is appended, whatever writes the inbox meanwhile. The
    /// caller holds the recording lock. Returns the names of the pipelines added.</summary>
    public static List<string> Import(string dir, string store, IProgress<string>? log = null)
    {
        var added = new List<string>();
        if (!Directory.Exists(dir)) return added;
        var inboxes = Directory.EnumerateFiles(dir, InboxPattern).Order(StringComparer.OrdinalIgnoreCase).ToList();
        // Linux has no share modes: a recording process holds its csv locked while it lives (vklayer.cpp) and writes only
        // archives it made, so with none holding it now every archive listed above is closed for good
        var live = !OperatingSystem.IsWindows() && Held(Path.Combine(dir, "scskiller_creates.csv"));
        foreach (var inbox in inboxes)
        {
            FileStream? closed = null;
            var staged = store + ".inbox.tmp";
            try
            {
                // only a delete shared: no writer gets in between the merge and the delete below
                try { if (!live) closed = new FileStream(inbox, FileMode.Open, FileAccess.Read, FileShare.Delete); }
                catch (IOException) { }   // the game holds it: what is whole now, the rest next time
                Directory.CreateDirectory(Path.GetDirectoryName(store)!);
                using (var f = closed ?? Foz.OpenShared(inbox))
                {
                    if (f.Length > Foz.MaxInbox) throw new InvalidDataException($"over {Foz.MaxInbox >> 30} GB");
                    long merged = 0;
                    if (f.Length > 0)   // created, nothing written yet
                    {
                        using (var copy = File.Create(staged))
                        {
                            f.CopyTo(copy);
                            merged = copy.Length;
                        }
                        using var s = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
                        added.AddRange(Foz.Merge(store, s).Where(e => Foz.IsPipeline(e.Tag)).Select(e => e.Name));
                    }
                    // gone once this handle closes; Steam's archives are only ever read
                    if (closed != null && !SteamPipelineCache.Owned(inbox)) DeleteMerged(closed, inbox, merged, log);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log?.Report($"could not import {inbox}: {e.Message}");
            }
            finally { File.Delete(staged); }
        }
        return added;
    }

    /// <summary>A process holds the file locked (flock, which .NET takes on Linux for a FileShare.None open).</summary>
    internal static bool Held(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }
}
