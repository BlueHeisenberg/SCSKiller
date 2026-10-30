using System.Runtime.InteropServices;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    public static partial ScsKiller CreateDefault()
    {
        var vendor = DetectGpu();
        var steam = SteamSource.Root();
        var k = new ScsKiller([new SteamSource(), new EpicSource(), new XboxSource(), new GogSource(), new UbisoftSource(), new BattleNetSource(), new PurpleSource(), new HoYoPlaySource(), new GryphlineSource(), new EaSource(), new GaijinSource(), new RiotSource(),
                new ManualSource(new AppStore(AppStore.DefaultDir))],
            vendor, DefaultReaders(), new Planner(Path.Combine(AppStore.DefaultDir, "packs"), SharedPackDir(AppStore.DefaultDir, vendor.Vendor)), new Warmer(vendor),
            AppStore.DefaultDir, NativeTools.Find("d3d12.dll")) { Adapters = GpuBackends.Adapters, AgsDll = NativeTools.Find("scskiller_ags.dll"), SteamRoot = steam };
        k.RedetectGpu();   // DriverId from DXGI before anything compares it
        return k;
    }

    private static partial string LedgerText(string ledgerDir) => Path.GetFullPath(ledgerDir);

    internal static partial void WriteKey(string temp, string text) => File.WriteAllText(temp, text);

    internal static partial byte[] ReadKey(string file) => File.ReadAllBytes(file);

    private static partial string RootIni(string here, string install) => Path.GetRelativePath(here, install);

    private static partial string RootFolder(string here, string root) => Path.GetFullPath(Path.Combine(here, root));

    private static partial bool RootNames(string here, string root, string install) => true;

    // a drive letter names the drive of all three
    private static partial bool OnFixedDrive(string root, string apps, string exeDir) =>
        Path.GetPathRoot(root) is { Length: 3 } drive && new DriveInfo(drive) is { DriveType: DriveType.Fixed, IsReady: true };

    private static partial bool BackgroundBegin() => SetThreadPriority(GetCurrentThread(), 0x00010000);   // THREAD_MODE_BACKGROUND_BEGIN; fails when already in it

    private static partial void BackgroundEnd() => SetThreadPriority(GetCurrentThread(), 0x00020000);

    [DllImport("kernel32.dll")] static extern bool SetThreadPriority(nint thread, int priority);
    [DllImport("kernel32.dll")] static extern nint GetCurrentThread();

    private static partial bool Linked(string path) => new FileInfo(path).LinkTarget != null;

    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle? OpenToDelete(string path, string dir)
    {
        const uint Delete = 0x00010000, Read = 0x80000000, OpenExisting = 3;
        var h = CreateFileW(path, Delete | Read, 0, 0, OpenExisting, 0, 0);
        if (!h.IsInvalid) return h;
        var (error, hr) = (Marshal.GetLastWin32Error(), Marshal.GetHRForLastWin32Error());
        h.Dispose();
        if (error is 2 or 3) return null;   // not there
        throw new IOException($"couldn't open the {LedgerStamp} in {dir}", hr);
    }

    private static partial void DeleteOpen(Microsoft.Win32.SafeHandles.SafeFileHandle h, string path, string dir)
    {
        byte delete = 1;
        if (!SetFileInformationByHandle(h, 4 /* FileDispositionInfo */, ref delete, 1))
            throw new IOException($"couldn't delete the {LedgerStamp} in {dir}", Marshal.GetHRForLastWin32Error());
    }

    private static partial bool Alive(OfflineSession s)
    {
        if (s.Pid == 0) return false;
        using var h = OpenProcess(Synchronize | QueryLimitedInformation, false, s.Pid);
        return !h.IsInvalid && GetProcessTimes(h, out var created, out _, out _, out _) && created == s.Started && WaitForSingleObject(h, 0) == WaitTimeout;
    }

    /// <summary>The logon entry's value name and command: "!" defers Windows' deletion of it until the command has run.</summary>
    internal static (string Name, string Command) RunOnceEntry(string gameId, string exe) =>
        ($"!SCSKiller offline cleanup {gameId}", $"\"{exe}\" {CleanupArg} \"{gameId}\"");

    private static partial void RunOnce(string gameId, string? exe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            var (name, command) = RunOnceEntry(gameId, exe ?? "");
            if (exe != null) key.SetValue(name, command);
            else if (key.GetValue(name) != null) key.DeleteValue(name, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, ref byte info, int size);

    private static partial string? CpuModel()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return CpuName(key?.GetValue("ProcessorNameString") as string);
    }

    private static partial TimeSpan UserIdleTime()
    {
        var info = new LastInputInfo { Size = 8 };
        // No input info (not an interactive session): don't hold background work back forever; games still pause it.
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.MaxValue;
    }

    struct LastInputInfo { public uint Size, Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static partial string? ThisAppProgram() => Environment.ProcessPath;

    public static partial string? StartDriverUpdated()
    {
        if (ScheduledTask.AppExe() is not { } app) return null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app, ScheduledTask.DriverUpdatedArg) { UseShellExecute = false })?.Dispose();
        return app;
    }
}
