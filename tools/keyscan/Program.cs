// Finds game keys and key-shaped constants in shipped files. Prints where a constant sits, its size and its SHA-256,
// never its value.
//   keyscan [--list | --all] [--known FILE] [--allow FILE [--no-pins]] PATH...
//   keyscan --removed ORIGINAL STRIPPED     known-list lines for the constants ORIGINAL has and STRIPPED doesn't
// A PATH is a file or a folder (recursive); .NET assemblies, PE files, zip/nupkg files and gzip resources are opened.
// Exit 1 when a known key turns up anywhere, or a key-shaped constant whose hash isn't on the allowlist. Key-shaped: 32
// random-looking bytes (Unreal's AES-256 key size) as an array, IL immediates, hex or base64. Shorter keys, masks and
// tables are found by the known list only, by SHA-256 of the value anywhere in a file's bytes.
// --list prints every key-shaped constant, allowed or not, with its SHA-256 and the 24-bit window hash a known line takes.
// --all also lists every array initializer, immediate run, string and resource, to fingerprint a key found by reading.
// An allowlist line "pin <assembly> <sha256>" fails any file of that assembly name with other bytes: a game-format library
// is shipped only as the build that was reviewed.
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var paths = new List<string>();
string? knownFile = null, allowFile = null, original = null, stripped = null;
bool list = false, all = false, noPins = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--list": list = true; break;
        case "--all": list = all = true; break;
        case "--known" when i + 1 < args.Length: knownFile = args[++i]; break;
        case "--allow" when i + 1 < args.Length: allowFile = args[++i]; break;
        case "--removed" when i + 2 < args.Length: original = args[++i]; stripped = args[++i]; break;
        case "--no-pins": noPins = true; break;
        case "--self-test": return SelfTest();
        default:
            if (args[i].StartsWith("--")) return Usage();
            paths.Add(args[i]);
            break;
    }
}
if (original is not null && stripped is not null) return Removed(original, stripped);
if (paths.Count == 0) return Usage();

var scan = new Scan(all, knownFile is null ? [] : Scan.ReadKnown(knownFile), allowFile is null ? [] : Scan.ReadAllow(allowFile));
if (knownFile is not null) scan.LoadWindows(knownFile);
if (allowFile is not null && !noPins) scan.Pins = Scan.ReadPins(allowFile);
foreach (var p in paths)
    if (!scan.Path(p)) { Console.Error.WriteLine($"keyscan: no such path: {p}"); return 2; }

var bad = Report(scan, list);
Console.WriteLine($"keyscan: {scan.Files} files, {scan.Found.Count} key-shaped or known constants, {bad} not allowed");
return bad == 0 ? 0 : 1;

static int Report(Scan scan, bool list)
{
    var bad = 0;
    foreach (var c in scan.Found.DistinctBy(c => (c.Kind, c.Where, c.Symbol, c.Sha)))
    {
        var known = scan.Known.GetValueOrDefault(c.Sha);
        var allowed = known is null && scan.Allow.Contains(c.Sha);
        if (!allowed) bad++;
        if (list || !allowed)
            Console.WriteLine($"{(known is not null ? "KNOWN-KEY" : allowed ? "allowed" : "NEW")}\t{c.Kind}\t{c.Where}\t{c.Symbol}\t{c.Size}\t{c.Sha}\t{c.Window:x6}{(known is not null ? "\t" + known : "")}");
    }
    return bad;
}

static int Usage()
{
    Console.Error.WriteLine("usage: keyscan [--list | --all] [--known FILE] [--allow FILE [--no-pins]] PATH... | keyscan --removed ORIGINAL STRIPPED | keyscan --self-test");
    return 2;
}

