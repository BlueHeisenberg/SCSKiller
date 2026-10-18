using System.Diagnostics;
using System.Reflection.PortableExecutable;
using CUE4Parse.Encryption.Aes;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Unreal;

/// <summary>The pak AES key of an encrypted Unreal game. Kept only locally, in %LOCALAPPDATA%\SCSKiller\games\&lt;id&gt;\aes.key
/// (hex; the user may also put a key there by hand), never in plans, logs or anything meant to be shared. Found statically
/// in the game's own exe file (<see cref="Scan"/>; an anti-cheat game's too: the file on disk, never the running process),
/// once per exe build: a failed scan is remembered in aes.scan with the exe's size and write time. Every key is checked
/// against an encrypted container before it is used or stored.</summary>
public sealed class UnrealKeys(string dataDir)
{
    string KeyFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "aes.key");
    string ScanFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "aes.scan");
    /// <summary>Bump when <see cref="Scan"/> finds keys it missed before: a remembered failure is then scanned again.</summary>
    const int ScanVersion = 4;
    static string Stamp(string exe) => new FileInfo(exe) is { Exists: true } f ? $"{ScanVersion}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";

    /// <summary>A key that <paramref name="opens"/> the game's encrypted containers: the stored one, else a static scan of
    /// the exe file. <paramref name="why"/> says how, without the key.</summary>
    public FAesKey? Get(Game game, Func<FAesKey, bool> opens, out string why)
    {
        if (File.Exists(KeyFile(game)) && Parse(File.ReadAllText(KeyFile(game))) is { } stored)
        {
            if (opens(stored)) { why = "stored key"; return stored; }
            why = $"the key in {KeyFile(game)} doesn't open the game's files";
            return null;
        }
        var stamp = Stamp(game.ExePath);
        if (File.Exists(ScanFile(game)) && File.ReadAllLines(ScanFile(game)) is [var s, var reason, ..] && s == stamp) { why = reason; return null; }
        var sw = Stopwatch.StartNew();
        FAesKey? key;
        string how;
        try { key = Scan(game.ExePath, opens, out how); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) // Xbox app games: the exe is encrypted at rest
        {
            why = "encrypted; the game's exe can't be read, so it isn't scanned for the key: give the key by hand";
            return null;
        }
        catch (BadImageFormatException) { (key, how) = (null, "not found: the exe's headers don't read as a PE image (a protector's?)"); }
        why = $"{how} ({sw.Elapsed.TotalSeconds:F1} s)";
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game))!);
        if (key != null) File.WriteAllText(KeyFile(game), key.KeyString);
        else File.WriteAllLines(ScanFile(game), [stamp, why]);
        return key;
    }

    /// <summary>The stored key file's write time, "" if none: a key added or changed re-runs detection.</summary>
    public string KeyStamp(Game game) => File.Exists(KeyFile(game)) ? File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString() : "";

    /// <summary>The stored key, unchecked (a full mount checks it: SubmitKey mounts only what it opens).</summary>
    public FAesKey? Stored(Game game) => File.Exists(KeyFile(game)) ? Parse(File.ReadAllText(KeyFile(game))) : null;

    /// <summary>A key the user supplies (hex, "0x" optional): stored if it opens the game's files.</summary>
    public bool Set(Game game, string key, Func<FAesKey, bool> opens)
    {
        if (Parse(key) is not { } k || !opens(k)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game))!);
        File.WriteAllText(KeyFile(game), k.KeyString);
        File.Delete(ScanFile(game));
        return true;
    }

    static FAesKey? Parse(string text)
    {
        var hex = text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? new FAesKey("0x" + hex) : null;
    }

    /// <summary>Static key search, the way AESDumpster works: UE's key callback (UE_REGISTER_ENCRYPTION_KEY) writes the 32 bytes
    /// to its out parameter as 8 x mov dword [reg+d], imm32 (adjacent or interleaved with other code), as 4 x mov r64, imm64
    /// + stores, as 2 x 16-byte loads from .rdata + stores, or, built for AVX, as the same loads and stores VEX-encoded or as
    /// one 32-byte load + store. Every candidate from the executable sections goes to <paramref name="opens"/>; the first
    /// that opens wins. Streams the exe (4 MB at a time); read-only.</summary>
    public static FAesKey? Scan(string exePath, Func<FAesKey, bool> opens, out string how)
    {
        using var f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        if (f.Length > int.MaxValue)   // PEReader takes no bigger stream
        {
            how = "not found: the exe is over 2 GB, more than the scan reads";
            return null;
        }
        SectionHeader[] sections;
        using (var pe = new PEReader(f, PEStreamOptions.LeaveOpen)) sections = [.. pe.PEHeaders.SectionHeaders];
        var code = sections.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 && s.PointerToRawData >= 0).ToList();   // a protector's may claim a negative offset
        var tried = new HashSet<string>();
        FAesKey? found = null;
        var unread = f.Length; // overlapping sections can't make the scan read more than the file
        bool Done() => found != null || tried.Count >= MaxCandidates || unread < 0;
        var pattern = "";
        void Try(byte[] k, string how)
        {
            if (Done() || k.Distinct().Count() < 16 || !tried.Add(Convert.ToHexString(k))) return; // a real key is random bytes
            var key = new FAesKey("0x" + Convert.ToHexString(k));
            if (opens(key)) (found, pattern) = (key, how);
        }
        long FileOf(long rva) => sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData) is { SizeOfRawData: > 0 } s ? s.PointerToRawData + rva - s.VirtualAddress : -1;
        byte[] At(long off, int size) { var b = new byte[size]; var p = f.Position; f.Position = off; f.ReadAtLeast(b, size, false); f.Position = p; return b; }

        const int Step = 4 << 20;
        var buf = new byte[Step + 64]; // + room for the longest pattern at the chunk's last position
        foreach (var s in code)
        {
            var stores = new List<(long Pos, int Size, int Base, int Disp, object Val)>(); // Val: bytes, or the file offset of Size bytes
            var regs = new (long Pos, byte[]? Val)[16];
            var xmms = new (long Pos, long Src, int Size)[16];
            void Store(long pos, int size, int bas, int disp, object val)
            {
                stores.RemoveAll(x => pos - x.Pos > 160);
                stores.Add((pos, size, bas, disp, val));
                var mine = stores.Where(x => x.Size == size && x.Base == bas).GroupBy(x => x.Disp).ToDictionary(g => g.Key, g => g.Last().Val);
                var n = 32 / size;
                foreach (var d0 in mine.Keys.Where(d => d <= disp && disp < d + 32))
                    if (Enumerable.Range(0, n).All(k => mine.ContainsKey(d0 + k * size)))
                        Try(Enumerable.Range(0, n).SelectMany(k => mine[d0 + k * size] is byte[] b ? b : At((long)mine[d0 + k * size], size)).ToArray(),
                            size == 4 ? "8 x mov dword, imm32" : size == 8 ? "4 x mov qword, imm64" : size == 16 ? "2 x 16 bytes from .rdata" : "32 bytes from .rdata");
            }
            for (long at = s.PointerToRawData, end = s.PointerToRawData + s.SizeOfRawData; at < end && !Done(); at += Step)
            {
                f.Position = at;
                var span = (int)Math.Min(Step, end - at);
                var len = f.ReadAtLeast(buf.AsSpan(0, span + 64), span + 64, false);
                if (len == 0) break;
                unread -= span;
                var last = (int)Math.Min(Step, end - 1 - at); // chunk positions 1..Step; the next chunk starts at Step
                for (var i = 1; i <= last && i + 16 < len && !Done(); i++)
                {
                    long pos = at + i;
                    var b0 = buf[i];
                    if (b0 == 0xC7) // mov dword [base+disp], imm32 (optional REX.B, no REX.W/X)
                    {
                        var rex = (buf[i - 1] & 0xF0) == 0x40 ? buf[i - 1] : 0;
                        // a 0x40-0x4F before it may be the last byte of the previous store's imm32, not a REX prefix: try both
                        int[] prefixes = rex == 0 ? [0] : [rex, 0];
                        foreach (var r in prefixes)
                            if ((r & 0x0A) == 0 && ((buf[i + 1] >> 3) & 7) == 0 && Mem(buf, i + 1, r & 1, out var bas, out var disp) is > 0 and var ml)
                                Store(pos, 4, bas, disp, buf[(i + 1 + ml)..(i + 5 + ml)]);
                    }
                    else if (b0 is 0x48 or 0x49 && buf[i + 1] is >= 0xB8 and <= 0xBF) // mov r64, imm64
                        regs[(buf[i + 1] - 0xB8) | ((b0 & 1) << 3)] = (pos, buf[(i + 2)..(i + 10)]);
                    else if (b0 is 0x48 or 0x49 or 0x4C or 0x4D && buf[i + 1] == 0x89) // mov qword [base+disp], r64
                    {
                        var src = ((buf[i + 2] >> 3) & 7) | ((b0 & 4) << 1);
                        if (regs[src].Val is { } v && pos - regs[src].Pos < 48 && Mem(buf, i + 2, b0 & 1, out var bas, out var disp) > 0) Store(pos, 8, bas, disp, v);
                    }
                    else if (b0 == 0x0F || (b0 is 0x66 or 0xF3 && (buf[i + 1] == 0x0F || ((buf[i + 1] & 0xF0) == 0x40 && buf[i + 2] == 0x0F))) || ((b0 & 0xF0) == 0x40 && buf[i + 1] == 0x0F))
                    {
                        var j = i + (b0 is 0x66 or 0xF3 ? 1 : 0);
                        var rex = (buf[j] & 0xF0) == 0x40 ? buf[j++] : 0;
                        if (buf[j] != 0x0F) continue;
                        var (op, modrm) = (buf[j + 1], buf[j + 2]);
                        var x = ((modrm >> 3) & 7) | ((rex & 4) << 1);
                        if (op is 0x10 or 0x28 or 0x6F && (modrm & 0xC7) == 0x05) // movups/movaps/movdqu/movdqa xmm, [rip+rel32]
                            xmms[x] = (pos, FileOf(s.VirtualAddress + (pos - s.PointerToRawData) + (j + 7 - i) + BitConverter.ToInt32(buf, j + 3)), 16);
                        else if (op is 0x11 or 0x29 or 0x7F && (modrm & 0xC0) != 0xC0 && xmms[x] is { Src: > 0, Size: 16 } && pos - xmms[x].Pos < 64
                                 && Mem(buf, j + 2, rex & 1, out var bas, out var disp) > 0)
                            Store(pos, 16, bas, disp, xmms[x].Src);
                    }
                    else if (b0 is 0xC5 or 0xC4) // VEX: vmovups/vmovaps/vmovdqu/vmovdqa, xmm or ymm
                    {
                        var three = b0 == 0xC4;
                        var p2 = buf[i + (three ? 2 : 1)]; // W/R, vvvv, L, pp
                        if (three && (buf[i + 1] & 0x1F) != 1 || ((p2 >> 3) & 0xF) != 0xF) continue; // the 0F map, no second source
                        var j = i + (three ? 3 : 2);
                        var (op, modrm) = (buf[j], buf[j + 1]);
                        var x = ((modrm >> 3) & 7) | ((~buf[i + 1] >> 4) & 8);
                        var size = (p2 & 4) != 0 ? 32 : 16;
                        if (op is 0x10 or 0x28 or 0x6F && (modrm & 0xC7) == 0x05)
                            xmms[x] = (pos, FileOf(s.VirtualAddress + (pos - s.PointerToRawData) + (j + 6 - i) + BitConverter.ToInt32(buf, j + 2)), size);
                        else if (op is 0x11 or 0x29 or 0x7F && (modrm & 0xC0) != 0xC0 && xmms[x].Src > 0 && xmms[x].Size == size && pos - xmms[x].Pos < 64
                                 && Mem(buf, j + 1, three ? (~buf[i + 1] >> 5) & 1 : 0, out var bas, out var disp) > 0)
                            Store(pos, size, bas, disp, xmms[x].Src);
                    }
                }
            }
            if (Done()) break;
        }
        var textSize = sections.Where(s => s.Name == ".text").Sum(s => (long)s.SizeOfRawData);
        var where = $"code in {string.Join(", ", code.Select(s => s.Name))}, .text {textSize >> 20} MB";
        how = found != null ? $"found in the exe: {pattern}"
            : tried.Count >= MaxCandidates || unread < 0 ? $"not found: the scan stopped at its limit ({tried.Count} candidates)"
            : Protector(sections) is not { } by ? $"not found in the exe's code ({tried.Count} candidates)"
            : by == OutsideText ? $"not found: the exe looks protected ({where}), a static scan can't see the key"
            : $"not found: the exe is protected by {by} ({where}), a static scan can't see the key";
        return found;
    }

    const string OutsideText = "code outside .text";

    /// <summary>Game exes give about 2,500 candidates.</summary>
    const int MaxCandidates = 1 << 16;

    /// <summary>What hides the exe's code on disk from a static scan, told by its sections: SteamStub (Steam DRM; its .bind
    /// stub decrypts the code at launch), Themida or WinLicense, VMProtect, Enigma, or most code outside .text (Denuvo's
    /// layout). Null when none shows or the exe can't be read.</summary>
    public static string? Protector(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        try
        {
            using var f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (f.Length > int.MaxValue) return null;   // PEReader takes no bigger stream
            using var pe = new PEReader(f);
            return Protector(pe.PEHeaders.SectionHeaders);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { return null; }
    }

    static string? Protector(IReadOnlyList<SectionHeader> sections)
    {
        var names = sections.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains(".bind")) return "SteamStub";
        if (names.Contains(".themida") || names.Contains(".winlice")) return "Themida";
        if (names.Any(n => n.StartsWith(".vmp", StringComparison.OrdinalIgnoreCase))) return "VMProtect";
        if (names.Any(n => n.StartsWith(".enigma", StringComparison.OrdinalIgnoreCase))) return "Enigma";
        var code = sections.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0).Sum(s => (long)s.SizeOfRawData);
        return sections.Where(s => s.Name == ".text").Sum(s => (long)s.SizeOfRawData) < code / 2 ? OutsideText : null;
    }

    /// <summary>A memory operand at modrm position p without an index register: base register (with REX.B) and displacement;
    /// returns its length, 0 if it isn't one (register operand, rip-relative, indexed).</summary>
    static int Mem(byte[] b, int p, int rexB, out int bas, out int disp)
    {
        bas = disp = 0;
        int mod = b[p] >> 6, rm = b[p] & 7, q = p + 1;
        if (mod == 3) return 0;
        if (rm == 4) { var sib = b[q++]; if (((sib >> 3) & 7) != 4) return 0; rm = sib & 7; if (mod == 0 && rm == 5) return 0; }
        else if (mod == 0 && rm == 5) return 0;
        bas = rm | (rexB << 3);
        if (mod == 1) disp = (sbyte)b[q++];
        else if (mod == 2) { disp = BitConverter.ToInt32(b, q); q += 4; }
        return q - p;
    }
}
