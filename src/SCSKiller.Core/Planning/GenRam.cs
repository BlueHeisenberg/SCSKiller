using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Planning;

/// <summary>scskiller_gen.db being written, its shader blobs past the first <see cref="FileFirst"/> bytes in memory instead of
/// the file: the plan's bytes are most of what a compile writes itself (Jedi Survivor: 1.49 GB for a 1.48 GB cache), and no form
/// of temporary file stayed off the disk. On Windows a named shared section, which a dead app's goes with. On Linux a file on
/// tmpfs, as a Wine child can't open a host's section: flock'd while it's in use, so <see cref="Sweep"/> takes a dead app's.
/// <see cref="MemFile"/> names it for the staged child (proxy gen_mem.h). The process holds it until <see cref="Drop"/>, so child
/// restarts find it. Its pages aren't the warm's private bytes, which its memory budget watches, and past
/// <see cref="Quota"/> the blobs go to the file as they always did.</summary>
public sealed class GenRam : Stream
{
    public const string MemFile = "scskiller_gen.mem";

    /// <summary>What a warm says when <see cref="MemFile"/> doesn't lead to the blobs: a sandbox with a /dev/shm of its own.</summary>
    public const string Unreadable = "the plan's shared memory can't be read";

    internal static string ShmDir = "/dev/shm";
    const string ShmPrefix = "scskiller-plan-";

    /// <summary>A smaller plan isn't worth holding memory for.</summary>
    internal static long FileFirst = 64L << 20;

    /// <summary>What a view commits at a time: the section reserves <see cref="Quota"/>, and a page holds commit charge only once
    /// a view of its chunk was made.</summary>
    const long Chunk = 64L << 20;

    static readonly ConcurrentDictionary<string, (IDisposable Holder, long Used, string? Shm)> Held = new(StringComparer.OrdinalIgnoreCase);

    readonly string _work;
    readonly Stream _file;
    readonly string _name = $@"Local\SCSKiller-plan-{Guid.NewGuid():N}";
    readonly long _quota = Quota() / Chunk * Chunk;
    readonly Queue<MemoryMappedViewStream> _views = new();
    MemoryMappedFile? _map;
    FileStream? _shm;   // Linux's in place of _map
    string? _shmPath;
    long _onFile, _used, _committed, _blobs;
    bool _closed;   // no more goes to the section
    bool _ended;
    string? _why;   // why

    readonly IProgress<string>? log;

    public GenRam(string work, Stream file, IProgress<string>? log = null) => (_work, _file, this.log) = (work, file, log);

