using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Rage;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Rage;

/// <summary>The RAGE reader on a synthetic install: an SGD2 library laid out as GTA V Enhanced's, at a block boundary of an
/// update archive, with shaders compiled here (<see cref="Hlsl"/>).</summary>
public class RageReaderTests
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>"SGD2", per stage slot (VS, PS, GS, DS, HS, CS) the shaders, then 8 zero bytes and one effect using every
    /// shader, whose pass table holds <paramref name="passes"/> (six slot bytes each, 1-based).</summary>
    internal static byte[] Library(byte[][][] slots, params byte[][] passes)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        w.Write("SGD2"u8);
        var n = 0;
        foreach (var slot in slots)
        {
            w.Write(slot.Length);
            foreach (var c in slot)
            {
                var name = Encoding.ASCII.GetBytes($"S{n++}\0");
                w.Write((byte)name.Length); w.Write(name); w.Write((byte)50);
                w.Write(c.Length); w.Write(c); w.Write(0L); w.Write(new byte[144]);
                w.Write(4); w.Write(0);
            }
        }
        w.Write(0L); w.Write(1);
        w.Write((byte)3); w.Write("fx\0"u8); w.Write(0);
        foreach (var slot in slots)
        {
            w.Write(slot.Length);
            for (var i = 0; i < slot.Length; i++) w.Write(i);
        }
        w.Write(new byte[16]);   // the body's state blocks
        w.Write(1); w.Write(passes.Length << 24); w.Write(passes.Length);
        foreach (var p in passes) { w.Write(p); w.Write(new byte[24 - p.Length]); }
        w.Write("proplst\0"u8); w.Write(1); w.Write(4); w.Write(0); w.Write(3); w.Write("\0p0"u8);
        return s.ToArray();
    }

    [Fact]
    public void IndexesEachPassAsAPipeline()
    {
        var vs = Hlsl.Vs(1, Hlsl.Rs1);
        byte[][] ps = [Hlsl.Ps(1, Hlsl.Rs1), Hlsl.Ps(2, Hlsl.Rs1)];
        var dir = Ff7.TempDir("rage-synthetic");
        Directory.CreateDirectory(Path.Combine(dir, "update"));
        // a stray magic at a block boundary, then the library at the next one
        var lib = Library([[vs], ps, [], [], [], []], [1, 1, 0, 0, 0, 0], [1, 2, 0, 0, 0, 0], [1, 2, 0, 0, 0, 0]);
        File.WriteAllBytes(Path.Combine(dir, @"update\update2.rpf"), [.. "RPF7"u8, .. new byte[508], .. "SGD2"u8, .. new byte[508], .. lib, .. new byte[600]]);
        File.WriteAllBytes(Path.Combine(dir, @"update\update.rpf"), [.. "RPF7"u8, .. new byte[4092]]);
        var game = new Game("test:rage", "rage", Store.Other, dir, Path.Combine(dir, "GTA5_Enhanced.exe"));

        var reader = new RageReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(RageReader.Family, "SGD2+RTS0", null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(3, index.Shaders.Count);
        Assert.All(index.Shaders.Values, s => Assert.NotNull(s.RootSignature));
        Assert.All(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>(), h => Assert.Contains(h, index.RootSignatures!.Keys));   // the planner checks coverage with them
        Assert.Equal(new[] { Sha(ps[0]), Sha(ps[1]) }.Order(), index.Maps.Select(m => Assert.Single(m.Shaders, h => h != Sha(vs))).Order());
        Assert.All(index.Maps, m => Assert.Contains(Sha(vs), m.Shaders));
        Assert.All(index.Maps, m => Assert.True(m.IsPipeline));
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, index.Shaders.Keys.ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(Sha(ps[1]), Sha(got[Sha(ps[1])]));
        Assert.Null(new RageReader().Detect(game with { InstallDir = Path.Combine(dir, "update") }));
    }

    /// <summary>A pass table naming a shader the effect doesn't have isn't one: the effect has no passes.</summary>
    [Fact]
    public void APassTableOutOfRangeIsNone()
    {
        uint[][] stages = [[0], [0], [], [], [], []];
        byte[] Body(byte vs, byte ps) => [.. new byte[8], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(1 << 24), .. BitConverter.GetBytes(1), vs, ps, .. new byte[22]];
        Assert.Single(RageReader.Effect.PassTable(Body(1, 1), stages)!);
        Assert.Null(RageReader.Effect.PassTable(Body(1, 2), stages));
    }
}
