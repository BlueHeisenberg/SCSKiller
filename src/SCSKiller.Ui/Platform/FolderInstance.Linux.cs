using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SCSKiller.Ui.Platform;

/// <summary>One instance per data folder. The holder has an flock on the folder's instance.lock, which the kernel releases
/// when the holder's process ends however it ends, and listens on an abstract Unix socket under a random name it writes
/// into that file (owner-only): another user can't learn the name before it is bound, so can't take it first. A later
/// launch on the folder reads the name and hands its arguments over there. Each end checks that the other runs as this
/// user. Every descriptor is close-on-exec, so a process the app starts never keeps the folder held.</summary>
sealed class FolderInstance : IDisposable
{
    public const string LockFile = "instance.lock";

    public enum Outcome
    {
        /// <summary>This process holds the folder.</summary>
        Holds,
        /// <summary>The holder took this launch's arguments.</summary>
        HandedOver,
        /// <summary>A holder runs but didn't take them in time: it keeps the folder.</summary>
        NoAnswer,
        /// <summary>The lock can't be had at all (no access, no flock on that file system): nobody holds the folder.</summary>
        NoLock,
    }

    internal const int MaxMessage = 64 << 10, MaxArgs = 256;
    static readonly byte[] Magic = "SCSK\u0001"u8.ToArray();
    const byte Ack = 6;
    const string Prefix = "SCSKiller/";
    /// <summary>How long a connected peer may take to send: one that stalls holds the next launch up at most this long.
    /// Replaceable for tests.</summary>
    internal static TimeSpan ReadFor = TimeSpan.FromSeconds(2);

    readonly int _lock, _listen = -1;
    readonly uint _uid;
    readonly Func<string[], bool> _launched;
    readonly Thread? _serve;
    volatile bool _closed;

    /// <summary>Holds <paramref name="dataDir"/>, or hands <paramref name="args"/> to the process that does, trying again
    /// until <paramref name="patience"/> has passed: a holder that ends meanwhile leaves the folder to this one.
    /// <paramref name="launched"/> gets each later launch's arguments, on another thread, and says whether it took them: a
    /// launch not taken isn't acknowledged, so its sender tries again. <paramref name="uid"/>: the user both ends must run
    /// as, this process's effective one unless a test says otherwise. <paramref name="log"/> gets why the folder can't be
    /// held, or how its mode was changed.</summary>
    public static Outcome Start(string dataDir, IReadOnlyList<string> args, Func<string[], bool> launched, TimeSpan patience,
        out FolderInstance? held, uint? uid = null, Action<string>? log = null)
    {
        for (var clock = Stopwatch.StartNew(); ; Thread.Sleep(50))
        {
            held = TryHold(dataDir, launched, out var taken, uid, log);
            if (held != null) return Outcome.Holds;
            if (!taken) return Outcome.NoLock;
            // one connection waits for the rest of the time: a busy holder gets the launch once
            if (Forward(dataDir, args, patience - clock.Elapsed, uid)) return Outcome.HandedOver;
            if (clock.Elapsed >= patience) return Outcome.NoAnswer;
        }
    }

