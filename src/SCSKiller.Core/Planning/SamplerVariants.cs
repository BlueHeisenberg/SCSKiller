using System.Security.Cryptography;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>A root signature's static samplers follow the player's texture filtering setting (filter, maximum anisotropy),
/// so the same pipeline recorded at another setting is another root signature and another compile: the same root
/// signature but for its samplers' parameters (registers, spaces and visibility the same).</summary>
public static class SamplerVariants
{
    /// <summary>A pipeline's records (or plan items) at every setting, one key: the record with its root signature's sampler
    /// settings zeroed; null for a record without static samplers or whose root signature isn't at hand.</summary>
    public static string? Pipeline(Rec r, Func<string, byte[]?> blob)
    {
        if (r.Tag is not ('G' or 'C' or 'S' or 'P')) return null;
        string rs;
        byte[]? neutral;
        try { neutral = blob(rs = r.Tag == 'P' ? ParseItem(r.Payload).Rs : Parse(r).Rs) is { } b ? RootSig.WithoutSamplerSettings(b) : null; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { return null; }
        if (neutral == null) return null;
        var payload = r.Payload.ToArray();
        var at = payload.AsSpan().IndexOf(Convert.FromHexString(rs));
        if (at < 0) return null;
        SHA1.HashData(neutral).CopyTo(payload, at);
        return Hex(SHA1.HashData([(byte)r.Tag, .. payload]));
    }

    /// <summary>A root signature's identity without its static samplers' parameters, and those samplers (hex); null without
    /// static samplers or for a blob that doesn't parse.</summary>
    public static (string RootSignature, string Samplers)? Setting(byte[] rs)
    {
        try { return RootSig.WithoutSamplerSettings(rs) is { } neutral ? (Hex(SHA1.HashData(neutral)), Convert.ToHexStringLower(RootSig.Samplers(rs))) : null; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>The sampler sets that are another setting of one of <paramref name="mine"/>: found on the same root signature
    /// as one of them in <paramref name="settings"/> (root signature -> its sampler sets).</summary>
    public static HashSet<string> OtherSettings(IReadOnlySet<string> mine, IEnumerable<HashSet<string>> settings) =>
        [.. settings.Where(s => s.Overlaps(mine)).SelectMany(s => s).Where(s => !mine.Contains(s))];

    /// <summary>The keys of <paramref name="others"/>' records made at another texture filtering setting than the one the
    /// compile takes: naming a root signature whose samplers are another setting, or building on such a state object. The
    /// setting taken is this PC's recording's (<paramref name="own"/>, every one it has); without an own recording with
    /// samplers, the others' most common at the player's <paramref name="anisotropy"/> (<see cref="Anisotropy"/>), else their
    /// most common (by records; a tie goes to the lowest sampler bytes, the same on every PC), or with <paramref name="every"/>
    /// (Maximum mode) all of them. Another setting is a sampler set the others have on a root signature also seen at a taken
    /// set; a set only ever on other root signatures is kept.</summary>
    public static HashSet<string> Foreign(string own, IEnumerable<string> others, bool every = false, int? anisotropy = null)
    {
        (List<Rec> Records, Dictionary<string, byte[]> Blobs) ownDb = File.Exists(own) ? Read(own) : ([], []);
        var dbs = others.Where(File.Exists).Select(Read).ToList();
        var mine = Settings(ownDb).ToList();
        if (mine.Count == 0)
        {
            if (every || ((anisotropy is { } a ? MostCommon(dbs, s => Anisotropy(s) == Math.Clamp(a, 1, 16)) : null) ?? MostCommon(dbs)) is not { } top) return [];
            mine = [.. dbs.SelectMany(Settings).Where(x => x.Samplers == top)];
        }
        var sets = mine.Select(x => x.Samplers).ToHashSet();
        var recorded = mine.Select(x => x.RootSignature).ToHashSet();
        var other = dbs.SelectMany(Settings).Where(x => recorded.Contains(x.RootSignature) && !sets.Contains(x.Samplers)).Select(x => x.Samplers).ToHashSet();
        if (other.Count == 0) return [];
        var keys = ownDb.Records.Select(r => r.Key).ToHashSet();
        var foreign = new HashSet<string>();
        foreach (var (records, blobs) in dbs)
        {
            var at = new Dictionary<string, bool>();   // root signature -> another setting's
            bool Other(string h) => at.TryGetValue(h, out var o) ? o : at[h] = blobs.GetValueOrDefault(h) is { } b && Setting(b) is { } x && other.Contains(x.Samplers);
            foreach (var r in records)   // a state object follows the ones it builds on
                if (!keys.Contains(r.Key) && (RootSignatures(r).Any(Other) || IsStateObject(r.Tag) && Depends(r).Any(foreign.Contains)))
                    foreign.Add(r.Key);
        }
        return foreign;
    }

    static IEnumerable<(string RootSignature, string Samplers)> Settings((List<Rec> Records, Dictionary<string, byte[]> Blobs) db) =>
        db.Records.SelectMany(RootSignatures).Distinct().Select(h => db.Blobs.GetValueOrDefault(h) is { } b ? Setting(b) : null).OfType<(string, string)>();

    /// <summary>A sampler set's (hex) anisotropy: its samplers' highest MaxAnisotropy, which D3D12 clamps to 1-16 as the engine does r.MaxAnisotropy.</summary>
    public static int Anisotropy(string samplers)
    {
        var b = Convert.FromHexString(samplers);
        var max = 0;
        for (var o = 20; o + 4 <= b.Length; o += 52) max = Math.Max(max, (int)Math.Min(BitConverter.ToUInt32(b, o), 16u));   // D3D12_STATIC_SAMPLER_DESC.MaxAnisotropy
        return Math.Max(max, 1);
    }

    /// <summary>The sampler set (hex) most records of <paramref name="dbs"/> name on a root signature they have at two sets or
    /// more (one only ever at one set, a middleware's, doesn't vote), of those <paramref name="take"/> takes; null when there is none.</summary>
    static string? MostCommon(List<(List<Rec> Records, Dictionary<string, byte[]> Blobs)> dbs, Func<string, bool>? take = null)
    {
        var varied = dbs.SelectMany(Settings).Distinct().GroupBy(x => x.RootSignature).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var count = new Dictionary<string, int>();
        foreach (var (records, blobs) in dbs)
        {
            var of = new Dictionary<string, string?>();   // root signature -> its sampler set, on a varied one
            foreach (var r in records)
                foreach (var set in RootSignatures(r).Select(h => of.TryGetValue(h, out var x) ? x
                             : of[h] = blobs.GetValueOrDefault(h) is { } b && Setting(b) is { } y && varied.Contains(y.RootSignature) ? y.Samplers : null)
                             .OfType<string>().Distinct())
                    count[set] = count.GetValueOrDefault(set) + 1;
        }
        return count.Where(c => take?.Invoke(c.Key) != false).OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key).FirstOrDefault();
    }

    static (List<Rec> Records, Dictionary<string, byte[]> Blobs) Read(string db)
    {
        var records = new List<Rec>();
        var blobs = new Dictionary<string, byte[]>();
        foreach (var r in PsoDb.Read(db))
            if (r.Tag == 'B') { if (r.Payload.Length > 20 && Carved.Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20))) blobs[Hex(r.Payload.AsSpan(0, 20))] = r.Payload[20..]; }
            else if (r.Tag is 'G' or 'C' or 'S' || IsStateObject(r.Tag)) records.Add(r);
        return (records, blobs);
    }

    static IEnumerable<string> RootSignatures(Rec r)
    {
        try { return IsStateObject(r.Tag) ? ParseStateObject(r).RootSignatures : [Parse(r).Rs]; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { return []; }
    }

    static List<string> Depends(Rec r)
    {
        try { return ParseStateObject(r).Depends; }
        catch (InvalidDataException) { return []; }
    }
}
