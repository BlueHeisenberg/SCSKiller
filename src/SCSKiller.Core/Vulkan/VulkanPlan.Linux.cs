namespace SCSKiller.Core.Vulkan;

public static partial class VulkanPlan
{
    // Linux opens a file for any writer whatever the share mode: a write lease is granted only while nothing else has the
    // file open, and the unlink is of that file, at the merged length
    private static partial void DeleteMerged(FileStream inbox, string path, long merged, IProgress<string>? log)
    {
        // moved aside under an inbox name of its own: one that can't be put back is imported next time
        var aside = $"scskiller.{Guid.NewGuid():N}.foz";
        if (Vendors.AppCacheFiles.TakeLease(inbox.SafeFileHandle) == 0
            && !Vendors.AppCacheFiles.UnlinkLeased(inbox.SafeFileHandle, path, merged, aside, out var stranded) && stranded)
            log?.Report($"{path} changed while it was being deleted and its name was taken meanwhile: kept as {aside}, to import next time");
    }
}
