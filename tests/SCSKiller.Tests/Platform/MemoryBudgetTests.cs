using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>"Maximum memory for compiling: Auto": a step per common RAM size, thresholds between them.</summary>
public class MemoryBudgetTests
{
    const long GB = 1L << 30;

    [Theory]
    [InlineData(16, 2)]
    [InlineData(24, 4)]
    [InlineData(32, 6)]
    [InlineData(48, 8)]
    [InlineData(64, 16)]
    [InlineData(128, 32)]  // a quarter from 64 GB
    [InlineData(188, 47)]
    [InlineData(8, 2)]     // the minimum
    [InlineData(96, 24)]
    public void AutoFollowsTheTable(int ramGB, int budgetGB) => Assert.Equal(budgetGB, ScsKiller.AutoCompileMemoryGB(ramGB * GB));

    [Fact]
    public void ReportedSizesJustUnderACommonSizeLandOnIt()
    {
        Assert.Equal(2, ScsKiller.AutoCompileMemoryGB((long)(15.8 * GB)));   // a 16 GB PC with memory reserved by the firmware / iGPU
        Assert.Equal(6, ScsKiller.AutoCompileMemoryGB((long)(31.7 * GB)));
        Assert.Equal(16, ScsKiller.AutoCompileMemoryGB((long)(63.6 * GB)));
        Assert.InRange(ScsKiller.AutoCompileMemoryGB(), 2, Math.Max(16, ScsKiller.RamGB() / 4));   // this PC
    }

    [Fact]
    public void TheSliderEndsAtTheRamRoundedDownLessTwoGb()
    {
        Assert.Equal(47, ScsKiller.RamGB((long)(47.65 * GB)));   // what Windows sees of 48 GB with the iGPU's share taken
        Assert.Equal(45, ScsKiller.MaxCompileMemoryGB((long)(47.65 * GB)));
        Assert.Equal(186, ScsKiller.MaxCompileMemoryGB(188 * GB));
        Assert.Equal(16, ScsKiller.MaxCompileMemoryGB(16 * GB));   // at least 16
    }
}