// Made-up keys in each form the scan reads must fail it, an allowlisted one and ordinary hashes must not.
static int SelfTest()
{
    // a random 32 bytes repeat values or span little now and then: those wouldn't be key-shaped
    static byte[] Key()
    {
        while (true)
            if (RandomNumberGenerator.GetBytes(32) is var k && Scan.Random(k) && Convert.ToBase64String(k).Any(char.IsAsciiDigit)) return k;
    }
    var key = Key();
    var ab = new PersistedAssemblyBuilder(new AssemblyName("keyscan.selftest"), typeof(object).Assembly);
    var tb = ab.DefineDynamicModule("keyscan.selftest").DefineType("Synthetic", TypeAttributes.Public | TypeAttributes.Class);
    tb.DefineInitializedData("Key", Key(), FieldAttributes.Static | FieldAttributes.Private);
    tb.DefineField("Hex", typeof(string), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault).SetConstant(Convert.ToHexString(Key()));
    tb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ObsoleteAttribute).GetConstructor([typeof(string)])!, [Convert.ToBase64String(Key())]));
    var il = tb.DefineMethod("Words", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator();
    var words = Key();
    for (var i = 0; i < 4; i++) il.Emit(OpCodes.Ldc_I8, BitConverter.ToInt64(words, 8 * i));
    for (var i = 0; i < 4; i++) il.Emit(OpCodes.Pop);
    il.Emit(OpCodes.Ret);
    tb.CreateType();
    var dll = new MemoryStream();
    ab.Save(dll);
    var pinned = new PersistedAssemblyBuilder(new AssemblyName("CUE4Parse"), typeof(object).Assembly);
    pinned.DefineDynamicModule("CUE4Parse").DefineType("Empty", TypeAttributes.Public).CreateType();
    var other = new MemoryStream();
    pinned.Save(other);
    var known = RandomNumberGenerator.GetBytes(16);
    var blob = RandomNumberGenerator.GetBytes(4096);
    known.CopyTo(blob, 1000);
    var allowed = Key();
    var modulus = RandomNumberGenerator.GetBytes(128);
    var pem = Convert.ToBase64String([.. RandomNumberGenerator.GetBytes(9), .. modulus, 2, 3]);

    var cases = new (string Name, byte[] Bytes, string[] Expect)[]
    {
        ("synthetic.dll", dll.ToArray(), ["base64", "hex", "il", "rva"]),
        ("CUE4Parse.dll", other.ToArray(), ["pin"]),
        ("hex.json", Encoding.UTF8.GetBytes($"{{\"key\": \"0x{Convert.ToHexString(key)}\"}}"), ["hex"]),
        ("base64.txt", Encoding.UTF8.GetBytes($"key={Convert.ToBase64String(Key())}\n"), ["base64"]),
        ("blob.bin", blob, ["bytes"]),
        ("array.rs", Encoding.UTF8.GetBytes($"const N: [u8; 128] = [{string.Join(", ", modulus.Reverse().Select(x => $"0x{x:X2}"))}];"), ["bytes"]),
        ("pem.cs", Encoding.UTF8.GetBytes($"[\"Data1\"] = \"{string.Join("\\n", pem.Chunk(64).Select(c => new string(c)))}\","), ["bytes"]),
        ("array16.txt", Encoding.Unicode.GetBytes(string.Join(", ", modulus.Reverse().Select(x => $"0x{x:X2}"))), ["bytes"]),
        ("tail.txt", Encoding.UTF8.GetBytes("n=" + Convert.ToBase64String(modulus).TrimEnd('=')), ["bytes"]),
        ("allowed.txt", Encoding.UTF8.GetBytes(Convert.ToHexString(allowed)), []),
        ("hashes.txt", Encoding.UTF8.GetBytes($"commit {Convert.ToHexStringLower(SHA1.HashData(key))} id {Guid.NewGuid():N} {Guid.NewGuid()}\n"), []),
    };
    var failed = 0;
    foreach (var (name, bytes, expect) in cases)
    {
        var scan = new Scan(false, [], [Scan.Sha(allowed)]) { Pins = new() { ["CUE4Parse"] = Scan.Sha(blob) } };
        scan.AddKnown(known, "self-test");
        scan.AddKnown([.. modulus.Reverse()], "self-test array");
        scan.AddKnown(modulus, "self-test pem");
        scan.File(name, bytes);
        var got = scan.Found.Where(f => !scan.Allow.Contains(f.Sha)).Select(f => f.Kind).Distinct().Order().ToArray();
        var ok = got.SequenceEqual(expect.Order());
        if (!ok) failed++;
        Console.WriteLine($"{(ok ? "ok" : "FAILED")}\t{name}: expected [{string.Join(", ", expect)}], found [{string.Join(", ", got)}]");
    }
    return failed == 0 ? 0 : 1;
}

