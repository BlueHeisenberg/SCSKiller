using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>Ray tracing collections synthesized from the index's DXIL libraries, one per library, the way Unreal 4.26/4.27
/// compiles each ray tracing shader into its own collection before linking pipelines from them. Only for a driver that
/// caches collections on their own (NVIDIA: a cached collection makes any pipeline linking it cheap; AMD caches whole
/// objects only, see ARCHITECTURE.md), <see cref="Planner.RtCollectionCache"/>.
///
/// A collection (Star Wars Jedi: Survivor's 2,919 recorded ones, UE 4.26's order): the library with its entry points
/// exported as "&lt;Prefix&gt;_&lt;16 hex&gt;" (RayGen / Miss / CHS / AHS / IS / Callable, renamed from the entry name), the
/// shader config, its association with every export, a hit group "HitGroup_&lt;16 hex&gt;" for hit shaders (closest hit,
/// any hit, intersection; triangles, procedural with an intersection shader), the pipeline config, the state object config,
/// the global root signature, the local root signature (<see cref="LocalRs"/>) and one association of it per export. The
/// 16 hex digits are UE's 64-bit shader hash, which the index doesn't carry: synthesized collections use the library's
/// SHA-1 instead. Export and hit group names aren't in NVIDIA's key (selftest dxr, ARCHITECTURE.md).</summary>
public static class RtCollections
{
    /// <summary>How the engine builds its collections: the global root signature (a hash; its blob goes into the plan),
    /// the state object config flags, the recursion depth and the shader config. <see cref="Verified"/>: learned from
    /// recorded collections that the synthesis rebuilds byte for byte.</summary>
    public sealed record Rule(string GlobalRs, uint Flags, uint Depth, uint Payload, uint Attributes, bool Verified);

    /// <summary>A collection's flags when it has no state object config subobject (UE 4.25 creates them without one).</summary>
    public const uint NoConfig = uint.MaxValue;

