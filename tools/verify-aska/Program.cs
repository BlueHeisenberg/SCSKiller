// Run without arguments for synthetic format checks, or pass installed game folders for full extraction and planning.
// dotnet run --project tools/verify-aska -- "/path/to/STAR OCEAN THE DIVINE FORCE" "/path/to/STAR OCEAN - THE LAST HOPE - 4K & Full HD Remaster"
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Aska;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using static Check;

var reader = new AskaReader();
var scratch = Path.Combine(Path.GetTempPath(), "scskiller-aska-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);

try
{
    Fixtures.Verify(reader, Path.Combine(scratch, "fixtures"));
    var hashes = new Dictionary<string, string>();
    foreach (var folder in args)
    {
        var legacy = File.Exists(Path.Combine(folder, "StarOceanTheLastHope.exe"));
        var game = new Game(
            "manual:aska",
            "ASKA",
            Store.Manual,
            folder,
            Path.Combine(folder, legacy ? "StarOceanTheLastHope.exe" : "SO6.exe"));
        var engine = reader.Detect(game) ?? throw new Exception("not detected");
        Require(engine.Unsupported == null && engine.GraphicsApi == "D3D11", "engine must support D3D11");
        Require(ScsKiller.DefaultReaders().Detect(game)?.Family == AskaReader.Family, "default reader registration");

        var index = reader.Index(game, engine, new Progress<string>(Console.WriteLine), CancellationToken.None);
        Require(index.Shaders.Count == (legacy ? 71_583 : 35_850), "expected complete installed shader set");
        Require(index.Shaders.Values.All(s => s.ShaderModel.EndsWith("5_0")), "expected SM5 shaders");
        if (hashes.TryGetValue(engine.Version, out var previous))
        {
            Require(index.ContentHash == previous, "store copies must index identically");
        }

        hashes[engine.Version] = index.ContentHash;
        var requested = index.Shaders.Keys.ToHashSet();
        var received = new HashSet<string>();
        reader.ReadShaders(
            game,
            engine,
            requested,
            (sha, bytes) =>
            {
                Require(requested.Contains(sha) && received.Add(sha), "serve each requested shader once");
                Require(
                    Dxbc.Valid(bytes) && Convert.ToHexStringLower(SHA1.HashData(bytes)) == sha,
                    "served bytes must match their index hash");
            },
            CancellationToken.None);
        Require(received.SetEquals(requested), "serve the complete indexed library");

        var plan = new Planner().Build(
            game,
            engine,
            index,
            null,
            NvidiaLinux.Caps,
            scratch,
            new Progress<string>(Console.WriteLine),
            CancellationToken.None);
        Require(plan.Stats.D3D11Shaders == index.Shaders.Count, "plan every indexed shader for DXVK");

        var work = Path.Combine(scratch, "work");
        new Planner().Materialize(plan, game, engine, reader, null, work, CancellationToken.None);
        var blobs = new HashSet<string>();
        var items = 0;
        foreach (var record in PsoDb.Read(Path.Combine(work, "scskiller_gen.db")))
        {
            if (record.Tag == '1')
            {
                items++;
            }

            if (record.Tag != 'B')
            {
                continue;
            }

            var sha = Convert.ToHexStringLower(record.Payload.AsSpan(0, 20));
            Require(
                Convert.ToHexStringLower(SHA1.HashData(record.Payload.AsSpan(20))) == sha,
                "warm input blob hash");
            blobs.Add(sha);
        }

        Require(
            blobs.SetEquals(requested) && items == requested.Count,
            "materialize every planned shader and its bytecode");
        Directory.Delete(work, true);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => reader.Index(game, engine, null, cancelled.Token));
        Console.WriteLine(
            $"PASS {folder}: {index.Shaders.Count} shaders retrieved, planned and materialized, hash {index.ContentHash}");
    }
}
finally
{
    Directory.Delete(scratch, true);
}