// strings stay out: a short passphrase's hash can be guessed, and a removed error message is no key
static int Removed(string original, string stripped)
{
    var a = new Scan(true, [], []);
    var b = new Scan(true, [], []);
    if (!a.Path(original) || !b.Path(stripped)) return 2;
    var kept = b.Found.Select(f => f.Sha).ToHashSet();
    foreach (var f in a.Found.Where(f => f.Kind is "rva" or "il" or "resource" or "hex" or "base64" && f.Size >= 16 && !kept.Contains(f.Sha)).DistinctBy(f => f.Sha))
        Console.WriteLine($"{f.Sha} {f.Size} {f.Window:x6} {f.Kind} {f.Symbol}");
    return 0;
}

sealed record Found(string Kind, string Where, string Symbol, int Size, string Sha, ulong Window);

sealed class Scan(bool all, Dictionary<string, string> known, HashSet<string> allow)
{
    public readonly Dictionary<string, string> Known = known;   // SHA-256 -> label
    public readonly HashSet<string> Allow = allow;
    public readonly List<Found> Found = [];
    public Dictionary<string, string> Pins = [];   // assembly name -> SHA-256 of the reviewed file
    public int Files;
    readonly Dictionary<int, Dictionary<ulong, List<string>>> windows = [];   // length -> 24-bit window hash -> SHA-256s

    const ulong B = 0x100000001B3;

    public static Dictionary<string, string> ReadKnown(string path)
    {
        var d = new Dictionary<string, string>();
        foreach (var f in Lines(path))
        {
            if (f.Length < 4 || f[0].Length != 64 || !int.TryParse(f[1], out _) || f[2].Length != 6) throw new InvalidDataException($"{path}: a known-key line is: sha256 length window label");
            d[f[0].ToLowerInvariant()] = string.Join(' ', f[3..]);
        }
        return d;
    }

    public static HashSet<string> ReadAllow(string path) =>
        Lines(path).Where(f => f[0] != "pin").Select(f => f[0].Length == 64 ? f[0].ToLowerInvariant() : throw new InvalidDataException($"{path}: an allowlist line starts with a sha256")).ToHashSet();

    public static Dictionary<string, string> ReadPins(string path) =>
        Lines(path).Where(f => f[0] == "pin").ToDictionary(f => f.Length == 3 && f[2].Length == 64 ? f[1] : throw new InvalidDataException($"{path}: a pin line is: pin assembly sha256"), f => f[2].ToLowerInvariant());

    static IEnumerable<string[]> Lines(string path) => System.IO.File.ReadLines(path).Select(l => l.Trim())
        .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public void LoadWindows(string path)
    {
        foreach (var f in Lines(path)) AddWindow(int.Parse(f[1]), Convert.ToUInt64(f[2], 16), f[0].ToLowerInvariant());
    }

    public void AddKnown(ReadOnlySpan<byte> value, string label)
    {
        Known[Sha(value)] = label;
        AddWindow(value.Length, Window(value), Sha(value));
    }

    void AddWindow(int len, ulong h, string sha)
    {
        if (!windows.TryGetValue(len, out var w)) windows[len] = w = [];
        if (!w.TryGetValue(h, out var shas)) w[h] = shas = [];
        shas.Add(sha);
    }

