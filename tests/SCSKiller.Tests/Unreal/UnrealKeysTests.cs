using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

/// <summary>Encrypted games on the dev machine (skipped when absent). Keys go to temp data dirs; no test prints a key.</summary>
[Trait("Needs", "Game")]
public class UnrealKeysTests(ITestOutputHelper output)
{
    static Game? Installed(string name)
    {
        try { return new SteamSource().Discover().FirstOrDefault(g => g.Name.Contains(name)); }
        catch (Exception) { return null; }
    }

    /// <summary>Windrose Demo (UE 5.6): the key is in its exe as 8 x mov dword, imm32; with it the files open, and they show
    /// the shaders live inside the materials (bShareMaterialShaderCode=False): indexable, see InlineShadersTests.</summary>
    [Fact]
    public void KeyFoundStaticallyStoredAndReused()
    {
        if (Installed("Windrose Demo") is not { } game) return;
        var data = Ff7.TempDir("keys-windrose");
        var sw = Stopwatch.StartNew();
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s: {e}\n{notes}");
        Assert.Contains("AES key: found in the exe", notes);
        Assert.False(e.Encrypted);
        Assert.Null(e.Unsupported);
        Assert.Equal("5.6", e.Version); // TOC version 8 says 5.5+; the package header layout says 5.6
        var keyFile = Path.Combine(data, "games", "steam_4291770", "aes.key");
        var key = File.ReadAllText(keyFile);
        Assert.DoesNotContain(key.Replace("0x", ""), notes, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("AES key: stored key", (new UnrealReader(data).Detect(game, out notes), notes).notes);

        // manual fallback: a wrong key is refused, the right one is stored
        var other = new UnrealReader(Ff7.TempDir("keys-manual"));
        Assert.False(other.SetKey(game, "0x" + new string('7', 64)));
        Assert.False(other.SetKey(game, "not a key"));
        Assert.True(other.SetKey(game, key));
        Assert.Contains("AES key: stored key", (other.Detect(game, out notes), notes).notes);
    }

    /// <summary>DRAGON BALL: Sparking! ZERO (UE 5.1): the fourth key dword ends in 0x41, the byte before the fifth store,
    /// which only reads as a REX prefix. An earlier scanner's remembered failure for the same exe is scanned again.</summary>
    [Fact]
    public void KeyFoundWhenAnImmediateEndsInARexByte()
    {
        if (Installed("Sparking! ZERO") is not { } game) return;
        var data = Ff7.TempDir("keys-dbsz");
        var exe = new FileInfo(game.ExePath);
        Directory.CreateDirectory(Path.Combine(data, "games", "steam_1790600"));
        File.WriteAllLines(Path.Combine(data, "games", "steam_1790600", "aes.scan"), [$"{exe.Length}:{exe.LastWriteTimeUtc.Ticks}", "not found in the exe's code"]);
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{e}\n{notes}");
        Assert.Contains("AES key: found in the exe", notes);
        Assert.False(e.Encrypted);
        Assert.True(e is { NoRayTracing: true, NoRtPipelines: true });   // r.RayTracing=False in its DefaultEngine.ini
    }

    /// <summary>Mafia: The Old Country (UE 5.3): its exe keeps its code outside .text (a protector), so the static scan finds
    /// no key; the verdict is remembered per exe build.</summary>
    [Fact]
    public void ProtectedExeIsReportedAndNotRescanned()
    {
        if (Installed("Mafia: The Old Country") is not { } game) return;
        var data = Ff7.TempDir("keys-mafia");
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{e}\n{notes}");
        Assert.True(e.Encrypted);
        Assert.Contains("looks protected", notes);
        var sw = Stopwatch.StartNew();
        new UnrealReader(data).Detect(game, out var again);
        output.WriteLine($"again: {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("looks protected", again);
        Assert.True(File.Exists(Path.Combine(data, "games", "steam_1941540", "aes.scan")));
    }
}

/// <summary>The static key scan over a synthetic exe: one executable section holding a key callback's stores.</summary>
public class UnrealKeyScanTests
{
    /// <summary>8 x mov dword [rbp+disp8], imm32 as Sparking! ZERO compiles them, with another instruction between the second
    /// and third: the fourth dword's last byte is 0x41, which also reads as a REX.B prefix of the fifth store.</summary>
    [Fact]
    public void KeyFoundWhenAnImmediateEndsInARexByte()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 37 + 11)).ToArray();
        key[15] = 0x41;
        var code = new List<byte> { 0x48, 0x8D, 0x55, 0xEF };
        for (var k = 0; k < 8; k++)
        {
            if (k == 2) code.AddRange([0x48, 0x8D, 0x41, 0x1F]);
            code.AddRange([0xC7, 0x45, (byte)(0xD0 + 4 * k), .. key.AsSpan(4 * k, 4)]);
        }
        var exe = Path.Combine(Ff7.TempDir("keys-synthetic"), "game.exe");
        File.WriteAllBytes(exe, Pe([.. code]));
        var expected = "0x" + Convert.ToHexString(key);
        var found = UnrealKeys.Scan(exe, k => k.KeyString == expected, out var how);
        Assert.Equal(expected, found?.KeyString);
        Assert.Contains("8 x mov dword, imm32", how);
    }

    /// <summary>An anti-cheat game's exe file is scanned like any other: the key it holds is found and stored; one that
    /// doesn't read as a PE image is a failed scan, remembered for that exe build.</summary>
    [Fact]
    public void AnAntiCheatGamesExeFileIsScannedAndAFailureRemembered()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 53 + 7)).ToArray();
        var code = new List<byte>();
        for (var k = 0; k < 8; k++) code.AddRange([0xC7, 0x45, (byte)(0xD0 + 4 * k), .. key.AsSpan(4 * k, 4)]);
        var dir = Ff7.TempDir("keys-anticheat");
        Directory.CreateDirectory(Path.Combine(dir, "EasyAntiCheat"));
        var game = new Game("steam:1", "Protected", Store.Steam, dir, Path.Combine(dir, "game.exe"));
        File.WriteAllBytes(game.ExePath, Pe([.. code]));
        Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(game));
        var expected = "0x" + Convert.ToHexString(key);
        var data = Ff7.TempDir("keys-anticheat-data");
        var keys = new UnrealKeys(data);
        Assert.Equal(expected, keys.Get(game, k => k.KeyString == expected, out var why)?.KeyString);
        Assert.Contains("found in the exe", why);
        Assert.Equal(expected, keys.Get(game, _ => true, out why)?.KeyString);
        Assert.Equal("stored key", why);

        var other = game with { Id = "steam:2", ExePath = Path.Combine(dir, "other.exe") };
        File.WriteAllBytes(other.ExePath, [.. "MZ"u8, .. new byte[100]]);
        Assert.Null(keys.Get(other, _ => true, out why));
        Assert.Contains("PE image", why);
        var scan = Path.Combine(data, "games", "steam_2", "aes.scan");
        File.WriteAllLines(scan, [File.ReadAllLines(scan)[0], "remembered"]);
        Assert.Null(keys.Get(other, _ => true, out var again));
        Assert.Equal("remembered", again);

        var negative = Pe([.. code]);
        BitConverter.TryWriteBytes(negative.AsSpan(0x98 + 0xF0 + 20), 0x80000000u);   // the section's raw data offset, read as signed
        File.WriteAllBytes(other.ExePath, negative);
        Assert.Null(UnrealKeys.Scan(other.ExePath, _ => true, out _));
    }

    /// <summary>Built for AVX, the callback copies the key from .rdata with VEX-encoded moves: one 32-byte ymm load and store,
    /// or two 16-byte halves, the second through xmm8 (3-byte VEX).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeyFoundInVexEncodedCopiesFromRdata(bool ymm)
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 29 + 5)).ToArray();
        var code = new List<byte> { 0x48, 0x8B, 0xC1 };
        void Load(byte[] prefix, int keyAt)
        {
            var next = 0x1010 + code.Count + prefix.Length + 4;
            code.AddRange([.. prefix, .. BitConverter.GetBytes(0x1100 + keyAt - next)]);
        }
        if (ymm)
        {
            Load([0xC5, 0xFC, 0x10, 0x05], 0);                // vmovups ymm0, [rip+key]
            code.AddRange([0xC5, 0xFC, 0x11, 0x01]);          // vmovups [rcx], ymm0
        }
        else
        {
            Load([0xC5, 0xF8, 0x10, 0x05], 0);                // vmovups xmm0, [rip+key]
            Load([0xC4, 0x61, 0x78, 0x10, 0x05], 16);         // vmovups xmm8, [rip+key+16]
            code.AddRange([0xC5, 0xF8, 0x11, 0x01]);          // vmovups [rcx], xmm0
            code.AddRange([0xC4, 0x61, 0x78, 0x11, 0x41, 0x10]);   // vmovups [rcx+16], xmm8
        }
        code.Add(0xC3);
        code.AddRange(new byte[0xF0 - code.Count]);           // the key at file offset 0x300, RVA 0x1100
        code.AddRange(key);
        var exe = Path.Combine(Ff7.TempDir("keys-vex"), "game.exe");
        File.WriteAllBytes(exe, Pe([.. code]));
        var expected = "0x" + Convert.ToHexString(key);
        Assert.Equal(expected, UnrealKeys.Scan(exe, k => k.KeyString == expected, out var how)?.KeyString);
        Assert.Contains(ymm ? "32 bytes from .rdata" : "2 x 16 bytes from .rdata", how);
    }

    /// <summary>An exe full of key-like stores (an attacker's) stops the scan at its candidate limit.</summary>
    [Fact]
    public void TheScanStopsAtItsCandidateLimit()
    {
        var code = new List<byte>();
        for (var c = 0; c <= 1 << 16; c++)
            for (var k = 0; k < 8; k++) code.AddRange([0xC7, 0x45, (byte)(0xD0 + 4 * k), .. BitConverter.GetBytes(c * 8 + k + 0x10203040 * (k + 1))]);
        var exe = Path.Combine(Ff7.TempDir("keys-limit"), "game.exe");
        File.WriteAllBytes(exe, Pe([.. code]));
        var asked = 0;
        Assert.Null(UnrealKeys.Scan(exe, _ => { asked++; return false; }, out var how));
        Assert.Equal(1 << 16, asked);
        Assert.Contains("stopped at its limit", how);
    }

    /// <summary>A SteamStub exe (its .bind section) has its code encrypted on disk: the scan says so.</summary>
    [Fact]
    public void ASteamStubExeIsReportedAsProtected()
    {
        var b = Pe([0xC3]);
        ".bind"u8.CopyTo(b.AsSpan(0x98 + 0xF0));
        var exe = Path.Combine(Ff7.TempDir("keys-steamstub"), "game.exe");
        File.WriteAllBytes(exe, b);
        Assert.Equal("SteamStub", UnrealKeys.Protector(exe));
        Assert.Null(UnrealKeys.Scan(exe, _ => true, out var how));
        Assert.Contains("protected by SteamStub", how);
        File.WriteAllBytes(exe, Pe([0xC3]));
        Assert.Null(UnrealKeys.Protector(exe));
    }

    /// <summary>A PE32+ image with one executable section (.text, file offset 0x200, RVA 0x1000) holding <paramref name="code"/>
    /// from offset 0x10.</summary>
    static byte[] Pe(byte[] code)
    {
        var raw = (0x10 + code.Length + 0x1FF) & ~0x1FF;
        var b = new byte[0x200 + Math.Max(raw, 0x200)];
        void U16(int at, int v) => BitConverter.TryWriteBytes(b.AsSpan(at), (ushort)v);
        void U32(int at, uint v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
        U16(0, 0x5A4D);
        U32(0x3C, 0x80);
        U32(0x80, 0x4550);
        U16(0x84, 0x8664);   // machine
        U16(0x86, 1);        // sections
        U16(0x94, 0xF0);     // optional header size
        U16(0x96, 0x22);
        const int opt = 0x98;
        U16(opt, 0x20B);
        U32(opt + 32, 0x1000);   // section alignment
        U32(opt + 36, 0x200);    // file alignment
        U32(opt + 56, (uint)(0x1000 + ((b.Length + 0xFFF) & ~0xFFF)));   // image size
        U32(opt + 60, 0x200);    // headers size
        U32(opt + 108, 16);      // data directories
        var sec = opt + 0xF0;
        ".text"u8.CopyTo(b.AsSpan(sec));
        U32(sec + 8, (uint)(b.Length - 0x200));
        U32(sec + 12, 0x1000);
        U32(sec + 16, (uint)(b.Length - 0x200));
        U32(sec + 20, 0x200);
        U32(sec + 36, 0x60000020);   // code, execute, read
        code.CopyTo(b, 0x210);
        return b;
    }
}
