using System.ComponentModel;

namespace SCSKiller.Core.App;

/// <summary>A game's compile size limit (<see cref="GameRecord.CacheCapGB"/>): what its DirectX 12 plan may add to the shader cache.</summary>
public static class CompileLimit
{
    public const int MinGb = 1, MaxGb = 128;
    public static readonly int[] Presets = [4, 8, 16, 32, 64, 128];
    /// <summary>A saved limit outside <see cref="MinGb"/>..<see cref="MaxGb"/> (only a hand-edited file holds one) is held to the nearest end.</summary>
    public static int Clamp(int gb) => Math.Clamp(gb, MinGb, MaxGb);
    /// <summary>The limit a game's plan is held to; null = none.</summary>
    public static int? Of(int? own) => own is > 0 and var gb ? Clamp(gb) : null;

    /// <summary>The slider's track, 0..<see cref="Track"/>, logarithmic: 1 to 16 GB take 4/7 of it.</summary>
    public const double Track = 1000;
    public static double TrackAt(int gb) => Math.Round(Math.Log(Clamp(gb)) / Math.Log(MaxGb) * Track);
    public static int GbAt(double pos) => double.IsNaN(pos) ? MinGb : Clamp((int)Math.Round(Math.Pow(MaxGb, Math.Clamp(pos, 0, Track) / Track)));
}

/// <summary>A game page's compile size limit picker: No limit or Custom size, and under Custom size a number box, a slider and
/// presets that show one size. A slider change is saved once it settles for <see cref="Settle"/>, so a drag writes once, the
/// rest at once, and saves run in order.</summary>
public sealed class LimitPick : INotifyPropertyChanged
{
    internal static TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    readonly Func<int?> read;
    readonly Func<int?, Task> write;
    readonly Func<long?> used;
    int spare = 8;
    (int? Value, int Version)? want;
    int version;

    /// <param name="read">The saved <see cref="GameRecord.CacheCapGB"/>.</param>
    /// <param name="write">Saves it, its errors its own.</param>
    /// <param name="used">What the game's shader cache uses now.</param>
    public LimitPick(Func<int?> read, Func<int?, Task> write, Func<long?> used)
    {
        (this.read, this.write, this.used) = (read, write, used);
        Presets = [.. CompileLimit.Presets.Select(gb => new LimitPreset(gb, this))];
    }

    int? Saved => want is { } w ? w.Value : read();

    public List<string> Modes { get; } = ["No limit", "Custom size"];

    public int ModeIndex
    {
        get => Saved is > 0 ? 1 : 0;
        set
        {
            if (value < 0 || value == ModeIndex) return;
            spare = Size;
            Save(value == 1 ? Size : null);
        }
    }
    public bool IsSize => ModeIndex == 1;
    public int Size => Saved is > 0 and var gb ? CompileLimit.Clamp(gb) : spare;

    /// <summary>The number box: an empty box (NaN) keeps the size.</summary>
    public double Gb
    {
        get => Size;
        set { if (!double.IsNaN(value)) SetSize((int)Math.Clamp(Math.Round(value), CompileLimit.MinGb, CompileLimit.MaxGb)); else Changed(); }
    }

    /// <summary>The slider, 0..<see cref="CompileLimit.Track"/>. A step of one (an arrow key) moves at least one GB.</summary>
    public double Track
    {
        get => CompileLimit.TrackAt(Size);
        set
        {
            if (double.IsNaN(value)) return;
            var (now, gb) = (CompileLimit.TrackAt(Size), CompileLimit.GbAt(value));
            SetSize(gb == Size && Math.Abs(value - now) is > 0 and <= 1.5 ? CompileLimit.Clamp(Size + Math.Sign(value - now)) : gb, settle: true);
        }
    }

    public IReadOnlyList<LimitPreset> Presets { get; }
    public void Pick(int gb) => SetSize(CompileLimit.Clamp(gb));

    void SetSize(int gb, bool settle = false)
    {
        if (gb == Size) return;   // a hidden slider's or box's echo never switches the mode
        spare = gb;
        Save(gb, settle);
    }

    /// <summary>"This game's shader cache uses 84.3 GB now", empty while unknown.</summary>
    public string UsesNow => used() is > 0 and var b ? $"This game's shader cache uses {Format.Bytes(b)} now." : "";
    public bool HasUsesNow => UsesNow.Length > 0;

    /// <summary>The last save: done once input settled and it was written.</summary>
    public Task Saving { get; private set; } = Task.CompletedTask;

    // Only a drag waits to settle: a debounced write is lost if the app quits first.
    void Save(int? value, bool settle = false)
    {
        want = (value, ++version);
        Saving = Write(version, settle);
        Changed();
    }

    // A change waits for writes under way, never for a superseded drag's settle.
    readonly SemaphoreSlim writing = new(1, 1);

    async Task Write(int v, bool settle)
    {
        if (settle) await Task.Delay(Settle);
        if (want?.Version != v) return;
        await writing.WaitAsync();
        try
        {
            if (want is not { } w || w.Version != v) return;
            try { await write(w.Value); }
            catch (Exception) { }   // the picker shows the saved value again, and later saves still run
            if (want?.Version == v) want = null;
        }
        finally { writing.Release(); }
        Changed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>The saved value or the cache's usage changed elsewhere.</summary>
    public void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
}

/// <summary>A <see cref="LimitPick"/> preset's button.</summary>
public sealed class LimitPreset(int gb, LimitPick pick) : System.Windows.Input.ICommand
{
    public int Gb => gb;
    public string Label => $"{gb} GB";
    public override string ToString() => Label;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? _) => true;
    public void Execute(object? _) => pick.Pick(gb);
}
