namespace SCSKiller.Core.App;

/// <summary>One line of the report's details: the two runs' values and an optional note beside them.</summary>
public sealed record ReportRow(string Label, string Before, string After, string? Note = null);

/// <summary>The report's words (doc 6.1) as both apps show them: the card's subtitle and three tiles, the details, the notes.</summary>
public sealed record ReportText(string Subtitle, IReadOnlyList<(string Label, string Value)> Tiles, IReadOnlyList<ReportRow> Rows, IReadOnlyList<string> Notes);

public static partial class BeforeAfter
{
    public static ReportText Report(string game, BeforeAfterResult r, GpuVendor vendor)
    {
        var c = r.Compare;
        string subtitle = c.Dx11 ? Dx11Label
            : c.AlreadySmooth ? "Already smooth on this PC: few shader compilation stutters even before the compile"
            : "Shader compilation stutter before and after SCSKiller compiled its shaders";
        // DirectX 11: no ready share and no shader split, every frame of 50 ms or more
        (string, string)[] tiles = c.Dx11
            ? [("STUTTERS", $"{c.Before.Hitches:N0}  →  {c.After.Hitches:N0}"),
               ("TIME FROZEN", $"{c.Before.HitchSeconds:0.0} s  →  {c.After.HitchSeconds:0.0} s"),
               ("WORST STUTTER", $"{Ms(c.Before.WorstHitchMs)}  →  {Ms(c.After.WorstHitchMs)}")]
            : [("SHADER COMPILATION STUTTERS", $"{c.Before.ShaderStutters:N0}  →  {c.After.ShaderStutters:N0}"),
               ("READY FROM THE CACHE", $"{Pct(c.Before.ReadyShare)}  →  {Pct(c.After.ReadyShare)}"),
               ("WORST STUTTER", $"{Ms(c.Before.WorstStutterMs)}  →  {Ms(c.After.WorstStutterMs)}")];

        string Count(long n, RunStats run) => $"{n:N0}" + (c.PerTenMinutes && run.PlaySeconds > 0 ? $"  ({n * 600 / run.PlaySeconds:0.#} per 10 min)" : "");
        var rows = new List<ReportRow> { new("Play time", Clock(c.Before.PlaySeconds), Clock(c.After.PlaySeconds)) };
        if (c.Dx11)
        {
            rows.Add(new("Stutters of 50 ms or more", Count(c.Before.Hitches, c.Before), Count(c.After.Hitches, c.After)));
            rows.Add(new("Time frozen by them", $"{c.Before.HitchSeconds:0.0} s", $"{c.After.HitchSeconds:0.0} s"));
            rows.Add(new("Worst stutter", Ms(c.Before.WorstHitchMs), Ms(c.After.WorstHitchMs)));
        }
        else
        {
            rows.Add(new("Shader compilation stutters", Count(c.Before.ShaderStutters, c.Before), Count(c.After.ShaderStutters, c.After)));
            rows.Add(new("Ready from the cache", Pct(c.Before.ReadyShare), Pct(c.After.ReadyShare)));
            // state objects aren't in the ready share
            if (r.Before.Creates is { } bc && r.After.Creates is { } ac && bc.StateObjectsReady + bc.StateObjectsCompiled + ac.StateObjectsReady + ac.StateObjectsCompiled > 0)
                rows.Add(new("Ray tracing ready from the cache", Rt(bc), Rt(ac)));
            rows.Add(new("Pipelines compiled while playing", Count(c.Before.PlayCompiles, c.Before), Count(c.After.PlayCompiles, c.After)));
            rows.Add(new("Time frozen by shader compilation stutters", $"{c.Before.FrozenSeconds:0.0} s", $"{c.After.FrozenSeconds:0.0} s"));
            rows.Add(new("Worst shader compilation stutter", Ms(c.Before.WorstStutterMs), Ms(c.After.WorstStutterMs)));
        }
        rows.Add(new("1% low", $"{c.Before.Low1PctFps:0} fps", $"{c.After.Low1PctFps:0} fps"));
        rows.Add(new("Average", $"{c.Before.AvgFps:0} fps", $"{c.After.AvgFps:0} fps",
            "1% low and average count from the game's first frame, menus and loading included, so a faster start alone raises them."));
        if (!c.Dx11)
            rows.Add(new("Other hitches (not shader-related)", Count(c.Before.OtherHitches, c.Before), Count(c.After.OtherHitches, c.After),
                "Streaming hitches can drop on a second run: Windows keeps the game's files in memory."));

        var notes = new List<string>(c.Notes);
        if (c.Dx11) notes.Add("On DirectX 11 stutters on loading screens count too, and streaming stutters can drop on a second run.");
        if (c.AlreadySmooth) notes.Add($"{game} barely stuttered from shaders on this PC even before the compile.");
        // the D3D11 recorder records input layouts on AMD only, frame times only on NVIDIA
        notes.Add(c.Dx11 && vendor != GpuVendor.Amd ? "Compiled from the game's files." : "Compiled from the game's files and your before run's recording.");
        notes.Add("Frame times and this report stay on your PC unless you share the image.");
        return new(subtitle, tiles, rows, notes);
    }

    /// <summary>The card's corner line: the app and, with the GPU shown, its driver (a driver version names the vendor).</summary>
    public static string Versions(string? driver) => $"SCSKiller {AppVersion.Current}" + (driver == null ? "" : $"  ·  driver {driver}");

    /// <summary>The card's footer: the play, the swap chain's size (all SCSKiller knows of the settings) and, when shown, the GPU.</summary>
    public static string Footer(BeforeAfterResult r, string? gpu)
    {
        var (b, a) = PlayMinutes(r.Compare);
        string? size = Size(r.Before.SwapChain) is { } s && s == Size(r.After.SwapChain) ? s : null;
        return string.Join("  ·  ", new[] { b == a ? $"{b} min of play each" : $"{b} and {a} min of play", "same PC", size, gpu }.OfType<string>());
    }

    /// <summary>"2560×1440" from the csv's swap chain line ("2560x1440 fmt 28").</summary>
    static string? Size(string? swapChain) => swapChain?.Split(' ')[0].Replace('x', '×');

    /// <summary>"5:00": minutes and seconds.</summary>
    public static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)seconds % 60:00}";

    // neither composer takes an image from a link: the apps copy it and say to paste it
    public static string PostOnX(string shareText) =>
        $"https://x.com/intent/post?text={Uri.EscapeDataString(WithoutLink(shareText))}&url={Uri.EscapeDataString(Link)}";

    // a text post: a link post has no body to paste the image into
    public static string PostOnReddit(string shareText) =>
        $"https://www.reddit.com/submit?selftext=true&title={Uri.EscapeDataString(WithoutLink(shareText))}&text={Uri.EscapeDataString(Link)}";

    /// <summary>X shows the url param as the link: the text's own trailing link would show twice.</summary>
    static string WithoutLink(string text)
    {
        text = text.TrimEnd();
        foreach (var link in new[] { Link, "scskiller.com" })
            if (text.EndsWith(link, StringComparison.OrdinalIgnoreCase)) return text[..^link.Length].TrimEnd();
        return text;
    }

    /// <summary>The save picker's suggested name, without the characters a file name can't hold.</summary>
    public static string ImageName(string game) =>
        string.Join(" ", $"{game} before and after".Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

    static string Rt(CreateStats c) => $"{c.StateObjectsReady:N0} of {c.StateObjectsReady + c.StateObjectsCompiled:N0}";
    static string Pct(double? share) => share is { } s ? $"{s * 100:0}%" : Format.Dash;
    static string Ms(double ms) => ms <= 0 ? "none" : ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";
}