    /// <summary>What the section may take: an eighth of the PC's memory, leaving a quarter of it free for the warm and the
    /// driver (the warm cuts its workers below an eighth free).</summary>
    static long Quota()
    {
        if (OperatingSystem.IsLinux()) return MemInfo() is var (total, free) && total > 0 ? Math.Clamp(free - total / 4, 0, total / 8) : 0;
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) ? Math.Clamp((long)m.AvailPhys - (long)(m.TotalPhys / 4), 0, (long)(m.TotalPhys / 8)) : 0;
    }

    /// <summary>MemTotal and MemAvailable, which leaves tmpfs pages out, in bytes; zeros when unreadable.</summary>
    static (long Total, long Free) MemInfo()
    {
        var (total, free) = (0L, 0L);
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var kb = () => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = kb();
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) free = kb();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or OverflowException) { return (0, 0); }
        return (total, free);
    }

    /// <summary>A quarter of the PC's memory stays free, and free to commit, for the app, the warm and the driver.</summary>
    static bool Spare()
    {
        if (OperatingSystem.IsLinux()) return MemInfo() is var (total, free) && total > 0 && free > total / 4 + Chunk && new DriveInfo(ShmDir).AvailableFreeSpace > Chunk;
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) && m.AvailPhys > m.TotalPhys / 4 + (ulong)Chunk && m.AvailPageFile > m.TotalPhys / 4 + (ulong)Chunk;
    }

    /// <summary>A blob record, taken when the section has room; false: it belongs in the file.</summary>
    internal bool Add(char tag, ReadOnlySpan<byte> head, ReadOnlySpan<byte> payload)
    {
        var size = 5 + head.Length + payload.Length;
        if (_onFile < FileFirst) { _onFile += size; return false; }
        if (_closed || !Room(_used + size)) return false;
        Span<byte> at = stackalloc byte[5];
        at[0] = (byte)tag;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(at[1..], (uint)(head.Length + payload.Length));
        try
        {
            Put(at);
            Put(head);
            Put(payload);
        }
        catch (IOException e) when (_shm != null)   // a full tmpfs: what it took of this blob goes
        {
            _shm.SetLength(_used);
            _shm.Position = _used;
            (_closed, _why) = (true, e.Message);
            return false;
        }
        _used += size;
        _blobs++;
        return true;
    }

    /// <summary>Commits chunks up to <paramref name="end"/>. Once it can't, the rest of the blobs stay in the file.</summary>
    bool Room(long end)
    {
        try
        {
            while (_committed < end)
            {
                if (_committed + Chunk > _quota || !Spare())
                {
                    (_closed, _why) = (true, _committed + Chunk > _quota && _quota < Chunk ? "too little free memory at the start" : _committed + Chunk > _quota ? "its limit reached" : "free memory ran short");
                    return false;
                }
                if (OperatingSystem.IsLinux())
                {
                    if (_shm == null)
                    {
                        Sweep();
                        if (new DriveInfo(ShmDir).DriveFormat != "tmpfs")
                        {
                            (_closed, _why) = (true, $"{ShmDir} isn't in memory");
                            return false;
                        }
                        // a name Sweep doesn't look at until the flock is held: opening and locking are two steps
                        var id = Guid.NewGuid().ToString("N");
                        var locking = Path.Combine(ShmDir, ".locking-" + id);
                        _shm = new FileStream(locking, new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 0,
                            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        });
                        _shmPath = Path.Combine(ShmDir, ShmPrefix + id);
                        File.Move(locking, _shmPath);
                    }
                    _committed += Chunk;
                    continue;
                }
                _map ??= MemoryMappedFile.CreateNew(_name, _quota, MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.DelayAllocatePages, HandleInheritability.None);
                var view = _map.CreateViewStream(_committed, Chunk, MemoryMappedFileAccess.Write);
                _views.Enqueue(view);
                if (VirtualAlloc(view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (nint)view.PointerOffset, (nuint)Chunk, 0x1000, 4) == 0)   // MEM_COMMIT, PAGE_READWRITE
                {
                    (_closed, _why) = (true, $"committing failed (error {Marshal.GetLastPInvokeError()})");
                    return false;
                }
                _committed += Chunk;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or PlatformNotSupportedException)
        {
            (_closed, _why) = (true, e.Message);
            return false;
        }
    }

    void Put(ReadOnlySpan<byte> bytes)
    {
        if (_shm != null) { _shm.Write(bytes); return; }
        while (!bytes.IsEmpty)
        {
            var view = _views.Peek();
            var n = (int)Math.Min(bytes.Length, view.Length - view.Position);
            view.Write(bytes[..n]);
            bytes = bytes[n..];
            if (view.Position == view.Length) _views.Dequeue().Dispose();
        }
    }

    /// <summary>Lets go of the section of a work folder.</summary>
    public static void Drop(string work)
    {
        if (!Held.TryRemove(work, out var held)) return;
        held.Holder.Dispose();
        if (held.Shm != null) File.Delete(held.Shm);
        if (Directory.Exists(work)) File.Delete(Path.Combine(work, MemFile));
    }

    /// <summary>Linux: removes what a killed app left on tmpfs, where it would stay in memory. A live one's flock keeps it.</summary>
    public static void Sweep()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(ShmDir, ShmPrefix + "*"))
                try
                {
                    using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) File.Delete(f);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // in use, or another user's
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The blob records the section of <paramref name="work"/> holds; none without one.</summary>
    public static IEnumerable<PsoDb.Rec> Blobs(string work)
    {
        if (!Held.TryGetValue(work, out var held)) yield break;
        if (held.Holder is FileStream shm)
        {
            shm.Position = 0;
            foreach (var r in PsoDb.Read(shm)) yield return r;
            yield break;
        }
        using var view = ((MemoryMappedFile)held.Holder).CreateViewStream(0, held.Used, MemoryMappedFileAccess.ReadWrite);
        foreach (var r in PsoDb.Read(view)) yield return r;
    }

    public override void Write(byte[] buffer, int offset, int count) => _file.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _file.Write(buffer);
    public override void Flush() => _file.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_ended)
        {
            _ended = true;
            while (_views.TryDequeue(out var view)) view.Dispose();
            _file.Dispose();
            if ((_map != null || _shm != null) && _used > 0)
            {
                Held[_work] = (_map ?? (IDisposable)_shm!, _used, _shmPath);
                var name = _shmPath != null ? @"\\?\unix" + _shmPath.Replace('/', '\\') : _name;   // Wine's form, whichever drives the prefix has
                App.AppStore.WriteAtomic(Path.Combine(_work, MemFile), System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{name} {_used} {_blobs}\n")));
            }
            else
            {
                _map?.Dispose();
                _shm?.Dispose();
                if (_shmPath != null) File.Delete(_shmPath);
            }
            log?.Report(FormattableString.Invariant($"the plan's shaders: {_used >> 20} MB in memory, the rest in scskiller_gen.db") + (_why != null ? $": {_why}" : ""));
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32", SetLastError = true)]
    static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
}
