using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace SCSKiller.Ui.Models;

/// <summary>A game's icon as image file bytes, read without the Windows shell: a packaged (Xbox / Game Pass) game's own
/// logo, else the first icon in the exe's resources as an .ico file. Null when there is none or it can't be read.</summary>
public static partial class GameIcon
{
    /// <summary>The icon size picked from the exe: the smallest at least this wide, else the largest.</summary>
    const int Wanted = 64;
    const uint IconType = 3, GroupIconType = 14;

    public static byte[]? Read(string exePath)
    {
        try
        {
            if (PackageLogo(exePath) is { } logo) return File.ReadAllBytes(logo);
            return File.Exists(exePath) ? ExeIcon(File.ReadAllBytes(exePath)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The Square44x44Logo of the package the exe sits in (appxmanifest.xml in a parent folder): a GDK game's exe
    /// often has no icon of its own (Atomic Heart). Null when not packaged, unreadable or the logo is only there scale-qualified.</summary>
    static string? PackageLogo(string exePath)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(exePath); dir != null; dir = Path.GetDirectoryName(dir))
            {
                var manifest = Path.Combine(dir, "appxmanifest.xml");
                if (!File.Exists(manifest)) continue;
                var m = Square44().Match(File.ReadAllText(manifest));
                return m.Success && Path.Combine(dir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)) is var logo && File.Exists(logo) ? logo : null;
            }
        }
        catch (Exception) { }
        return null;
    }

    [GeneratedRegex("Square44x44Logo=\"([^\"]+)\"")]
    private static partial Regex Square44();

    /// <summary>The exe's first icon group (the one Explorer shows) as an .ico file with the one picked image.</summary>
    public static byte[]? ExeIcon(byte[] pe)
    {
        try
        {
            var r = new Resources(pe);
            if (r.First(GroupIconType) is not { } group || group.Length < 6) return null;
            int count = U16(group, 4);
            int best = -1, bestWidth = 0, bestBits = 0;
            for (int i = 0; i < count && 6 + 14 * (i + 1) <= group.Length; i++)
            {
                int e = 6 + 14 * i, width = group[e] == 0 ? 256 : group[e], bits = U16(group, e + 6);
                bool better = best < 0
                    || (width >= Wanted ? bestWidth < Wanted || width < bestWidth : bestWidth < Wanted && width > bestWidth)
                    || width == bestWidth && bits > bestBits;
                if (better) (best, bestWidth, bestBits) = (i, width, bits);
            }
            if (best < 0) return null;
            int entry = 6 + 14 * best;
            if (r.Find(IconType, U16(group, entry + 12)) is not { } image) return null;
            // ICONDIR, one ICONDIRENTRY (the group's entry with the image's file offset in place of its resource id), the image
            var ico = new byte[22 + image.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
            group.AsSpan(entry, 8).CopyTo(ico.AsSpan(6));
            BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(14), image.Length);
            BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(18), 22);
            image.CopyTo(ico, 22);
            return ico;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { return null; }   // a damaged or truncated file
    }

    static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    /// <summary>The PE resource tree: type, then name or id, then language; offsets in it are from the tree's start.</summary>
    sealed class Resources
    {
        readonly byte[] pe;
        readonly (uint Rva, uint Size, uint Raw)[] sections;
        readonly int root;

        public Resources(byte[] pe)
        {
            this.pe = pe;
            int nt = (int)U32(pe, 0x3C);
            if (U32(pe, nt) != 0x4550) throw new ArgumentException("not a PE file");
            int count = U16(pe, nt + 6), optional = nt + 24, optionalSize = U16(pe, nt + 20);
            int directories = optional + (U16(pe, optional) == 0x20B ? 112 : 96);
            sections = [.. Enumerable.Range(0, count).Select(i => optional + optionalSize + 40 * i).Select(s => (U32(pe, s + 12), Math.Max(U32(pe, s + 8), U32(pe, s + 16)), U32(pe, s + 20)))];
            root = Offset(U32(pe, directories + 2 * 8)) ?? throw new ArgumentException("no resources");
        }

        int? Offset(uint rva) => sections.FirstOrDefault(s => rva >= s.Rva && rva < s.Rva + s.Size) is { Size: > 0 } s ? (int)(rva - s.Rva + s.Raw) : null;

        IEnumerable<(uint Id, bool Named, uint Target)> Entries(int dir)
        {
            int named = U16(pe, dir + 12), ids = U16(pe, dir + 14);
            for (int i = 0; i < named + ids; i++)
                yield return (U32(pe, dir + 16 + 8 * i), i < named, U32(pe, dir + 20 + 8 * i));
        }

        int? Child(int dir, Func<(uint Id, bool Named, uint Target), bool> pick) =>
            Entries(dir).Where(pick).Select(e => (int?)e.Target).FirstOrDefault() is { } t ? root + (t & 0x7FFF_FFFF) : null;

        byte[]? Data(int languages)
        {
            if (Child(languages, _ => true) is not { } leaf || Offset(U32(pe, leaf)) is not { } at) return null;
            return pe.AsSpan(at, (int)U32(pe, leaf + 4)).ToArray();
        }

        public byte[]? First(uint type) =>
            Child(root, e => !e.Named && e.Id == type) is { } names && Child(names, _ => true) is { } languages ? Data(languages) : null;

        public byte[]? Find(uint type, int id) =>
            Child(root, e => !e.Named && e.Id == type) is { } names && Child(names, e => !e.Named && e.Id == id) is { } languages ? Data(languages) : null;
    }
}
