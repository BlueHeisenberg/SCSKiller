using System.Security.Cryptography;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <param name="Referenced">distinct non-zero shader / root-signature SHA-1s the records name (PSO stages and root
/// signatures, plan items, D3D11 items)</param>
/// <param name="AlreadyPresent">of those, the ones the input already carries as 'B' records (e.g. recorded root signatures)</param>
/// <param name="Found">pulled from the install (engine reader and the extra blob source) and written as new 'B' records</param>
/// <param name="Missing">neither in the input nor in the install: a different game build than the recording's, or a
/// shader built at run time (sorted)</param>
/// <param name="ContentHashMatches">the index ContentHash against the caller's expected one; null when either wasn't given</param>
/// <param name="FoundInMiddleware">of <paramref name="Found"/>, the ones the extra blob source gave (the middleware DLLs
/// next to the exe: FSR4's compute shaders in amdxcffx64.dll...)</param>
public sealed record RehydrateResult(int Referenced, int AlreadyPresent, int Found, IReadOnlyList<string> Missing, bool? ContentHashMatches, long OutputBytes,
    int FoundInMiddleware = 0)
{
    public bool Complete => Missing.Count == 0;
}

/// <summary>Turns a hash-only recording (a proxy db whose 'B' records were stripped, as shared plans and the community
/// database carry it) back into a replayable db: the same records plus a 'B' blob for every shader the records reference,
/// pulled from the local install through <see cref="IEngineReader.ReadShaders"/> like <see cref="Planner.Materialize"/>,
/// then what that leaves from an optional extra blob source (the caller passes <see cref="Middleware.Blobs"/>: middleware
/// shaders are in DLLs next to the exe, not in the game's index). Engine-independent. Output order: the input's 'B' records, the pulled blobs, then every other record in input order
/// (blobs before the PSOs that use them, as the proxy expects). Written to a temp file and moved into place.</summary>
public static class Rehydrate
{
    /// <summary>Every non-zero blob hash the records reference (stages + root signatures of 'G'/'C'/'S', 'P' items, '1' / '2' / '3' D3D11 items, 'D' D3D11 draws,
    /// DXIL libraries + root signatures of 'R'/'A' state objects).</summary>
    public static HashSet<string> References(IEnumerable<Rec> records)
    {
        var refs = new HashSet<string>();
        foreach (var r in records)
            switch (r.Tag)
            {
                case 'G' or 'C' or 'S': { var p = Parse(r); refs.UnionWith(p.Stages.Values); refs.Add(p.Rs); break; }
                case 'P': { var (_, rs, st) = ParseItem(r.Payload); refs.UnionWith(st.Values); refs.Add(rs); break; }
                case '1': refs.Add(Hex(r.Payload.AsSpan(4, 20))); break;
                case '2': refs.Add(Hex(r.Payload.AsSpan(0, 20))); refs.Add(Hex(r.Payload.AsSpan(20, 20))); break;
                case 'D' or '3': { var d = ParseDraw11(r.Payload); refs.UnionWith([d.Vs, d.Gs, d.Hs, d.Ds]); break; }
                case 'R' or 'A': { var so = ParseStateObject(r); refs.UnionWith(so.Libraries); refs.UnionWith(so.RootSignatures); break; } // not Depends: records, not blobs
                case 'Y': { var y = RtCollections.ParseItem(r.Payload); refs.UnionWith([y.Library, y.Global, y.LocalRayGen, y.LocalOther]); break; }
                case 'H': { var h = RedEngine.RedRayTracing.ParseItem(r.Payload); refs.UnionWith([h.ClosestHit, h.AnyHit ?? Zero, h.Shape.Global, h.Local]); break; }
            }
        refs.Remove(Zero);
        return refs;
    }

    /// <param name="index">the caller's index of this install, only for <paramref name="expectedContentHash"/></param>
    /// <param name="expectedContentHash">the ContentHash the recording was made against (from the plan / community entry)</param>
    /// <param name="moreBlobs">asked once for the hashes the reader didn't give; yields (sha1, bytes), each checked by SHA-1</param>
    public static RehydrateResult Run(string hashOnlyDb, string outDb, Game game, EngineInfo engine, IEngineReader reader,
        ShaderIndex? index = null, string? expectedContentHash = null, CancellationToken ct = default,
        Func<IReadOnlySet<string>, IEnumerable<(string Sha1, byte[] Bytes)>>? moreBlobs = null)
    {
        var have = Read(hashOnlyDb).Where(r => r.Tag == 'B').Select(r => Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
        var refs = References(Read(hashOnlyDb));
        var want = refs.Where(h => !have.Contains(h)).ToHashSet();

        var dir = Path.GetDirectoryName(Path.GetFullPath(outDb))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(outDb) + ".tmp");
        var found = new HashSet<string>();
        var fromMore = 0;
        try
        {
            using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
            {
                foreach (var r in Read(hashOnlyDb).Where(r => r.Tag == 'B')) Write(f, 'B', r.Payload);
                if (want.Count > 0)
                    reader.ReadShaders(game, engine, want, (h, b) =>
                    {
                        lock (found) if (want.Contains(h) && found.Add(h)) WriteBlob(f, h, b);
                    }, ct);
                if (moreBlobs != null && want.Where(h => !found.Contains(h)).ToHashSet() is { Count: > 0 } rest)
                    foreach (var (h, b) in moreBlobs(rest))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!rest.Contains(h) || found.Contains(h) || Hex(SHA1.HashData(b)) != h) continue;
                        found.Add(h);
                        WriteBlob(f, h, b);
                        fromMore++;
                    }
                foreach (var r in Read(hashOnlyDb).Where(r => r.Tag != 'B')) Write(f, r.Tag, r.Payload);
            }
            File.Move(tmp, outDb, true);
        }
        finally { File.Delete(tmp); }

        var missing = want.Where(h => !found.Contains(h)).Order(StringComparer.Ordinal).ToList();
        bool? match = index != null && expectedContentHash != null ? string.Equals(index.ContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase) : null;
        return new RehydrateResult(refs.Count, refs.Count - want.Count, found.Count, missing, match, new FileInfo(outDb).Length, fromMore);
    }
}
