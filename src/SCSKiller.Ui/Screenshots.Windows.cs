using SCSKiller.App.Design;
using SCSKiller.Core;

namespace SCSKiller.Ui;

static partial class Screenshots
{
    private static partial Task PlatformShots(MainWindow main, FakeScsKiller fake, IGpuVendorBackend sampleGpu, Func<string, Task> shot) => Task.CompletedTask;
}
