using System.Globalization;
using System.Text;

namespace SCSKiller.Core.App;

/// <summary>The mounts folders are on (/proc/self/mountinfo) and the block devices under them (sysfs).</summary>
static class Mounts
{
    /// <summary>A mountinfo line: the mount's id, its file system type, its source (a block device's path for a disk's) and
    /// where it is mounted.</summary>
    internal sealed record Mount(ulong Id, string Type, string Source, string Point = "");

    // a local disk's file systems: fuseblk is a FUSE driver's on a block device (ntfs-3g, exFAT)
    static readonly HashSet<string> DiskTypes = new(["ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "jfs", "reiserfs", "bcachefs", "nilfs2",
        "ntfs", "ntfs3", "vfat", "msdos", "exfat", "hfsplus", "fuseblk"], StringComparer.Ordinal);

    /// <summary>What Windows calls a fixed, ready drive: the folders are all on one mount, tmpfs or a local disk's file system
    /// whose block device, and each device it is made of, isn't removable media. Network file systems (NFS, SMB, sshfs and
    /// the other FUSE ones), overlays, ZFS, loop devices (disk images) and any type not listed are not; nor is a mount or
    /// device that can't be read.</summary>
    internal static bool Fixed(IEnumerable<string> dirs, Func<string, Mount?> mountOf, Func<string, bool?> removable)
    {
        Mount? at = null;
        foreach (var dir in dirs)
        {
            if (mountOf(dir) is not { } m || at != null && m.Id != at.Id) return false;
            at = m;
        }
        // tmpfs is memory: it can't be away while it's mounted, and its folders go with it when it isn't
        return at != null && (at.Type == "tmpfs" || DiskTypes.Contains(at.Type) && removable(at.Source) == false);
    }

    /// <summary>The mount holding <paramref name="path"/>, its symlinks followed; null when it can't be read.</summary>
    internal static Mount? Of(string path)
    {
        if (Libc.MountId(path) is not { } id) return null;
        try { return In(File.ReadAllText("/proc/self/mountinfo"), id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Mount <paramref name="id"/>'s line in the mountinfo <paramref name="text"/>; null when it has none.</summary>
    internal static Mount? In(string text, ulong id)
    {
        // id parent major:minor root mount-point options [optional fields] - type source super-options
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(' ');
            var dash = Array.IndexOf(f, "-", Math.Min(6, f.Length));
            if (dash < 0 || dash + 2 >= f.Length || !ulong.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var at) || at != id) continue;
            return new Mount(id, Unescape(f[dash + 1]), Unescape(f[dash + 2]), Unescape(f[4]));
        }
        return null;
    }

    // the kernel writes a space, tab, newline or backslash in a field as a backslash and three octal digits
    static string Unescape(string field)
    {
        if (!field.Contains('\\')) return field;
        var s = new StringBuilder();
        for (var i = 0; i < field.Length; i++)
            if (field[i] == '\\' && i + 3 < field.Length && field.AsSpan(i + 1, 3) is var oct && oct.IndexOfAnyExceptInRange('0', '7') < 0)
            {
                s.Append((char)Convert.ToInt32(oct.ToString(), 8));
                i += 3;
            }
            else s.Append(field[i]);
        return s.ToString();
    }

    /// <summary>The block device <paramref name="source"/> names is removable media, or one it is made of is (the disk of a
    /// partition, what a device-mapper or RAID device stacks on); null when it isn't a block device, sysfs doesn't say, or
    /// it is or stacks on a loop device.</summary>
    internal static bool? Removable(string source) =>
        source.StartsWith("/dev/", StringComparison.Ordinal) && Libc.BlockDevice(source) is var (major, minor) ? RemovableAt("/sys", major, minor) : null;

    /// <summary><see cref="Removable"/> for device <paramref name="major"/>:<paramref name="minor"/>, read from the sysfs at <paramref name="sys"/>.</summary>
    internal static bool? RemovableAt(string sys, uint major, uint minor) =>
        Libc.RealPath(Path.Combine(sys, "dev", "block", $"{major}:{minor}")) is { } dev ? RemovableDevice(dev, 0) : null;

    static bool? RemovableDevice(string dev, int depth)
    {
        // a partition's folder is inside its disk's, which holds the flag and what the disk is made of
        var disk = File.Exists(Path.Combine(dev, "partition")) ? Path.GetDirectoryName(dev)! : dev;
        // a loop device's file is named by a path, which a mount made since may cover with another file
        if (Directory.Exists(Path.Combine(disk, "loop"))) return null;
        string flag;
        string[] under;
        try
        {
            flag = File.ReadAllText(Path.Combine(disk, "removable")).Trim();
            var slaves = Path.Combine(disk, "slaves");
            under = Directory.Exists(slaves) ? Directory.GetFileSystemEntries(slaves) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (flag != "0") return flag == "1" ? true : null;
        if (under.Length > 0 && depth == 8) return null;   // no real stack is that deep
        foreach (var s in under)
            if ((Libc.RealPath(s) is { } real ? RemovableDevice(real, depth + 1) : null) is not false and var r) return r;
        return false;
    }
}
