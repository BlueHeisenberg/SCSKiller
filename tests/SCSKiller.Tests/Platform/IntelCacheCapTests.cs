using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

/// <summary>Intel's 512 MiB per-game cache cap (ARCHITECTURE.md, Intel) is its plan budget, never AMD's shared-cache budgets,
/// and what Intel users read about it names Intel.</summary>
public class IntelCacheCapTests
{
    static readonly VendorCaps Intel = new IntelBackend(new GpuInfo(GpuVendor.Intel, "Intel Graphics", "1", 1, 0)).Caps;
    static readonly VendorCaps Amd = new("amd-1", true, false, false, PerStageCache: true);

    static PlanStats Plan(long generated) => new(1, generated, 0, 0, true);

    [Fact]
    public void An_intel_plan_budget_is_the_per_game_cap()
    {
        Assert.Equal(512L << 20, PlanBuilder.CacheBudgetOf(Intel));
        Assert.Equal(PlanBuilder.CacheBudget, PlanBuilder.CacheBudgetOf(Amd));
        Assert.Equal((int)(IntelBackend.PerGameCap / PlanBuilder.ExtraBytes), PlanBuilder.MaximumExtras(Intel, int.MaxValue));
        Assert.Equal((int)(PlanBuilder.ExtraBudget / PlanBuilder.ExtraBytes), PlanBuilder.MaximumExtras(Amd, int.MaxValue));

        var over = IntelBackend.PerGameCap / PlanBuilder.PsoBytes;   // with the one recorded PSO
        Assert.True(ScsKiller.OverCacheBudget(Intel, Plan(over)));
        Assert.False(ScsKiller.OverCacheBudget(Intel, Plan(over - 1)));
        Assert.False(ScsKiller.OverCacheBudget(Amd, Plan(over)));
    }

    [Fact]
    public void Intel_users_read_about_the_intel_cache()
    {
        Assert.Equal("More than the Intel shader cache keeps per game", ScsKiller.CacheBudgetTitle(Intel));
        Assert.StartsWith("This game has more pipelines than the Intel driver keeps for one game (512 MB). ", ScsKiller.CacheBudgetNote(Intel));
        Assert.Contains("AMD", ScsKiller.CacheBudgetNote(Amd));
        foreach (var text in new[] { ScsKiller.CacheBudgetTitle(Intel), ScsKiller.CacheBudgetNote(Intel) })
        {
            Assert.DoesNotContain("AMD", text);
            Assert.DoesNotContain(";", text);
        }
    }

    [Fact]
    public void Shared_reports_say_intel_but_the_active_check_says_other()
    {
        Assert.Equal("intel", ScsKiller.ShareVendor(GpuVendor.Intel));
        Assert.Equal("nvidia", ScsKiller.ShareVendor(GpuVendor.Nvidia));
        Assert.Equal("amd", ScsKiller.ShareVendor(GpuVendor.Amd));
        Assert.Equal("other", ActiveCheck.Gpu(GpuVendor.Intel));   // the server answers 400 to "intel" there
        Assert.Equal("nvidia", ActiveCheck.Gpu(GpuVendor.Nvidia));
        Assert.Equal("amd", ActiveCheck.Gpu(GpuVendor.Amd));
    }
}