    public static string Sha(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(SHA256.HashData(b));

    // 24 bits: a prefilter only, too short to say anything about the value
    const ulong WindowMask = 0xFFFFFF;

    public static ulong Window(ReadOnlySpan<byte> b) => Poly(b) & WindowMask;

    static ulong Poly(ReadOnlySpan<byte> b)
    {
        ulong h = 0;
        foreach (var x in b) h = h * B + x;
        return h;
    }

    public bool Path(string p)
    {
        if (Directory.Exists(p))
            foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                File(System.IO.Path.GetRelativePath(p, f).Replace('\\', '/'), System.IO.File.ReadAllBytes(f));
        else if (System.IO.File.Exists(p)) File(System.IO.Path.GetFileName(p), System.IO.File.ReadAllBytes(p));
        else return false;
        return true;
    }

    void Add(string kind, string where, string symbol, ReadOnlySpan<byte> value) =>
        Found.Add(new(kind, where, symbol, value.Length, Sha(value), Window(value)));

    void AddIfKnown(string kind, string where, string symbol, ReadOnlySpan<byte> value)
    {
        if (all || Known.Count > 0 && Known.ContainsKey(Sha(value))) Add(kind, where, symbol, value);
    }

    public void File(string where, byte[] bytes)
    {
        Files++;
        RawWindows(where, bytes);
        if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4) { Zip(where, bytes); return; }
        if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B) { Gzip(where, bytes); return; }
        if (bytes.Length > 64 && bytes[0] == 'M' && bytes[1] == 'Z')
        {
            try
            {
                using var pe = new PEReader(ImmutableArray.Create(bytes));
                if (pe.HasMetadata)
                {
                    var name = pe.GetMetadataReader() is var md && md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : "";
                    if (Pins.TryGetValue(name, out var pin) && Sha(bytes) != pin) Add("pin", where, name, bytes);
                    Managed(where, pe);
                    return;
                }
            }
            catch (BadImageFormatException) { }
        }
        Text(where, Encoding.Latin1.GetString(bytes));
    }

    void Zip(string where, byte[] bytes)
    {
        using var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var e in z.Entries.Where(e => e.Length > 0).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = e.Open();
            var m = new MemoryStream();
            s.CopyTo(m);
            File($"{where}!{e.FullName}", m.ToArray());
        }
    }

    void Gzip(string where, byte[] bytes)
    {
        var m = new MemoryStream();
        using (var g = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)) g.CopyTo(m);
        File($"{where}!gunzip", m.ToArray());
    }

    // a known key anywhere in a file's bytes, whatever holds it
    void RawWindows(string where, ReadOnlySpan<byte> b)
    {
        foreach (var (len, w) in windows)
        {
            if (b.Length < len) continue;
            ulong top = 1;
            for (var i = 1; i < len; i++) top *= B;
            var h = Poly(b[..len]);
            for (var i = 0; ; i++)
            {
                if (w.TryGetValue(h & WindowMask, out var shas) && shas.Contains(Sha(b.Slice(i, len)))) Add("bytes", where, $"offset {i}", b.Slice(i, len));
                if (i + len >= b.Length) break;
                h = (h - b[i] * top) * B + b[i + len];
            }
        }
    }

    static readonly Regex Hex = new(@"(?<![0-9A-Fa-f])(?:0[xX])?([0-9A-Fa-f]{64})(?![0-9A-Fa-f])", RegexOptions.Compiled);
    static readonly Regex B64 = new(@"(?<![A-Za-z0-9+/])([A-Za-z0-9+/]{43}=)(?![A-Za-z0-9+/=])", RegexOptions.Compiled);

    // key-shaped text: 32 bytes as hex or base64, random-looking once decoded
    void Text(string where, string s, string symbol = "")
    {
        // a known value of any length written out: its hex digits joined across separators (a byte array), its base64 across
        // escaped line breaks (a PEM body in a string), every alignment
        if (windows.Count > 0)
        {
            var t = s.Replace("\0", "");   // UTF-16 text
            var hex = string.Concat(t.Replace("0x", "").Replace("0X", "").Where(char.IsAsciiHexDigit));
            for (var p = 0; p < 2 && p < hex.Length; p++) RawWindows(where + "!hex", Convert.FromHexString(hex.AsSpan(p, (hex.Length - p) / 2 * 2)));
            var b64 = string.Concat(t.Replace("\\n", "").Replace("\\r", "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/'));
            for (var a = 0; a < 4 && a < b64.Length; a++) RawWindows(where + "!base64", Base64Tail(b64[a..]));
        }
        foreach (Match m in Hex.Matches(s))
        {
            var v = Convert.FromHexString(m.Groups[1].Value);
            if (Random(v)) Add("hex", where, symbol.Length > 0 ? symbol : $"offset {m.Index}", v);
        }
        foreach (Match m in B64.Matches(s))
        {
            if (m.Groups[1].Value.All(char.IsAsciiLetterOrDigit) && !m.Groups[1].Value.Any(char.IsAsciiDigit)) continue;   // a plain identifier
            byte[] v;
            try { v = Convert.FromBase64String(m.Groups[1].Value); } catch (FormatException) { continue; }
            if (Random(v)) Add("base64", where, symbol.Length > 0 ? symbol : $"offset {m.Index}", v);
        }
    }

    // padding put back, so a value ending the text keeps its last bytes
    static byte[] Base64Tail(string b) => Convert.FromBase64String((b.Length % 4) switch { 1 => b[..^1], 2 => b + "==", 3 => b + "=", _ => b });

    public static bool Random(ReadOnlySpan<byte> v)
    {
        if (v.Length != 32) return false;
        Span<bool> seen = stackalloc bool[256];
        int distinct = 0, min = 255, max = 0;
        foreach (var x in v)
        {
            if (!seen[x]) { seen[x] = true; distinct++; }
            min = Math.Min(min, x);
            max = Math.Max(max, x);
        }
        return distinct >= v.Length * 13 / 16 && max - min >= 128;
    }

    void Managed(string where, PEReader pe)
    {
        var md = pe.GetMetadataReader();
        var (refs, strings) = Users(pe, md);

        foreach (var fh in md.FieldDefinitions)
        {
            var f = md.GetFieldDefinition(fh);
            var rva = f.GetRelativeVirtualAddress();
            if (rva == 0) continue;
            var size = FieldSize(md, f);
            if (size <= 0) continue;
            var data = pe.GetSectionData(rva);
            if (data.Length < size) continue;
            var value = data.GetContent(0, size).AsSpan();
            var symbol = refs.TryGetValue(fh, out var u) ? u : $"{TypeName(md, f.GetDeclaringType())}::{md.GetString(f.Name)}";
            if (Random(value) && !all) Add("rva", where, symbol, value);
            else AddIfKnown("rva", where, symbol, value);
        }

        var us = MetadataTokens.UserStringHandle(1);
        while (!us.IsNil)
        {
            var s = md.GetUserString(us);
            var symbol = strings.TryGetValue(MetadataTokens.GetHeapOffset(us), out var m) ? m : $"string #{MetadataTokens.GetHeapOffset(us)}";
            Text(where, s, symbol);
            if (Known.Count > 0 || all && s.Length >= 8)
            {
                AddIfKnown("string", where, symbol, Encoding.UTF8.GetBytes(s));
                AddIfKnown("string", where, symbol, Encoding.Unicode.GetBytes(s));
            }
            us = md.GetNextHandle(us);
        }

        // const fields, parameter defaults and attribute arguments live in the blob heap, not in #US
        for (var i = 1; i <= md.GetTableRowCount(TableIndex.Constant); i++)
        {
            var c = md.GetConstant(MetadataTokens.ConstantHandle(i));
            if (c.TypeCode == ConstantTypeCode.String) Text(where, Encoding.Unicode.GetString(md.GetBlobBytes(c.Value)), $"constant #{i}");
        }
        foreach (var ah in md.CustomAttributes)
            Text(where, Encoding.Latin1.GetString(md.GetBlobBytes(md.GetCustomAttribute(ah).Value)), $"attribute #{MetadataTokens.GetRowNumber(ah)}");

        foreach (var (symbol, run) in ImmediateRuns(pe, md))
        {
            if (RandomRun(run) && !all) Add("il", where, symbol, run);
            else if (run.Length >= 16) AddIfKnown("il", where, symbol, run);
        }

        foreach (var rh in md.ManifestResources)
        {
            var r = md.GetManifestResource(rh);
            if (!r.Implementation.IsNil) continue;
            var dir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            if (!pe.PEHeaders.TryGetDirectoryOffset(dir, out var off)) continue;
            var image = pe.GetEntireImage().GetContent().AsSpan();
            var at = off + (int)r.Offset;
            var len = BitConverter.ToInt32(image[at..]);
            AddIfKnown("resource", where, md.GetString(r.Name), image.Slice(at + 4, len));
            File($"{where}!{md.GetString(r.Name)}", image.Slice(at + 4, len).ToArray());
        }
    }

    // an immediate run is key-shaped at 32 random-looking bytes or more (Vector256.Create(ulong, ...), a byte[32] built inline)
    static bool RandomRun(byte[] run)
    {
        if (run.Length < 32) return false;
        for (var i = 0; i + 32 <= run.Length; i += 32)
            if (!Random(run.AsSpan(i, 32))) return false;
        return true;
    }

    static int FieldSize(MetadataReader md, FieldDefinition f)
    {
        var sig = md.GetBlobReader(f.Signature);
        sig.ReadSignatureHeader();
        var code = sig.ReadSignatureTypeCode();
        switch (code)
        {
            case SignatureTypeCode.Boolean or SignatureTypeCode.SByte or SignatureTypeCode.Byte: return 1;
            case SignatureTypeCode.Char or SignatureTypeCode.Int16 or SignatureTypeCode.UInt16: return 2;
            case SignatureTypeCode.Int32 or SignatureTypeCode.UInt32 or SignatureTypeCode.Single: return 4;
            case SignatureTypeCode.Int64 or SignatureTypeCode.UInt64 or SignatureTypeCode.Double: return 8;
            case SignatureTypeCode.TypeHandle:
                var h = sig.ReadTypeHandle();
                if (h.Kind != HandleKind.TypeDefinition) return 0;
                var layout = md.GetTypeDefinition((TypeDefinitionHandle)h).GetLayout();
                return layout.Size;
            default: return 0;
        }
    }

    static string TypeName(MetadataReader md, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var name = md.GetString(t.Name);
        if (t.GetDeclaringType() is { IsNil: false } outer) return $"{TypeName(md, outer)}+{name}";
        var ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? $"{ns}.{name}" : name;
    }

    static readonly Dictionary<short, OperandType> Ops = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value, o => o.OperandType);

    static IEnumerable<(string Method, int Offset, int Code, long Operand)> Il(PEReader pe, MetadataReader md)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                MethodBodyBlock body;
                try { body = pe.GetMethodBody(m.RelativeVirtualAddress); } catch (BadImageFormatException) { continue; }
                var il = body.GetILBytes()!;
                var name = $"{TypeName(md, th)}::{md.GetString(m.Name)}";
                for (var i = 0; i < il.Length;)
                {
                    var at = i;
                    int code = il[i++];
                    if (code == 0xFE && i < il.Length) code = 0xFE00 | il[i++];
                    if (!Ops.TryGetValue(unchecked((short)code), out var type)) break;
                    var size = type switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * (i + 4 <= il.Length ? BitConverter.ToInt32(il, i) : 0),
                        _ => 4,
                    };
                    if (i + size > il.Length) break;
                    long operand = size switch
                    {
                        1 => code == 0x1F ? (sbyte)il[i] : il[i],
                        2 => BitConverter.ToInt16(il, i),
                        4 => BitConverter.ToInt32(il, i),
                        8 => BitConverter.ToInt64(il, i),
                        _ => 0,
                    };
                    yield return (name, at, code, operand);
                    i += size;
                }
            }
        }
    }

    static (Dictionary<FieldDefinitionHandle, string>, Dictionary<int, string>) Users(PEReader pe, MetadataReader md)
    {
        var fields = new Dictionary<FieldDefinitionHandle, string>();
        var strings = new Dictionary<int, string>();
        foreach (var (method, _, code, operand) in Il(pe, md))
        {
            if (code == 0x72) strings.TryAdd((int)operand & 0xFFFFFF, method);
            else if (code is 0xD0 or 0x7E or 0x7F && (operand >> 24) == 0x04)
                fields.TryAdd(MetadataTokens.FieldDefinitionHandle((int)operand & 0xFFFFFF), method);
        }
        return (fields, strings);
    }

    // adjacent integer loads: bytes when every value fits one, else 4- or 8-byte little-endian words
    static IEnumerable<(string, byte[])> ImmediateRuns(PEReader pe, MetadataReader md)
    {
        var values = new List<long>();
        string? method = null;
        var start = 0;
        foreach (var (m, at, code, operand) in Il(pe, md).Append((null!, 0, -1, 0)))
        {
            long? v = code switch
            {
                >= 0x15 and <= 0x1E => code - 0x16,
                0x1F or 0x20 or 0x21 => operand,
                _ => null,
            };
            if (v is { } x && m == method) { values.Add(x); continue; }
            if (values.Count >= 2) yield return ($"{method} IL_{start:x4}", Pack(values));
            values.Clear();
            method = m;
            start = at;
            if (v is { } y) values.Add(y);
        }
    }

    static byte[] Pack(List<long> values)
    {
        var width = values.All(v => v is >= -128 and <= 255) ? 1 : values.All(v => v is >= int.MinValue and <= uint.MaxValue) ? 4 : 8;
        var b = new byte[values.Count * width];
        for (var i = 0; i < values.Count; i++)
            for (var k = 0; k < width; k++) b[i * width + k] = (byte)(values[i] >> (8 * k));
        return b;
    }
}
