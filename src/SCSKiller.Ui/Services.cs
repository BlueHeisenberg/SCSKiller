using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.App.Design;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui;

sealed class UiThread : IUiThread
{
    public bool TryEnqueue(Action run)
    {
        Dispatcher.UIThread.Post(run);
        return true;
    }
}

/// <summary>Game icons read off the UI thread (<see cref="GameIcon"/>), decoded once per exe and kept.</summary>
sealed class Icons : IGameIcons
{
    const int Size = 64;   // Library 32 px, the game page 56 px
    readonly Dictionary<string, Bitmap?> cache = [];
    public event Action? Changed;

    public object? Get(string exePath)
    {
        if (cache.TryGetValue(exePath, out var known)) return known;
        cache[exePath] = null;
        _ = LoadAsync(exePath);
        return null;
    }

    async Task LoadAsync(string path)
    {
        var icon = await Task.Run(() =>
        {
            if (GameIcon.Read(path) is not { } bytes) return null;
            try
            {
                using var s = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(s, Size);
            }
            catch (Exception) { return null; }   // a format or image the decoder can't read: the tile
        });
        if (icon == null) return;
        cache[path] = icon;
        Changed?.Invoke();
    }
}

/// <summary>The design data's members that IScsKiller doesn't carry.</summary>
sealed class FakeDesign(FakeScsKiller fake) : IDesignData
{
    public ManualSource Manual => fake.Manual;
    public string? ScanProgress => fake.ScanProgress;
    public string? AfterUpdateLine => fake.AfterUpdateLine;
    public string? FoldLine(string gameId) => fake.FoldLine(gameId);
    public string? FoldingLine => fake.Folding == null ? null : fake.AfterUpdateLine;
    public IReadOnlyList<string> FoldingGames => fake.Folding is { } id ? [id] : [];
    public void RenameManualGame(string gameId, string name) => fake.RenameManualGame(gameId, name);
}
