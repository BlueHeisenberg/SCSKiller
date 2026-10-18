using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// AMD's DirectX 11 cache files set aside for a before run and put back.
public sealed class DxQuarantineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "scskiller-dxq-" + Guid.NewGuid().ToString("N")[..8]);
    string Dx => Path.Combine(_root, "AMD", "DxCache");
    string Journal => Path.Combine(_root, "data", "dxcache-set-aside.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    string File(string app, int size = 100)
    {
        Directory.CreateDirectory(Dx);
        var path = Path.Combine(Dx, $"{app}.1.2.3.0.parc");
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void Set_aside_files_go_back_except_the_learned_keys()
    {
        var (kept, other, game) = (File("aaaa"), File("bbbb"), File("cccc"));
        var q = new DxQuarantine(Dx, Journal);
        var j = q.SetAside("game", k => k == "dx:000000000000aaaa");
        Assert.Equal(2, j.Moves.Count);
        Assert.True(System.IO.File.Exists(kept));
        Assert.False(System.IO.File.Exists(other) || System.IO.File.Exists(game));
        Assert.Equal(Path.GetDirectoryName(Dx), Path.GetDirectoryName(q.Folder));   // beside it: a rename on one volume
        Assert.Equal("game", q.Pending!.GameId);

        File("cccc", 10);   // the game makes its key again during the run
        Assert.Empty(q.Restore("dx:000000000000cccc"));
        Assert.True(System.IO.File.Exists(other));
        Assert.Equal(10, new FileInfo(game).Length);   // its old cache is gone, the run's stays
        Assert.Null(q.Pending);
        Assert.False(Directory.Exists(q.Folder));
    }

    [Fact]
    public void A_file_in_use_stays_and_is_listed()
    {
        var held = File("aaaa");
        File("bbbb");
        using var open = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var j = new DxQuarantine(Dx, Journal).SetAside("game", _ => false);
        Assert.Equal([held], j.Skipped);
        Assert.Single(j.Moves);
        Assert.True(System.IO.File.Exists(held));
    }

    /// <summary>The app stopped mid-run: a new start reads the journal and puts everything back. A file made again at its
    /// place meanwhile: the larger stays.</summary>
    [Fact]
    public void A_set_aside_cut_off_is_put_back_by_the_next_start()
    {
        var (a, b) = (File("aaaa", 100), File("bbbb", 100));
        new DxQuarantine(Dx, Journal).SetAside("game", _ => false);
        File("aaaa", 500);   // another app made a larger file at a's place
        File("bbbb", 5);     // and a smaller one at b's
        var next = new DxQuarantine(Dx, Journal);
        Assert.NotNull(next.Pending);
        Assert.Empty(next.Restore());
        Assert.Equal((500L, 100L), (new FileInfo(a).Length, new FileInfo(b).Length));
        Assert.Null(next.Pending);
    }

    /// <summary>A smaller file made again at a set-aside one's place and still in use: the set-aside one waits, nothing lost.</summary>
    [Fact]
    public void A_larger_set_aside_file_waits_while_its_replacement_is_in_use()
    {
        var a = File("aaaa", 100);
        var q = new DxQuarantine(Dx, Journal);
        q.SetAside("game", _ => false);
        File("aaaa", 5);
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Empty(q.Restore());   // not a failure: it waits
            Assert.NotNull(q.Pending);
            Assert.Equal(5, new FileInfo(a).Length);
        }
        Assert.Empty(q.Restore());
        Assert.Equal(100, new FileInfo(a).Length);
        Assert.Null(q.Pending);
    }

    /// <summary>Nothing to set aside still leaves the run's journal, so its end learns the key the game made.</summary>
    [Fact]
    public void Nothing_to_set_aside_still_marks_the_run()
    {
        File("aaaa");
        var q = new DxQuarantine(Dx, Journal);
        Assert.Empty(q.SetAside("game", _ => true).Moves);
        Assert.Equal("game", q.Pending?.GameId);
        Assert.Empty(q.Restore());
        Assert.Null(q.Pending);
        Assert.False(Directory.Exists(q.Folder));
    }
}
