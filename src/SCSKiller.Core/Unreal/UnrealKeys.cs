using System.Diagnostics;
using System.Reflection.PortableExecutable;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Unreal;

/// <summary>The pak AES keys of an encrypted Unreal game: its own, and keys of single containers (Fortnite's per-item ones).
/// Kept only locally, in %LOCALAPPDATA%\SCSKiller\games\&lt;id&gt;\aes.key (hex, one per line; the user may also put keys
/// there by hand), never in plans, logs or anything meant to be shared. Found statically
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

    /// <summary>The keys that <paramref name="opens"/> the game's encrypted containers: the stored ones, else a static scan of
    /// the exe file. <paramref name="why"/> says how, without the keys.</summary>
    public IReadOnlyList<UnrealKey> Get(Game game, Func<FAesKey, bool> opens, out string why)
    {
        if (Stored(game) is { Count: > 0 } stored)
        {
            var open = stored.Where(k => opens(k.Aes)).ToList();
            why = open.Count == 0 ? $"the key in {KeyFile(game)} doesn't open the game's files" : stored.Count == 1 ? "stored key" : $"{open.Count} of {stored.Count} stored keys open the game's files";
            return open;
        }
        var stamp = Stamp(game.ExePath);
        if (File.Exists(ScanFile(game)) && File.ReadAllLines(ScanFile(game)) is [var s, var reason, ..] && s == stamp) { why = reason; return []; }
        var sw = Stopwatch.StartNew();
        FAesKey? key;
        string how;
        try { key = Scan(game.ExePath, opens, out how); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) // Xbox app games: the exe is encrypted at rest
        {
            why = "encrypted; the game's exe can't be read, so it isn't scanned for the key: give the key by hand";
            return [];
        }
        catch (BadImageFormatException) { (key, how) = (null, "not found: the exe's headers don't read as a PE image (a protector's?)"); }
        why = $"{how} ({sw.Elapsed.TotalSeconds:F1} s)";
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game))!);
        if (key != null) File.WriteAllText(KeyFile(game), key.KeyString);
        else File.WriteAllLines(ScanFile(game), [stamp, why]);
        return key != null ? [new UnrealKey(key, null)] : [];
    }

    /// <summary>The stored key file's write time, "" if none: a key added or changed re-runs detection.</summary>
    public string KeyStamp(Game game) => File.Exists(KeyFile(game)) ? File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString() : "";

    /// <summary>The stored keys in the order they were given, unchecked (a full mount checks each: SubmitKeys mounts only
    /// what it opens). A line that isn't a key is skipped.</summary>
    public IReadOnlyList<UnrealKey> Stored(Game game) =>
        File.Exists(KeyFile(game)) ? [.. Lines(File.ReadAllText(KeyFile(game))).Select(l => l.Key).OfType<UnrealKey>().Distinct()] : [];

    public bool Has(Game game) => File.Exists(KeyFile(game));

    /// <summary>Forgets the stored keys and the failed-scan note: the next detection scans the exe again.</summary>
    public void Remove(Game game)
    {
        File.Delete(KeyFile(game));
        File.Delete(ScanFile(game));
    }

    /// <summary>Keys the user supplies, one per line (hex, "0x" optional), each optionally after the GUID of the container it
    /// is for: a key is kept if it opens one of the game's encrypted containers (<paramref name="opened"/>: the GUIDs of
    /// those it opens), that one when a GUID is given (<paramref name="present"/>: the GUIDs the game's encrypted containers have, null when one can't be read).
    /// Kept keys go after the stored ones that still open something.
    /// Refused: each line that isn't kept and why, never its text.</summary>
    public (bool Stored, IReadOnlyList<string> Refused) Set(Game game, string text, Func<FAesKey, IReadOnlyCollection<FGuid>> opened, IReadOnlyCollection<FGuid>? present)
    {
        var lines = Lines(text).ToList();
        if (lines.Count == 0) return (false, ["no key given"]);
        if (lines.Count > MaxKeys) return (false, [$"more than {MaxKeys} keys"]);
        var add = new List<UnrealKey>();
        var refused = new List<string>();
        foreach (var (n, key) in lines)
        {
            var guids = key != null ? opened(key.Aes) : [];
            var why = key == null ? "not a key (0x and 64 hex digits)"
                : key.Guid is { } g && present?.Contains(g) == false ? "no archive in this game has that GUID"
                : key.Guid is { } h && !guids.Contains(h) ? "it doesn't open the archives with that GUID"
                : guids.Count == 0 ? "it doesn't open this game's files" : null;
            if (why == null) add.Add(key!);
            else refused.Add(lines.Count > 1 ? $"line {n}: {why}" : why);
        }
        if (add.Count == 0) return (false, refused);
        var keep = Stored(game).Where(k => opened(k.Aes).Count > 0);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game))!);
        File.WriteAllLines(KeyFile(game), keep.Concat(add).Distinct().Select(k => k.Line));
        File.Delete(ScanFile(game));
        return (true, refused);
    }

    /// <summary>A paste of a season's keys stays well below this; each key is tried on every encrypted container.</summary>
    const int MaxKeys = 512;

    // "[GUID][ :,=]key": the GUID as UE prints it (32 hex digits) or with dashes and braces, the key with "0x" optional
    static readonly System.Text.RegularExpressions.Regex Line = new(@"^(?:\{?(?<g>[0-9A-Fa-f]{8}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{12})\}?\s*[:,=\s]\s*)?(?:0[xX])?(?<k>[0-9A-Fa-f]{64})$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The non-empty lines of a key text, numbered from 1: Key null when the line isn't one.</summary>
    static IEnumerable<(int Line, UnrealKey? Key)> Lines(string text)
    {
        var n = 0;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))   // a WinUI TextBox separates lines with a bare \r
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length > 200 || Line.Match(line) is not { Success: true } m) { yield return (n, null); continue; }
            FGuid? guid = null;
            if (m.Groups["g"].Success)
            {
                var h = m.Groups["g"].Value.Replace("-", "");
                guid = new FGuid(Convert.ToUInt32(h[..8], 16), Convert.ToUInt32(h[8..16], 16), Convert.ToUInt32(h[16..24], 16), Convert.ToUInt32(h[24..], 16));
            }
            yield return (n, new UnrealKey(new FAesKey("0x" + m.Groups["k"].Value.ToUpperInvariant()), guid));
        }
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

/// <summary>An AES key, and the key GUID of the containers it is for when the user named one.</summary>
public sealed record UnrealKey(FAesKey Aes, FGuid? Guid)
{
    /// <summary>Its line in aes.key: "&lt;GUID&gt;:0x&lt;key&gt;", or the key alone.</summary>
    public string Line => (Guid is { } g ? $"{g.A:X8}{g.B:X8}{g.C:X8}{g.D:X8}:" : "") + Aes.KeyString.ToUpperInvariant().Replace("0X", "0x");

    public bool Equals(UnrealKey? other) => other != null && Line == other.Line;
    public override int GetHashCode() => Line.GetHashCode();
    public override string ToString() => "UnrealKey";   // never the key
}
