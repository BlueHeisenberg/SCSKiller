namespace SCSKiller.Core.App;

public static partial class ProcessMemory
{
    // Linux has no working-set trim: it takes an idle process's pages back under memory pressure.
    public static partial void Trim() { }
}
