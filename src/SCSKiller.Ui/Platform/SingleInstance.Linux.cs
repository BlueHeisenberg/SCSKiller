using SCSKiller.Core.App;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Platform;

/// <summary>One instance per data folder (<see cref="FolderInstance"/>). Unlike Windows, which scopes the redirect to the
/// identity's key and the exe's path, every build on a folder hands its launch to the one holding it.</summary>
static partial class SingleInstance
{
    static FolderInstance? folder;
    static readonly Launches launches = new();

    /// <summary>This process holds its data folder's lock: the only instance of the app on that folder.</summary>
    public static bool Held => folder != null || macLock != null;

    /// <summary>True: another process holds the data folder and took this launch's arguments, or didn't take them within 30
    /// seconds and keeps the folder, so this one exits.</summary>
    public static bool RedirectedToRunning(InstanceIdentity identity, bool toastStart) => OperatingSystem.IsMacOS() ? !HoldsOnMac(AppStore.DefaultDir)
        : RedirectedToRunning(AppStore.DefaultDir, Environment.GetCommandLineArgs()[1..], launches, TimeSpan.FromSeconds(30), out folder);

    static FileStream? macLock;

    // FolderInstance needs statx and abstract sockets. FileShare.None is an flock here: one app per folder, no hand-off.
    static bool HoldsOnMac(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            macLock = new FileStream(Path.Combine(dir, FolderInstance.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException e) when (e.HResult == 35)   // EWOULDBLOCK: another process holds it
        {
            Console.Error.WriteLine($"SCSKiller already runs on {dir}");
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"SCSKiller couldn't lock {Path.Combine(dir, FolderInstance.LockFile)}, so it leaves game folders alone: {e.Message}");
            return true;
        }
    }

    internal static bool RedirectedToRunning(string dir, IReadOnlyList<string> args, Launches launches, TimeSpan patience, out FolderInstance? held)
    {
        switch (FolderInstance.Start(dir, args, launches.Offer, patience, out held, log: Console.Error.WriteLine))
        {
            case FolderInstance.Outcome.HandedOver:
                return true;
            case FolderInstance.Outcome.NoAnswer:
                Console.Error.WriteLine($"SCSKiller already runs on {dir} and didn't answer");
                return true;
            case FolderInstance.Outcome.NoLock:
                Console.Error.WriteLine($"SCSKiller couldn't lock {Path.Combine(dir, FolderInstance.LockFile)}, so it leaves game folders alone");
                return false;
            default:
                return false;
        }
    }

    /// <summary>No toast starts the app on Linux.</summary>
    public static IDictionary<string, string>? LaunchToast() => null;

    /// <summary>Another launch handed its command line over (any thread), also one that came before this call.</summary>
    public static void Listen(Action<string> launched, Action<IDictionary<string, string>> toast) => launches.Listen(launched);

    /// <summary>The desktop decides: the window's own activation is the request.</summary>
    public static void ToFront(object window) { }

    /// <summary>The launches handed over, kept until <see cref="Listen"/>, at most <see cref="EarlyMax"/>: one past that isn't
    /// taken, so its sender tries again until it gives up.</summary>
    internal sealed class Launches
    {
        public const int EarlyMax = 32;
        readonly List<string> _early = [];
        Action<string>? _handle;

        /// <summary>Any thread. False: not taken.</summary>
        public bool Offer(string[] args)
        {
            var line = string.Join(' ', args);
            Action<string>? h;
            lock (_early)
                if ((h = _handle) == null)
                {
                    if (_early.Count == EarlyMax) return false;
                    _early.Add(line);
                }
            h?.Invoke(line);
            return true;
        }

        public void Listen(Action<string> launched)
        {
            List<string> before;
            lock (_early)
            {
                _handle = launched;
                before = [.. _early];
                _early.Clear();
            }
            foreach (var line in before) launched(line);
        }
    }
}
