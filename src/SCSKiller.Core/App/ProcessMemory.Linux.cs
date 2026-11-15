namespace SCSKiller.Core.App;

public static partial class ProcessMemory
{
    // Linux has no working-set trim: it takes an idle process's pages back under memory pressure.
    public static partial void Trim() { }

    /// <summary>Linux: RssAnon + VmSwap of /proc/self/status, the anonymous pages touched. Process.PrivateMemorySize64 is VmData,
    /// which counts reserved and untouched mappings too (measured: +1 GB for 1 GB never touched, +1 GB for 120 parked threads'
    /// stacks). macOS: <see cref="Footprint"/>.</summary>
    public static partial long Private()
    {
        if (OperatingSystem.IsMacOS()) return Footprint();
        try
        {
            long kb = 0;
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("RssAnon:", StringComparison.Ordinal) || line.StartsWith("VmSwap:", StringComparison.Ordinal))
                    kb += long.Parse(line.AsSpan(line.IndexOf(':') + 1).Trim().TrimEnd("kB").Trim(), System.Globalization.CultureInfo.InvariantCulture);
            return kb << 10;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { return 0; }
    }
}
