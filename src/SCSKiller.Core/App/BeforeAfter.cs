using System.Globalization;

namespace SCSKiller.Core.App;

/// <summary>The before and after recording's pure parts: whether a run counts, the comparison, the share text.</summary>
public static partial class BeforeAfter
{
    /// <summary>Play a run needs to count; the countdown asks for <see cref="BeforeAfterRecord.Minutes"/>.</summary>
    public static readonly TimeSpan MinPlay = TimeSpan.FromMinutes(3);

    /// <summary>With fewer shader stutters than this in the before run, the game was already smooth.</summary>
    public const int SmoothStutters = 3;

    // Every user-facing line of the report and the share text
    const string NoFrames = "The {0} run has no frame times";
    const string NoCreates = "The {0} run has no pipeline log";
    const string TooShort = "The {0} run had {1} of play, play at least {2}.";
    const string LibraryLoads = "The after run loaded {0} more pipelines from the game's own cache, not counted for SCSKiller";
    const string OtherSettings = "The runs had different resolutions or display formats: {0} before, {1} after";
    const string OverlayOnce = "The in-game countdown was on in one run only";
    const string MaxCompiled = "The after run was compiled with Max compile";
    const string Crashed = "The game didn't close normally in the before run";
    public const string MaybeWarm = "AMD's DirectX 11 driver cache can't be checked, so the before run may not have been fully cold";
    public const string VulkanNotCold = "Some of the driver's Vulkan cache couldn't be set aside or cleared, so the before run may not have been fully cold";
    const string MaybeHelped = "The game's DirectX 11 driver cache couldn't be told from another app's, so the after run may have used the before run's";
    const string FrameGenBoth = "Frame generation was {0} in both runs.";
    const string FrameGenOne = "Frame generation was on in the {0} run only.";
    const string Share = "Shader compilation stutter in {0}: {1} → {2} in {3} of play after SCSKiller precompiled its shaders.";
    const string ShareReady = " {0}% of pipelines were ready instead of {1}%.";
    const string ShareSmooth = "{0} was already smooth here: SCSKiller took it from {1}% to {2}% of pipelines ready.";
    const string ShareSmoothNoShare = "{0} was already smooth here: {1} → {2} shader compilation stutters in {3} of play with SCSKiller.";
    const string ShareDx11 = "{0} on DirectX 11: {1} → {2} stutters of 50 ms or more in {3} of play, before and after SCSKiller precompiled its shaders.";
    public const string Dx11Label = "DirectX 11: SCSKiller can't tell shader compilation stutters from other stutters";
    const string ShareLink = " scskiller.com";
    const int ShareMax = 280, LinkWeight = 23;   // X's limit; X counts any link as 23

    /// <summary>The game page's button with no pair: hidden only with the setting off, greyed with the reason as its note
    /// while <see cref="ScsKiller.BeforeAfterBlocked"/> says why a pair can't start.</summary>
    public static (bool Shown, bool Enabled, string Note) StartButton(bool on, string? blocked) => (on, on && blocked == null, blocked ?? "");

    /// <summary>Why the run doesn't count, one line for the button's note; null = it counts.</summary>
    public static string? RunProblem(SessionSnapshot? run, bool before)
    {
        var which = before ? "before" : "after";
        if (run == null || run.Frames == 0) return F(NoFrames, which);
        if (run.Creates == null) return F(NoCreates, which);
        if (run.PlaySeconds < MinPlay.TotalSeconds)
            return F(TooShort, which, run.PlaySeconds < 60 ? "under a minute" : $"{(int)(run.PlaySeconds / 60)} min",
                $"{(int)MinPlay.TotalMinutes} min");
        return null;
    }

    public static RunStats Stats(SessionSnapshot s)
    {
        var shader = s.Hitches.Where(h => h.Cause == HitchCause.Shader).Select(h => h.Ms).ToList();
        var play = s.Hitches.Where(h => h.Cause is HitchCause.Shader or HitchCause.Other).Select(h => h.Ms).ToList();
        return new RunStats(s.PlaySeconds, shader.Count, shader.Sum() / 1000, shader.DefaultIfEmpty(0).Max(),
            s.Creates?.ReadyShare, s.Creates?.PlayCompiles ?? 0, s.Low1PctFps, s.AvgFps, s.Hitches.Count(h => h.Cause == HitchCause.Other),
            play.Count, play.Sum() / 1000, play.DefaultIfEmpty(0).Max());
    }

