namespace SCSKiller.Core.App;

/// <summary>Whether another user could replace a path, folder by folder.</summary>
static class OwnPath
{
    /// <summary>Why another user could replace <paramref name="path"/> or a folder above it, from it up to /; null when none
    /// could (<see cref="Node"/>).</summary>
    internal static string? Exposed(string path, uint uid, Func<string, (uint Uid, uint Mode)?> lstat, Func<string, int> acl, bool stickyRoot = false)
    {
        for (; ; path = Path.GetDirectoryName(path)!)
        {
            if (Node(path, uid, lstat, acl, stickyRoot) is { } why) return why;
            if (path == "/") return null;
        }
    }

    /// <summary>Why another user could replace <paramref name="path"/> itself; null when none could. It belongs to root or
    /// <paramref name="uid"/>, isn't a link, has no group or other write (no group can be proven to hold this user alone) and
    /// no access ACL, whose named users the group bits don't show. <paramref name="stickyRoot"/>: a sticky folder of root's
    /// (/tmp) may have group and other write, as only an entry's owner can rename or delete it. <paramref name="lstat"/>: the
    /// path's own owner and mode. <paramref name="acl"/>: <see cref="Libc.AccessAcl"/>.</summary>
    internal static string? Node(string path, uint uid, Func<string, (uint Uid, uint Mode)?> lstat, Func<string, int> acl, bool stickyRoot = false)
    {
        if (lstat(path) is not { } node) return $"{path} can't be read";
        if ((node.Mode & 0xF000) == 0xA000) return $"{path} is a link";
        if (node.Uid != 0 && node.Uid != uid) return $"{path} belongs to another user";
        var sticky = stickyRoot && node.Uid == 0 && (node.Mode & 0x200) != 0;   // S_ISVTX
        if ((node.Mode & 0x2) != 0 && !sticky) return $"every user can write to {path}";   // S_IWOTH
        if ((node.Mode & 0x10) != 0 && !sticky) return $"a group can write to {path}";   // S_IWGRP
        // a default ACL is left: it grants nothing on the folder itself, and what it seeds below is checked there
        if (acl(path) is var error and not (Libc.NoData or Libc.NotSupported)) return error == 0 ? $"{path} has an ACL" : $"{path}'s ACL can't be read";
        return null;
    }
}
