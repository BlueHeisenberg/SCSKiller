using System.Diagnostics;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// AMD DirectX 12 cache files kept aside as hard links, on fake cache folders under the test's own root.
public sealed class AmdKeptTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "scskiller-kept-" + Guid.NewGuid().ToString("N")[..8]);
    string Dxc => Path.Combine(_root, "AMD", "DxcCache");
    string KeptDir => Path.Combine(_root, "data", "games", "g", "kept");
    static readonly HashSet<string> Ours = new(StringComparer.OrdinalIgnoreCase) { "dxc:6b2fcd83" };
    const string Name = "6b2fcd83.dfac411e.71efbc0e.2b1a674a.0.parc", OtherKey = "12345678.dfac411e.71efbc0e.2b1a674a.0.parc";

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    string File(string name, int size = 4096, byte fill = 1)
    {
        Directory.CreateDirectory(Dxc);
        var path = Path.Combine(Dxc, name);
        System.IO.File.WriteAllBytes(path, Enumerable.Repeat(fill, size).ToArray());
        return path;
    }

    AmdKept Kept() => new(Dxc, KeptDir);

    [Fact]
    public void A_removed_file_goes_back_as_the_same_file()
    {
        var path = File(Name);
        var id = AmdKept.Id(path);
        var k = Kept();
        Assert.Equal(0, k.Keep([Name], Ours, "drv").Unkept);
        Assert.Equal(id, k.KeptId(Name));
        Assert.Equal((4096L, 0L), k.Size());

        System.IO.File.Delete(path);   // the driver evicts it
        Assert.Equal((4096L, 4096L), k.Size());
        var at = DateTimeOffset.Now;
        Assert.Equal(1, k.PutBack(Ours.Contains, late: true, at));
        Assert.Equal(id, AmdKept.Id(path));
        Assert.Equal((4096L, 0L), k.Size());
        Assert.True(k.Read() is { Late: true, Unkept: 0, Driver: "drv" } state && state.PutBackAt == at);
        Assert.Equal(0, k.PutBack(Ours.Contains, late: false, at.AddMinutes(1)));   // nothing missing: the state stays
        Assert.True(k.Read().Late);
    }

    [Fact]
    public void Put_back_never_overwrites_a_file_and_skips_keys_no_longer_ours()
    {
        var path = File(Name);
        var k = Kept();
        k.Keep([Name], Ours, null);
        System.IO.File.Delete(path);
        File(Name, 100, fill: 7);   // the game made the name again meanwhile
        Assert.Equal(0, k.PutBack(Ours.Contains, false, DateTimeOffset.Now));
        Assert.Equal(Enumerable.Repeat((byte)7, 100), System.IO.File.ReadAllBytes(path));

        System.IO.File.Delete(path);
        Assert.Equal(0, k.PutBack(_ => false, false, DateTimeOffset.Now));   // another game shares the key now
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void A_file_made_again_replaces_the_kept_one_and_other_keys_go()
    {
        var path = File(Name);
        File(OtherKey);
        var k = Kept();
        k.Keep([Name, OtherKey], new HashSet<string> { "dxc:6b2fcd83", "dxc:12345678" }, null);
        var old = k.KeptId(Name);
        System.IO.File.Delete(path);
        File(Name, 100);   // a new file under the name: another file id
        Assert.NotEqual(old, AmdKept.Id(path));

        k.Keep([Name, OtherKey], Ours, null);
        Assert.Equal(AmdKept.Id(path), k.KeptId(Name));
        Assert.Null(k.KeptId(OtherKey));   // not this game's key any more: only the kept name goes
        Assert.True(System.IO.File.Exists(Path.Combine(Dxc, OtherKey)));
    }

    [Fact]
    public void Drop_deletes_only_the_kept_names_and_never_DxcCache()
    {
        var path = File(Name);
        var k = Kept();
        k.Keep([Name], Ours, null);
        var foreign = Path.Combine(KeptDir, "notes.txt");
        var odd = Path.Combine(KeptDir, OtherKey);   // a key's name, but no link this class made
        System.IO.File.WriteAllText(foreign, "x");
        System.IO.File.WriteAllText(odd, "x");

        Assert.True(k.Drop());
        Assert.True(System.IO.File.Exists(path));
        Assert.Equal(4096, new FileInfo(path).Length);
        Assert.False(System.IO.File.Exists(Path.Combine(KeptDir, Name)) || System.IO.File.Exists(Path.Combine(KeptDir, AmdKept.StateFile)));
        Assert.True(System.IO.File.Exists(foreign) && System.IO.File.Exists(odd));
        Assert.False(k.Any);

        System.IO.File.Delete(foreign);
        System.IO.File.Delete(odd);
        Assert.True(k.Drop());
        Assert.False(Directory.Exists(KeptDir));
    }

    [Fact]
    public void A_kept_name_that_is_not_its_link_is_never_deleted_or_put_back()
    {
        var path = File(Name);
        var k = Kept();
        k.Keep([Name], Ours, null);
        var kept = Path.Combine(KeptDir, Name);
        System.IO.File.Delete(kept);
        System.IO.File.WriteAllBytes(kept, [9]);   // another file under the kept name
        System.IO.File.Delete(path);
        Assert.Equal(0, k.PutBack(Ours.Contains, false, DateTimeOffset.Now));
        Assert.False(System.IO.File.Exists(path));
        File(Name);
        Assert.Equal(1, k.Keep([Name], Ours, null).Unkept);   // left alone, not replaced
        Assert.True(k.Drop());
        Assert.Equal([9], System.IO.File.ReadAllBytes(kept));
    }

    [Fact]
    public void A_link_in_use_at_a_drop_is_neither_counted_nor_put_back_and_goes_at_the_next_keep()
    {
        var path = File(Name);
        var k = Kept();
        k.Keep([Name], Ours, "old");
        var kept = Path.Combine(KeptDir, Name);
        using (new FileStream(kept, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // no delete sharing
            Assert.False(k.Drop());
        Assert.True(k.Read().Dropping);
        Assert.False(k.Any);
        System.IO.File.Delete(path);
        Assert.Equal(0, k.PutBack(Ours.Contains, false, DateTimeOffset.Now));
        Assert.True(System.IO.File.Exists(kept));

        File(Name, 100);
        var s = k.Keep([Name], Ours, "new");
        Assert.False(s.Dropping);
        Assert.Equal("new", s.Driver);
        Assert.Equal(AmdKept.Id(path), k.KeptId(Name));
    }

    [Fact]
    public void A_link_in_use_is_left_in_place_and_counted()
    {
        var path = File(Name);
        var k = Kept();
        k.Keep([Name], Ours, null);
        var old = k.KeptId(Name);
        System.IO.File.Delete(path);
        File(Name, 100);   // made again while the game holds the old file
        using (new FileStream(Path.Combine(KeptDir, Name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // no delete sharing, as the driver
        {
            var s = k.Keep([Name], Ours, null);
            Assert.Equal(1, s.Unkept);
            Assert.Equal(Process.GetCurrentProcess().ProcessName + ".exe", s.HeldBy, ignoreCase: true);
        }
        Assert.Equal(old, k.KeptId(Name));
        Assert.Equal(100, new FileInfo(path).Length);
        Assert.Equal(0, k.Keep([Name], Ours, null).Unkept);   // let go: replaced at the next try
        Assert.Equal(AmdKept.Id(path), k.KeptId(Name));
    }

    [Fact]
    public void DxcCache_moved_through_a_junction_gets_no_kept_files()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        Assert.False(AmdKept.Usable(Dxc, data));   // no DxcCache yet
        Directory.CreateDirectory(Dxc);
        Assert.True(AmdKept.Usable(Dxc, data));

        var moved = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var link = Path.Combine(_root, "AMD", "DxcCache-link");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{moved}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mk.WaitForExit();
        try { Assert.False(AmdKept.Usable(link, data)); }
        finally { Directory.Delete(link); }
    }
}
