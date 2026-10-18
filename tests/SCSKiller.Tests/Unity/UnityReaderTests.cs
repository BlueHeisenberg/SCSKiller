using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using SCSKiller.Core;
using SCSKiller.Core.Unity;
using SCSKiller.Tests.Carved;

namespace SCSKiller.Tests.Unity;

/// <summary>Synthetic Unity files: a SerializedFile (format 22, stripped type tree) with a Shader object whose LZ4 blob holds
/// real DXBC behind Unity's per-program prefix, a raw ComputeShader and BuildSettings; the same file inside a UnityFS bundle
/// (LZ4HC blocks info, one LZ4 and one LZMA data block); a fake install read end to end.</summary>
public class UnityReaderTests
{
    static readonly byte[] Vs = Hlsl.Vs(1, Hlsl.Rs1), Ps = Hlsl.Ps(2, Hlsl.Rs1);
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    [Fact]
    public void ShaderBlobSkipsDecoysAndReadsBothLayouts()
    {
        var programs = Programs(Vs, Ps);
        foreach (var nested in new[] { true, false })
        {
            var blob = UnityFiles.ShaderBlob(ShaderObject(programs, nested));
            Assert.NotNull(blob);
            Assert.Equal(new[] { Sha(Vs), Sha(Ps) }, blob!.SelectMany(b => SCSKiller.Core.Carved.Dxbc.Containers(b)).Select(c => Sha(c.Container)));
        }
        Assert.Null(UnityFiles.ShaderBlob(ShaderObject(programs, true, platform: 18))); // a Vulkan-only shader: no d3d11 code
    }

