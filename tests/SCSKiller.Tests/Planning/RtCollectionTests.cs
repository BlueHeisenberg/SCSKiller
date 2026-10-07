using System.Diagnostics;
using System.Globalization;
using SCSKiller.Core.App;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>Ray tracing collections synthesized per DXIL library (<see cref="RtCollections"/>): only for a driver
/// that caches collections on their own (NVIDIA), with a rule learned from a recording's collections (checked byte for byte)
/// or, for Unreal 4.26/4.27 without a recording, UE 4.26's; a library whose resources the root signatures don't give is left
/// out. The Jedi: Survivor tests use the install and its recording on this machine (read-only) and return early without.</summary>
public class RtCollectionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly EngineInfo Ue427 = new("Unreal", "4.27", null, "D3D12", false, null);
    static readonly VendorCaps Nvidia = Ff7.Nvidia with { PerStageCache = true };
    static readonly ShaderInfo Vs = Shader("rt-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [In("SV_Position", 0, 0, 0xF, 3, 1)]);
    static ShaderInfo Lib(string name, params Binding[] b) => new(Hash(name), Stage.Library, "lib_6_3", 100, new ResourceCounts(2, 3, 0, 1), b, [], []);
    static readonly ShaderInfo Chs = Lib("rt-chs", new Binding("srv", 0, 0, 3), new Binding("sampler", 0, 0, 1), new Binding("cbv", 0, 0, 1), new Binding("cbv", 0, 1, 1), new Binding("srv", 2, 0, 1), new Binding("cbv", 2, 0, 1));
    static readonly ShaderInfo Bindless = Lib("rt-bindless", new Binding("srv", 3, 0, -1)); // a bindless range no rule has (spaces 4-9 are Avalanche's fork)

    static ShaderIndex Index() => new("rt", ["PCD3D_SM5"], new[] { Vs, Chs, Bindless }.ToDictionary(s => s.Sha1),
        [new ShaderMap("m", "Game", "PCD3D_SM5", [Vs.Sha1, Chs.Sha1, Bindless.Sha1])]);

    static List<RtCollections.ItemFields> Items(Plan p) => PlanFile.Read(p.FilePath).Records.Where(r => r.Tag == 'Y').Select(r => RtCollections.ParseItem(r.Payload)).ToList();

    /// <summary>Kuro's 4.26 fork (Wuthering Waves): its own global root signature (space 77 then UE 4.26's space 1, NVAPI's slot
    /// u0 space 1001 last), each library's own payload, and the NVAPI slot set on every collection, all without a recording.</summary>
    [Fact]
    public void KuroForkHasItsOwnCollectionRule()
    {
        var dir = Ff7.TempDir("rt-plan-kuro");
        var wuwa = Ue427 with { Version = "4.26", Fork = "GAME_WutheringWaves" };
        var plan = new Planner().Build(Ff7.Game, wuwa, Index(), null, Nvidia, Path.Combine(dir, "nv"), new Log(output.WriteLine), CancellationToken.None);
        var y = Assert.Single(Items(plan)); // the bindless library is left out: space 4 isn't in the fork's global root signature either
        var (global, blob) = RtCollections.Serialize(RtCollections.WuwaGlobal, RootSig.Ue426Samplers);
        Assert.Equal(global, y.Global);
        Assert.Equal((Chs.Sha1, 0u, 8u, 1u, 4u, new RtCollections.Nv(0, 1001, 0)), (y.Library, y.Payload, y.Attributes, y.Depth, y.Flags, y.Nv!.Value));
        var ranges = RootSig.Parse(blob);
        Assert.Equal(0u, ranges.Flags);
        Assert.Equal(24 + 6, ranges.Slots.Length); // 4 in space 77, 19 in space 1, the NVAPI table, 6 static samplers
        Assert.Equal((0u, 1u, 0u, 16u, 77u, true), ranges.Slots[0]);  // UAV table u0-u15 space 77 first
        Assert.Equal((0u, 2u, 2u, 1u, 77u, false), ranges.Slots[2]);  // root CBV b2 space 77 before b1
        Assert.Equal((0u, 1u, 0u, 1u, 1001u, true), ranges.Slots[23]); // NVAPI u0 space 1001 as a table
        if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(blob));
    }

    [Fact]
    public void CollectionsOnlyWhereTheDriverCachesThem()
    {
        var dir = Ff7.TempDir("rt-plan");
        var nv = new Planner().Build(Ff7.Game, Ue427, Index(), null, Nvidia, Path.Combine(dir, "nv"), new Log(output.WriteLine), CancellationToken.None);
        var y = Assert.Single(Items(nv)); // the bindless library is left out
        Assert.Equal(Chs.Sha1, y.Library);
        Assert.Equal((32u, 8u, 1u, 4u), (y.Payload, y.Attributes, y.Depth, y.Flags));
        var blobs = PlanFile.Read(nv.FilePath).Records.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        Assert.Equal(RtCollections.Serialize(RtCollections.Ue426Global, RootSig.Ue426Samplers).Hash, y.Global);
        var local = RootSig.Parse(blobs[y.LocalOther]);
        Assert.Equal(0x80u, local.Flags); // LOCAL_ROOT_SIGNATURE
        Assert.Null(RootSig.Uncovered(new RootSig.Ranges(0, [.. RootSig.Parse(blobs[y.Global]).Slots, .. local.Slots]), Stage.Library, Chs));
        Assert.Empty(RootSig.Parse(blobs[y.LocalRayGen]).Slots);
        Assert.Equal(nv.Stats.Generated, PlanFile.Read(nv.FilePath).Records.Count(r => r.Tag is 'S' or 'P' or 'Y'));

        Assert.Equal((2L, 1L), (nv.Stats.RtLibraries, nv.Stats.RtUncovered)); // the rt-bindless library is left to a recording

        // AMD caches whole objects only; UE 5's rule needs 5.1's binding shape, which these libraries lack: every library is left to a recording
        var amd = new Planner().Build(Ff7.Game, Ue427, Index(), null, Ff7.Amd, Path.Combine(dir, "amd"), null, CancellationToken.None);
        var ue5 = new Planner().Build(Ff7.Game, Ue427 with { Version = "5.2" }, Index(), null, Nvidia, Path.Combine(dir, "ue5"), null, CancellationToken.None);
        // r.RayTracing.AllowPipeline=0: a collection would never be linked
        var off = new Planner().Build(Ff7.Game, Ue427 with { NoRtPipelines = true }, Index(), null, Nvidia, Path.Combine(dir, "off"), null, CancellationToken.None);
        Assert.Empty(Items(off));
        Assert.Equal((2L, 0L), (off.Stats.RtLibraries, off.Stats.RtUncovered));
        Assert.Empty(Items(amd));
        Assert.Empty(Items(ue5));
        Assert.Equal((2L, 2L), (ue5.Stats.RtLibraries, ue5.Stats.RtUncovered));
        var db = Path.Combine(dir, "rec.db");
        using (var f = File.Create(db))
        {
            var rs = RootSig.Serialize(RootSig.Build(RootSig.Rule.Ue426, new Dictionary<Stage, ShaderInfo> { [Stage.Vertex] = Vs }, false), RootSig.Ue426Samplers);
            var h = Hex(System.Security.Cryptography.SHA1.HashData(rs));
            WriteBlob(f, h, rs);
            var s = Stream(h, new SortedDictionary<int, string> { [(int)Stage.Vertex] = Vs.Sha1 }, Planner.VsLayout(Vs), 3, [], D32Float);
            Write(f, 'S', s);
        }
        // a recording without state objects (ray tracing off as played): the no-recording rule, as without one
        Assert.Single(Items(new Planner().Build(Ff7.Game, Ue427, Index(), new Recording(db), Nvidia, Path.Combine(dir, "rec"), null, CancellationToken.None)));
    }

    /// <summary>The uncovered count is what neither a synthesized collection nor a recorded state object compiles, also when
    /// the recording has ray tracing: on AMD (nothing synthesized) a recorded pipeline with one of the two libraries leaves
    /// the other uncovered. The game then doesn't need a recording for its ray tracing: it has one.</summary>
    [Fact]
    public void UncoveredLibrariesAreThePlansGapWithARecordingToo()
    {
        var dir = Ff7.TempDir("rt-gap");
        var db = Path.Combine(dir, "rec.db");
        var (rs, blob) = RtCollections.Serialize(RtCollections.Ue426Global, RootSig.Ue426Samplers);
        using (var f = File.Create(db))
        {
            WriteBlob(f, rs, blob);
            var so = new MemoryStream();
            var w = new BinaryWriter(so);
            w.Write(3u); w.Write(2u);
            w.Write(1u); w.Write(Convert.FromHexString(rs));
            w.Write(5u); w.Write(Convert.FromHexString(Chs.Sha1)); w.Write(0u);
            Write(f, 'R', so.ToArray());
        }
        var amd = new Planner().Build(Ff7.Game, Ue427, Index(), new Recording(db), Ff7.Amd, Path.Combine(dir, "amd"), null, CancellationToken.None);
        Assert.Equal((2L, 1L, 1L), (amd.Stats.RtLibraries, amd.Stats.RtUncovered, amd.Stats.RtStateObjects)); // the bindless library: in no state object
        Assert.False(ScsKiller.NeedsRtRecording(amd.Stats));
        // what the plan counts as replayable comes out of Materialize: the library from the install, the recording without it
        long Replayed(Plan plan, string db, string name)
        {
            var work = Path.Combine(dir, name);
            new Planner().Materialize(plan, Ff7.Game, Ue427, new Shaders(new() { [Chs.Sha1] = HitLib }), new Recording(db), work, CancellationToken.None);
            var main = Read(Path.Combine(work, "scskiller.db")).ToList();
            Assert.All(main.Where(r => IsStateObject(r.Tag)).SelectMany(r => Core.Planning.Rehydrate.References([r])),
                h => Assert.Contains(main.Concat(Read(Path.Combine(work, "scskiller_gen.db"))), r => r.Tag == 'B' && Hex(r.Payload.AsSpan(0, 20)) == h));
            return main.Count(r => IsStateObject(r.Tag));
        }
        Assert.Equal(amd.Stats.RtStateObjects, Replayed(amd, db, "amd-work"));
        var none = new Planner().Build(Ff7.Game, Ue427, Index(), null, Ff7.Amd, Path.Combine(dir, "none"), null, CancellationToken.None);
        Assert.Equal((2L, 2L, 0L), (none.Stats.RtLibraries, none.Stats.RtUncovered, none.Stats.RtStateObjects));
        Assert.True(ScsKiller.NeedsRtRecording(none.Stats));

        // a base naming a library neither the recording nor the install has, and an addition on it with the indexed one: the
        // warm replays neither, so neither covers anything
        var shared = Path.Combine(dir, "shared.db");
        var runtime = new string('7', 40);
        byte[] So(string lib, string? on)
        {
            var so = new MemoryStream();
            var w = new BinaryWriter(so);
            if (on != null) w.Write(Convert.FromHexString(on));
            w.Write(3u); w.Write(2u);
            w.Write(1u); w.Write(Convert.FromHexString(rs));
            w.Write(5u); w.Write(Convert.FromHexString(lib)); w.Write(0u);
            return so.ToArray();
        }
        using (var f = File.Create(shared))
        {
            WriteBlob(f, rs, blob);
            var b = new Rec('R', So(runtime, null));
            Write(f, b.Tag, b.Payload);
            Write(f, 'A', So(Chs.Sha1, b.Key));
        }
        var gone = new Planner().Build(Ff7.Game, Ue427, Index(), new Recording(shared), Ff7.Amd, Path.Combine(dir, "gone"), null, CancellationToken.None);
        Assert.Equal((2L, 2L, 0L), (gone.Stats.RtLibraries, gone.Stats.RtUncovered, gone.Stats.RtStateObjects));
        Assert.Equal(0, Replayed(gone, shared, "gone-work"));
        Assert.True(ScsKiller.NeedsRtRecording(gone.Stats));
    }

    /// <summary>A DXIL container with just an SFI0 part of these feature flags.</summary>
    internal static byte[] Sfi0(ulong flags)
    {
        byte[] part = [.. "SFI0"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(flags)];
        return [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36 + part.Length), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36), .. part];
    }

    /// <summary>A game whose ray tracing is inline (RayQuery in compute and pixel shaders) ships DXIL libraries it never builds a
    /// state object from: a recording that traces rays inline and has no state object leaves none to a recording. Without a
    /// recording, or with one without ray tracing (off as played), UE 5.6 (no collection rule) leaves them all.</summary>
    [Fact]
    public void InlineRayTracingLeavesNoLibraryToARecording()
    {
        var dir = Ff7.TempDir("rt-inline");
        var ue56 = Ue427 with { Version = "5.6" };
        (long, long) Rt(string name, ShaderIndex index, params byte[][] shaders)
        {
            Recording? rec = null;
            if (shaders.Length > 0)
            {
                var db = Path.Combine(dir, name + ".db");
                using (var f = File.Create(db))
                    foreach (var c in shaders)
                    {
                        var h = Hex(System.Security.Cryptography.SHA1.HashData(c));
                        WriteBlob(f, h, c);
                        Write(f, 'C', Compute(Hash("rs"), h));
                    }
                rec = new Recording(db);
            }
            var s = new Planner().Build(Ff7.Game, ue56, index, rec, Nvidia, Path.Combine(dir, name), new Log(output.WriteLine), CancellationToken.None).Stats;
            return (s.RtLibraries, s.RtUncovered);
        }
        const ulong RayQuery = 0x100000, WaveOps = 0x4000;
        Assert.True(Core.Carved.Dxbc.InlineRayTracing(Sfi0(RayQuery | WaveOps)));
        Assert.False(Core.Carved.Dxbc.InlineRayTracing(Sfi0(WaveOps)));
        Assert.Equal((2L, 2L), Rt("none", Index()));
        Assert.Equal((2L, 2L), Rt("off", Index(), Sfi0(WaveOps)));
        Assert.Equal((2L, 0L), Rt("inline", Index(), Sfi0(WaveOps), Sfi0(RayQuery)));
        var noLibraries = new ShaderIndex("rt", ["PCD3D_SM5"], new Dictionary<string, ShaderInfo> { [Vs.Sha1] = Vs }, [new ShaderMap("m", "Game", "PCD3D_SM5", [Vs.Sha1])]);
        Assert.Equal((0L, 0L), Rt("inline-only", noLibraries, Sfi0(RayQuery)));
    }

    /// <summary>Unreal 5 whose game files have shaders that trace rays inline (hardware Lumen) counts them: its DXIL
    /// libraries, still uncovered, then don't need a recording. Unreal 4 and a state object recorded keep the DXR rule.</summary>
    [Fact]
    public void Unreal5InlineShadersInTheGameFilesCount()
    {
        var dir = Ff7.TempDir("rt-ue5-inline");
        var cs = new ShaderInfo(Hash("rq-cs"), Stage.Compute, "cs_6_5", 100, new ResourceCounts(1, 1, 1, 0), [], [], [], InlineRayTracing: true);
        var index = new ShaderIndex("rt", ["PCD3D_SM5"], new[] { Vs, Chs, Bindless, cs }.ToDictionary(s => s.Sha1),
            [new ShaderMap("m", "Game", "PCD3D_SM5", [Vs.Sha1, Chs.Sha1, Bindless.Sha1]), new ShaderMap("c", "Game", "PCD3D_SM5", [cs.Sha1])]);
        PlanStats Stats(EngineInfo e, string name) => new Planner().Build(Ff7.Game, e, index, null, Nvidia, Path.Combine(dir, name), null, CancellationToken.None).Stats;
        var ue56 = Stats(Ue427 with { Version = "5.6" }, "ue56");
        Assert.Equal((2L, 2L, (long?)1), (ue56.RtLibraries, ue56.RtUncovered, ue56.RtInline));   // the page's count stays
        Assert.Equal((false, true), (ScsKiller.NeedsRtRecording(ue56), ScsKiller.RtInlineCovers(ue56)));
        Assert.True(ScsKiller.NeedsRtRecording(ue56 with { RtInline = 0 }));
        Assert.False(ScsKiller.NeedsRtRecording(ue56 with { RtStateObjects = 1 }));   // a recording with a state object: the DXR path
        var ue427 = Stats(Ue427 with { Version = "4.27" }, "ue427");
        Assert.Equal((long?)0, ue427.RtInline);
        Assert.False(ScsKiller.RtInlineCovers(ue427));

        // r.RayTracing.AllowPipeline=0: no state object is ever built, the libraries are counted but uncovered by none
        var off = Stats(Ue427 with { Version = "5.6", NoRtPipelines = true }, "off");
        Assert.Equal((2L, 0L), (off.RtLibraries, off.RtUncovered));
        Assert.Equal((false, false), (ScsKiller.NeedsRtRecording(off), ScsKiller.RtInlineCovers(off)));
    }

    /// <summary>The Windows device profile's r.RayTracing.AllowPipeline from the configs in the paks (SILENT HILL: Townfall's
    /// Townfall/Platforms/Windows/Config/WindowsDeviceProfiles.ini), the project's over the engine's.</summary>
    [Fact]
    public void RayTracingPipelinesOffInTheWindowsDeviceProfile()
    {
        static string Ini(params string[] lines) => string.Join(Environment.NewLine, lines);
        var townfall = Ini("[Windows DeviceProfile]", "DeviceType=Windows", "+CVars=r.RayTracing.AllowPipeline=0", "");
        bool Off(params (string Path, string Text)[] files) => UnrealRhi.RtPipelinesOff(files.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase), "Townfall");
        const string platform = "Townfall/Platforms/Windows/Config/WindowsDeviceProfiles.ini", project = "Townfall/Config/DefaultDeviceProfiles.ini", engine = "Engine/Config/BaseDeviceProfiles.ini";
        Assert.True(UnrealRhi.IsConfig(platform, "Townfall"));
        Assert.True(Off((platform, townfall)));
        Assert.False(Off());
        Assert.False(Off((project, Ini("[WindowsNoEditor DeviceProfile]", "+CVars=r.RayTracing.AllowPipeline=0"))));   // another profile
        Assert.False(Off((engine, townfall), (project, Ini("[Windows DeviceProfile]", "+CVars=r.RayTracing.AllowPipeline=1"))));
        Assert.False(Off((engine, townfall), (project, Ini("[Windows DeviceProfile]", "-CVars=r.RayTracing.AllowPipeline=0"))));
        Assert.False(Off((platform, townfall + Ini("!CVars=ClearArray"))));
        Assert.True(Off((project, Ini("[Windows DeviceProfile]", "+CVars=r.RayTracing.AllowPipeline=1")), (platform, townfall)));   // the platform file comes last
    }

    /// <summary>r.RayTracing off in [/Script/Engine.RendererSettings] (DRAGON BALL: Sparking! ZERO's DefaultEngine.ini): off
    /// only when the last of the Engine ini hierarchy and the user's Engine.ini says off and nothing that outranks it says
    /// anything else; unset, on or unclear anywhere is not off.</summary>
    [Fact]
    public void RayTracingOffOnlyWhenEveryReadableSourceSaysOff()
    {
        static string Ini(params string[] lines) => string.Join(Environment.NewLine, lines);
        const string P = "SparkingZERO";
        const string baseEngine = "Engine/Config/BaseEngine.ini", basePlatform = "Engine/Platforms/Windows/Config/BaseWindowsEngine.ini",
            project = P + "/Config/DefaultEngine.ini", enginePlatform = "Engine/Platforms/Windows/Config/WindowsEngine.ini",
            projectWindows = P + "/Config/Windows/WindowsEngine.ini", projectPlatform = P + "/Platforms/Windows/Config/WindowsEngine.ini",
            consoleVariables = P + "/Config/ConsoleVariables.ini", profiles = P + "/Config/DefaultDeviceProfiles.ini";
        static string Renderer(string v) => Ini("[/Script/Engine.RendererSettings]", "r.SkinCache.CompileShaders=True", $"r.RayTracing={v}", "");
        bool Off(string? user, string launch, params (string Path, string Text)[] files) =>
            UnrealRhi.RayTracingOff(files.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase), P, user, launch);
        bool Project(string v, params (string Path, string Text)[] more) => Off(null, "", [(project, Renderer(v)), .. more]);

        foreach (var path in new[] { basePlatform, enginePlatform, projectWindows, projectPlatform, consoleVariables })
            Assert.True(UnrealRhi.IsConfig(path, P), path);

        Assert.True(Project("False"));
        Assert.True(Project("0", (profiles, Ini("[Windows DeviceProfile]", "+CVars=r.RayTracing.AllowPipeline=1"))));
        Assert.False(Project("True"));
        Assert.False(Off(null, "", (project, Ini("[/Script/Engine.RendererSettings]", "r.SkinCache.CompileShaders=True"))));   // unset
        Assert.False(Off(null, "", (project, Ini("[SystemSettings]", "r.RayTracing=0"))));   // no renderer setting

        // the hierarchy: base, project default, Windows platform layers (both layouts), the last that sets it wins
        Assert.False(Off(null, "", (basePlatform, Renderer("False")), (project, Renderer("True"))));
        Assert.True(Off(null, "", (baseEngine, Renderer("True")), (project, Renderer("False"))));
        Assert.False(Project("False", (enginePlatform, Renderer("1"))));
        Assert.False(Project("False", (projectWindows, Renderer("1"))));
        Assert.False(Project("False", (projectPlatform, Renderer("True"))));
        Assert.True(Project("True", (projectPlatform, Renderer("False"))));

        // what outranks the renderer setting: on or unclear there is not off
        Assert.False(Off(null, "", (project, Renderer("False") + Ini("[SystemSettings]", "r.RayTracing=1"))));
        Assert.True(Off(null, "", (project, Renderer("False") + Ini("[SystemSettings]", "r.RayTracing=0"))));
        Assert.False(Off(null, "", (project, Renderer("False") + Ini("[SystemSettings]", "r.RayTracing=2"))));
        Assert.False(Off(null, "", (baseEngine, Ini("[ConsoleVariables]", "r.RayTracing=True")), (project, Renderer("False"))));
        Assert.False(Project("False", (consoleVariables, Ini("[Startup]", "r.RayTracing=1"))));
        Assert.False(Project("False", (profiles, Ini("[Windows DeviceProfile]", "+CVars=r.RayTracing=1"))));
        Assert.True(Project("False", (profiles, Ini("[Windows DeviceProfile]", "+CVars=r.RayTracing.AllowPipeline=1"))));
        Assert.False(Off(null, "-dx12 -ini:Engine:[SystemSettings]:r.RayTracing=1", (project, Renderer("False"))));
        Assert.True(Off(null, "-dx12 -ExecCmds=\"r.RayTracing.AllowPipeline 1\"", (project, Renderer("False"))));

        // the user's Saved config comes last
        var saved = Ff7.TempDir("rt-user");
        var config = Directory.CreateDirectory(Path.Combine(saved, "Config", "Windows")).FullName;
        Assert.True(Off(saved, "", (project, Renderer("False"))));
        File.WriteAllText(Path.Combine(config, "Engine.ini"), Renderer("True"));
        Assert.False(Off(saved, "", (project, Renderer("False"))));
        File.WriteAllText(Path.Combine(config, "Engine.ini"), Renderer("False"));
        Assert.True(Off(saved, "", (project, Renderer("True"))));
        File.WriteAllText(Path.Combine(config, "Engine.ini"), Renderer("False") + Ini("[SystemSettings]", "r.RayTracing=1"));
        Assert.False(Off(saved, "", (project, Renderer("False"))));
        File.WriteAllText(Path.Combine(config, "Engine.ini"), Renderer("False"));
        File.WriteAllText(Path.Combine(config, "DeviceProfiles.ini"), Ini("[Windows DeviceProfile]", "+CVars=r.RayTracing=1"));
        Assert.False(Off(saved, "", (project, Renderer("False"))));
    }

    /// <summary>UE 4.26's local root signature: none for a ray generation shader; the hit group system parameters, then the
    /// shader's tables and root CBVs.</summary>
    [Fact]
    public void LocalRootSignatures()
    {
        Assert.Empty(RtCollections.LocalRs(new(3, 5, 1, 2), rayGen: true).Rows);
        var rows = RtCollections.LocalRs(new(2, 5, 1, 3), rayGen: false).Rows;
        Assert.Equal(["3,0,0,2,0", "3,0,1,2,0", "1,0,0,2,4", "0,0,0,5,0,0,5", "0,0,3,3,0,0,1", "0,0,1,1,0,0,3", "2,0,0,0,8", "2,0,1,0,8"], rows.Select(r => string.Join(',', r)));
        Assert.Equal(3, RtCollections.LocalRs(new(0, 0, 0, 0), rayGen: false).Rows.Count); // a miss shader: the system parameters only
        Assert.Equal("1,0,0,2,6", string.Join(',', RtCollections.LocalRs(new(0, 0, 0, 0), rayGen: false, systemConstants: 6).Rows[2])); // UE 5.1
    }

    // UE 5.0-5.4's binding shape: a ray generation library's uniform buffers in space 1, a hit library's index and vertex buffers t0/t1 in space 2
    static readonly ShaderInfo Rgs = Lib("rt-rgs", new Binding("cbv", 1, 0, 1), new Binding("cbv", 1, 1, 1), new Binding("srv", 1, 0, 5), new Binding("uav", 1, 0, 1));
    static readonly ShaderInfo Hit5 = Lib("rt-hit5", new Binding("srv", 0, 0, 3), new Binding("sampler", 0, 0, 1), new Binding("cbv", 0, 0, 1), new Binding("cbv", 0, 1, 1),
        new Binding("srv", 2, 0, 1), new Binding("srv", 2, 1, 1), new Binding("cbv", 2, 0, 1));
    // UE 5.6's bindless ray tracing (SILENT HILL: Townfall): CBVs only, shared uniform buffers in space 4, heap access flagged
    static readonly ShaderInfo Hit56 = new(Hash("rt-hit56"), Stage.Library, "lib_6_6", 100,
        new ResourceCounts(2, 0, 0, 0, ShaderContainer.UeFlags.BindlessResources | ShaderContainer.UeFlags.BindlessSamplers),
        [new Binding("cbv", 0, 0, 1), new Binding("cbv", 2, 0, 1), new Binding("cbv", 4, 0, 1), new Binding("cbv", 4, 1, 1)], [], []);

    static ShaderIndex Ue5Index(params ShaderInfo[] libs) => new("rt", ["PCD3D_SM6"], libs.Prepend(Vs).ToDictionary(s => s.Sha1),
        [new ShaderMap("m", "Game", "PCD3D_SM6", [Vs.Sha1, .. libs.Select(l => l.Sha1)])]);

    [Fact]
    public void Ue5BindingShape()
    {
        Assert.Null(RtCollections.Ue5ShapeMismatch([Rgs, Hit5, Bindless]));
        Assert.Null(RtCollections.Ue5ShapeMismatch([Rgs, Hit5 with { Bindings = [.. Hit5.Bindings.Take(4), new Binding("srv", 2, 0, 2), new Binding("cbv", 2, 0, 1)] }])); // t0-t1 as one range
        Assert.Equal("no hit group index and vertex buffers (t0/t1 space 2)", RtCollections.Ue5ShapeMismatch([Rgs, Chs])); // t0 only
        Assert.Equal("no uniform buffers in space 1", RtCollections.Ue5ShapeMismatch([Hit5]));
        Assert.Equal("bindless descriptor heap access", RtCollections.Ue5ShapeMismatch([Rgs, Hit5, Hit56]));
        Assert.Equal("bindless descriptor heap access", RtCollections.Ue5ShapeMismatch([Rgs, Hit5, Hit5 with { Sha1 = Hash("rt-heap"), Counts = Hit5.Counts with { Flags = ShaderContainer.UeFlags.BindlessResources } }]));
        Assert.Equal("shared uniform buffers in space 4", RtCollections.Ue5ShapeMismatch([Rgs, Hit5, Hit56 with { Counts = Hit56.Counts with { Flags = 0 } }]));
    }

    /// <summary>UE 5.0-5.4 without a recording, for libraries of 5.1's binding shape: <see cref="RtCollections.Ue51Global"/>
    /// (5.4: <see cref="RtCollections.Ue54Global"/>, its 32 samplers), 6 system constants, each library's own payload.</summary>
    [Theory]
    [InlineData("5.0")]
    [InlineData("5.1")]
    [InlineData("5.3")]
    [InlineData("5.4")]
    public void Ue5CollectionsWithoutARecording(string version)
    {
        var dir = Ff7.TempDir("rt-ue5-" + version);
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue427 with { Version = version }, Ue5Index(Rgs, Hit5, Bindless), null, Nvidia, dir, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        var ys = Items(plan).ToDictionary(y => y.Library); // the bindless library is left out
        Assert.Equal([Rgs.Sha1, Hit5.Sha1], ys.Keys.Order());
        var y = ys[Hit5.Sha1];
        Assert.Equal((0u, 8u, 1u, 4u), (y.Payload, y.Attributes, y.Depth, y.Flags));
        var desc = version == "5.4" ? RtCollections.Ue54Global : RtCollections.Ue51Global;
        var (global, blob) = RtCollections.Serialize(desc, RootSig.Ue426Samplers);
        Assert.Equal(global, y.Global);
        Assert.Equal(["0,0,0,64,0,1,5", $"0,0,3,{(version == "5.4" ? 32 : 16)},0,1,1", "0,0,1,16,0,1,3", "2,0,0,1,8"], desc.Rows.Take(4).Select(r => string.Join(',', r)));
        Assert.Equal("4,0,0,999,2", string.Join(',', desc.Rows[^1]));
        if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(blob));
        Assert.Equal(RtCollections.Serialize(RtCollections.LocalRs(Hit5.Counts, false, Hit5.Bindings, 6), []).Hash, y.LocalOther);
        Assert.Equal((3L, 1L), (plan.Stats.RtLibraries, plan.Stats.RtUncovered));
        Assert.Contains(log, l => l.Contains(version == "5.1" ? "Oblivion Remastered's recording" : $"unverified for {version}"));
    }

    /// <summary>Libraries not of 5.1's binding shape (5.4 bindless, a 5.1 game whose hit libraries bind no index buffers) and
    /// UE 5.5/5.6 (bindless ray tracing, no rule): nothing synthesized, every library left to a recording, the log says why.</summary>
    [Theory]
    [InlineData("5.4", "bindless descriptor heap access")]
    [InlineData("5.1", "no hit group index and vertex buffers")]
    [InlineData("5.5", "no collection rule for Unreal 5.5")]
    [InlineData("5.6", "no collection rule for Unreal 5.6")]
    public void NoUe5CollectionsForAnotherShape(string version, string why)
    {
        var log = new List<string>();
        ShaderInfo[] libs = version == "5.1" ? [Rgs, Chs] : [Rgs, Hit5, Hit56];
        var plan = new Planner().Build(Ff7.Game, Ue427 with { Version = version }, Ue5Index(libs), null, Nvidia, Ff7.TempDir("rt-ue5-off-" + version), new Log(log.Add), CancellationToken.None);
        Assert.Empty(Items(plan));
        Assert.Equal((libs.Length, libs.Length), ((int)plan.Stats.RtLibraries, (int)plan.Stats.RtUncovered));
        Assert.Contains(log, l => l.StartsWith("ray tracing") && l.Contains(why) && l.Contains("none synthesized"));
    }

    /// <summary>A DXIL library container with just an RDAT part (a string buffer and a function table): what
    /// <see cref="ShaderContainer.Rdat"/> reads, enough for <see cref="RtCollections.Collection"/>.</summary>
    internal static byte[] Library(params (int Kind, string Name, int Payload)[] fns)
    {
        var strings = new List<byte>();
        uint Str(string s) { var at = (uint)strings.Count; strings.AddRange([.. System.Text.Encoding.ASCII.GetBytes(s), 0]); return at; }
        var table = new List<byte>(BitConverter.GetBytes(fns.Length)) { };
        table.AddRange(BitConverter.GetBytes(28));
        foreach (var f in fns)
        {
            var n = Str(f.Name);
            foreach (var v in new uint[] { n, n, 0, 0, (uint)f.Kind, (uint)f.Payload, f.Kind == 7 || f.Kind == 11 ? 0u : 8 }) table.AddRange(BitConverter.GetBytes(v));
        }
        byte[] Part(uint type, List<byte> data) => [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes(data.Count), .. data];
        byte[] a = Part(1, strings), b = Part(4, table);
        byte[] rdat = [.. BitConverter.GetBytes(0x10), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(16), .. BitConverter.GetBytes(16 + a.Length), .. a, .. b];
        byte[] part = [.. "RDAT"u8, .. BitConverter.GetBytes(rdat.Length), .. rdat];
        return [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36 + part.Length), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36), .. part];
    }

    static readonly byte[] HitLib = Library((10, "MaterialCHS", 64), (9, "MaterialAHS", 64)), ShadowLib = Library((10, "ShadowCHS", 12)), RayGenLib = Library((7, "RayGen", 0));

    /// <summary>Elden Ring's collection of a material pair (<see cref="SCSKiller.Core.FromSoft.SoulsRayTracing.Collection"/>):
    /// the closest hit first whichever library the item names first, both root signatures, and the byte layout its recorded
    /// collections have (pinned); null unless one library is a closest hit and the other an any hit.</summary>
    [Fact]
    public void EldenRingCollectionIsTheGamesShape()
    {
        byte[] ch = Library((10, "ClosestHit", 4)), ah = Library((9, "AnyHit", 4));
        string chSha = Hex(System.Security.Cryptography.SHA1.HashData(ch)), ahSha = Hex(System.Security.Cryptography.SHA1.HashData(ah));
        var souls = SCSKiller.Core.FromSoft.SoulsRayTracing.Shape;
        var item = SCSKiller.Core.RedEngine.RedRayTracing.ParseItem(SCSKiller.Core.RedEngine.RedRayTracing.Item(ahSha, chSha, SCSKiller.Core.FromSoft.SoulsRayTracing.Local.Hash, souls, null));
        var so = SCSKiller.Core.FromSoft.SoulsRayTracing.Collection(ah, item, ch, "Mat")!;
        var parsed = ParseStateObject(new Rec('R', so));
        Assert.Equal(0u, parsed.Type);
        Assert.Equal([chSha, ahSha], parsed.Libraries);
        Assert.Equal([souls.Global, item.Local], parsed.RootSignatures);
        var text = System.Text.Encoding.Unicode.GetString(so);
        Assert.Contains("Mat_RayTracing_[ClosestHit]_[AO]", text);
        Assert.Contains("HitGroup_Mat_[AO]_[A]", text);
        Assert.Equal("522e9ed9136b1272cc21459abeb883a4a24171e9", new Rec('R', so).Key);
        Assert.Null(SCSKiller.Core.FromSoft.SoulsRayTracing.Collection(ch, item, ch));
    }

    /// <summary>UE 4.25 creates its collections without a state object config (Returnal: 403 of 403). Such a recorded
    /// collection reads back as having none, rebuilds byte for byte, and the rule learned from it gives the libraries no
    /// state object has collections without one.</summary>
    [Fact]
    public void CollectionsWithoutAStateObjectConfigRebuild()
    {
        var recorded = Lib("rt425-recorded");
        var other = Lib("rt425-other");
        var lib = Library((10, "DefaultMainCHS", 24));
        var (local, localBlob) = RtCollections.Serialize(RtCollections.LocalRs(recorded.Counts, false, recorded.Bindings), []);
        var (global, globalBlob) = RtCollections.Serialize(RtCollections.Ue426Global, RootSig.Ue426Samplers);
        const string name = "7ec183cab5332e20";
        var so = new MemoryStream();
        var w = new BinaryWriter(so);
        void Str(string? v) { if (v == null) { w.Write(uint.MaxValue); return; } w.Write((uint)v.Length); w.Write(System.Text.Encoding.Unicode.GetBytes(v)); }
        w.Write(0u); w.Write(8u);
        w.Write(5u); w.Write(Convert.FromHexString(recorded.Sha1)); w.Write(1u); Str($"CHS_{name}"); Str("DefaultMainCHS"); w.Write(0u);
        w.Write(9u); w.Write(24u); w.Write(8u);
        w.Write(7u); w.Write(1u); w.Write(1u); Str($"CHS_{name}");
        w.Write(11u); Str($"HitGroup_{name}"); w.Write(0u); Str(null); Str($"CHS_{name}"); Str(null);
        w.Write(10u); w.Write(1u);
        w.Write(1u); w.Write(Convert.FromHexString(global));
        w.Write(2u); w.Write(Convert.FromHexString(local));
        w.Write(7u); w.Write(6u); w.Write(1u); Str($"CHS_{name}");
        var rec = new Rec('R', so.ToArray());

        var c = RtCollections.Read(rec)!;
        Assert.Equal(RtCollections.NoConfig, c.Flags);
        Assert.Equal(rec.Payload, RtCollections.Collection(lib, c.Library, global, local, local, c.Payload, c.Attributes, c.Depth, c.Flags, c.NameHash));

        var dir = Ff7.TempDir("rt-no-config");
        var db = Path.Combine(dir, "rec.db");
        using (var f = File.Create(db))
        {
            WriteBlob(f, global, globalBlob);
            WriteBlob(f, local, localBlob);
            WriteBlob(f, recorded.Sha1, lib);
            Write(f, rec.Tag, rec.Payload);
        }
        var index = new ShaderIndex("rt425", ["PCD3D_SM5"], new[] { recorded, other }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", [recorded.Sha1, other.Sha1])]);
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue427 with { Version = "4.25" }, index, new Recording(db), Nvidia, Path.Combine(dir, "plan"), new Log(log.Add), CancellationToken.None);
        Assert.Contains(log, l => l.Contains("1/1 rebuilt byte for byte"));
        Assert.Equal([(other.Sha1, RtCollections.NoConfig)], Items(plan).Select(y => (y.Library, y.Flags)));
    }

    /// <summary>UE 4.25 without a recording: 4.26's global root signature without the NVAPI slot, with 4.25's static samplers
    /// (s1000-s1005, space 0), no state object config, payload 64 (Returnal's recording: 401 of its 403 collections rebuilt).</summary>
    [Fact]
    public void Ue425CollectionsWithoutARecording()
    {
        var dir = Ff7.TempDir("rt-425");
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue427 with { Version = "4.25" }, Index(), null, Nvidia, Path.Combine(dir, "plan"), new Log(log.Add), CancellationToken.None);
        var (global, blob) = RtCollections.Serialize(RtCollections.Ue425Global, RootSig.StaticSamplers(RootSig.Rule.Ue425));
        var y = Items(plan).Single(i => i.Library == Chs.Sha1);
        Assert.Equal((global, 64u, 8u, 1u, RtCollections.NoConfig), (y.Global, y.Payload, y.Attributes, y.Depth, y.Flags));
        Assert.Equal("0,0,0,64,0,1,5", string.Join(',', RtCollections.Ue425Global.Rows[0]));
        Assert.Contains(log, l => l.Contains("UE 4.25's"));
        if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(blob));
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(plan, Ff7.Game, Ue427 with { Version = "4.25" }, new Shaders(new() { [Chs.Sha1] = HitLib }), null, work, CancellationToken.None);
        var made = Read(Path.Combine(work, "scskiller_gen.db")).Where(r => r.Tag == 'R').Select(RtCollections.Read).Single()!;
        Assert.Equal((Chs.Sha1, RtCollections.NoConfig, 64u), (made.Library, made.Flags, made.Payload));
    }

    /// <summary>A collection whose one library exports a closest hit AND an any hit shader (Hogwarts Legacy: 290 of its 981)
    /// reads back as UE-shaped with every export.</summary>
    [Fact]
    public void ReadTakesALibraryWithSeveralExports()
    {
        var sha = Hash("hit-lib");
        var c = RtCollections.Collection(HitLib, sha, Hash("g"), Hash("lr"), Hash("lo"), 64, 8, 1, 4, "e00cd9132f99c247")!;
        Assert.Equal(new RtCollections.Recorded(sha, Hash("g"), Hash("lo"), 64, 8, 1, 4, "e00cd9132f99c247"), RtCollections.Read(new Rec('R', c)));
        Assert.Equal([sha], ParseStateObject(new Rec('R', c)).Libraries);
    }

    /// <summary>Avalanche's 4.27 fork (Hogwarts Legacy; its recording: 981/981 collections rebuilt from its files): its libraries'
    /// bindless SRVs (spaces 4-9) select its global root signature (128 SRVs in space 1, a table per bindless space from 5 up,
    /// samplers, UAVs, b0-b15, u0 space 1001 at offset 0; no NVAPI slot), a local one with the space-4 table after the SRV
    /// table, and each library's own payload; a ray generation library is compiled once per payload the other libraries have.</summary>
    [Fact]
    public void AvalancheForkCollectionsWithoutARecording()
    {
        var hit = Lib("fork-hit", new Binding("srv", 0, 0, 3), new Binding("srv", 4, 0, -1), new Binding("srv", 1, 100, 1), new Binding("uav", 1001, 0, 1));
        var shadow = Lib("fork-shadow", new Binding("srv", 7, 0, -1));
        var rayGen = Lib("fork-raygen", new Binding("srv", 6, 0, -1));
        var index = new ShaderIndex("fork", ["PCD3D_SM5"], new[] { hit, shadow, rayGen }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", [hit.Sha1, shadow.Sha1, rayGen.Sha1])]);
        var dir = Ff7.TempDir("rt-fork");
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue427, index, null, Nvidia, Path.Combine(dir, "plan"), new Log(log.Add), CancellationToken.None);
        var items = Items(plan);
        Assert.Equal(3, items.Count); // none uncovered
        Assert.All(items, y => Assert.Equal((0u, 8u, 1u, 4u), (y.Payload, y.Attributes, y.Depth, y.Flags)));
        var global = RtCollections.GlobalFor([hit, shadow, rayGen]);
        Assert.Equal(RtCollections.Serialize(global, RootSig.Ue426Samplers).Hash, items[0].Global);
        Assert.Equal(["0,0,0,128,0,1,5", "0,0,0,4294967295,0,5,5", "0,0,0,4294967295,0,6,5", "0,0,0,4294967295,0,7,5", "0,0,3,16,0,1,1", "0,0,1,16,0,1,3"],
            global.Rows.Take(6).Select(r => string.Join(',', r)));
        Assert.Equal("0,0,1,1,0,1001,0,0", string.Join(',', global.Rows[^1])); // explicit offset 0
        var blob = RtCollections.Serialize(global, RootSig.Ue426Samplers).Blob;
        if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(blob));
        var local = RtCollections.LocalRs(hit.Counts, false, hit.Bindings).Rows.Select(r => string.Join(',', r)).ToList();
        Assert.Equal(["3,0,0,2,0", "3,0,1,2,0", "1,0,0,2,4", "0,0,0,3,0,0,5", "0,0,0,4294967295,0,4,5", "0,0,3,1,0,0,1"], local.Take(6));
        Assert.Contains(log, l => l.Contains("Avalanche's UE 4.27 fork") && l.Contains("payload each library's own") && l.Contains("NVAPI state unknown without a recording"));
        Assert.All(items, y => Assert.Null(y.Nv));

        // materialized: each library's own payload; the ray generation one once per other payload (12 and 64)
        var libs = new Dictionary<string, byte[]> { [hit.Sha1] = HitLib, [shadow.Sha1] = ShadowLib, [rayGen.Sha1] = RayGenLib };
        new Planner().Materialize(plan, Ff7.Game, Ue427, new Shaders(libs), null, Path.Combine(dir, "work"), CancellationToken.None);
        Assert.Equal("103de659042dfb8a2a8070b2029174e752c188665dee38e02a9804de568d3be6", MaterializeOutputTests.Digest(Path.Combine(dir, "work")));
        var made = Read(Path.Combine(dir, "work", "scskiller_gen.db")).Where(r => r.Tag == 'R').Select(RtCollections.Read).ToList();
        Assert.Equal([(hit.Sha1, 64u), (rayGen.Sha1, 12u), (rayGen.Sha1, 64u), (shadow.Sha1, 12u)], made.Select(c => (c!.Library, c.Payload)).Order());
        Assert.Equal(0, Planner.SkippedIn(Path.Combine(dir, "work")));
    }

    /// <summary>A ray generation library no recorded state object has, its hit / miss libraries all recorded (so no 'Y' of
    /// their own): its collections take the recorded collections' payloads.</summary>
    [Fact]
    public void RayGenerationCollectionsTakeTheRecordedPayloads()
    {
        var rayGen = Lib("rec-raygen");
        var rule = new RtCollections.Rule(Hash("g"), 4, 1, 0, 8, true);
        var dir = Ff7.TempDir("rt-recorded-payloads");
        var recording = Path.Combine(dir, "recording.db");
        using (var f = File.Create(recording)) Write(f, 'R', RtCollections.Collection(HitLib, Hash("rec-hit"), Hash("g"), Hash("lr"), Hash("lo"), 64, 8, 1, 4)!);
        var plan = new Plan("t", "t", "PCD3D_SM5", "nvidia-1", new PlanStats(0, 1, 0, 0, true), Path.Combine(dir, "plan.bin"));
        PlanFile.Write(plan, [new('Y', RtCollections.Item(rayGen.Sha1, rule.GlobalRs, Hash("lr"), Hash("lo"), rule))]);
        new Planner().Materialize(plan, Ff7.Game, Ue427, new Shaders(new() { [rayGen.Sha1] = RayGenLib }), new Recording(recording), Path.Combine(dir, "work"), CancellationToken.None);
        var made = Read(Path.Combine(dir, "work", "scskiller_gen.db")).Where(r => r.Tag == 'R').Select(RtCollections.Read).ToList();
        Assert.Equal([(rayGen.Sha1, 64u)], made.Select(c => (c!.Library, c.Payload)));
    }

    /// <summary>A library with several functions of a kind (UE compiles one per library; a FromSoftware library may not):
    /// each export its own name, a hit group per closest hit, and the collection still reads as UE-shaped.</summary>
    [Fact]
    public void SeveralFunctionsOfAKindGetTheirOwnExportNames()
    {
        var lib = Library((11, "MissA", 16), (11, "MissB", 16), (10, "HitA", 16), (10, "HitB", 16), (9, "AnyA", 16));
        var c = RtCollections.Collection(lib, Hash("two-of-a-kind"), Hash("g"), Hash("lr"), Hash("lo"), 0, 8, 1, 4, "0123456789abcdef")!;
        var text = System.Text.Encoding.Unicode.GetString(c);
        foreach (var n in new[] { "Miss_0123456789abcdef_1", "CHS_0123456789abcdef_1", "HitGroup_0123456789abcdef_1" }) Assert.Contains(n, text);
        Assert.DoesNotContain("AHS_0123456789abcdef_1", text);
        Assert.Equal("0123456789abcdef", RtCollections.Read(new Rec('R', c))!.NameHash);
    }

    /// <summary>The NVAPI state a recording's collections were created with ('N') is learned when (nearly) all carry one, goes
    /// into each 'Y', and Materialize writes an 'N' (thread scope) for every collection it makes from that 'Y'.</summary>
    [Fact]
    public void RecordedNvapiStateGoesToSynthesizedCollections()
    {
        var cols = Enumerable.Range(0, 200).Select(i => new Rec('R', [(byte)i, 1, 2, 3])).ToList();
        var nv = cols.Select(c => new NvState(c.Key, 0, 1001, 2, 0).ToRec()).ToList();
        Assert.Equal(new RtCollections.Nv(0, 1001, 0), RtCollections.LearnedNv(cols, nv));
        Assert.Null(RtCollections.LearnedNv(cols, nv.Skip(10)));  // 95%: not the game's rule
        Assert.Null(RtCollections.LearnedNv(cols, []));
        Assert.Equal(NvState.Parse(nv[0]), NvState.Parse(NvState.Parse(nv[0]).ToRec()));

        var hit = Lib("nv-hit", new Binding("srv", 4, 0, -1));
        var rule = new RtCollections.Rule(Zero.Replace('0', '1'), 4, 1, 0, 8, true);
        var y = RtCollections.ParseItem(RtCollections.Item(hit.Sha1, rule.GlobalRs, Zero, Zero, rule, new(0, 1001, 0)));
        Assert.Equal((hit.Sha1, 4u, new RtCollections.Nv(0, 1001, 0)), (y.Library, y.Flags, y.Nv!.Value));
        Assert.Null(RtCollections.ParseItem(RtCollections.Item(hit.Sha1, rule.GlobalRs, Zero, Zero, rule)).Nv);

        var dir = Ff7.TempDir("rt-nvapi");
        var plan = new Plan("t", "t", "PCD3D_SM5", "nvidia-1", new PlanStats(0, 1, 0, 0, true), Path.Combine(dir, "plan.bin"));
        PlanFile.Write(plan, [new('Y', RtCollections.Item(hit.Sha1, rule.GlobalRs, Zero, Zero, rule, new(0, 1001, 0)))]);
        new Planner().Materialize(plan, Ff7.Game, Ue427, new Shaders(new() { [hit.Sha1] = HitLib }), null, Path.Combine(dir, "work"), CancellationToken.None);
        Assert.Equal("f1a3b6f12bea1b04b24beead706ab935a8d86cf556ed0bcd58e8d7c537c89b1a", MaterializeOutputTests.Digest(Path.Combine(dir, "work")));
        var gen = Read(Path.Combine(dir, "work", "scskiller_gen.db")).ToList();
        var r = Assert.Single(gen, r => r.Tag == 'R');
        Assert.Equal(new NvState(r.Key, 0, 1001, 2, 0), NvState.Parse(Assert.Single(gen, r => r.Tag == 'N')));
    }

    /// <summary>Hash-only recordings keep an 'N' only with the record it applies to.</summary>
    [Fact]
    public void HashOnlyKeepsNvapiStateWithItsRecord()
    {
        var rs = RootSig.Serialize(new RootSig.Desc(0x80, []), []);
        var rsSha = Hex(System.Security.Cryptography.SHA1.HashData(rs));
        var cs = new Rec('C', Compute(rsSha, Vs.Sha1));
        var kept = new NvState(cs.Key, 0, 1001, 3, 0).ToRec();
        var orphan = new NvState(Zero.Replace('0', '2'), 0, 1001, 2, 0).ToRec();
        var canonical = HashOnly.Canonical([new('B', [.. Convert.FromHexString(rsSha), .. rs]), orphan, kept, cs], local: false, out _);
        Assert.Equal(['B', 'C', 'N'], canonical.Select(r => r.Tag));
        Assert.Equal(kept.Key, canonical[^1].Key);
        Assert.Throws<InvalidDataException>(() => HashOnly.Canonical([cs, new Rec('N', new byte[35])], local: true, out _));
    }

    sealed class Shaders(Dictionary<string, byte[]> bytes) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var (h, b) in bytes) if (sha1s.Contains(h)) sink(h, b);
        }
    }

    /// <summary>Hogwarts Legacy (Avalanche's 4.27 fork) against SCSKiller's recording of it (54,680 PSOs, 981 collections,
    /// 8 pipelines, 154 additions; read, never written): every recorded root signature
    /// is rebuilt byte for byte by the stock 4.26 rule + the fork's bindless tables and 128-SRV tables, and the plan made
    /// WITHOUT the recording reproduces every recorded collection byte for byte (given UE's export names; ours differ, and
    /// NVIDIA doesn't key on them). Install read only; returns early without the game or recording.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void HogwartsRecordingConfirmsTheForkRules()
    {
        var game = new SCSKiller.Core.Games.SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:990080");
        if (game == null) return;
        Ff7.Codecs();
        var reader = new UnrealReader(Ff7.TempDir("rt-hogwarts-data"));
        var engine = reader.Detect(game)!;
        if (Ff7.Recording(game, engine, reader, "rt-hogwarts-rec") is not { } db) return;
        var recs = Read(db).ToList();
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var bc = index.Shaders;
        var blobs = recs.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        var maxSrvs = RootSig.MaxSrvsFor(RootSig.Rule.Ue426, bc.Values);
        int n = 0, ok = 0;
        foreach (var pso in recs.Where(r => r.Tag is 'G' or 'C' or 'S').Select(Parse))
        {
            if (!pso.Stages.Values.All(bc.ContainsKey) || !blobs.TryGetValue(pso.Rs, out var blob)) continue;
            n++;
            if (RootSig.Serialize(RootSig.Build(RootSig.Rule.Ue426, pso.Stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]), false, maxSrvs), RootSig.Ue426Samplers).AsSpan().SequenceEqual(blob)) ok++;
        }
        output.WriteLine($"root signatures rebuilt byte-exact: {ok}/{n} recorded PSOs (SRV tables of {maxSrvs})");
        Assert.True(n > 10_000, $"only {n} recorded PSOs of index shaders");
        Assert.Equal(n, ok);

        var plan = new Planner().Build(game, engine, index, null, Nvidia, Ff7.TempDir("rt-hogwarts-plan"), new Log(output.WriteLine), CancellationToken.None);
        Assert.Equal(0, plan.Stats.Uncovered);
        var ys = Items(plan).ToDictionary(y => y.Library);
        var keys = recs.Where(r => IsStateObject(r.Tag)).Select(r => r.Key).ToHashSet();
        var cols = recs.Where(r => IsStateObject(r.Tag)).Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        if (cols.Count == 0) { output.WriteLine("no ray tracing collections in the recording (ray tracing off)"); return; }
        var own = cols.Select(c => c.Library).Distinct().ToDictionary(h => h, h => RtCollections.OwnPayload(blobs[h]));
        var payloads = own.Values.Where(p => p > 0).Distinct().Order().ToList();
        var same = cols.Count(c => ys.TryGetValue(c.Library, out var y) && RtCollections.Payloads(y.Payload, own[c.Library], payloads).Any(p =>
            keys.Contains(new Rec('R', RtCollections.Collection(blobs[c.Library], c.Library, y.Global, y.LocalRayGen, y.LocalOther, p, y.Attributes, y.Depth, y.Flags, c.NameHash)!).Key)));
        output.WriteLine($"{ys.Count} collections planned without the recording; recorded collections rebuilt from them: {same}/{cols.Count}");
        Assert.True(cols.Count > 900, $"only {cols.Count} recorded collections");
        Assert.Equal(cols.Count, same);
    }

    /// <summary>Oblivion Remastered (stock UE 5.1) against SCSKiller's recording of it, read only: every root signature and
    /// collection rebuilt byte for byte, and the no-recording plan covers the units of most of its PSOs.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void OblivionRemasteredRecordingConfirmsTheUe51Rules()
    {
        var game = new SCSKiller.Core.Games.XboxSource().Discover().FirstOrDefault(g => g.Id.StartsWith("xbox:BethesdaSoftworks.ProjectAltar"));
        if (game == null) return;
        Ff7.Codecs();
        var reader = new UnrealReader(Ff7.TempDir("rt-oblivion-data"));
        var engine = reader.Detect(game)!;
        if (Ff7.Recording(game, engine, reader, "rt-oblivion-rec") is not { } db) return;
        var recs = Read(db).ToList();
        Assert.Equal((RootSig.Rule.Ue51, true), (RootSig.RuleFor(engine), RootSig.Verified(engine)));
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var bc = index.Shaders;
        var blobs = recs.Where(r => r.Tag == 'B').GroupBy(r => Hex(r.Payload.AsSpan(0, 20))).ToDictionary(g => g.Key, g => g.First().Payload[20..]);
        var psos = recs.Where(r => r.Tag is 'G' or 'C' or 'S').Select(Parse).Where(p => p.Stages.Values.All(bc.ContainsKey) && blobs.ContainsKey(p.Rs)).ToList();
        var ok = psos.Count(p => RootSig.Serialize(RootSig.Build(RootSig.Rule.Ue51, p.Stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]), true), RootSig.Ue426Samplers).AsSpan().SequenceEqual(blobs[p.Rs]));
        output.WriteLine($"root signatures rebuilt byte-exact: {ok}/{psos.Count} recorded PSOs");
        Assert.True(psos.Count > 20_000, $"only {psos.Count} recorded PSOs of index shaders");
        Assert.Equal(psos.Count, ok);

        var plan = new Planner().Build(game, engine, index, null, Nvidia, Ff7.TempDir("rt-oblivion-plan"), new Log(output.WriteLine), CancellationToken.None);
        Assert.Equal((0, 0L), (plan.Stats.Uncovered, plan.Stats.RtUncovered));
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var units = body.Where(r => r.Tag == 'S').Select(Parse).SelectMany(p => p.Stages.Select(s => (s.Key, s.Value, p.Rs))).ToHashSet();
        var covered = psos.Count(p => p.Stages.All(s => units.Contains((s.Key, s.Value, p.Rs))));
        output.WriteLine($"recorded PSOs whose stages the no-recording plan compiles with their root signature: {covered}/{psos.Count}");
        Assert.True(covered >= 0.995 * psos.Count, $"{covered}/{psos.Count}"); // 99.96% measured; 95% without MS per-primitive outputs and default-material shaders across maps
        Assert.All(psos.Where(p => p.Stages.ContainsKey((int)Stage.Mesh)), p => Assert.True(p.Stages.All(s => units.Contains((s.Key, s.Value, p.Rs)))));

        var ys = body.Where(r => r.Tag == 'Y').Select(r => RtCollections.ParseItem(r.Payload)).ToDictionary(y => y.Library);
        var keys = recs.Where(r => IsStateObject(r.Tag)).Select(r => r.Key).ToHashSet();
        var cols = recs.Where(r => IsStateObject(r.Tag)).Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        var own = cols.Select(c => c.Library).Distinct().ToDictionary(h => h, h => RtCollections.OwnPayload(blobs[h]));
        var payloads = own.Values.Where(p => p > 0).Distinct().Order().ToList();
        var same = cols.Count(c => ys.TryGetValue(c.Library, out var y) && RtCollections.Payloads(y.Payload, own[c.Library], payloads).Any(p =>
            keys.Contains(new Rec('R', RtCollections.Collection(blobs[c.Library], c.Library, y.Global, y.LocalRayGen, y.LocalOther, p, y.Attributes, y.Depth, y.Flags, c.NameHash)!).Key)));
        output.WriteLine($"{ys.Count} collections planned without the recording; recorded collections rebuilt from them: {same}/{cols.Count}");
        Assert.True(cols.Count > 500, $"only {cols.Count} recorded collections");
        Assert.Equal(cols.Count, same);
    }

    /// <summary>Darwin's Paradox (stock UE 5.4, no recording), read only: its DXIL libraries have 5.1's binding shape, so
    /// its plan synthesizes a collection per library with <see cref="RtCollections.Ue54Global"/>, unverified in game.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void DarwinsParadoxGetsTheUe5Rule()
    {
        var game = new SCSKiller.Core.Games.SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:2989180");
        if (game == null) return;
        Ff7.Codecs();
        var reader = new UnrealReader(Ff7.TempDir("rt-darwin-data"));
        var engine = reader.Detect(game)!;
        Assert.Equal(("5.4", null), (engine.Version, engine.Fork));
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var libs = index.Shaders.Values.Where(s => s.Stage == Stage.Library).ToList();
        Assert.Null(RtCollections.Ue5ShapeMismatch(libs));
        var log = new List<string>();
        var plan = new Planner().Build(game, engine, index, null, Nvidia, Ff7.TempDir("rt-darwin-plan"), new Log(log.Add), CancellationToken.None);
        foreach (var l in log.Where(l => l.StartsWith("ray tracing"))) output.WriteLine(l);
        var ys = Items(plan);
        Assert.All(ys, y => Assert.Equal(RtCollections.Serialize(RtCollections.Ue54Global, RootSig.Ue426Samplers).Hash, y.Global));
        Assert.Equal(plan.Stats.RtLibraries, ys.Count + plan.Stats.RtUncovered);
        Assert.True(ys.Count > 0.99 * libs.Count, $"{ys.Count}/{libs.Count}");
    }

    const string JediInstall = @"D:\EA\Jedi Survivor";
    static readonly Game Jedi = new("ea:198300", "STAR WARS Jedi: Survivor", Store.EA, JediInstall, Path.Combine(JediInstall, @"SwGame\Binaries\Win64\JediSurvivor.exe"));
    static readonly Lazy<(UnrealReader Reader, EngineInfo Engine)> JediReader = new(() =>
    {
        Ff7.Codecs();
        var reader = new UnrealReader(Ff7.TempDir("rt-jedi-data"));
        return (reader, reader.Detect(Jedi)!);
    });
    static readonly Lazy<IReadOnlyDictionary<string, ShaderInfo>> JediShaders = new(() => JediReader.Value.Reader.Index(Jedi, JediReader.Value.Engine, null, CancellationToken.None).Shaders);
    static readonly Lazy<string?> JediDb = new(() => File.Exists(Jedi.ExePath) ? Ff7.Recording(Jedi, JediReader.Value.Engine, JediReader.Value.Reader, "rt-jedi-rec") : null);
    static bool HasJedi => JediDb.Value != null;

    sealed record JediData(List<Rec> StateObjects, Dictionary<string, byte[]> Blobs, List<RtCollections.Recorded> Collections, string Global);

    static JediData ReadJedi()
    {
        var recs = Read(JediDb.Value!).ToList();
        var blobs = recs.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        var so = recs.Where(r => IsStateObject(r.Tag)).ToList();
        var cols = so.Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        return new(so, blobs, cols, cols.GroupBy(c => c.Global).MaxBy(g => g.Count())!.Key);
    }

    static (string Hash, byte[] Blob) Local(ShaderInfo lib, bool rayGen) => RtCollections.Serialize(RtCollections.LocalRs(lib.Counts, rayGen), []);

    /// <summary>Every UE-shaped collection of Jedi's recording (2,917 of its 2,987 state objects) is rebuilt byte for byte from
    /// its library (exports and kinds from RDAT), the library's resource counts (local root signature) and the recording's
    /// global root signature and configs, names included when given UE's shader hash; UE 4.26's global root signature is
    /// the recorded one; the index gives every library its RDAT resources, which the two root signatures cover.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void JediCollectionsRebuildByteForByte()
    {
        if (!HasJedi) return;
        var x = ReadJedi();
        var bc = JediShaders.Value;
        Assert.Equal(x.Global, RtCollections.Serialize(RtCollections.Ue426Global, RootSig.Ue426Samplers).Hash);
        var keys = x.StateObjects.Select(r => r.Key).ToHashSet();
        var global = RootSig.Parse(x.Blobs[x.Global]);
        int n = 0, same = 0, sameButNames = 0, covered = 0, withResources = 0;
        foreach (var c in x.Collections.Where(c => bc.ContainsKey(c.Library)))
        {
            n++;
            var (g, o) = (Local(bc[c.Library], true), Local(bc[c.Library], false));
            var lib = x.Blobs[c.Library];
            if (keys.Contains(new Rec('R', RtCollections.Collection(lib, c.Library, x.Global, g.Hash, o.Hash, c.Payload, c.Attributes, c.Depth, c.Flags, c.NameHash)!).Key)) same++;
            var ours = RtCollections.Read(new Rec('R', RtCollections.Collection(lib, c.Library, x.Global, g.Hash, o.Hash, c.Payload, c.Attributes, c.Depth, c.Flags)!))!;
            if (ours with { NameHash = c.NameHash } == c) sameButNames++;
            if (bc[c.Library].Bindings.Count > 0) withResources++;
            if (RootSig.Uncovered(new RootSig.Ranges(0, [.. global.Slots, .. RootSig.Parse(o.Blob).Slots]), Stage.Library, bc[c.Library]) == null) covered++;
        }
        output.WriteLine($"{x.Collections.Count} UE-shaped collections of {x.StateObjects.Count} state objects; {n} of index libraries: {same} rebuilt byte for byte, {sameButNames} with our names; {withResources} declare RDAT resources, {covered} all covered by the two root signatures");
        Assert.True(n > 2900 && withResources > 2800, $"{n} {withResources}");
        Assert.Equal((n, n, n), (same, sameButNames, covered));
    }

    /// <summary>Materialize turns a plan's 'Y' into the collection's 'R' record, its exports read from the library the reader
    /// serves; a library without a ray tracing export is skipped.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void MaterializeWritesTheCollection()
    {
        if (!HasJedi) return;
        var x = ReadJedi();
        var c = x.Collections.First(c => c.NameHash.Length == 16 && x.Blobs.ContainsKey(c.Library));
        var lib = x.Blobs[c.Library];
        var (g, o) = (RtCollections.Serialize(RtCollections.LocalRs(new(3, 5, 0, 2), true), []), RtCollections.Serialize(RtCollections.LocalRs(new(3, 5, 0, 2), false), []));
        var dir = Ff7.TempDir("rt-materialize");
        var rule = new RtCollections.Rule(x.Global, 4, 1, 32, 8, true);
        var plan = new Plan("t", "t", "PCD3D_SM5", "nvidia-1", new PlanStats(0, 1, 0, 3, true), Path.Combine(dir, "plan.bin"));
        PlanFile.Write(plan, [new('B', [.. Convert.FromHexString(x.Global), .. x.Blobs[x.Global]]), new('B', [.. Convert.FromHexString(g.Hash), .. g.Blob]),
            new('B', [.. Convert.FromHexString(o.Hash), .. o.Blob]), new('Y', RtCollections.Item(c.Library, x.Global, g.Hash, o.Hash, rule)),
            new('Y', RtCollections.Item(Vs.Sha1, x.Global, g.Hash, o.Hash, rule))]); // not a library: skipped
        new Planner().Materialize(plan, Ff7.Game, Ue427, new OneShader(c.Library, lib), null, Path.Combine(dir, "work"), CancellationToken.None);
        var gen = Read(Path.Combine(dir, "work", "scskiller_gen.db")).ToList();
        var r = Assert.Single(gen, r => r.Tag == 'R');
        Assert.Equal(RtCollections.Collection(lib, c.Library, x.Global, g.Hash, o.Hash, 32, 8, 1, 4), r.Payload);
        Assert.Contains(gen, b => b.Tag == 'B' && Hex(b.Payload.AsSpan(0, 20)) == c.Library);
        Assert.Equal(1, Planner.SkippedIn(Path.Combine(dir, "work")));
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string v) => a(v); }

    sealed class OneShader(string sha1, byte[] bytes) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { if (sha1s.Contains(sha1)) sink(sha1, bytes); }
    }

    /// <summary>Elden Ring: one collection per material's closest hit and any hit pair of one ray payload, in the game's
    /// shape (<see cref="SCSKiller.Core.FromSoft.SoulsRayTracing"/>). Offline: an 'H' per pair (1,068 materials x 2 payloads),
    /// none uncovered, no 'Y'; NVIDIA only. SCSKILLER_RT_ER=warp: the plan's collections, materialized from the install, all
    /// create on WARP; =gpu (NVIDIA; not while TestEnv.GpuBusyElsewhere; holds the GPU lock; throwaway name, never
    /// eldenring.exe; its cache files deleted): a second process re-creating them hits the cache.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void EldenRingMaterialCollections()
    {
        var game = SCSKiller.Tests.FromSoft.FromSoftGameTests.Games["ER"].Game;
        if (!File.Exists(game.ExePath)) return;
        Ff7.Codecs();
        var reader = new SCSKiller.Core.FromSoft.FromSoftReader(Ff7.TempDir("rt-er-data"));
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var pairs = index.Maps.Count(SCSKiller.Core.FromSoft.SoulsRayTracing.IsPair);
        var dir = Ff7.TempDir("rt-er");
        var log = new List<string>();
        var plan = new Planner().Build(game, engine, index, null, Nvidia, Path.Combine(dir, "plan"), new Log(log.Add), CancellationToken.None);
        foreach (var l in log.Where(l => l.StartsWith("ray tracing"))) output.WriteLine(l);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.Equal((pairs, 0), (body.Count(r => r.Tag == 'H'), body.Count(r => r.Tag == 'Y')));
        Assert.Contains(log, l => l.Contains($"{pairs} material collections synthesized") && l.Contains(", 0 uncovered"));
        Assert.Empty(Items(new Planner().Build(game, engine, index, null, Ff7.Amd, Path.Combine(dir, "amd"), null, CancellationToken.None)));

        var mode = Environment.GetEnvironmentVariable("SCSKILLER_RT_ER");
        var warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
        if (mode is not ("warp" or "gpu") || !File.Exists(warm)) return;
        if (mode == "gpu" && TestEnv.GpuBusyElsewhere) { output.WriteLine("GPU busy (another tool's lock): not measured"); return; }
        var rt = plan with { FilePath = Path.Combine(dir, "rt.bin") };
        PlanFile.Write(rt, body.Where(r => r.Tag is 'B' or 'H'));
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(rt, game, engine, reader, null, work, CancellationToken.None);
        Assert.Equal(0, Planner.SkippedIn(work));
        (long Done, long Failed, double[] Ms, string Log) Run(string exe, params string[] extra)
        {
            var psi = new ProcessStartInfo(warm, [work, exe, .. extra]) { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false };
            psi.Environment["SCSKILLER_WARM_TIMES"] = "1";
            string o;
            using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var done = System.Text.Json.JsonDocument.Parse(o.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]).RootElement;
            var stage = TestEnv.WarmStage(o, work);
            var ms = File.ReadAllLines(Path.Combine(stage, "scskiller_warm_times.csv")).Select(l => double.Parse(l.Split(',')[1], CultureInfo.InvariantCulture)).Order().ToArray();
            return (done.GetProperty("done").GetInt64(), done.GetProperty("failed").GetInt64(), ms, File.ReadAllText(Path.Combine(stage, "scskiller.log")));
        }
        string Stats(double[] v) => $"median {v[v.Length / 2]:F2} ms, p90 {v[(int)(v.Length * 0.9)]:F2}, max {v[^1]:F1}, total {v.Sum() / 1000:F1} s";

        if (mode == "warp")
        {
            var luid = Process.Start(new ProcessStartInfo(Path.Combine(Ff7.ProxyBin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd().Trim();
            var r = Run("scsktrter_warp.exe", "--adapter-luid", luid);
            output.WriteLine($"WARP: {r.Done} created, {r.Failed} failed ({Stats(r.Ms)})");
            Assert.True(r.Done == Read(Path.Combine(work, "scskiller_gen.db")).Count(x => x.Tag == 'R') && r.Failed == 0, r.Log[^Math.Min(r.Log.Length, 3000)..]);
            return;
        }
        var nvCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache");
        var before = Directory.GetFiles(nvCache).ToHashSet();
        var lockFile = TestEnv.GpuLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        using (new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose))
            try
            {
                var name = $"scsktrter{DateTime.Now:HHmmss}.exe";
                var cold = Run(name, "--threads", "1");
                var hot = Run(name, "--threads", "1");
                output.WriteLine($"{name}: first process {cold.Done} collections, {cold.Failed} failed: {Stats(cold.Ms)}");
                output.WriteLine($"{name}: second process (re-create): {Stats(hot.Ms)}");
                Assert.Equal(0, cold.Failed + hot.Failed);
                Assert.True(hot.Ms[hot.Ms.Length / 2] * 3 < cold.Ms[cold.Ms.Length / 2], "the re-create did not hit");
            }
            finally
            {
                var made = Directory.GetFiles(nvCache).Where(p => !before.Contains(p)).ToList();
                if (made.Count <= 4) foreach (var p in made) File.Delete(p);
                output.WriteLine($"{made.Count} new driver-cache files{(made.Count <= 4 ? ", deleted" : ": not all ours, left in place: " + string.Join(", ", made))}");
            }
    }

    /// <summary>The claim (NVIDIA): collections synthesized from the index's libraries, warmed under exe name X, make
    /// the game's own recorded state objects (Jedi: 2,917 collections, 5 pipelines linking them, 63 additions) cache hits
    /// under X. Timed per object on one thread (SCSKILLER_WARM_TIMES): the recording replayed under X after warming only
    /// synthesized collections, vs under a fresh name Z (cold), vs Z again (exact repeats). GPU part: SCSKILLER_GPU_TESTS=1;
    /// not while TestEnv.GpuBusyElsewhere; holds TestEnv.GpuLockPath; throwaway names, the cache files they create deleted.</summary>
    [Trait("Needs", "Gpu")]
    [Fact]
    public void SynthesizedCollectionsMakeJedisRecordedStateObjectsHits()
    {
        var warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
        if (Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") != "1" || !HasJedi || !File.Exists(warm)) return;
        if (TestEnv.GpuBusyElsewhere) { output.WriteLine("GPU busy (another tool's lock): not measured"); return; }
        var x = ReadJedi();
        var bc = JediShaders.Value;
        var dir = Ff7.TempDir("rt-gpu");
        var (flags, depth) = (x.Collections.GroupBy(c => c.Flags).MaxBy(g => g.Count())!.Key, x.Collections.GroupBy(c => c.Depth).MaxBy(g => g.Count())!.Key);
        var (payload, attributes) = x.Collections.GroupBy(c => (c.Payload, c.Attributes)).MaxBy(g => g.Count())!.Key;

        // A: only synthesized collections (our export names, the rule's config), for every library the recording's collections use
        var synth = Directory.CreateDirectory(Path.Combine(dir, "synth")).FullName;
        File.WriteAllBytes(Path.Combine(synth, "scskiller.db"), []);
        using (var f = File.Create(Path.Combine(synth, "scskiller_gen.db")))
        {
            var written = new HashSet<string>();
            void Blob(string h, byte[] b) { if (written.Add(h)) WriteBlob(f, h, b); }
            Blob(x.Global, x.Blobs[x.Global]);
            foreach (var lib in x.Collections.Select(c => c.Library).Distinct().Where(bc.ContainsKey))
            {
                var (g, o) = (Local(bc[lib], true), Local(bc[lib], false));
                Blob(g.Hash, g.Blob);
                Blob(o.Hash, o.Blob);
                Blob(lib, x.Blobs[lib]);
                Write(f, 'R', RtCollections.Collection(x.Blobs[lib], lib, x.Global, g.Hash, o.Hash, payload, attributes, depth, flags)!);
            }
        }
        // B: the recording's state objects, as recorded
        var game = Directory.CreateDirectory(Path.Combine(dir, "game")).FullName;
        File.WriteAllBytes(Path.Combine(game, "scskiller_gen.db"), []);
        using (var f = File.Create(Path.Combine(game, "scskiller.db")))
        {
            foreach (var h in Rehydrate.References(x.StateObjects)) WriteBlob(f, h, x.Blobs[h]);
            foreach (var r in x.StateObjects) Write(f, r.Tag, r.Payload);
        }
        var kinds = x.StateObjects.Select(r => r.Tag == 'A' ? "addition" : ParseStateObject(r).Type == 0 ? "collection" : "pipeline").ToArray();

        var nvCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache");
        var before = Directory.GetFiles(nvCache).ToHashSet();
        var stamp = DateTime.Now.ToString("HHmmss");
        var (nameX, nameZ) = ($"scsktrtx{stamp}.exe", $"scsktrtz{stamp}.exe");
        var lockFile = TestEnv.GpuLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        using (new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose))
            try
            {
                var (sOk, sFailed, sSec) = Run(synth, nameX, false);
                output.WriteLine($"synthesized collections warmed under {nameX}: {sOk} created, {sFailed} failed, {sSec:F0} s");
                Assert.Equal(0, sFailed);
                var rows = new List<(string Run, double[] Ms)>();
                foreach (var (run, name) in new[] { ("after the synthesized warm (X)", nameX), ("cold (fresh name Z)", nameZ), ("exact repeat (Z again)", nameZ) })
                    rows.Add((run, Times(game, name)));
                foreach (var kind in kinds.Distinct())
                    foreach (var (run, ms) in rows)
                    {
                        var v = ms.Where((_, i) => kinds[i] == kind).Order().ToArray();
                        output.WriteLine($"{kind,-10} x{v.Length,-5} {run,-32} median {v[v.Length / 2],8:F2} ms, p90 {v[(int)(v.Length * 0.9)],8:F2}, max {v[^1],8:F1}, >= 20 ms {v.Count(t => t >= 20),5}, total {v.Sum() / 1000,6:F1} s");
                    }
                double Median(double[] ms) { var v = ms.Where((_, i) => kinds[i] == "collection").Order().ToArray(); return v[v.Length / 2]; }
                Assert.True(Median(rows[0].Ms) * 4 < Median(rows[1].Ms), "recorded collections are not cheaper after the synthesized warm");
            }
            finally
            {
                var made = Directory.GetFiles(nvCache).Where(p => !before.Contains(p)).ToList();
                if (made.Count <= 8) foreach (var p in made) File.Delete(p);
                output.WriteLine($"{made.Count} new driver-cache files{(made.Count <= 8 ? ", deleted" : ": not all ours, left in place: " + string.Join(", ", made))}");
            }

        string? stage = null;
        (long Ok, long Failed, double Seconds) Run(string work, string exe, bool timed)
        {
            var psi = new ProcessStartInfo(warm, timed ? [work, exe, "--threads", "1"] : [work, exe]) { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false };
            if (timed) psi.Environment["SCSKILLER_WARM_TIMES"] = "1";
            string o;
            using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            stage = TestEnv.WarmStage(o, work);
            var done = System.Text.Json.JsonDocument.Parse(o.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]).RootElement;
            return (done.GetProperty("done").GetInt64() - done.GetProperty("failed").GetInt64(), done.GetProperty("failed").GetInt64(), done.GetProperty("seconds").GetDouble());
        }

        double[] Times(string work, string exe)
        {
            var (_, failed, _) = Run(work, exe, true);
            Assert.Equal(0, failed);
            var ms = new double[kinds.Length];
            foreach (var l in File.ReadAllLines(Path.Combine(stage!, "scskiller_warm_times.csv")).Select(l => l.Split(',')))
                ms[int.Parse(l[0])] = double.Parse(l[1], CultureInfo.InvariantCulture);
            return ms;
        }
    }
}