    /// <summary>The before run is cold (a run that started with old driver-cache files is played again), unless
    /// <paramref name="notCold"/>, the line saying why: its clear didn't know the key of a cache SCSKiller can't watch (AMD's
    /// DirectX 11), or couldn't set aside a Vulkan game's. <paramref name="afterHelped"/>: that key was still unknown at the
    /// clear before the compile. A cold run's ready pipelines are the driver's own reuse within the launch (both vendors
    /// cache per shader stage, so a new pipeline of stages it already compiled is ready), not a cache it started with.</summary>
    public static Comparison Compare(SessionSnapshot before, SessionSnapshot after, string? notCold = null, bool afterHelped = false, bool max = false)
    {
        var (b, a) = (Stats(before), Stats(after));
        bool dx11 = before.D3D11 || after.D3D11;   // either run without create rows: no split claimed for the pair
        var notes = new List<string>();
        if ((after.Creates?.Library ?? 0) - (before.Creates?.Library ?? 0) is var more and > 0) notes.Add(F(LibraryLoads, more));
        if (before.SwapChain != null && after.SwapChain != null && before.SwapChain != after.SwapChain) notes.Add(F(OtherSettings, before.SwapChain, after.SwapChain));
        if (before.Overlay != after.Overlay) notes.Add(OverlayOnce);
        if (!before.CleanExit) notes.Add(Crashed);
        if (max) notes.Add(MaxCompiled);
        if (notCold != null) notes.Add(notCold);
        else if (afterHelped) notes.Add(MaybeHelped);
        if (before.FrameGen is { } fb && after.FrameGen is { } fa)   // a run whose module loaded but whose frames can't tell says nothing
            notes.Add(fb == fa ? F(FrameGenBoth, fb ? "on" : "off") : F(FrameGenOne, fb ? "before" : "after"));
        return new Comparison(b, a, Math.Abs(b.PlaySeconds - a.PlaySeconds) > 0.1 * Math.Min(b.PlaySeconds, a.PlaySeconds),
            !dx11 && b.ShaderStutters < SmoothStutters, notes, dx11);
    }

    /// <summary>Each run's play in whole minutes, as the card's footer and the share text name them.</summary>
    public static (int Before, int After) PlayMinutes(Comparison c) =>
        ((int)Math.Round(c.Before.PlaySeconds / 60, MidpointRounding.AwayFromZero), (int)Math.Round(c.After.PlaySeconds / 60, MidpointRounding.AwayFromZero));

    /// <summary>Within X's 280 with the link (<see cref="PostLength"/>); a long game name is cut to fit. The counts are the card's.</summary>
    public static string ShareText(string game, Comparison c)
    {
        var (b, a) = PlayMinutes(c);
        var span = b == a ? $"{b} minutes" : $"{b} and {a} minutes";
        var (before, after) = (c.Before.ReadyShare, c.After.ReadyShare);
        string Text(string name) =>
            (c.Dx11 ? F(ShareDx11, name, c.Before.Hitches, c.After.Hitches, span)
            : c.AlreadySmooth
                ? before != null && after != null ? F(ShareSmooth, name, Percent(before.Value), Percent(after.Value))
                    : F(ShareSmoothNoShare, name, c.Before.ShaderStutters, c.After.ShaderStutters, span)
                : F(Share, name, c.Before.ShaderStutters, c.After.ShaderStutters, span)
                    + (before != null && after != null ? F(ShareReady, Percent(after.Value), Percent(before.Value)) : ""))
            + ShareLink;
        var text = Text(game);
        for (int n = game.Length - 1; n >= 1 && PostLength(text) > ShareMax; n--) text = Text(game[..n].TrimEnd() + "…");
        return text;
    }

    /// <summary>The share text's length as X counts it (docs.x.com, counting characters): a link 23, a code point in Latin
    /// and general punctuation 1, any other 2 (arrows, the ellipsis, CJK, emoji). Anything shaped like a domain counts as a
    /// link, a game name's "GOG.com" too: X's own list of domains is longer, so this errs on the short side.</summary>
    public static int PostLength(string text) =>
        Domain().Replace(text, new string('x', LinkWeight)).EnumerateRunes()
            .Sum(r => r.Value is <= 4351 or >= 8192 and <= 8205 or >= 8208 and <= 8223 or >= 8242 and <= 8247 ? 1 : 2);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}\b")]
    private static partial System.Text.RegularExpressions.Regex Domain();

    /// <summary>A share in whole percent, 100 only when every pipeline was ready.</summary>
    static int Percent(double share) => share >= 1 ? 100 : Math.Min(99, (int)Math.Round(share * 100));

    static string F(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

    public const string Link = "https://scskiller.com";
}
