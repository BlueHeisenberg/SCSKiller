using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.FromSoft;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.FromSoft;

/// <summary>FromSoftware containers built in the test (no game content): an RSA-"encrypted" BHD5 with an AES range, DFLT DCX,
/// BND4 with a DCX-compressed file, a loose-file install (Dark Souls: Remastered's layout) and an archive install whose keys
/// come from a fake exe's PEM blocks, read end to end. Every key is generated here (no game's).</summary>
public class FromSoftFormatTests
{
    [Fact]
    public void NameHashNormalizesAndUsesTheGamesPrime()
    {
        Assert.Equal(47UL * 37 + 'a', Souls.NameHash("a", false));    // leading '/' added, h * 37 + c
        Assert.Equal(47UL * 133 + 'a', Souls.NameHash("/A", true));   // lower case, h * 0x85 + c (Elden Ring)
        Assert.Equal(Souls.NameHash("/shader/gxgui.shaderbnd.dcx", true), Souls.NameHash(@"Shader\GXGui.shaderbnd.dcx ", true));
        Assert.True(Souls.NameHash("/shader/gxgui.shaderbnd.dcx", false) <= uint.MaxValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BhdDecryptsParsesAndReadsAesRanges(bool hash64)
    {
        using var rsa = RSA.Create(2048);
        var file0 = RandomNumberGenerator.GetBytes(100);
        var file1 = RandomNumberGenerator.GetBytes(80);   // its first 32 bytes AES-encrypted in the .bdt
        var aesKey = RandomNumberGenerator.GetBytes(16);
        var bdt = new byte[16 + 100 + 12 + 80];
        file0.CopyTo(bdt, 16);
        var enc = file1.ToArray();
        using (var aes = Aes.Create()) { aes.Key = aesKey; aes.EncryptEcb(file1.AsSpan(0, 32), enc.AsSpan(0, 32), PaddingMode.None); }
        enc.CopyTo(bdt, 128);
        (ulong Hash, long Off, int Size) e0 = (Souls.NameHash("/a/one.dcx", hash64), 16, 100), e1 = (Souls.NameHash("/b/two.dcx", hash64), 128, 80);
        var (padded, cipher) = Bhd(rsa, hash64, (e0.Hash, e0.Off, e0.Size, null), (e1.Hash, e1.Off, e1.Size, aesKey));
        var key = Convert.ToBase64String(rsa.ExportRSAPublicKey());
        var decrypted = Souls.DecryptBhd(cipher, key);
        Assert.Equal(padded, decrypted);

        var entries = Souls.ParseBhd(decrypted, hash64);
        Assert.Equal([(e0.Hash, 16L, 100L), (e1.Hash, 128L, 80L)], entries.Select(e => (e.Hash, e.Offset, e.Size)));
        Assert.Equal(aesKey, entries[1].AesKey);
        var path = Path.Combine(Ff7.TempDir($"bhd-{hash64}"), "Data0.bdt");
        File.WriteAllBytes(path, bdt);
        using var bdtFile = File.OpenHandle(path);
        Assert.Equal(file0, Souls.Read(bdtFile, entries[0]));
        Assert.Equal(file1, Souls.Read(bdtFile, entries[1]));

        using var other = RSA.Create(2048);
        byte[]? wrong = null;
        try { wrong = Souls.DecryptBhd(cipher, Convert.ToBase64String(other.ExportRSAPublicKey())); }
        catch (AggregateException) { } // another game's key: garbage, or a block the other modulus can't hold
        Assert.NotEqual(padded, wrong);
        if (wrong != null) Assert.ThrowsAny<Exception>(() => Souls.ParseBhd(wrong, hash64));
    }

    [Fact]
    public void DcxInflatesDflt()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("FromSoftware DCX ", 200)));
        var dcx = Dflt(content);
        Assert.True(Souls.IsDcx(dcx));
        Assert.Equal("DFLT", Souls.DcxCodec(dcx));
        Assert.Equal(content, Souls.Dcx(dcx));
        Assert.Throws<NotSupportedException>(() => Souls.Dcx(Codec(dcx, "EDGE")));
    }

    [Fact]
    public void Bnd4ListsNamedFilesAndInflatesCompressedOnes()
    {
        var files = new (string Name, byte[] Data, bool Compressed)[] { ("a.vpo", [1, 2, 3], false), ("dir\\b.ppo", Encoding.ASCII.GetBytes("pixel shader bytes"), true) };
        foreach (var unicode in new[] { true, false })
        {
            var bnd = Bnd4(files, unicode);
            Assert.True(Souls.IsBnd4(bnd));
            Assert.Equal(files.Select(f => (f.Name, Convert.ToHexString(f.Data))), Souls.Bnd4(bnd).Select(f => (f.Name, Convert.ToHexString(f.Data))));
        }
    }

    /// <summary>Dark Souls: Remastered's layout (loose shader\*.shaderbnd.dcx next to the exe), built from real compiled
    /// shaders: detect, index (one pool per binder), read back by hash.</summary>
    [Fact]
    public void LooseInstallIndexesEndToEnd()
    {
        var dir = Ff7.TempDir("fromsoft-loose");
        File.WriteAllBytes(Path.Combine(dir, "DarkSoulsRemastered.exe"), []);
        Directory.CreateDirectory(Path.Combine(dir, "shader"));
        byte[] vs = Hlsl.Vs(1, Hlsl.Rs1), ps = Hlsl.Ps(1, Hlsl.Rs1), vs2 = Hlsl.Vs(2, Hlsl.Rs1);
        File.WriteAllBytes(Path.Combine(dir, "shader", "A_DX11.shaderbnd.dcx"), Dflt(Bnd4([("A.vpo", vs, false), ("A.fpo", ps, true)], true)));
        File.WriteAllBytes(Path.Combine(dir, "shader", "B_DX11.shaderbnd.dcx"), Dflt(Bnd4([("B.vpo", [.. "hdr"u8, .. vs2, 0, 0], false)], false))); // a container behind a header: carved
        File.WriteAllBytes(Path.Combine(dir, "shader", "C_DX9.shaderbnd.dcx"), Dflt("no shaders here"u8.ToArray()));
        var game = new Game("test:dsr", "dsr", Store.Steam, dir, Path.Combine(dir, "DarkSoulsRemastered.exe"));

        var reader = new FromSoftReader(Path.Combine(dir, "data"));
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(FromSoftReader.Family, "DXBC+RTS0", "Dark Souls: Remastered", "D3D11", false, null), engine);
        var index = reader.Index(game, engine, null, CancellationToken.None);
        string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
        Assert.Equal(new[] { vs, ps, vs2 }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Equal([$"/shader/A_DX11.shaderbnd.dcx: {Sha(vs)},{Sha(ps)}", $"/shader/B_DX11.shaderbnd.dcx: {Sha(vs2)}"],
            index.Maps.Select(m => $"{m.Library}: {string.Join(',', m.Shaders)}").Order());
        Assert.All(index.Shaders.Values, s => Assert.NotNull(s.RootSignature));
        Assert.All(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>(), h => Assert.Contains(h, index.RootSignatures!.Keys));   // the planner checks coverage with them
        Assert.Equal(["D3D11"], index.Platforms);

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new HashSet<string> { Sha(ps), Sha(vs2) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(new[] { Sha(ps), Sha(vs2) }.Order(), got.Keys.Order());
        Assert.Equal(ps, got[Sha(ps)]);

        // the content hash follows the files (a patch makes plans stale)
        File.AppendAllText(Path.Combine(dir, "shader", "C_DX9.shaderbnd.dcx"), "x");
        Assert.NotEqual(index.ContentHash, reader.Index(game, engine, null, CancellationToken.None).ContentHash);
        Assert.Null(reader.Detect(game with { ExePath = Path.Combine(dir, "other.exe") }));   // not a title it knows, no archives
    }

    /// <summary>Elden Ring's layout with keys generated here: the fake exe carries each archive's public key as PEM text (plus
    /// a key of no archive), found at run time, matched to its archive, kept only in the data folder and reused; an archive
    /// no key opens is left out (the page says so, the log says where a user can put its key) and the rest compiles.</summary>
    [Fact]
    public void ArchiveKeysComeFromTheExe()
    {
        var dir = Ff7.TempDir("fromsoft-archives");
        using RSA k0 = RSA.Create(2048), k1 = RSA.Create(2048), decoy = RSA.Create(2048), missing = RSA.Create(2048);
        byte[] vs = Hlsl.Vs(1, Hlsl.Rs1), ps = Hlsl.Ps(1, Hlsl.Rs1), vs2 = Hlsl.Vs(2, Hlsl.Rs1);
        void Archive(string name, RSA rsa, string file, byte[] content)
        {
            var bdt = new byte[16].Concat(content).ToArray();
            File.WriteAllBytes(Path.Combine(dir, name + ".bdt"), bdt);
            File.WriteAllBytes(Path.Combine(dir, name + ".bhd"), Bhd(rsa, true, (Souls.NameHash(file, true), 16, content.Length, null)).Cipher);
        }
        Archive("Data0", k0, "/shader/gxgui.shaderbnd.dcx", Dflt(Bnd4([("A.vpo", vs, false), ("A.fpo", ps, true)], true)));
        Archive("Data1", k1, "/shader/gxposteffect.shaderbnd.dcx", Dflt(Bnd4([("B.vpo", vs2, false)], true)));
        var exe = Path.Combine(dir, "eldenring.exe");
        File.WriteAllBytes(exe, [.. RandomNumberGenerator.GetBytes(5000), .. Encoding.ASCII.GetBytes(Pem(k1) + "\0\0" + Pem(decoy) + "\0"),
            .. RandomNumberGenerator.GetBytes(3000), .. Encoding.ASCII.GetBytes(Pem(k0) + "\0-----BEGIN RSA PUBLIC KEY-----\nnot base64\n-----END RSA PUBLIC KEY-----\n")]);
        var game = new Game("test:er", "er", Store.Steam, dir, exe);
        var data = Path.Combine(dir, "data");
        var reader = new FromSoftReader(data, Offline);

        var engine = reader.Detect(game)!;
        Assert.Equal((FromSoftReader.Family, "Elden Ring", (string?)null, (string?)null), (engine.Family, engine.Fork, engine.Unsupported, engine.Note));
        var index = reader.Index(game, engine, null, CancellationToken.None);
        string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
        Assert.Equal(new[] { vs, ps, vs2 }.Select(Sha).Order(), index.Shaders.Keys.Order());
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new HashSet<string> { Sha(vs2) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(vs2, got[Sha(vs2)]);

        // kept locally with the exe's hash, and reused without the exe
        var keyFile = new SoulsKeys(data).KeyFile(game);
        var lines = File.ReadAllLines(keyFile);
        Assert.Equal($"exe {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(exe)))}", string.Join(' ', lines[0].Split(' ')[..2]));
        Assert.Equal([$"Data0 {Convert.ToBase64String(k0.ExportRSAPublicKey())}", $"Data1 {Convert.ToBase64String(k1.ExportRSAPublicKey())}"], lines[1..]);
        File.Move(exe, exe + ".gone");
        Assert.Null(new FromSoftReader(data, Offline).Detect(game)!.Unsupported);
        File.Move(exe + ".gone", exe);
        Assert.Equal("", reader.IndexStamp(game));

        // an archive no key opens (the exe doesn't carry it, offline): left out, the rest compiles, the page says so plainly
        var dlc = Hlsl.Ps(2, Hlsl.Rs1);
        Archive("DLC", missing, "/shader/grass.shaderbnd.dcx", Dflt(Bnd4([("C.fpo", dlc, false)], true)));
        var asked = new List<string>();
        var skip = new FromSoftReader(data, u => { asked.Add(u); return null; });
        var partial = skip.Detect(game)!;
        Assert.Equal(((string?)null, "Its DLC shaders are left out."), (partial.Unsupported, partial.Note));
        Assert.Equal(partial.Note, Core.App.ScsKiller.GuessNote(partial));
        Assert.Equal([SoulsKeys.Uxm], asked); // Elden Ring's keys are tried from UXM's file too
        Assert.Equal("DLC", skip.IndexStamp(game));
        Assert.Equal(skip.IndexStamp(game), skip.DetectStamp(game, partial));
        var logged = new List<string>();
        var left = skip.Index(game, partial, new SyncLog(logged.Add), CancellationToken.None);
        Assert.Equal(index.Shaders.Keys.Order(), left.Shaders.Keys.Order());
        Assert.Equal(index.ContentHash, left.ContentHash); // only the archives a key opens: the same as before the DLC
        Assert.Contains(logged, l => l.Contains(keyFile) && l.Contains("PEM"));
        Assert.DoesNotContain("PEM", partial.Note);
        Assert.DoesNotContain(keyFile, partial.Note);
        got.Clear();
        skip.ReadShaders(game, partial, new HashSet<string> { Sha(vs2) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(vs2, got[Sha(vs2)]);

        // its key turns up later (UXM's pinned file, downloaded): kept, the stamp and the content hash change, the DLC is indexed
        byte[] uxm = Encoding.UTF8.GetBytes(UxmSource(("EldenRingKeys", [("Data0", k0), ("DLC", missing)])));
        var all = new FromSoftReader(data, _ => uxm, Sha256(uxm));
        var allEngine = all.Detect(game)!;
        Assert.Equal($"DLC {Convert.ToBase64String(missing.ExportRSAPublicKey())}", File.ReadAllLines(keyFile)[^1]);
        Assert.Equal("", skip.IndexStamp(game));
        Assert.Equal(((string?)null, (string?)null), (allEngine.Unsupported, allEngine.Note));
        var full = all.Index(game, allEngine, null, CancellationToken.None);
        Assert.Equal(4, full.Shaders.Count);
        Assert.Contains(Sha(dlc), full.Shaders.Keys);
        Assert.NotEqual(left.ContentHash, full.ContentHash);
    }

    /// <summary>Every archive keyless: refused in plain words (Index too), where to put the keys only in the log.</summary>
    [Fact]
    public void NoArchiveAKeyOpensIsRefusedWithoutKeyFileText()
    {
        var dir = Ff7.TempDir("fromsoft-nokey");
        using RSA k0 = RSA.Create(2048);
        var content = Dflt(Bnd4([("A.vpo", Hlsl.Vs(1, Hlsl.Rs1), false)], true));
        File.WriteAllBytes(Path.Combine(dir, "Data0.bdt"), new byte[16].Concat(content).ToArray());
        File.WriteAllBytes(Path.Combine(dir, "Data0.bhd"), Bhd(k0, true, (Souls.NameHash("/shader/gxgui.shaderbnd.dcx", true), 16, content.Length, null)).Cipher);
        var exe = Path.Combine(dir, "eldenring.exe");
        File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(4000));
        var game = new Game("test:er-nokey", "er", Store.Steam, dir, exe);
        var data = Path.Combine(dir, "data");
        var reader = new FromSoftReader(data, Offline);
        var keyFile = new SoulsKeys(data).KeyFile(game);

        var engine = reader.Detect(game)!;
        Assert.Equal("no key found for its archives, and none could be downloaded", engine.Unsupported);
        var logged = new List<string>();
        var thrown = Assert.Throws<InvalidDataException>(() => reader.Index(game, engine, new SyncLog(logged.Add), CancellationToken.None));
        Assert.Equal(engine.Unsupported, thrown.Message);
        Assert.Contains(logged, l => l.Contains(keyFile) && l.Contains("PEM"));
    }

    static readonly Func<string, byte[]?> Offline = _ => null;

    static string Sha256(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    static string Pem(RSA rsa) => new(PemEncoding.Write("RSA PUBLIC KEY", rsa.ExportRSAPublicKey()));

    /// <summary>UXM's ArchiveKeys.cs shape (one C# dictionary of PEM strings per game), with generated keys: only the named
    /// game's section is read.</summary>
    static string UxmSource(params (string Section, (string Archive, RSA Key)[] Keys)[] games) =>
        "using System.Collections.Generic;\nnamespace UXM\n{\n    static class ArchiveKeys\n    {\n" + string.Concat(games.Select(g =>
            $"        public static Dictionary<string, string> {g.Section} = new Dictionary<string, string>\n        {{\n"
            + string.Concat(g.Keys.Select(k => $"            [\"{k.Archive}\"] =\n@\"{Pem(k.Key).Replace("\n", "\r\n")}\",\n\n")) + "        };\n\n")) + "    }\n}\n";

    [Fact]
    public void UxmKeysReadsOnlyTheTitlesSection()
    {
        using RSA a = RSA.Create(2048), b = RSA.Create(2048), c = RSA.Create(2048);
        var src = UxmSource(("DarkSouls3KeysOld", [("Data1", c)]), ("SekiroKeys", [("Data1", a)]), ("DarkSouls3Keys", [("Data1", b), ("DLC1", c)]),
            ("EldenRingKeys", [("Data0", a), ("DLC", b), (@"sd\\sd", c)]), ("EldenRingNightreignKeys", [("Data0", c)]));
        string B64(RSA k) => Convert.ToBase64String(k.ExportRSAPublicKey());
        Assert.Equal([B64(b), B64(c)], SoulsKeys.UxmKeys(src, "DarkSouls3Keys"));
        Assert.Equal([B64(a), B64(b), B64(c)], SoulsKeys.UxmKeys(src, "EldenRingKeys")); // an archive name like UXM's ["sd\\sd"] doesn't stop it
        Assert.Empty(SoulsKeys.UxmKeys(src, "ArmoredCore6Keys"));
        Assert.Empty(SoulsKeys.UxmKeys("not C# at all", "DarkSouls3Keys"));
    }

    /// <summary>Dark Souls III's case with keys generated here: the exe has none, so the keys come from UXM's published file
    /// (a fake download of its shape): only keys that open the archives are kept, and a failed download, or one that isn't
    /// the pinned file, leaves the game Unsupported in plain words with nothing written; the next Detect tries again.</summary>
    [Fact]
    public void ArchiveKeysAreDownloadedWhenTheExeHasNone()
    {
        var dir = Ff7.TempDir("fromsoft-download");
        using RSA k1 = RSA.Create(2048), k2 = RSA.Create(2048), decoy = RSA.Create(2048);
        byte[] vs = Hlsl.Vs(1, Hlsl.Rs1), ps = Hlsl.Ps(1, Hlsl.Rs1);
        void Archive(string name, RSA rsa, string file, byte[] content)
        {
            File.WriteAllBytes(Path.Combine(dir, name + ".bdt"), new byte[16].Concat(content).ToArray());
            File.WriteAllBytes(Path.Combine(dir, name + ".bhd"), Bhd(rsa, false, (Souls.NameHash(file, false), 16, content.Length, null)).Cipher);
        }
        Archive("Data1", k1, "/shader/gxgui.shaderbnd.dcx", Dflt(Bnd4([("A.vpo", vs, false)], true)));
        Archive("DLC1", k2, "/shader/gxposteffect.shaderbnd.dcx", Dflt(Bnd4([("B.fpo", ps, false)], true)));
        var exe = Path.Combine(dir, "DarkSoulsIII.exe");
        File.WriteAllBytes(exe, RandomNumberGenerator.GetBytes(4000));
        var game = new Game("test:ds3", "ds3", Store.Steam, dir, exe);
        var data = Path.Combine(dir, "data");
        var keyFile = new SoulsKeys(data).KeyFile(game);

        var asked = new List<string>();
        var offline = new FromSoftReader(data, u => { asked.Add(u); return null; }).Detect(game)!;
        Assert.Equal("no key found for its archives, and none could be downloaded", offline.Unsupported);
        Assert.Equal([SoulsKeys.Uxm], asked);
        Assert.StartsWith("https://raw.githubusercontent.com/Nordgaren/UXM-Selective-Unpack/9501be87e272b6dae55e60a13c3f4753ca6fb3bb/", SoulsKeys.Uxm); // pinned
        Assert.DoesNotContain(File.ReadAllLines(keyFile), l => !l.StartsWith("exe ")); // only the exe scan's stamp

        // served with a BOM, as UXM's file is: the hash covers the bytes as served
        byte[] src = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(UxmSource(("SekiroKeys", [("Data1", k1)]), ("DarkSouls3Keys", [("Data1", decoy), ("Data2", k1), ("DLC1", k2)])))];
        var logged = new List<string>();
        var tampered = new FromSoftReader(data, _ => src); // the real pin: not this file
        Assert.Equal(offline.Unsupported, tampered.Detect(game)!.Unsupported);
        Assert.Throws<InvalidDataException>(() => tampered.Index(game, offline, new SyncLog(logged.Add), CancellationToken.None));
        Assert.Contains(logged, l => l.Contains("not the pinned"));
        Assert.DoesNotContain(File.ReadAllLines(keyFile), l => !l.StartsWith("exe ")); // nothing kept from it
        Assert.Equal(offline.Unsupported, new FromSoftReader(data, _ => src, Sha256(src[3..])).Detect(game)!.Unsupported); // the text's hash isn't the file's

        var engine = new FromSoftReader(data, _ => src, Sha256(src)).Detect(game)!;
        Assert.Equal(("Dark Souls III", (string?)null), (engine.Fork, engine.Unsupported));
        Assert.Equal([$"Data1 {Convert.ToBase64String(k1.ExportRSAPublicKey())}", $"DLC1 {Convert.ToBase64String(k2.ExportRSAPublicKey())}"], File.ReadAllLines(keyFile)[1..]);
        var reader = new FromSoftReader(data, _ => throw new InvalidOperationException("kept locally: no second download"));
        Assert.Equal(2, reader.Index(game, reader.Detect(game)!, null, CancellationToken.None).Shaders.Count);
    }

    // Builders: the layouts SoulsFormats documents, written the way the games write them.

    /// <summary>A BHD5 (one bucket; each file's AES record, if any, covers its first 32 bytes), padded to 255-byte blocks,
    /// and the same "encrypted" with the private key the way the game's files are: each block -> 256 bytes (m^d mod n).</summary>
    static (byte[] Padded, byte[] Cipher) Bhd(RSA rsa, bool hash64, params (ulong Hash, long Off, int Size, byte[]? Aes)[] files)
    {
        var h = new MemoryStream();
        var w = new BinaryWriter(h);
        w.Write("BHD5"u8); w.Write((byte)0xFF); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write(1); w.Write(0); // file size: unread
        var salt = "salt"u8.ToArray();
        long bucketsAt = hash64 ? 0x20 + 4 + salt.Length : 0x18 + 4 + salt.Length;
        if (hash64) { w.Write(1L); w.Write(bucketsAt); } else { w.Write(1); w.Write((int)bucketsAt); }
        w.Write(salt.Length); w.Write(salt);
        var headersAt = bucketsAt + (hash64 ? 16 : 8);
        if (hash64) { w.Write(files.Length); w.Write(1); w.Write(headersAt); } else { w.Write(files.Length); w.Write((int)headersAt); }
        var aesAt = headersAt + 40 * files.Length;
        foreach (var (hash, off, size, key) in files)
        {
            var aes = key == null ? 0L : aesAt;
            if (key != null) aesAt += 16 + 4 + 16;
            if (hash64) { w.Write(hash); w.Write((size + 15) & ~15); w.Write(size); w.Write(off); w.Write(0L); w.Write(aes); }
            else { w.Write((uint)hash); w.Write((size + 15) & ~15); w.Write(off); w.Write(0L); w.Write(aes); w.Write((long)size); }
        }
        foreach (var f in files.Where(f => f.Aes != null)) { w.Write(f.Aes!); w.Write(1); w.Write(0L); w.Write(32L); }
        var plain = h.ToArray();

        var p = rsa.ExportParameters(true);
        BigInteger n = new(p.Modulus, true, true), d = new(p.D, true, true);
        var padded = plain.Concat(new byte[(255 - plain.Length % 255) % 255]).ToArray();
        var cipher = new MemoryStream();
        for (var i = 0; i < padded.Length; i += 255)
        {
            var c = BigInteger.ModPow(new BigInteger(padded.AsSpan(i, 255), true, true), d, n).ToByteArray(true, true);
            cipher.Write(new byte[256 - c.Length]); cipher.Write(c);
        }
        return (padded, cipher.ToArray());
    }

    static byte[] Dflt(byte[] content)
    {
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(content);
        var h = new byte[0x4C];
        "DCX\0"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4), 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(8), 0x18);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(12), 0x24);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(16), 0x24);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(20), 0x2C);
        "DCS\0"u8.CopyTo(h.AsSpan(0x18));
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(0x1C), content.Length);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(0x20), (int)z.Length);
        "DCP\0DFLT"u8.CopyTo(h.AsSpan(0x24));
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(0x2C), 0x20);
        h[0x30] = 9;
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(0x40), 0x00010100);
        "DCA\0"u8.CopyTo(h.AsSpan(0x44));
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(0x48), 8);
        return [.. h, .. z.ToArray()];
    }

    static byte[] Codec(byte[] dcx, string fourcc) { var b = dcx.ToArray(); Encoding.ASCII.GetBytes(fourcc).CopyTo(b, 0x28); return b; }

    /// <summary>A little-endian BND4, format byte 0x74 as FromSoftware writes it (bit-reversed: IDs, names, compression; 0x24-byte
    /// file headers), file flags 0x40 (0xC0 = DCX-compressed).</summary>
    static byte[] Bnd4((string Name, byte[] Data, bool Compressed)[] files, bool unicode)
    {
        const int headerSize = 0x24;
        var stored = files.Select(f => f.Compressed ? Dflt(f.Data) : f.Data).ToArray();
        var names = files.Select(f => unicode ? [.. Encoding.Unicode.GetBytes(f.Name), 0, 0] : Encoding.ASCII.GetBytes(f.Name + "\0")).ToArray();
        var namesAt = 0x40 + headerSize * files.Length;
        var dataAt = namesAt + names.Sum(x => x.Length);
        var b = new MemoryStream();
        var w = new BinaryWriter(b);
        w.Write("BND4"u8); w.Write(new byte[] { 0, 0, 0, 0, 0, 0, 1, 0 }); // bytes 9 (big-endian) 0, 10 (bit big-endian = !this) 1
        w.Write(files.Length); w.Write(0x40L); w.Write("07D7R6\0\0"u8); w.Write((long)headerSize); w.Write((long)dataAt);
        w.Write((byte)(unicode ? 1 : 0)); w.Write((byte)0x74); w.Write((byte)0); w.Write((byte)0); w.Write(0); w.Write(0L);
        int nameOff = namesAt, dataOff = dataAt;
        for (var i = 0; i < files.Length; i++)
        {
            w.Write((byte)(files[i].Compressed ? 0xC0 : 0x40)); w.Write((byte)0); w.Write((short)0); w.Write(-1);
            w.Write((long)stored[i].Length); w.Write((long)files[i].Data.Length); w.Write(dataOff); w.Write(i); w.Write(nameOff);
            nameOff += names[i].Length; dataOff += stored[i].Length;
        }
        foreach (var x in names) w.Write(x);
        foreach (var x in stored) w.Write(x);
        return b.ToArray();
    }

    /// <summary>A binder whose one file is the binder itself: refused, not a stack overflow that ends the process.</summary>
    [Fact]
    public void ASelfReferentialBinderIsRefused()
    {
        var dir = Ff7.TempDir("fromsoft-self");
        File.WriteAllBytes(Path.Combine(dir, "DarkSoulsRemastered.exe"), []);
        Directory.CreateDirectory(Path.Combine(dir, "shader"));
        var bnd = Bnd4([("A.vpo", new byte[16], false)], false);
        BinaryPrimitives.WriteInt64LittleEndian(bnd.AsSpan(0x40 + 8), bnd.Length);   // the file's size: the whole binder
        BinaryPrimitives.WriteInt32LittleEndian(bnd.AsSpan(0x40 + 24), 0);           // its offset: the binder's start
        File.WriteAllBytes(Path.Combine(dir, "shader", "A_DX11.shaderbnd.dcx"), bnd);
        var game = new Game("test:dsr-self", "dsr", Store.Steam, dir, Path.Combine(dir, "DarkSoulsRemastered.exe"));
        Assert.Contains("nested", Assert.Throws<InvalidDataException>(() => new FromSoftReader(Path.Combine(dir, "data")).Detect(game)).Message);
    }

    /// <summary>A DFLT DCX claiming 1 GiB over one byte of data is refused before its output is allocated.</summary>
    [Fact]
    public void ADcxClaimingMoreThanItsDataCanHoldIsRefusedWithoutAllocatingIt()
    {
        var dcx = Dflt(new byte[100]);
        BinaryPrimitives.WriteInt32BigEndian(dcx.AsSpan(0x1C), 1 << 30);
        BinaryPrimitives.WriteInt32BigEndian(dcx.AsSpan(0x20), 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => Souls.Dcx(dcx.AsSpan(0, 0x4C + 1)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 << 20);
    }
}
