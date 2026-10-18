using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Tests.Planning;

/// <summary>Static samplers follow the player's texture filtering setting: FF7 Rebirth's community recording has the same
/// pipelines at 1x and at 16x anisotropic. With a recording of this PC's, only its setting is compiled and counted; without
/// one, the community's most common, or every setting in Maximum mode, and each pipeline counts once.</summary>
public class SamplerVariantsTests
{
    internal static readonly EngineInfo Ue426 = new("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);
    static ShaderInfo Cs(char c, int srvs = 0) => new(new string(c, 40), Stage.Compute, "cs_6_0", 0, new(0, srvs, 0, 0), srvs > 0 ? [new("srv", 0, 0, 1)] : [], [], []);
    internal static readonly ShaderInfo A = Cs('a'), B = Cs('b'), Unseen = Cs('e', 1);

    internal static readonly byte[] X1 = RootSig.StaticSamplers(Rules.Ff7).ToArray();
    internal static readonly byte[] X16 = Aniso(16);
    internal static byte[] Aniso(uint n)
    {
        var s = RootSig.StaticSamplers(Rules.Ff7).ToArray();
        for (var o = 0; o < s.Length; o += 52)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(o), 0x55);   // D3D12_FILTER_ANISOTROPIC
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(o + 20), n);
        }
        return s;
    }

    /// <summary>The rule's root signature for <paramref name="cs"/>'s resources (A's: none) with these samplers.</summary>
    static (string Sha, byte[] Blob) Rs(byte[] samplers, ShaderInfo? cs = null)
    {
        var b = RootSig.Serialize(RootSig.Build(Rules.Ff7, new Dictionary<Stage, ShaderInfo> { [Stage.Compute] = cs ?? A }, true), samplers);
        return (Hex(SHA1.HashData(b)), b);
    }

    static Rec Pso(byte[] samplers, ShaderInfo cs) => new('C', Compute(Rs(samplers).Sha, cs.Sha1));

    internal static string Db(string dir, string name, params (byte[] Samplers, ShaderInfo Cs)[] psos)
    {
        var path = Path.Combine(dir, name);
        using var f = File.Create(path);
        foreach (var s in psos.Select(p => p.Samplers).DistinctBy(Convert.ToHexString)) { var (h, b) = Rs(s); WriteBlob(f, h, b); }
        foreach (var (s, cs) in psos) Write(f, 'C', Pso(s, cs).Payload);
        return path;
    }

    [Fact]
    public void A_pipeline_at_any_setting_is_one_pipeline()
    {
        var blobs = new[] { Rs(X1), Rs(X16) }.ToDictionary(r => r.Sha, r => r.Blob);
        byte[]? Blob(string h) => blobs.GetValueOrDefault(h);
        Assert.Equal(SamplerVariants.Pipeline(Pso(X1, A), Blob), SamplerVariants.Pipeline(Pso(X16, A), Blob));
        Assert.NotEqual(SamplerVariants.Pipeline(Pso(X1, A), Blob), SamplerVariants.Pipeline(Pso(X1, B), Blob));
        Assert.NotNull(SamplerVariants.Pipeline(Pso(X1, A), Blob));
        Assert.Null(SamplerVariants.Pipeline(Pso(X1, A), _ => null));   // its root signature not at hand
        Assert.Equal(SamplerVariants.Setting(Rs(X1).Blob)!.Value.RootSignature, SamplerVariants.Setting(Rs(X16).Blob)!.Value.RootSignature);
        Assert.NotEqual(SamplerVariants.Setting(Rs(X1).Blob)!.Value.Samplers, SamplerVariants.Setting(Rs(X16).Blob)!.Value.Samplers);
        Assert.Equal(RootSig.StaticSamplers(Rules.Ff7), X1);   // untouched
    }

    [Fact]
    public void Only_another_setting_of_the_own_recordings_samplers_is_foreign()
    {
        var dir = Ff7.TempDir("variants-foreign");
        var own = Db(dir, "own.db", (X1, A));
        var oneSampler = X1[..52];   // another root signature: not a setting of the own recording's
        var community = Db(dir, "community.db", (X1, A), (X1, B), (X16, A), (X16, B), (oneSampler, A));
        // 1x and 4x on a root signature this PC didn't record: 4x is kept (another kind of root signature may take other samplers)
        using (var f = new FileStream(community, FileMode.Append))
            foreach (var (rs, blob) in new[] { Rs(X1, Unseen), Rs(Aniso(4), Unseen) })
            {
                WriteBlob(f, rs, blob);
                Write(f, 'C', Compute(rs, Unseen.Sha1));
            }
        Assert.Equal(new[] { Pso(X16, A).Key, Pso(X16, B).Key }.Order(), SamplerVariants.Foreign(own, [community]).Order());
        Assert.Empty(SamplerVariants.Foreign(Path.Combine(dir, "none.db"), [community], every: true));   // Maximum mode, no recording of this PC's: every setting stays
        Assert.Empty(SamplerVariants.Foreign(Db(dir, "both.db", (X1, A), (X16, B)), [community]));   // both of its own

        var merged = Path.Combine(dir, "merged.db");
        Community.Union(own, [community], merged, SamplerVariants.Foreign(own, [community]));
        Assert.DoesNotContain(Read(merged), r => r.Key == Pso(X16, A).Key || r.Key == Pso(X16, B).Key);
        Assert.Contains(Read(merged), r => r.Key == Pso(X1, B).Key);
        var inputs = WarmInputs.Of(WarmInputs.Recorded.Read([own, community], _ => true, true, SamplerVariants.Foreign(own, [community])), null, [], _ => true);
        Assert.DoesNotContain(inputs, i => WarmInputs.Records(i).Contains(Pso(X16, B).Key));
        Assert.Contains(inputs, i => WarmInputs.Records(i).Contains(Pso(X1, B).Key));
    }

    /// <summary>Without a recording of this PC's, the community's most common setting is taken, by records: the others are
    /// foreign, unless in Maximum mode. A setting of this PC's recording wins over it, in Maximum mode too.</summary>
    [Fact]
    public void Without_an_own_recording_the_most_common_setting_is_taken_unless_maximum()
    {
        var dir = Ff7.TempDir("variants-common");
        var none = Path.Combine(dir, "none.db");
        var (x4, x8) = (Aniso(4), Aniso(8));
        var oneSampler = X1[..52];   // another root signature, only ever at this set: no vote however many records
        var community = Db(dir, "community.db", (X1, A), (X1, B), (x4, A), (x8, A), (X16, A), (oneSampler, A), (oneSampler, B), (oneSampler, Unseen));
        Assert.Equal(new[] { Pso(x4, A).Key, Pso(x8, A).Key, Pso(X16, A).Key }.Order(), SamplerVariants.Foreign(none, [community]).Order());
        Assert.Empty(SamplerVariants.Foreign(none, [community], every: true));
        var own = Db(dir, "own.db", (X16, B));
        var others = new[] { Pso(X1, A).Key, Pso(X1, B).Key, Pso(x4, A).Key, Pso(x8, A).Key }.Order();
        Assert.Equal(others, SamplerVariants.Foreign(own, [community]).Order());
        Assert.Equal(others, SamplerVariants.Foreign(own, [community], every: true).Order());
        Assert.Empty(SamplerVariants.Foreign(none, [Db(dir, "one.db", (X1, A), (X1, B))]));   // one setting: nothing to choose
    }

    /// <summary>Without a recording of this PC's, the player's texture filtering from the game's config picks the setting at
    /// its anisotropy over the most common (D3D12 clamps both to 1-16); none at it falls back to the most common. This PC's
    /// recording and Maximum mode still win.</summary>
    [Fact]
    public void The_players_texture_filtering_picks_its_setting_over_the_most_common()
    {
        var dir = Ff7.TempDir("variants-config");
        var none = Path.Combine(dir, "none.db");
        var (x4, x8) = (Aniso(4), Aniso(8));
        var community = Db(dir, "community.db", (X1, A), (X1, B), (x4, A), (x8, A), (X16, A));
        Assert.Equal(new[] { 1, 4, 16 }, new[] { X1, x4, X16 }.Select(s => SamplerVariants.Anisotropy(Convert.ToHexStringLower(s))));
        var at16 = new[] { Pso(X1, A).Key, Pso(X1, B).Key, Pso(x4, A).Key, Pso(x8, A).Key }.Order();
        Assert.Equal(at16, SamplerVariants.Foreign(none, [community], anisotropy: 16).Order());
        Assert.Equal(at16, SamplerVariants.Foreign(none, [community], anisotropy: 32).Order());
        var mostCommon = new[] { Pso(x4, A).Key, Pso(x8, A).Key, Pso(X16, A).Key }.Order();
        Assert.Equal(mostCommon, SamplerVariants.Foreign(none, [community], anisotropy: 0).Order());   // 1x
        Assert.Equal(mostCommon, SamplerVariants.Foreign(none, [community], anisotropy: 2).Order());   // no setting at 2x
        Assert.Equal(mostCommon, SamplerVariants.Foreign(none, [community]).Order());
        Assert.Empty(SamplerVariants.Foreign(none, [community], every: true, anisotropy: 16));
        Assert.Equal(new[] { Pso(X1, A).Key, Pso(X1, B).Key, Pso(x8, A).Key, Pso(X16, A).Key }.Order(),
            SamplerVariants.Foreign(Db(dir, "own.db", (x4, B)), [community], anisotropy: 16).Order());   // this PC's own
    }

    /// <summary>Two settings with as many records each: the same one is taken whatever the order of the recordings.</summary>
    [Fact]
    public void A_tie_takes_the_same_setting_whatever_the_order()
    {
        var dir = Ff7.TempDir("variants-tie");
        var none = Path.Combine(dir, "none.db");
        var (one, other) = (Db(dir, "one.db", (X1, A)), Db(dir, "other.db", (X16, B)));
        var foreign = SamplerVariants.Foreign(none, [one, other]);
        Assert.Single(foreign);
        Assert.Contains(foreign.Single(), new[] { Pso(X1, A).Key, Pso(X16, B).Key });
        Assert.Equal(foreign, SamplerVariants.Foreign(none, [other, one]));
        Assert.Equal(foreign, SamplerVariants.Foreign(none, [Db(dir, "both.db", (X16, B), (X1, A))]));
    }

    /// <summary>With a recording at several settings (Maximum mode without a recording of this PC's), the plan compiles what
    /// it synthesizes at each one, and counts each pipeline once: the recorded ones at any setting, the synthesized ones without their copies.</summary>
    [Fact]
    public void Without_an_own_recording_every_setting_is_compiled_and_each_pipeline_counted_once()
    {
        var dir = Ff7.TempDir("variants-plan");
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], new[] { A, B, Unseen }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", [A.Sha1, B.Sha1, Unseen.Sha1])]);
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Amd })
        {
            var mixed = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Db(dir, "mixed.db", (X1, A), (X1, B), (X16, A), (X16, B))), caps,
                Path.Combine(dir, caps.Profile + "-mixed"), null, CancellationToken.None);
            var one = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Db(dir, "one.db", (X1, A), (X1, B))), caps,
                Path.Combine(dir, caps.Profile + "-one"), null, CancellationToken.None);
            Assert.Equal((one.Stats.Recorded, one.Stats.Generated), (mixed.Stats.Recorded, mixed.Stats.Generated));   // 2 recorded, the unseen shader planned
            Assert.Equal((0L, 2L + one.Stats.Generated), (one.Stats.Variants, mixed.Stats.Variants));   // a recorded duplicate each, a copy of each planned
            string Aniso(Plan p, string rs) => BinaryPrimitives.ReadUInt32LittleEndian(RootSig.Samplers(PlanFile.Read(p.FilePath).Records
                .Single(r => r.Tag == 'B' && Hex(r.Payload.AsSpan(0, 20)) == rs).Payload[20..]).AsSpan(20)).ToString();
            var unseen = PlanFile.Read(mixed.FilePath).Records.Where(r => r.Tag is 'C' or 'S' or 'P')
                .Select(r => r.Tag == 'P' ? (ParseItem(r.Payload).Rs, ParseItem(r.Payload).Stages) : (Parse(r).Rs, Parse(r).Stages))
                .Where(x => x.Stages.ContainsValue(Unseen.Sha1)).Select(x => Aniso(mixed, x.Rs)).Order();
            Assert.Equal(["1", "16"], unseen);
        }
    }

    /// <summary>A recording's inputs are keyed as before for records without settings variants: a warm's key file from
    /// an earlier build still takes them (keys computed with 1.2.4-internal.3's WarmInputs).</summary>
    [Fact]
    public void Inputs_keep_their_keys()
    {
        var dir = Ff7.TempDir("variants-keys");
        var db = Db(dir, "rec.db", (X1, A), (X1, B), (X1, Unseen));
        using (var f = new FileStream(db, FileMode.Append))
        {
            var g = new byte[616];
            Convert.FromHexString(Rs(X1).Sha).CopyTo(g, 0);
            Convert.FromHexString(A.Sha1).CopyTo(g, 20);
            Convert.FromHexString(new string('f', 40)).CopyTo(g, 40);
            var gfx = new Rec('G', g);
            Write(f, 'G', gfx.Payload);
            Write(f, 'N', new NvState(gfx.Key, 12, 1, 1, 0).ToRec().Payload);
        }
        bool Has(string h) => h != new string('f', 40);
        string Digest(bool nvidia) => Hex(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
            WarmInputs.Of(WarmInputs.Recorded.Read([db], Has, nvidia), null, [], Has).Order(StringComparer.Ordinal).Select(WarmInputs.Token)))));
        Assert.Equal(("f86dfd1323cf6ae1a395e5de4935ea1271a0dedc", "1643a2a9551ca16cf56d46b04279cc56cc2b236b"), (Digest(true), Digest(false)));
    }
}