    [Fact]
    public void ReadsSerializedFileAndBundle()
    {
        var file = Serialized(Shader(ShaderObject(Programs(Vs, Ps), true)), Compute(Vs), Build(18, 2));
        var sf = UnityFiles.ReadSerialized(At(file), file.Length)!;
        Assert.Equal("2022.3.1f1", sf.UnityVersion);
        Assert.Equal(new[] { UnityFiles.ShaderClass, UnityFiles.ComputeShaderClass, UnityFiles.BuildSettingsClass }, sf.Objects.Select(o => o.ClassId));
        Assert.Null(UnityFiles.ReadSerialized(At(file[..^1]), file.Length - 1)); // size mismatch: not a serialized file

        var dir = Temp();
        var path = Path.Combine(dir, "x.bundle");
        File.WriteAllBytes(path, Bundle(file, "CAB-0123"));
        using var b = UnityFiles.Bundle.Open(path)!;
        Assert.Equal("CAB-0123", Assert.Single(b.Nodes).Path);
        Assert.Equal(file, b.Read(0, file.Length));                   // across the LZ4 and the LZMA block
        var bs = UnityFiles.ReadSerialized(b.Reader(b.Nodes[0]), file.Length)!.Objects[2];
        Assert.Equal(new[] { 18, 2 }, UnityFiles.GraphicsApis(b.Read(bs.Start, (int)bs.Size)));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void FakeInstallEndToEnd()
    {
        var dir = Temp();
        var data = Directory.CreateDirectory(Path.Combine(dir, "Game_Data")).FullName;
        File.WriteAllBytes(Path.Combine(dir, "UnityPlayer.dll"), []);
        File.WriteAllBytes(Path.Combine(data, "globalgamemanagers"), Serialized(Build(2)));
        var cs = Ps; // any container stands in for a raw compute kernel
        Directory.CreateDirectory(Path.Combine(data, "StreamingAssets"));
        File.WriteAllBytes(Path.Combine(data, "StreamingAssets", "a.bundle"), Bundle(Serialized(Shader(ShaderObject(Programs(Vs, Ps), true)), Compute(cs)), "CAB-a"));
        File.WriteAllBytes(Path.Combine(data, "StreamingAssets", "b.bundle"), Bundle(Serialized(Shader(ShaderObject(Programs(Vs), false))), "CAB-b"));
        File.WriteAllBytes(Path.Combine(data, "sharedassets0.assets.resS"), new byte[4096]);

        var game = new Game("test:unity", "Unity test", Store.Other, dir, Path.Combine(dir, "Game.exe"));
        var reader = new UnityReader();
        var e = reader.Detect(game)!;
        Assert.Equal(("Unity", "2022.3.1f1", "D3D11"), (e.Family, e.Version, e.GraphicsApi));
        Assert.Null(reader.Detect(game with { InstallDir = data, ExePath = Path.Combine(data, "x.exe") })); // no UnityPlayer.dll there

        var index = reader.Index(game, e, null, CancellationToken.None);
        Assert.Equal(new[] { Sha(Vs), Sha(Ps) }.Order(), index.Shaders.Keys.Order());
        Assert.Equal(new[] { Stage.Vertex, Stage.Pixel }, new[] { Sha(Vs), Sha(Ps) }.Select(h => index.Shaders[h].Stage));
        Assert.Equal(3, index.Maps.Count); // shader a (VS+PS), compute a (PS stand-in), shader b (VS)
        Assert.All(index.Maps, m => Assert.Equal("PCD3D_SM5", m.Platform));

        var got = new Dictionary<string, byte[]>();
        new UnityReader().ReadShaders(game, e, new HashSet<string> { Sha(Ps) }, (h, b) => got[h] = b, CancellationToken.None); // re-indexes
        Assert.Equal(Ps, got[Sha(Ps)]);

        // Detect probes the built-in shaders: none readable (here: Vulkan only) -> Unsupported, not an empty index later
        File.WriteAllBytes(Path.Combine(data, "globalgamemanagers.assets"), Serialized(Shader(ShaderObject(Programs(Vs), true, platform: 18))));
        Assert.NotNull(reader.Detect(game)!.Unsupported);
        File.WriteAllBytes(Path.Combine(data, "globalgamemanagers.assets"), Serialized(Shader(ShaderObject(Programs(Vs), true))));
        Assert.Null(reader.Detect(game)!.Unsupported);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void HiddenDataFolderIsRead()
    {
        foreach (var attr in new[] { FileAttributes.Hidden, FileAttributes.Hidden | FileAttributes.System })
        {
            var dir = Temp();
            var data = Directory.CreateDirectory(Path.Combine(dir, "Game_Data")).FullName;
            File.WriteAllBytes(Path.Combine(dir, "UnityPlayer.dll"), []);
            File.WriteAllBytes(Path.Combine(data, "globalgamemanagers"), Serialized(Build(2)));
            Directory.CreateDirectory(Path.Combine(data, "StreamingAssets"));
            File.WriteAllBytes(Path.Combine(data, "StreamingAssets", "a.bundle"), Bundle(Serialized(Shader(ShaderObject(Programs(Vs, Ps), true))), "CAB-a"));
            // Explorer's Hidden applies to the folder, its subfolders and files
            foreach (var p in Directory.EnumerateFileSystemEntries(data, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Prepend(data))
                File.SetAttributes(p, File.GetAttributes(p) | attr);

            var game = new Game("test:unity", "Unity test", Store.Other, dir, Path.Combine(dir, "Game.exe"));
            var reader = new UnityReader();
            var e = reader.Detect(game)!;
            Assert.Equal(("2022.3.1f1", "D3D11"), (e.Version, e.GraphicsApi));
            Assert.Equal(new[] { Sha(Vs), Sha(Ps) }.Order(), reader.Index(game, e, null, CancellationToken.None).Shaders.Keys.Order());
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApiFromTheListOrTheLastRun()
    {
        Assert.Equal("D3D12", UnityReader.Api([18, 2], null));
        Assert.Equal("D3D11", UnityReader.Api([2, 21], null));
        Assert.Equal("D3D11 (last run)", UnityReader.Api([18, 2], "D3D11"));
        Assert.Equal("D3D11 or D3D12", UnityReader.Api(null, null));
        Assert.Null(UnityFiles.GraphicsApis([1, 0, 0, 0, 7, 0, 0, 0])); // 7 isn't a desktop renderer
        Assert.True(UnityReader.ApiGuessed([2, 18], null));   // DirectX 11 and 12 built, no log: the first is a guess
        Assert.True(UnityReader.ApiGuessed([18, 21, 2], null));
        Assert.False(UnityReader.ApiGuessed([18, 2], "D3D11"));
        Assert.False(UnityReader.ApiGuessed([2, 21], null));
        Assert.False(UnityReader.ApiGuessed([21, 2, 18], null));   // Vulkan first: no DirectX guessed
        Assert.False(UnityReader.ApiGuessed(null, null));
    }

    static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-tests", "unity-" + Guid.NewGuid().ToString("n")[..8])).FullName;

    static UnityFiles.ReadAt At(byte[] b) => (o, n) => b.AsSpan((int)o, (int)Math.Max(0, Math.Min(n, b.Length - o))).ToArray();

    /// <summary>A decompressed d3d11 blob: a program count, then each program as Unity stores it (a few bytes of its own
    /// before the container).</summary>
    static byte[] Programs(params byte[][] containers)
    {
        var w = new List<byte>(Le(containers.Length));
        foreach (var c in containers) w.AddRange([.. Le(201806140), 0x05, 0x00, .. c, 0, 0]);
        return [.. w];
    }

    /// <summary>A Shader object: junk standing in for m_ParsedForm with a decoy that has the blob's shape but isn't LZ4,
    /// then platforms / offsets / compressedLengths / decompressedLengths / compressedBlob, then trailing fields.</summary>
    static byte[] ShaderObject(byte[] programs, bool nested, uint platform = UnityFiles.D3D11Platform)
    {
        var lz = new byte[LZ4Codec.MaximumOutputSize(programs.Length)];
        lz = lz[..LZ4Codec.Encode(programs, lz, LZ4Level.L09_HC)];
        var w = new List<byte>();
        w.AddRange(Encoding.ASCII.GetBytes("Hidden/Test\0\0\0\0\0"));
        void Arrays(uint off, uint cl, uint dl)
        {
            foreach (var v in new[] { off, cl, dl })
                if (nested) w.AddRange([.. Le(1), .. Le(1), .. Le((int)v)]);
                else w.AddRange([.. Le(1), .. Le((int)v)]);
        }
        w.AddRange([.. Le(1), .. Le(4)]);                   // decoy: platforms [4], 8 bytes that decode to nothing
        Arrays(0, 8, 100);
        w.AddRange([.. Le(8), .. Enumerable.Repeat((byte)0xFF, 8)]);
        w.AddRange([.. Le(1), .. Le((int)platform)]);
        Arrays(0, (uint)lz.Length, (uint)programs.Length);
        w.AddRange(Le(lz.Length));
        w.AddRange(lz);
        while (w.Count % 4 != 0) w.Add(0);
        w.AddRange([.. Le(0), .. Le(0), 0, 0, 0, 0]);      // m_Dependencies, m_NonModifiableTextures, m_ShaderIsBaked
        return [.. w];
    }

    static byte[] BuildSettings(params int[] apis) =>
        [.. Le(1), .. Le(18), .. Encoding.ASCII.GetBytes("Assets/Main.unity"), 0, 0, 0, .. Le(10), .. Encoding.ASCII.GetBytes("2022.3.1f1"), 0, 0,
            .. Le(apis.Length), .. apis.SelectMany(Le)];

    static (int, byte[]) Shader(byte[] b) => (UnityFiles.ShaderClass, b);
    static (int, byte[]) Compute(byte[] b) => (UnityFiles.ComputeShaderClass, b);
    internal static (int, byte[]) Build(params int[] apis) => (UnityFiles.BuildSettingsClass, BuildSettings(apis));

    /// <summary>A format-22 SerializedFile with the given objects (class id, bytes), one type per object.</summary>
    internal static byte[] Serialized(params (int Class, byte[] Bytes)[] objs)
    {
        var classes = objs.Select(o => o.Class).ToArray();
        var objects = objs.Select(o => o.Bytes).ToArray();
        var meta = new List<byte>();
        meta.AddRange([.. Encoding.ASCII.GetBytes("2022.3.1f1"), 0, .. Le(19), 0, .. Le(classes.Length)]);
        foreach (var c in classes) meta.AddRange([.. Le(c), 0, 0xFF, 0xFF, .. new byte[16]]);
        meta.AddRange(Le(objects.Length));
        long at = 0;
        var starts = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            while (meta.Count % 4 != 0) meta.Add(0);
            starts.Add(at);
            meta.AddRange([.. BitConverter.GetBytes((long)i + 1), .. BitConverter.GetBytes(at), .. Le(objects[i].Length), .. Le(i)]);
            at = (at + objects[i].Length + 7) & ~7L;
        }
        meta.AddRange(Le(0)); // script types, externals...: not read
        var dataOffset = (48 + meta.Count + 15) & ~15;
        var file = new byte[dataOffset + at];
        var h = file.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], 22);
        BinaryPrimitives.WriteUInt32BigEndian(h[20..], (uint)meta.Count);
        BinaryPrimitives.WriteInt64BigEndian(h[24..], file.Length);
        BinaryPrimitives.WriteInt64BigEndian(h[32..], dataOffset);
        meta.ToArray().CopyTo(file, 48);
        for (var i = 0; i < objects.Length; i++) objects[i].CopyTo(file, dataOffset + starts[i]);
        return file;
    }

    /// <summary>A UnityFS (format 8) bundle holding one node: blocks info LZ4HC, the data split into an LZ4 and an LZMA block.</summary>
    static byte[] Bundle(byte[] content, string node)
    {
        var half = content.Length / 2;
        byte[] a = content[..half], b = content[half..];
        var lzA = new byte[LZ4Codec.MaximumOutputSize(a.Length)];
        lzA = lzA[..LZ4Codec.Encode(a, lzA, LZ4Level.L09_HC)];
        var lzmaB = Lzma(b);
        var info = new List<byte>(new byte[16]);
        info.AddRange([.. Be(2), .. Be(a.Length), .. Be(lzA.Length), 0, 2, .. Be(b.Length), .. Be(lzmaB.Length), 0, 1]);
        info.AddRange([.. Be(1), .. Be64(0), .. Be64(content.Length), .. Be(4), .. Encoding.ASCII.GetBytes(node), 0]);
        var infoBytes = info.ToArray();
        var infoLz = new byte[LZ4Codec.MaximumOutputSize(infoBytes.Length)];
        infoLz = infoLz[..LZ4Codec.Encode(infoBytes, infoLz, LZ4Level.L09_HC)];

        var w = new List<byte>();
        w.AddRange([.. Encoding.ASCII.GetBytes("UnityFS"), 0, .. Be(8), .. Encoding.ASCII.GetBytes("5.x.x"), 0, .. Encoding.ASCII.GetBytes("2022.3.1f1"), 0]);
        var sizeAt = w.Count;
        w.AddRange([.. Be64(0), .. Be(infoLz.Length), .. Be(infoBytes.Length), .. Be(3 | 0x40 | 0x200)]);
        while (w.Count % 16 != 0) w.Add(0);
        w.AddRange(infoLz);
        while (w.Count % 16 != 0) w.Add(0);
        w.AddRange(lzA);
        w.AddRange(lzmaB);
        var bytes = w.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(sizeAt), bytes.Length);
        return bytes;
    }

    static byte[] Lzma(byte[] src)
    {
        var enc = new SevenZip.Compression.LZMA.Encoder();
        using var o = new MemoryStream();
        enc.WriteCoderProperties(o);
        using var i = new MemoryStream(src);
        enc.Code(i, o, -1, -1, null);
        return o.ToArray();
    }

    static byte[] Le(int v) => BitConverter.GetBytes(v);
    static byte[] Be(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    static byte[] Be64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }

    /// <summary>A block whose data the file doesn't hold is refused at open, before a read allocates it; a closed bundle
    /// keeps none of its decompressed blocks (the index keeps every bundle it opened until it ends).</summary>
    [Fact]
    public void BundleBlocksAreCheckedAtOpenAndFreedAtClose()
    {
        var dir = Temp();
        var path = Path.Combine(dir, "x.bundle");
        var content = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var bytes = Bundle(content, "CAB-0123");
        File.WriteAllBytes(path, bytes[..^20]);
        Assert.Throws<InvalidDataException>(() => UnityFiles.Bundle.Open(path));

        File.WriteAllBytes(path, bytes);
        var b = UnityFiles.Bundle.Open(path)!;
        Assert.Equal(content, b.Read(0, content.Length));
        b.Dispose();
        Assert.Throws<ObjectDisposedException>(() => b.Read(0, content.Length));
        Directory.Delete(dir, true);
    }

    /// <summary>A bundle keeps the 16 blocks it read last, not all of them: a block read again after 16 others is
    /// decompressed again; one still kept isn't. Reads at any offset find their block.</summary>
    [Fact]
    public void ABundleKeepsOnlyItsLastReadBlocks()
    {
        var dir = Temp();
        var path = Path.Combine(dir, "x.bundle");
        const int Size = 100, Kept = 16, Count = 3 * Kept;
        var content = Enumerable.Range(0, Size * Count).Select(i => (byte)(i * 7 + i / 251)).ToArray();
        File.WriteAllBytes(path, StoredBundle(content, "CAB-0123", Size));
        using (var b = UnityFiles.Bundle.Open(path)!)
        {
            foreach (var at in new[] { 0, 1, 99, 100, 150, 4795 }) Assert.Equal(content[at..(at + 5)], b.Read(at, 5));
            var before = b.BlocksDecompressed;
            b.Read(0, 1);
            Assert.Equal(before, b.BlocksDecompressed);   // block 0 was read just now
            for (var i = 1; i <= Kept; i++) b.Read(i * Size, 1);
            before = b.BlocksDecompressed;
            b.Read(0, 1);
            Assert.Equal(before + 1, b.BlocksDecompressed);
            Assert.Equal(content, b.Read(0, content.Length));
        }
        Directory.Delete(dir, true);
    }

    /// <summary>A UnityFS (format 8) bundle holding one node, its data in uncompressed blocks of <paramref name="size"/> bytes.</summary>
    static byte[] StoredBundle(byte[] content, string node, int size)
    {
        var info = new List<byte>(new byte[16]);
        var n = (content.Length + size - 1) / size;
        info.AddRange(Be(n));
        for (var i = 0; i < n; i++)
        {
            var len = Math.Min(size, content.Length - i * size);
            info.AddRange([.. Be(len), .. Be(len), 0, 0]);
        }
        info.AddRange([.. Be(1), .. Be64(0), .. Be64(content.Length), .. Be(4), .. Encoding.ASCII.GetBytes(node), 0]);
        var w = new List<byte>();
        w.AddRange([.. Encoding.ASCII.GetBytes("UnityFS"), 0, .. Be(8), .. Encoding.ASCII.GetBytes("5.x.x"), 0, .. Encoding.ASCII.GetBytes("2022.3.1f1"), 0]);
        var sizeAt = w.Count;
        w.AddRange([.. Be64(0), .. Be(info.Count), .. Be(info.Count), .. Be(0x40 | 0x200)]);
        while (w.Count % 16 != 0) w.Add(0);
        w.AddRange(info);
        while (w.Count % 16 != 0) w.Add(0);
        w.AddRange(content);
        var bytes = w.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(sizeAt), bytes.Length);
        return bytes;
    }
}
