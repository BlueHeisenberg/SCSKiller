namespace SCSKiller.Core.Vulkan;

public static partial class VulkanPlan
{
    // Import's open shares the inbox only for delete: while it holds it no writer opens it, so what it merged is all of it
    private static partial void DeleteMerged(FileStream inbox, string path, long merged, IProgress<string>? log) => File.Delete(path);
}
