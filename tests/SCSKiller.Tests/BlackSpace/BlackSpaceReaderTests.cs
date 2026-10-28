using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using SCSKiller.Core;
using SCSKiller.Core.BlackSpace;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.BlackSpace;

/// <summary>The BlackSpace reader on a synthetic install laid out as Crimson Desert's: meta\0.papgt, a group of other assets,
/// and a shader group whose .padxil files are stored or LZ4-compressed across two archives (shaders compiled here,
/// <see cref="Hlsl"/>).</summary>
public class BlackSpaceReaderTests
{
    const string VsSrc = "float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, out float2 ouv : TEXCOORD0) : SV_Position { ouv = uv; return float4(p, 1); }";
    const string PsSrc = "Texture2D t : register(t0); SamplerState s : register(s0); float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return t.Sample(s, uv); }";
    const string CsSrc = "RWByteAddressBuffer u : register(u0); [numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) { u.Store(i * 4, i * {0}); }";

    // seven fields as the game names them: effect, entry point, stage, permutation, 2, two switches. The VS and its PS
    // share the effect and both switches, as Crimson Desert's pairs do
    const string VsName = "4c7f406d_6b037f3d_0_f997c9a7_2_deba1dcd_33e3354e.padxil";
    const string PsName = "4c7f406d_edecaf87_4_3f7375e9_2_deba1dcd_33e3354e.padxil";
    const string CsName = "023cb92f_3f8f0b31_5_1eb5f012_3_deba1dcd_deba1dcd.padxil";