    /// <summary>The folder's holder, or null: <paramref name="taken"/> when another process holds it.</summary>
    internal static FolderInstance? TryHold(string dataDir, Func<string[], bool> launched, out bool taken, uint? uid = null,
        Action<string>? log = null)
    {
        taken = false;
        try { Directory.CreateDirectory(dataDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var me = uid ?? geteuid();
        var dir = Retry(() => open(dataDir, ORdOnly | OCloExec | ODirectory, 0));
        if (dir < 0) return null;
        try
        {
            if (!OwnersOnly(dir, dataDir, me, log)) return null;
            // relative to the folder just checked: a path renamed meanwhile can't put another folder's lock in its place
            var fd = Retry(() => openat(dir, LockFile, ORdWr | OCreat | OCloExec | ONoFollow, OwnerOnly));
            if (fd < 0) return null;
            if (Retry(() => flock(fd, LockEx | LockNb)) != 0)
            {
                taken = Marshal.GetLastPInvokeError() == EWouldBlock;
                close(fd);
                return null;
            }
            fchmod(fd, OwnerOnly);   // a file an older run made with another mode: the name in it is this user's alone
            return new FolderInstance(fd, launched, me);
        }
        finally { close(dir); }
    }

    /// <summary>Only the folder's owner may add or remove its entries, or another user could unlink the lock under its holder
    /// and hold a fresh one. Another user's folder is refused. This user's, when others may write it (a umask of 002 makes
    /// it 0775), loses group and world write first.</summary>
    static bool OwnersOnly(int dir, string path, uint uid, Action<string>? log)
    {
        var st = new byte[256];   // struct statx: stx_uid at 20, stx_mode at 28, on every architecture
        if (statx(dir, "", AtEmptyPath, StatxType | StatxMode | StatxUid, st) != 0) return false;
        var (owner, mode) = (BitConverter.ToUInt32(st, 20), BitConverter.ToUInt16(st, 28) & 0xFFF);
        if (owner != uid)
        {
            log?.Invoke($"{path} belongs to user {owner}, not to this one");
            return false;
        }
        if ((mode & GroupOthersWrite) == 0) return true;
        var tight = mode & ~GroupOthersWrite;
        if (fchmod(dir, tight) != 0)
        {
            log?.Invoke($"{path} is writable by other users and its mode can't be changed");
            return false;
        }
        log?.Invoke($"{path} was mode {Convert.ToString(mode, 8)}, now {Convert.ToString(tight, 8)}: only its owner may change its files");
        return true;
    }

    FolderInstance(int lockFd, Func<string[], bool> launched, uint uid)
    {
        (_lock, _launched, _uid) = (lockFd, launched, uid);
        var name = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var s = socket(AfUnix, SockStream | SockCloExec, 0);
        if (s >= 0)
        {
            var (addr, len) = Address(name);
            if (bind(s, addr, len) == 0 && listen(s, 16) == 0) _listen = s;
            else close(s);
        }
        // the name only once it is bound; none when nothing listens, so a later launch gives up instead of reaching a stale one
        var text = Encoding.ASCII.GetBytes(_listen >= 0 ? name : "");
        pwrite(_lock, text, text.Length, 0);
        ftruncate(_lock, text.Length);
        if (_listen < 0) return;
        _serve = new Thread(Serve) { IsBackground = true, Name = "SCSKiller instance" };
        _serve.Start();
    }

    /// <summary>Hands <paramref name="args"/> to the folder's holder: true once it has taken them, within about
    /// <paramref name="wait"/>.</summary>
    internal static bool Forward(string dataDir, IReadOnlyList<string> args, TimeSpan wait, uint? uid = null)
    {
        if (Name(dataDir) is not { } name) return false;
        var s = socket(AfUnix, SockStream | SockCloExec, 0);
        if (s < 0) return false;
        try
        {
            wait = TimeSpan.FromMilliseconds(Math.Max(wait.TotalMilliseconds, 100));
            Timeout(s, SoSndTimeo, wait);
            Timeout(s, SoRcvTimeo, wait);
            var (addr, len) = Address(name);
            // another user's socket under the name never gets the arguments
            if (Retry(() => connect(s, addr, len)) != 0 || PeerUid(s) != (uid ?? geteuid())) return false;
            var message = Encode(args);
            for (var sent = 0; sent < message.Length;)
            {
                var n = send(s, ref message[sent], message.Length - sent, MsgNoSignal);
                if (n < 0 && Marshal.GetLastPInvokeError() == EIntr) continue;
                if (n <= 0) return false;
                sent += (int)n;
            }
            shutdown(s, ShutWr);
            var reply = new byte[1];
            nint got;
            do got = recv(s, ref reply[0], 1, 0);
            while (got < 0 && Marshal.GetLastPInvokeError() == EIntr);
            return got == 1 && reply[0] == Ack;
        }
        finally { close(s); }
    }

    /// <summary>The holder's socket name from the lock file; null while there is none (being written, or nothing listens).</summary>
    internal static string? Name(string dataDir)
    {
        var fd = Retry(() => open(Path.Combine(dataDir, LockFile), ORdOnly | OCloExec | ONoFollow, 0));
        if (fd < 0) return null;
        try
        {
            var buf = new byte[64];
            var n = read(fd, ref buf[0], buf.Length);
            return n == 32 && buf.AsSpan(0, 32).IndexOfAnyExcept("0123456789abcdef"u8) < 0 ? Encoding.ASCII.GetString(buf, 0, 32) : null;
        }
        finally { close(fd); }
    }

    void Serve()
    {
        while (!_closed)
        {
            var c = accept4(_listen, 0, 0, SockCloExec);
            if (c < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (_closed || errno is EBadF or EInval) return;
                if (errno != EIntr) Thread.Sleep(100);   // out of descriptors and the like: no spin
                continue;
            }
            try { Answer(c); }
            catch (Exception) { }   // a peer's message never ends the listening
            finally { close(c); }
        }
    }

    void Answer(int c)
    {
        if (PeerUid(c) != _uid) return;
        Timeout(c, SoSndTimeo, ReadFor);
        var buf = new byte[MaxMessage + 1];
        var n = 0;
        // one deadline for the whole message: each receive waits only for what is left of it
        for (var clock = Stopwatch.StartNew(); ;)
        {
            var left = ReadFor - clock.Elapsed;
            if (n == buf.Length || left <= TimeSpan.Zero) return;
            Timeout(c, SoRcvTimeo, left);
            var got = recv(c, ref buf[n], buf.Length - n, 0);
            if (clock.Elapsed >= ReadFor) return;
            if (got == 0) break;
            if (got < 0 && Marshal.GetLastPInvokeError() == EIntr) continue;
            if (got < 0) return;
            n += (int)got;
        }
        if (Parse(buf.AsSpan(0, n)) is not { } args || !_launched(args)) return;
        var ack = Ack;
        send(c, ref ack, 1, MsgNoSignal);
    }

    /// <summary>The magic, then each argument in UTF-8 and a NUL, as many whole ones as fit.</summary>
    internal static byte[] Encode(IReadOnlyList<string> args)
    {
        var m = new List<byte>(Magic);
        foreach (var a in args.Take(MaxArgs))
        {
            var b = Encoding.UTF8.GetBytes(a.Replace("\0", ""));
            if (m.Count + b.Length + 1 > MaxMessage) break;
            m.AddRange(b);
            m.Add(0);
        }
        return [.. m];
    }

    /// <summary>A message's arguments; null when it isn't one: another magic, cut short, too long or too many arguments.
    /// Bytes that aren't UTF-8 read as U+FFFD.</summary>
    internal static string[]? Parse(ReadOnlySpan<byte> m)
    {
        if (m.Length > MaxMessage || !m.StartsWith(Magic)) return null;
        m = m[Magic.Length..];
        if (m.Length > 0 && m[^1] != 0) return null;
        var args = new List<string>();
        for (int end; m.Length > 0; m = m[(end + 1)..])
        {
            if (args.Count == MaxArgs) return null;
            end = m.IndexOf((byte)0);
            args.Add(Encoding.UTF8.GetString(m[..end]));
        }
        return [.. args];
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        if (_listen >= 0)
        {
            shutdown(_listen, ShutRdWr);   // wakes the accept
            _serve?.Join();
            close(_listen);
        }
        close(_lock);
    }

    /// <summary>A sockaddr_un in the abstract namespace: a NUL, then the name, without a terminating NUL.</summary>
    static (byte[] Addr, int Len) Address(string name)
    {
        var addr = new byte[110];
        BitConverter.TryWriteBytes(addr, (ushort)AfUnix);
        return (addr, 3 + Encoding.ASCII.GetBytes(Prefix + name, 0, Prefix.Length + name.Length, addr, 3));
    }

    /// <summary>The peer's effective user id when it connected or listened (SO_PEERCRED's struct ucred: pid, uid, gid).</summary>
    static uint? PeerUid(int s)
    {
        var cred = new int[3];
        var len = sizeof(int) * 3;
        return getsockopt(s, SolSocket, SoPeerCred, cred, ref len) == 0 && len == sizeof(int) * 3 ? (uint)cred[1] : null;
    }

    static void Timeout(int s, int option, TimeSpan t)
    {
        t = TimeSpan.FromTicks(Math.Max(t.Ticks, TimeSpan.TicksPerMillisecond));   // a zero timeval waits forever
        var tv = new[] { (long)t.TotalSeconds, (long)(t.TotalMicroseconds % 1_000_000) };   // struct timeval on 64-bit
        setsockopt(s, SolSocket, option, tv, sizeof(long) * 2);
    }

    static int Retry(Func<int> call)
    {
        int r;
        do r = call();
        while (r < 0 && Marshal.GetLastPInvokeError() == EIntr);
        return r;
    }

    // the generic values, the same on x86_64 and arm64 but O_NOFOLLOW
    const int AfUnix = 1, SockStream = 1, SockCloExec = 0x80000, SolSocket = 1, SoPeerCred = 17, SoRcvTimeo = 20, SoSndTimeo = 21,
        MsgNoSignal = 0x4000, ShutWr = 1, ShutRdWr = 2, ORdOnly = 0, ORdWr = 2, OCreat = 0x40, OCloExec = 0x80000, OwnerOnly = 0x180,
        LockEx = 2, LockNb = 4, EIntr = 4, EBadF = 9, EWouldBlock = 11, EInval = 22, AtEmptyPath = 0x1000, GroupOthersWrite = 0x12;
    const uint StatxType = 0x1, StatxMode = 0x2, StatxUid = 0x8;
    static readonly bool Arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    static readonly int ONoFollow = Arm64 ? 0x8000 : 0x20000, ODirectory = Arm64 ? 0x4000 : 0x10000;

    [DllImport("libc", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buf);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern int flock(int fd, int operation);
    [DllImport("libc", SetLastError = true)] static extern int fchmod(int fd, int mode);
    [DllImport("libc", SetLastError = true)] static extern nint pwrite(int fd, byte[] buf, nint count, long offset);
    [DllImport("libc", SetLastError = true)] static extern int ftruncate(int fd, long length);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, ref byte buf, nint count);
    [DllImport("libc", SetLastError = true)] static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] static extern int bind(int fd, byte[] addr, int len);
    [DllImport("libc", SetLastError = true)] static extern int listen(int fd, int backlog);
    [DllImport("libc", SetLastError = true)] static extern int accept4(int fd, nint addr, nint len, int flags);
    [DllImport("libc", SetLastError = true)] static extern int connect(int fd, byte[] addr, int len);
    [DllImport("libc", SetLastError = true)] static extern nint send(int fd, ref byte buf, nint len, int flags);
    [DllImport("libc", SetLastError = true)] static extern nint recv(int fd, ref byte buf, nint len, int flags);
    [DllImport("libc", SetLastError = true)] static extern int shutdown(int fd, int how);
    [DllImport("libc", SetLastError = true)] static extern int getsockopt(int fd, int level, int name, int[] value, ref int len);
    [DllImport("libc", SetLastError = true)] static extern int setsockopt(int fd, int level, int name, long[] value, int len);
    [DllImport("libc")] static extern uint geteuid();
}
