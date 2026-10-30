using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

/// <summary><paramref name="Group"/>: a "### " heading, the version a group of headings came from.</summary>
public sealed record WhatsNewLine(string Text, bool Heading = false, bool Group = false);

public sealed record WhatsNewSection(string Version, IReadOnlyList<WhatsNewLine> Lines);

public enum SpanKind { Plain, Bold, Code, Link }

public sealed record Span(string Text, SpanKind Kind = SpanKind.Plain, string? Url = null);

/// <summary>"What's new" after an update: the headings and bullets of the embedded CHANGELOG.md's "## [X.Y.Z]" section, the one
/// the release notes are taken from. A pre-release or internal build shows its X.Y.Z's
/// section, or nothing.</summary>
public static partial class WhatsNew
{
    public static string Changelog => field ??= ContentFile.Embedded("SCSKiller.Core.CHANGELOG.md");

    [GeneratedRegex(@"^## \[(\d+\.\d+\.\d+)\]")]
    private static partial Regex Released();

    [GeneratedRegex(@"^\*\*([^*]+)\*\*\s*$")]
    private static partial Regex Subheading();

    [GeneratedRegex(@"\*\*(?<b>[^*]+)\*\*|`(?<c>[^`]+)`|\[(?<t>[^\]]+)\]\((?<u>https://[^)\s]+)\)")]
    private static partial Regex Inline();

    /// <summary>The Markdown a CHANGELOG bullet uses: **bold**, `code` and [text](https-url); anything else, such as a
    /// lone asterisk, stays as written.</summary>
    public static IReadOnlyList<Span> Spans(string text)
    {
        var spans = new List<Span>();
        var at = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Groups["u"].Success && !(Uri.TryCreate(m.Groups["u"].Value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)) continue;   // stays plain text: a bad link must not throw in the dialog
            if (m.Index > at) spans.Add(new(text[at..m.Index]));
            spans.Add(m.Groups["b"].Success ? new(m.Groups["b"].Value, SpanKind.Bold)
                : m.Groups["c"].Success ? new(m.Groups["c"].Value, SpanKind.Code)
                : new(m.Groups["t"].Value, SpanKind.Link, m.Groups["u"].Value));
            at = m.Index + m.Length;
        }
        if (at < text.Length) spans.Add(new(text[at..]));
        return spans;
    }

    static string Key(AppVersion v) => $"{v.Major}.{v.Minor}.{v.Patch}";

    public static WhatsNewSection? Section(string changelog, string version)
    {
        var lines = new List<WhatsNewLine>();
        var inside = false;
        foreach (var raw in changelog.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ")) inside = line.Contains($"[{version}]");
            else if (!inside) continue;
            else if (line.StartsWith("### ")) lines.Add(new(line[4..].Trim(), Heading: true, Group: true));
            else if (Subheading().Match(line) is { Success: true } m) lines.Add(new(m.Groups[1].Value.Trim(), Heading: true));
            else if (line.StartsWith("- ")) lines.Add(new(line[2..].Trim()));
            else if (line.StartsWith("  ") && line.Trim().Length > 0 && lines.Count > 0 && !lines[^1].Heading) lines[^1] = new(lines[^1].Text + " " + line.Trim());
        }
        return lines.Any(l => !l.Heading) ? new(version, lines.Select(l => l.Heading ? l : new(FirstSentence(l.Text))).ToList()) : null;
    }

    /// <summary>A bullet's first sentence: the dialog stays short, the release notes have the rest.</summary>
    static string FirstSentence(string s) => s.IndexOf(". ", StringComparison.Ordinal) is var end and > 0 ? s[..(end + 1)] : s;

    /// <summary>About's: this build's section, else the newest released one (a dev build).</summary>
    public static WhatsNewSection? ForAbout(AppVersion v) =>
        Section(Changelog, Key(v)) ?? (Changelog.Split('\n').Select(l => Released().Match(l)).FirstOrDefault(m => m.Success) is { } m ? Section(Changelog, m.Groups[1].Value) : null);

    /// <summary>The section to show at this start: once per X.Y.Z, recorded before it shows; on a fresh install's first
    /// launch (<paramref name="firstRun"/>, the welcome's) only recorded. A build without its section records nothing, so
    /// a later build of the same X.Y.Z that has one still shows it.</summary>
    public static WhatsNewSection? AtStart(AppStore store, bool firstRun, AppVersion v, string? changelog = null)
    {
        var seen = store.LoadNewsSeen();
        if (seen.WhatsNew == Key(v)) return null;
        var section = firstRun ? null : Section(changelog ?? Changelog, Key(v));
        if (firstRun || section != null) store.SaveNewsSeen(seen with { WhatsNew = Key(v) });
        return section;
    }
}