    sealed class Fixture
    {
        public readonly string Dir = Ff7.TempDir("blackspace-synthetic");
        public readonly byte[] Vs = Hlsl.Compile(VsSrc, "main", "vs_5_1"), Ps = Hlsl.Compile(PsSrc, "main", "ps_5_1");
        public readonly byte[] Cs = Hlsl.Compile(CsSrc.Replace("{0}", "1"), "main", "cs_5_1"), Loose = Hlsl.Compile(CsSrc.Replace("{0}", "2"), "main", "cs_5_1");
        public Game Game => new("test:blackspace", "blackspace", Store.Other, Dir, Path.Combine(Dir, @"bin64\CrimsonDesert.exe"));
        public string Shaders => Path.Combine(Dir, "0017");

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Dir, "meta"));
            File.WriteAllBytes(Path.Combine(Dir, @"meta\0.papgt"), new byte[16]);
            Group("0000", ["object", "/decals"], [("stone.dds", 0, Pamt.Stored, "DDS |"u8.ToArray())]);
            Group("0017", ["shadercache__", "/d3d12", "/windows"], [
                (VsName, 0, Pamt.Stored, Pasc(Vs)),
                (PsName, 1, Pamt.Lz4, Pasc(Ps)),
                (CsName, 1, Pamt.Lz4, Pasc(Cs)),
                ("loose.padxil", 0, Pamt.Stored, Pasc(Loose)),                 // a name of another layout: read, in no map
                ("broken.padxil", 0, Pamt.Stored, "PASC, then nothing"u8.ToArray()),
                ("atmosphericscattering.pars", 0, 0x30, new byte[64]),        // a render pass file: not read
            ]);
        }

        /// <summary>A group folder: its archives and index (<see cref="Pamt"/>'s layout). The folder is a chain of trie nodes
        /// with every file in its last node; a file name with a '_' is split in two nodes, the trie walk the reader does.</summary>
        void Group(string name, string[] folder, (string Name, ushort Archive, ushort Flags, byte[] Bytes)[] files)
        {
            var dir = Directory.CreateDirectory(Path.Combine(Dir, name)).FullName;
            var paz = files.Select(f => f.Archive).Distinct().Order().ToDictionary(a => a, _ => new MemoryStream());
            var fileTrie = new MemoryStream();
            var records = new List<(int Node, uint Offset, uint Stored, uint Size, ushort Archive, ushort Flags)>();
            foreach (var f in files)
            {
                var data = f.Flags == Pamt.Lz4 ? Lz4(f.Bytes) : f.Bytes;
                var s = paz[f.Archive];
                s.Write(new byte[16]);   // other bytes between files, as in the game's archives
                var offset = (uint)s.Position;
                s.Write(data);
                var cut = f.Name.IndexOf('_');
                var node = cut < 0 ? Node(fileTrie, uint.MaxValue, f.Name) : Node(fileTrie, (uint)Node(fileTrie, uint.MaxValue, f.Name[..(cut + 1)]), f.Name[(cut + 1)..]);
                records.Add((node, offset, (uint)data.Length, (uint)f.Bytes.Length, f.Archive, f.Flags));
            }
            foreach (var (a, s) in paz) File.WriteAllBytes(Path.Combine(dir, $"{a}.paz"), s.ToArray());
            var folderTrie = new MemoryStream();
            var nodes = new List<int>();
            foreach (var part in folder) nodes.Add(Node(folderTrie, nodes.Count == 0 ? uint.MaxValue : (uint)nodes[^1], part));
            var index = new MemoryStream();
            var w = new BinaryWriter(index);
            w.Write(0x7253_8e37u); w.Write(paz.Count); w.Write(0x610e_0232u);
            foreach (var (a, s) in paz) { w.Write((uint)a); w.Write(0u); w.Write((uint)s.Length); }
            w.Write((uint)folderTrie.Length); w.Write(folderTrie.ToArray());
            w.Write((uint)fileTrie.Length); w.Write(fileTrie.ToArray());
            w.Write(nodes.Count);
            for (var i = 0; i < nodes.Count; i++) { w.Write(0u); w.Write((uint)nodes[i]); w.Write(0u); w.Write(i == nodes.Count - 1 ? records.Count : 0); }
            w.Write(records.Count);
            foreach (var r in records) { w.Write((uint)r.Node); w.Write(r.Offset); w.Write(r.Stored); w.Write(r.Size); w.Write(r.Archive); w.Write(r.Flags); }
            File.WriteAllBytes(Path.Combine(dir, Pamt.FileName), index.ToArray());
        }
    }

    static readonly Lazy<Fixture> Data = new(() => new Fixture());
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    static int Node(MemoryStream trie, uint parent, string text)
    {
        var at = (int)trie.Position;
        var b = Encoding.ASCII.GetBytes(text);
        trie.Write(BitConverter.GetBytes(parent));
        trie.WriteByte((byte)b.Length);
        trie.Write(b);
        return at;
    }

    static byte[] Lz4(byte[] b)
    {
        var lz = new byte[LZ4Codec.MaximumOutputSize(b.Length)];
        return lz[..LZ4Codec.Encode(b, lz)];
    }

    /// <summary>A .padxil as the game ships it: "PASC", version 8, the file size, 61, the header size, a 16-byte hash, the container.</summary>
    internal static byte[] Pasc(byte[] container)
    {
        var f = new byte[36 + container.Length];
        "PASC"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), (uint)f.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(12), 61);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 36);
        SHA1.HashData(container).AsSpan(0, 16).CopyTo(f.AsSpan(20));
        container.CopyTo(f, 36);
        return f;
    }

    [Fact]
    public void Pamt_ReadsFoldersFilesAndArchives()
    {
        var pamt = Pamt.Parse(File.ReadAllBytes(Path.Combine(Data.Value.Shaders, Pamt.FileName)));
        Assert.Equal([0u, 1u], pamt.Archives.Select(a => a.Index));
        Assert.Equal(["shadercache__", "shadercache__/d3d12", BlackSpaceReader.ShaderFolder], pamt.Folders.Select(f => f.Name));
        Assert.Equal(6, pamt.Folders[^1].FileCount);
        Assert.Equal([VsName, PsName, CsName, "loose.padxil", "broken.padxil", "atmosphericscattering.pars"], pamt.Files.Select(f => f.Name));
        Assert.Equal([Pamt.Stored, Pamt.Lz4, Pamt.Lz4], pamt.Files.Take(3).Select(f => f.Flags));
        Assert.True(pamt.Files[1].StoredSize < pamt.Files[1].Size);
    }

    [Fact]
    public void Pamt_RejectsAnotherLength()
    {
        var b = File.ReadAllBytes(Path.Combine(Data.Value.Shaders, Pamt.FileName));
        Assert.Throws<InvalidDataException>(() => Pamt.Parse([.. b, 0]));
        Assert.Throws<InvalidDataException>(() => Pamt.Parse(b[..^1]));
    }

    [Fact]
    public void Pamt_RejectsALoopingTrie()
    {
        var b = File.ReadAllBytes(Path.Combine(Data.Value.Shaders, Pamt.FileName));
        var trie = 12 + 12 * 2 + 4;   // header, two archives, the folder trie's length: its first node
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(trie), 0);   // "shadercache__" its own parent
        Assert.Throws<InvalidDataException>(() => Pamt.Parse(b));
    }

    [Fact]
    public void Detect_FindsTheShaderGroup()
    {
        var f = Data.Value;
        var e = new BlackSpaceReader().Detect(f.Game);
        Assert.NotNull(e);
        Assert.Equal((BlackSpaceReader.Family, "D3D12", (string?)null), (e.Family, e.GraphicsApi, e.Unsupported));
        Assert.Equal(f.Shaders, BlackSpaceReader.ShaderGroup(f.Dir));
    }

    [Fact]
    public void Detect_NeedsTheGroupList()
    {
        var dir = Ff7.TempDir("blackspace-no-meta");
        Directory.CreateDirectory(Path.Combine(dir, "0017"));
        File.Copy(Path.Combine(Data.Value.Shaders, Pamt.FileName), Path.Combine(dir, "0017", Pamt.FileName), true);
        Assert.Null(new BlackSpaceReader().Detect(new Game("test:bs-none", "none", Store.Other, dir, Path.Combine(dir, "x.exe"))));
    }

    [Fact]
    public void Index_ReadsStoredAndCompressedShaders()
    {
        var f = Data.Value;
        var r = new BlackSpaceReader();
        var index = r.Index(f.Game, r.Detect(f.Game)!, null, CancellationToken.None);
        Assert.True(index.Shaders.Keys.ToHashSet().SetEquals([Sha(f.Vs), Sha(f.Ps), Sha(f.Cs), Sha(f.Loose)]));   // not the broken one
        Assert.Equal((Stage.Vertex, Stage.Pixel, Stage.Compute), (index.Shaders[Sha(f.Vs)].Stage, index.Shaders[Sha(f.Ps)].Stage, index.Shaders[Sha(f.Cs)].Stage));
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal(new ResourceCounts(0, 1, 0, 1), index.Shaders[Sha(f.Ps)].Counts);
    }

    [Fact]
    public void Index_PutsAVsInItsPsMap()
    {
        var f = Data.Value;
        var r = new BlackSpaceReader();
        var index = r.Index(f.Game, r.Detect(f.Game)!, null, CancellationToken.None);
        Assert.Contains(index.Maps, m => m.Shaders.Contains(Sha(f.Vs)) && m.Shaders.Contains(Sha(f.Ps)));
        Assert.DoesNotContain(index.Maps, m => m.Shaders.Contains(Sha(f.Loose)));
    }

    [Fact]
    public void Index_ContentHashFollowsTheIndex()
    {
        var f = Data.Value;
        var r = new BlackSpaceReader();
        var index = r.Index(f.Game, r.Detect(f.Game)!, null, CancellationToken.None);
        Assert.Equal(Sha(File.ReadAllBytes(Path.Combine(f.Shaders, Pamt.FileName))), index.ContentHash);
    }

    [Fact]
    public void ReadShaders_ReturnsTheContainers()
    {
        var f = Data.Value;
        var r = new BlackSpaceReader();
        var got = new Dictionary<string, byte[]>();
        r.ReadShaders(f.Game, r.Detect(f.Game)!, new HashSet<string> { Sha(f.Vs), Sha(f.Ps), Sha(f.Cs) }, (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(f.Vs, got[Sha(f.Vs)]);
        Assert.Equal(f.Ps, got[Sha(f.Ps)]);
        Assert.Equal(f.Cs, got[Sha(f.Cs)]);
    }

    [Fact]
    public void Unwrap_ChecksTheHeader()
    {
        var f = Pasc(Data.Value.Cs);
        Assert.Equal(Data.Value.Cs, BlackSpaceReader.Unwrap(f).ToArray());
        var size = f.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(size.AsSpan(8), (uint)f.Length + 1);
        Assert.True(BlackSpaceReader.Unwrap(size).IsEmpty);
        var header = f.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 40);
        Assert.True(BlackSpaceReader.Unwrap(header).IsEmpty);
        Assert.True(BlackSpaceReader.Unwrap(f[..^1]).IsEmpty);
    }
}