    /// <summary>UE 4.26's global ray tracing root signature as Jedi: Survivor's recording has it (the stock rule is read
    /// from it, unverified on other 4.26/4.27 games): NVAPI's extension UAV u0 space 404, then in space 1 (the RHI's
    /// global ray tracing space) an SRV table of 64, a sampler table of 16, a UAV table of 16 and root CBVs b0-b15, all
    /// visible to every shader; UE's six static samplers.</summary>
    public static readonly RootSig.Desc Ue426Global = new(0, [
        [0, 0, 1, 1, 0, 404, 3],
        [0, 0, 0, 64, 0, 1, 5], [0, 0, 3, 16, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
        .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 })]);

    /// <summary>Kuro's 4.26 fork's (Wuthering Waves' recording: 2411 collections, one global root signature): in space 77 (the
    /// fork's own global ray tracing space, which 127415 of its 127439 libraries bind) a UAV table of 16, an SRV table of 64
    /// and root CBVs b2 then b1; then UE 4.26's space 1 block without its NVAPI slot; then NVAPI's extension UAV u0 in space
    /// 1001 as a table (offset 0, no flags; where UE 5.8 puts it); UE's six static samplers.</summary>
    public static readonly RootSig.Desc WuwaGlobal = new(0, [
        [0, 0, 1, 16, 0, 77, 3], [0, 0, 0, 64, 0, 77, 5], [2, 0, 2, 77, 8], [2, 0, 1, 77, 8],
        .. Ue426Global.Rows.Skip(1),
        [0, 0, 1, 1, 0, 1001, 0, 0]]);

    /// <summary>UE 4.25's (Returnal's recording): 4.26's without the NVAPI slot, with 4.25's static samplers
    /// (<see cref="RootSig.StaticSamplers"/>).</summary>
    public static readonly RootSig.Desc Ue425Global = new(0, [.. Ue426Global.Rows.Skip(1)]);

    /// <summary>UE 5.1's (Oblivion Remastered's recording): 4.26's without the NVAPI slot, plus the diagnostic root UAV u0 space 999.</summary>
    public static readonly RootSig.Desc Ue51Global = new(0, [
        [0, 0, 0, 64, 0, 1, 5], [0, 0, 3, 16, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
        .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 }),
        [4, 0, 0, 999, 2]]);

    /// <summary>UE 5.4's, unverified: 5.1's with a sampler table of 32.</summary>
    // 5.1's tables are D3D12RHI's MAX_SRVS/MAX_SAMPLERS/MAX_UAVS/MAX_CBS, and 5.4 raised MAX_SAMPLERS to 32 (RootSig.Rule.Ue54)
    public static readonly RootSig.Desc Ue54Global = new(0, [
        [0, 0, 0, 64, 0, 1, 5], [0, 0, 3, 32, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
        .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 }),
        [4, 0, 0, 999, 2]]);

    /// <summary>Why a game's DXIL libraries don't have stock UE 5.0-5.4's binding shape, which the 5.1 rule is built for
    /// (Oblivion Remastered's and Darwin's Paradox's libraries have it); null when they do. The shape: uniform buffers as
    /// CBVs in space 1 (ray generation), the hit groups' index and vertex buffers t0/t1 in space 2, no bindless descriptor
    /// heap access, no shared uniform buffers in space 4 (UE 5.6's bindless ray tracing, SILENT HILL: Townfall's).</summary>
    public static string? Ue5ShapeMismatch(IReadOnlyCollection<ShaderInfo> libs)
    {
        var b = libs.SelectMany(l => l.Bindings).ToList();
        bool Binds(string cls, int space, int reg) => b.Any(x => x.Class == cls && x.Space == space && x.Lower <= reg && (x.Count < 0 || reg < x.Lower + x.Count));
        if (libs.Any(l => (l.Counts.Flags & (ShaderContainer.UeFlags.BindlessResources | ShaderContainer.UeFlags.BindlessSamplers)) != 0)) return "bindless descriptor heap access";
        if (b.Any(x => x is { Class: "cbv", Space: 4 })) return "shared uniform buffers in space 4";
        if (!b.Any(x => x is { Class: "cbv", Space: 1 })) return "no uniform buffers in space 1";
        if (!Binds("srv", 2, 0) || !Binds("srv", 2, 1)) return "no hit group index and vertex buffers (t0/t1 space 2)";
        return null;
    }

    /// <summary>Northlight's local root signature (version 1.1), as Control's renderer builds it: the hit geometry's index and
    /// vertex buffer SRVs t0/t1 and 10 root constants b0, all in space 3. Its global one is <see cref="RootSig.NorthlightCompute"/>.</summary>
    public static readonly RootSig.Desc NorthlightLocal = new(0x80, [[3, 0, 0, 3, 0], [3, 0, 1, 3, 0], [1, 0, 0, 3, 10]]);

    /// <summary>The global root signature of the libraries' engine: UE 4.26's (<see cref="Ue426Global"/>), or Avalanche's 4.27
    /// fork's when the libraries carry its bindless marker (unbounded SRVs t0 in spaces 4-9, <see cref="RootSig.MaxSrvsFor"/>;
    /// Hogwarts Legacy's recording: all 691 collections have it): in space 1 an SRV table of 128, one unbounded SRV table per
    /// space from 5 up to the highest bindless space a library declares (space 4 is in each local one), a sampler table of 16,
    /// a UAV table of 16, root CBVs b0-b15, then a table of one UAV u0 in space 1001 (offset 0, no flags); no NVAPI slot; UE's
    /// six static samplers.</summary>
    public static RootSig.Desc GlobalFor(IEnumerable<ShaderInfo> libs)
    {
        var top = libs.SelectMany(l => l.Bindings).Where(IsSpaceBindless).Select(b => (uint)b.Space).DefaultIfEmpty(0u).Max();
        if (top == 0) return Ue426Global;
        return new(0, [
            [0, 0, 0, 128, 0, 1, 5],
            .. Enumerable.Range(5, Math.Max(0, (int)top - 4)).Select(k => new uint[] { 0, 0, 0, uint.MaxValue, 0, (uint)k, 5 }),
            [0, 0, 3, 16, 0, 1, 1], [0, 0, 1, 16, 0, 1, 3],
            .. Enumerable.Range(0, 16).Select(b => new uint[] { 2, 0, (uint)b, 1, 8 }),
            [0, 0, 1, 1, 0, 1001, 0, 0]]);
    }

    static bool IsSpaceBindless(Binding b) => b is { Class: "srv", Count: -1, Space: >= 4 and < 10 };

    /// <summary>UE 4.26's local root signature of a library (LOCAL_ROOT_SIGNATURE, every parameter visible to all): none for
    /// a ray generation shader (it binds through the global one); else the hit group system parameters (index and vertex
    /// buffer SRVs t0/t1 and 4 root constants b0, space 2), then an SRV, sampler and UAV table of the shader's counts
    /// (only those it has) and its root CBVs b0.., space 0. Rebuilds all 277 of Jedi's recorded local root signatures
    /// from the library's resource counts. <paramref name="systemConstants"/>: 4 in UE 4, 6 in UE 5.1.</summary>
    public static RootSig.Desc LocalRs(ResourceCounts c, bool rayGen, IReadOnlyList<Binding>? bindings = null, uint systemConstants = 4)
    {
        if (rayGen) return new(0x80, []);
        List<uint[]> rows = [[3, 0, 0, 2, 0], [3, 0, 1, 2, 0], [1, 0, 0, 2, systemConstants]];
        if (c.Srv > 0) rows.Add([0, 0, 0, (uint)c.Srv, 0, 0, 5]);
        if (bindings?.Any(b => b is { Class: "srv", Count: -1, Space: 4 }) == true) rows.Add([0, 0, 0, uint.MaxValue, 0, 4, 5]); // Avalanche's fork: bindless space 4
        if (c.Sampler > 0) rows.Add([0, 0, 3, (uint)c.Sampler, 0, 0, 1]);
        if (c.Uav > 0) rows.Add([0, 0, 1, (uint)c.Uav, 0, 0, 3]);
        for (var b = 0u; b < c.Cb; b++) rows.Add([2, 0, b, 0, 8]);
        return new(0x80, rows);
    }

    /// <summary>Plan record 'Y': library sha1[20], global root signature[20], local root signature for a ray generation
    /// library[20], for any other[20], u32 payload, attributes, recursion depth, state object flags, then optionally the
    /// NVAPI state its collections are created with (<see cref="Nv"/>: u32 slot, space, options). Materialize turns it into
    /// the collection's 'R' record (<see cref="Collection"/>) once it has the library's bytes (its exports), and an 'N' for it.</summary>
    public static byte[] Item(string library, string global, string localRayGen, string localOther, Rule r, Nv? nv = null)
    {
        var b = new byte[nv == null ? 96 : 108];
        foreach (var (h, i) in new[] { library, global, localRayGen, localOther }.Select((h, i) => (h, i))) Convert.FromHexString(h).CopyTo(b, 20 * i);
        uint[] v = nv is { } x ? [r.Payload, r.Attributes, r.Depth, r.Flags, x.Slot, x.Space, x.Options] : [r.Payload, r.Attributes, r.Depth, r.Flags];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(80 + 4 * i), v[i]);
        return b;
    }

    /// <summary>The NVAPI state a game creates its collections with (from a recording's 'N' records, <see cref="NvState"/>).</summary>
    public readonly record struct Nv(uint Slot, uint Space, uint Options);

    public sealed record ItemFields(string Library, string Global, string LocalRayGen, string LocalOther, uint Payload, uint Attributes, uint Depth, uint Flags, Nv? Nv = null);

    public static ItemFields ParseItem(byte[] p)
    {
        string H(int i) => Hex(p.AsSpan(20 * i, 20));
        uint U(int i) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(80 + 4 * i));
        return new(H(0), H(1), H(2), H(3), U(0), U(1), U(2), U(3), p.Length >= 108 ? new Nv(U(4), U(5), U(6)) : null);
    }

    /// <summary>The NVAPI state a recording's collections share, from its 'N' records: the most common one when at least 99%
    /// of <paramref name="collections"/> carry one; null otherwise (the game sets none, or the recorder predates capturing it).</summary>
    public static Nv? LearnedNv(IReadOnlyCollection<Rec> collections, IEnumerable<Rec> records)
    {
        var keys = collections.Select(r => r.Key).ToHashSet();
        var nv = records.Where(r => r.Tag == 'N').Select(NvState.Parse).Where(n => keys.Contains(n.Target)).ToList();
        if (keys.Count == 0 || nv.Select(n => n.Target).Distinct().Count() < 0.99 * keys.Count) return null;
        return nv.GroupBy(n => new Nv(n.Slot, n.Space, n.Options)).MaxBy(g => g.Count())!.Key;
    }

    /// <summary>The payload a library's ray tracing functions declare (their largest; 0 for a ray generation library: it has none).</summary>
    public static uint OwnPayload(ReadOnlySpan<byte> library) =>
        (uint)ShaderContainer.Rdat(library).Functions.Where(f => Kinds.Any(k => k.Kind == f.Kind)).Select(f => f.Payload).DefaultIfEmpty(0).Max();

    /// <summary>The payloads a 'Y' item's collection is created with: its rule's (at least the library's own), or with rule payload 0
    /// the library's own, and for a library without one (ray generation) each payload <paramref name="pipelinePayloads"/> holds
    /// (the other libraries' own: a ray generation shader is compiled into a collection per pipeline payload, Hogwarts Legacy's
    /// 146 of them twice, 12 and 64 bytes).</summary>
    public static IEnumerable<uint> Payloads(uint rule, uint own, IReadOnlyCollection<uint> pipelinePayloads) =>
        rule > 0 || own > 0 || pipelinePayloads.Count == 0 ? [Math.Max(rule, own)] : pipelinePayloads;

    static readonly (int Kind, string Prefix)[] Kinds =[(7, "RayGen"), (11, "Miss"), (10, "CHS"), (9, "AHS"), (8, "IS"), (12, "Callable")];

    /// <summary>The 'R' payload of a library's collection (UE's order, see the class), or null when the library exports no
    /// ray tracing entry point. <paramref name="nameHash"/>: the 16 hex digits of the export names (UE's shader hash; by
    /// default the library's SHA-1's first 8 bytes, as UE prints its hash: little-endian).</summary>
    public static byte[]? Collection(ReadOnlySpan<byte> library, string sha1, string global, string localRayGen, string localOther,
        uint payload, uint attributes, uint depth, uint flags, string? nameHash = null)
    {
        var fns = ShaderContainer.Rdat(library).Functions.Where(f => Kinds.Any(k => k.Kind == f.Kind))
            .OrderBy(f => Array.FindIndex(Kinds, k => k.Kind == f.Kind)).ToList();
        if (fns.Count == 0) return null;
        nameHash ??= Convert.ToHexStringLower([.. Convert.FromHexString(sha1).AsSpan(0, 8).ToArray().Reverse()]);
        // export names must differ within a state object (DXR spec)
        var names = fns.Select((f, i) => $"{Kinds.First(k => k.Kind == f.Kind).Prefix}_{nameHash}" + (fns.Take(i).Count(g => g.Kind == f.Kind) is var n and > 0 ? $"_{n}" : ""))
            .ToList();
        string Export(ShaderContainer.LibraryFunction f) => names[fns.IndexOf(f)];
        var hit = fns.Where(f => f.Kind is 8 or 9 or 10).ToList();
        var rayGen = fns.Any(f => f.Kind == 7);
        var w = new W();
        var subs = 0u;
        w.U32(0).U32(0); // type: collection; subobject count, filled in below
        W Sub(uint t) { subs++; return w.U32(t); }
        Sub(5).Hash(sha1).U32((uint)fns.Count);
        foreach (var f in fns) w.Str(Export(f)).Str(f.Name).U32(0);
        Sub(9).U32(Math.Max(payload, (uint)fns.Max(f => f.Payload))).U32(Math.Max(attributes, (uint)fns.Max(f => f.Attributes)));
        Sub(7).U32(1).Names(fns.Select(Export));
        // ponytail: hit group k takes the k-th hit shader of each kind; the library doesn't say how the game pairs them
        for (var g = 0; g < hit.GroupBy(f => f.Kind).Select(k => k.Count()).DefaultIfEmpty(0).Max(); g++)
        {
            string? Of(int kind) => hit.Where(f => f.Kind == kind).ElementAtOrDefault(g) is { } f ? Export(f) : null;
            Sub(11).Str($"HitGroup_{nameHash}" + (g > 0 ? $"_{g}" : "")).U32(Of(8) != null ? 1u : 0).Str(Of(9)).Str(Of(10)).Str(Of(8));
        }
        Sub(10).U32(depth);
        if (flags != NoConfig) Sub(0).U32(flags);
        Sub(1).Hash(global);
        var local = subs;
        Sub(2).Hash(rayGen ? localRayGen : localOther);
        foreach (var f in fns) Sub(7).U32(local).Names([Export(f)]);
        var bytes = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), subs);
        return bytes;
    }

    /// <summary>A recorded collection in UE's shape (one library, both root signatures, the shader, pipeline and state object
    /// configs; <see cref="Collection"/>'s order), as the fields a rule needs and the 16 hex digits of its export names; null
    /// for anything else (a pipeline, an addition, another engine's shape).</summary>
    public sealed record Recorded(string Library, string Global, string Local, uint Payload, uint Attributes, uint Depth, uint Flags, string NameHash);

    public static Recorded? Read(Rec r)
    {
        if (r.Tag != 'R') return null;
        var p = r.Payload;
        var pos = 0;
        uint U() { var v = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(pos)); pos += 4; return v; }
        string H() { var h = Hex(p.AsSpan(pos, 20)); pos += 20; return h; }
        string? S() { var n = U(); if (n == uint.MaxValue) return null; var s = Encoding.Unicode.GetString(p, pos, 2 * (int)n); pos += 2 * (int)n; return s; }
        try
        {
            if (U() != 0) return null;
            string? lib = null, global = null, local = null, name = null;
            uint payload = 0, attr = 0, depth = 0, flags = NoConfig;
            for (var n = U(); n > 0; n--)
                switch (U())
                {
                    case 5:
                        if (lib != null) return null;
                        lib = H();
                        for (var e = U(); e > 0; e--) { var export = S(); name ??= export; S(); U(); } // not "name ??= S()": that skips reading the next exports
                        break;
                    case 9: payload = U(); attr = U(); break;
                    case 10: depth = U(); break;
                    case 0: flags = U(); break;
                    case 1: global = H(); break;
                    case 2: local = H(); break;
                    case 7: U(); for (var e = U(); e > 0; e--) S(); break;
                    case 11: S(); U(); S(); S(); S(); break;
                    default: return null;
                }
            if (pos != p.Length || lib == null || global == null || local == null || name?.LastIndexOf('_') is not (>= 0 and var u) || name.Length - u != 17) return null;
            return new(lib, global, local, payload, attr, depth, flags, name[(u + 1)..]);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    sealed class W
    {
        readonly MemoryStream s = new();
        public W U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); return this; }
        public W Hash(string hex) { s.Write(Convert.FromHexString(hex)); return this; }
        public W Str(string? v) { if (v == null) return U32(uint.MaxValue); U32((uint)v.Length); s.Write(Encoding.Unicode.GetBytes(v)); return this; }
        public W Names(IEnumerable<string> names) { var l = names.ToList(); U32((uint)l.Count); foreach (var n in l) Str(n); return this; }
        public byte[] ToArray() => s.ToArray();
    }

    /// <summary>A root signature's blob and hash (RootSig.Serialize; no static samplers in a local one).</summary>
    public static (string Hash, byte[] Blob) Serialize(RootSig.Desc d, byte[] samplers)
    {
        var b = RootSig.Serialize(d, samplers);
        return (Hex(SHA1.HashData(b)), b);
    }
}
