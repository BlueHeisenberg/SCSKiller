using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>A Proton's own script, read as text and never run: its proton file and the modules beside it that file
/// imports (GE-Proton's utilities.py).</summary>
sealed partial class ProtonScript
{
    readonly string _proton, _text, _all;

    ProtonScript(string proton, string text, string all) => (_proton, _text, _all) = (proton, text, all);

    /// <summary>The script of the Proton in <paramref name="proton"/>; null when its proton file can't be read.</summary>
    public static ProtonScript? Read(string proton)
    {
        try
        {
            if (SteamSource.Small(Path.Combine(proton, "proton")) is not { } text) return null;
            var all = new StringBuilder(text);
            foreach (var module in Imports().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
                if (File.Exists(Path.Combine(proton, module + ".py")) && SteamSource.Small(Path.Combine(proton, module + ".py")) is { } more)
                    all.Append('\n').Append(more);
            return new(proton, text, all.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The script reads the environment variable: it names it in quotes. Valve's Proton names PROTON_USE_WINED3D but
    /// not PROTON_ADD_CONFIG, GE-Proton both.</summary>
    public bool Reads(string variable) =>
        _all.Contains($"\"{variable}\"", StringComparison.Ordinal) || _all.Contains($"'{variable}'", StringComparison.Ordinal);

    /// <summary>Why verb run would set up again the prefix of <paramref name="compatData"/>, or the Proton's own files, before
    /// Steam's next launch of the game does: what the script's main and setup_prefix and Wine's wineboot compare before they
    /// change anything. Null when nothing would change. <paramref name="version"/>: the script's CURRENT_PREFIX_VERSION.
    /// <paramref name="steam"/>: Steam's folder as a compile's run names it (STEAM_COMPAT_CLIENT_INSTALL_PATH), set to
    /// config_info's name for it where that names the same folder otherwise. A compile's run computes Proton's defaults for
    /// the settings config_info records: its launcher drops the variables that change them.</summary>
    public string? Refreshes(string compatData, string version, ref string steam)
    {
        try { return Refresh(compatData, version, ref steam); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"its prefix's files couldn't be read: {e.Message}"; }
    }

    string? Refresh(string compatData, string version, ref string steam)
    {
        var pfx = Path.Combine(compatData, "pfx");
        if (Names("cleanup_legacy_dist") && Directory.Exists(Path.Combine(_proton, "dist")))
            return $"its Proton in {_proton} was updated and hasn't run since (its old dist folder is still there): start it once from Steam";
        var fixups = Path.Combine(_proton, "steampipe_fixups.json");
        if (Names("do_steampipe_fixups") && Resolves(fixups) && FirstLine(Path.Combine(_proton, "files", "steampipe_fixups_mtime")) != MtimeString(fixups))
            return $"its Proton in {_proton} was updated and hasn't run since (its steampipe_fixups.json isn't the one its last run restored): start it once from Steam";
        if (Names("missing_default_prefix"))
        {
            if (ProtonPath("default_pfx_dir") is not { } defaultPfx) return Unread("self.default_pfx_dir");
            if (!Directory.Exists(defaultPfx)) return $"its Proton in {_proton} has no default prefix yet: start it once from Steam";
        }

        var ran = FirstLine(Path.Combine(compatData, "version"));
        if (ran != version)
            return $"its prefix last ran on {ran ?? "an unknown Proton"} and its Proton is now {version}: start it once from Steam to move the prefix to {version}";
        if (Names("creation_sync_guard"))
        {
            if (Attribute("creation_sync_guard") is not { } guard) return Unread("self.creation_sync_guard");
            if (!LinkExists(Path.Combine(compatData, guard))) return "its prefix has no creation_sync_guard yet: start it once from Steam";
        }
        else if (!Resolves(Path.Combine(pfx, "user.reg"))) return "its prefix has no user.reg yet: start it once from Steam";
        if (Migration() is not { } moves) return Unread("migrate_user_paths");
        foreach (var (old, moved, link) in moves)
            if (LinkTarget(Path.Combine(pfx, moved)) is { } loop && loop.EndsWith(old, StringComparison.Ordinal) || LinkTarget(Path.Combine(pfx, old)) != link)
                return $"its prefix's {old} isn't yet the link its Proton makes there: start it once from Steam";
        foreach (Match m in Drives().Matches(_text))
            if (!LinkExists(Path.Combine(pfx, "dosdevices", m.Groups[1].Value)))
                return $"its prefix has no {m.Groups[1].Value} drive yet: start it once from Steam";

        if (ConfigInfoChange(compatData, version, ref steam) is { } change) return change;

        // wineboot's update_wineprefix: a .update-timestamp other than wine.inf's time, or none, updates the prefix
        var inf = Path.Combine(_proton, "files", "share", "wine", "wine.inf");
        if (File.Exists(inf) && Libc.Modified(inf) is (var seconds, _) && WineTimestamp(Path.Combine(pfx, ".update-timestamp")) is var stamp
            && stamp != null && stamp != unchecked((ulong)seconds))
            return "its Proton's Wine would update its prefix (its .update-timestamp isn't its wine.inf's time): start it once from Steam";
        return null;
    }

    /// <summary>Why setup_prefix's prefix_info, as a compile's run computes it, isn't config_info's text; null when it is.</summary>
    string? ConfigInfoChange(string compatData, string version, ref string steam)
    {
        if (PrefixInfo(version, steam, out var unknown) is not { } lines) return Unread(unknown!);
        var path = Path.Combine(compatData, "config_info");
        var had = (File.Exists(path) ? SteamSource.Small(path) : null)?.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n') ?? [""];
        for (var i = 0; i < Math.Max(had.Length, lines.Count); i++)
        {
            if (i >= had.Length || i >= lines.Count) return "its prefix's config_info isn't what its Proton writes: start it once from Steam";
            var (source, value, kind) = lines[i];
            if (had[i] == value || had[i] == "/run/host" + value) continue;
            if (kind == Kind.Steam && Libc.RealPath(had[i]) is { } real && real == Libc.RealPath(value))
            {
                steam = had[i];
                continue;
            }
            return kind == Kind.Setting
                ? value.Length > 40 || had[i].Length > 40
                    ? $"its prefix was last set up with another {source} than a compile's run has, which would set it up again"
                    : $"its prefix was last set up with {source} {had[i]} and a compile's run has {value}, which would set it up again"
                : $"its prefix was last set up with {source} {had[i]} and its Proton now has {value}: start it once from Steam";
        }
        return null;
    }

    enum Kind { File, Steam, Setting }

    /// <summary>setup_prefix's prefix_info lines as a compile's run computes them, each with the script's text for it; null,
    /// with that text in <paramref name="unknown"/>, for a line computed in a way this doesn't know.</summary>
    List<(string Source, string Value, Kind Kind)>? PrefixInfo(string version, string steam, out string? unknown)
    {
        unknown = "prefix_info";
        if (PrefixInfoAt().Match(_text) is not { Success: true } at) return null;
        var lines = new List<(string, string, Kind)>();
        var element = new List<Token>();
        var depth = 0;
        foreach (var t in Tokens(_text, at.Index + at.Length))
        {
            if (t.Kind == TokenKind.Line) continue;
            if (t.Kind == TokenKind.Op && depth == 0 && t.Text is "," or ")")
            {
                if (element.Count > 0)
                {
                    var source = _text[element[0].Start..element[^1].End];
                    if (Value(element, version, steam) is not { } line)
                    {
                        unknown = source;
                        return null;
                    }
                    lines.Add((source, line.Value, line.Kind));
                    element.Clear();
                }
                if (t.Text == ")") return lines;
                continue;
            }
            depth += t.Text switch { "(" or "[" or "{" => 1, ")" or "]" or "}" => -1, _ => 0 };
            element.Add(t);
        }
        return null;
    }

    /// <summary>A prefix_info element's value for a compile's run.</summary>
    (string Value, Kind Kind)? Value(List<Token> e, string version, string steam)
    {
        if (e is [{ Text: "CURRENT_PREFIX_VERSION", Kind: TokenKind.Name }]) return (version, Kind.File);
        if (e is [{ Text: "g_proton" }, { Text: "." }, { Kind: TokenKind.Name } attribute])
            return ProtonPath(attribute.Text) is { } path ? (path, Kind.File) : null;
        if (e is [{ Text: "getmtimestr" }, { Text: "(" }, .., { Text: ")" }])
        {
            var parts = new List<string>();
            foreach (var arg in Split(e[2..^1]))
                if (arg is [{ Kind: TokenKind.Str } s]) parts.Add(s.Text);
                else if (Value(arg, version, steam) is ({ } p, Kind.File or Kind.Steam)) parts.Add(p);
                else return null;
            return parts.Count == 0 ? null : (MtimeString(Join(parts)), Kind.File);
        }
        if (e is [{ Text: "str" }, { Text: "(" }, { Kind: TokenKind.Name } name, { Text: ")" }])
            return Assignment(name.Text) is { } rhs && Settings.TryGetValue(Canonical(rhs), out var setting) && setting() is { } value ? (value, Kind.Setting) : null;
        if (e is [{ Kind: TokenKind.Name } variable] && Assignment(variable.Text) is { } assigned)
        {
            if (assigned is [{ Text: "os" }, { Text: "." }, { Text: "environ" }, { Text: "[" }, { Kind: TokenKind.Str, Text: "STEAM_COMPAT_CLIENT_INSTALL_PATH" }, { Text: "]" }])
                return (steam, Kind.Steam);
            // the default alone: a compile's launcher drops PROTON_DLL_COPY
            if (assigned is [{ Text: "os" }, { Text: "." }, { Text: "environ" }, { Text: "." }, { Text: "get" }, { Text: "(" }, { Kind: TokenKind.Str, Text: "PROTON_DLL_COPY" }, { Text: "," }, .. var rest, { Text: ")" }]
                && Concatenation(rest) is { } copy)
                return (copy, Kind.Setting);
        }
        return null;
    }

    /// <summary>The settings config_info records, by their assignment's tokens, as a compile's run computes them: no wined3d,
    /// no dxgi=b, no disablenvapi or forcenvapi, no dxvkd3d8 in its compatibility config or environment.</summary>
    static readonly Dictionary<string, Func<string?>> Settings = new(StringComparer.Ordinal)
    {
        ["\"wined3d\" in g_session . compat_config"] = () => "False",
        ["not use_wined3d and not ( \"WINEDLLOVERRIDES\" in g_session . env and \"dxgi=b\" in g_session . env [ \"WINEDLLOVERRIDES\" ] )"] = () => "True",
        ["\"disablenvapi\" not in g_session . compat_config or \"forcenvapi\" in g_session . compat_config"] = () => "True",
        ["( ( \"disablenvapi\" not in g_session . compat_config or \"forcenvapi\" in g_session . compat_config ) and g_proton . host_pe_arch != \"aarch64-windows\" )"] =
            () => RuntimeInformation.OSArchitecture == Architecture.X64 ? "True" : null,
        ["\"dxvkd3d8\" in g_session . compat_config"] = () => "False",
    };

    static string Canonical(List<Token> tokens) =>
        string.Join(' ', tokens.Select(t => t.Kind == TokenKind.Str ? $"\"{t.Text}\"" : t.Text));

    /// <summary>The value of string literals joined by +; null for anything else.</summary>
    static string? Concatenation(List<Token> tokens)
    {
        var s = new StringBuilder();
        for (var i = 0; i < tokens.Count; i += 2)
        {
            if (tokens[i].Kind != TokenKind.Str || i + 1 < tokens.Count && tokens[i + 1].Text != "+") return null;
            s.Append(tokens[i].Text);
        }
        return tokens.Count % 2 == 1 ? s.ToString() : null;
    }

    static IEnumerable<List<Token>> Split(List<Token> tokens)
    {
        var part = new List<Token>();
        var depth = 0;
        foreach (var t in tokens)
        {
            if (depth == 0 && t.Text == "," && t.Kind == TokenKind.Op)
            {
                yield return part;
                part = [];
                continue;
            }
            depth += t.Text switch { "(" or "[" or "{" => 1, ")" or "]" or "}" => -1, _ => 0 };
            part.Add(t);
        }
        if (part.Count > 0) yield return part;
    }

    /// <summary>os.path.join's path.</summary>
    static string Join(List<string> parts)
    {
        var path = "";
        foreach (var p in parts)
            path = p.StartsWith('/') ? p : path.Length == 0 || path.EndsWith('/') ? path + p : path + "/" + p;
        return path;
    }

    /// <summary>The tokens of the last assignment to <paramref name="name"/> before prefix_info, to its line's end.</summary>
    List<Token>? Assignment(string name)
    {
        var end = PrefixInfoAt().Match(_text) is { Success: true } at ? at.Index : _text.Length;
        var last = Regex.Matches(_text[..end], $@"^[ \t]*{Regex.Escape(name)}[ \t]*=(?!=)", RegexOptions.Multiline).LastOrDefault();
        return last == null ? null : [.. Tokens(_text, last.Index + last.Length).TakeWhile(t => t.Kind != TokenKind.Line)];
    }

    /// <summary>The Proton's file a Proton attribute names (self.&lt;name&gt; = self.path("...")), as the script's base_dir
    /// spells it: its folder as run, then /.</summary>
    string? ProtonPath(string name) => Attribute(name) is { } rel ? _proton + "/" + rel : null;

    /// <summary>The path an attribute assignment self.&lt;name&gt; = self.path("...") gives, relative to its object's folder.</summary>
    string? Attribute(string name) =>
        Regex.Match(_text, $@"self\.{Regex.Escape(name)}[ \t]*=[ \t]*self\.path\([ \t]*([""'])([^""'\n]*)\1[ \t]*\)") is { Success: true } m ? m.Groups[2].Value : null;

    /// <summary>migrate_user_paths's moves: the winxp-style path, the vista one and the link that replaces the first.</summary>
    List<(string Old, string New, string Link)>? Migration()
    {
        var at = _text.IndexOf("def migrate_user_paths(", StringComparison.Ordinal);
        if (at < 0) return [];
        var next = NextDef().Match(_text, at + 1);
        var body = _text[at..(next.Success ? next.Index : _text.Length)];
        var moves = Moves().Matches(body).Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)).ToList();
        return moves.Count > 0 ? moves : null;
    }

    bool Names(string name) => _text.Contains(name, StringComparison.Ordinal);

    string Unread(string what) =>
        $"SCSKiller can't tell whether its Proton in {_proton} would set its prefix up again: its script computes {what} in a way SCSKiller doesn't know";

    /// <summary>Python's readline().strip() of the file; null when there is none.</summary>
    static string? FirstLine(string path) =>
        File.Exists(path) && SteamSource.Small(path) is { } text ? text.Split('\n')[0].Trim() : null;

    static bool LinkExists(string path) => Path.Exists(path) || LinkTarget(path) != null;

    /// <summary>os.path.exists: the path leads somewhere, its links followed.</summary>
    static bool Resolves(string path) => Libc.Modified(path) != null;

    static string? LinkTarget(string path) => new FileInfo(path).LinkTarget;

    /// <summary>Proton's getmtimestr: str(os.path.getmtime()), a Python float's repr; "0" when the file can't be read.</summary>
    internal static string MtimeString(string path)
    {
        if (Libc.Modified(path) is not (var seconds, var nanoseconds)) return "0";
        var s = (seconds + nanoseconds * 1e-9).ToString(CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }

    /// <summary>The time a .update-timestamp holds as wineboot reads it (strtoul), null where it updates nothing ("disable", or a
    /// file it can't open), 0 where the file is missing or a link to nothing: no time matches it.</summary>
    internal static ulong? WineTimestamp(string path)
    {
        byte[] head;
        try
        {
            using var f = File.OpenRead(path);
            head = new byte[99];
            head = head[..f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (head.AsSpan().StartsWith("disable"u8)) return null;
        var i = 0;
        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r') i++;
        var negative = i < head.Length && head[i] == '-';
        if (i < head.Length && head[i] is (byte)'+' or (byte)'-') i++;
        ulong n = 0;
        for (; i < head.Length && head[i] is >= (byte)'0' and <= (byte)'9'; i++)
            n = n > (ulong.MaxValue - 9) / 10 ? ulong.MaxValue : n * 10 + (ulong)(head[i] - '0');
        return negative ? unchecked(0 - n) : n;
    }

    enum TokenKind { Name, Str, Num, Op, Line }

    /// <summary>A Python token: a string's <see cref="Text"/> is its value, any other's its source text.</summary>
    readonly record struct Token(TokenKind Kind, string Text, int Start, int End);

    /// <summary>Python's tokens from <paramref name="at"/>, comments and continued lines skipped: a Line token ends each
    /// line outside brackets.</summary>
    static IEnumerable<Token> Tokens(string s, int at)
    {
        var depth = 0;
        var i = at;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t' or '\r' or '\f') i++;
            else if (c == '\\' && i + 1 < s.Length && s[i + 1] is '\n' or '\r') i = s.IndexOf('\n', i) is var n and >= 0 ? n + 1 : s.Length;
            else if (c == '\n')
            {
                if (depth == 0) yield return new(TokenKind.Line, "\n", i, i + 1);
                i++;
            }
            else if (c == '#') i = s.IndexOf('\n', i) is var n and >= 0 ? n : s.Length;
            else if (c is '"' or '\'')
            {
                var start = i;
                var triple = i + 2 < s.Length && s[i + 1] == c && s[i + 2] == c;
                var quote = triple ? new string(c, 3) : c.ToString();
                i += quote.Length;
                var value = new StringBuilder();
                while (i < s.Length && string.CompareOrdinal(s, i, quote, 0, quote.Length) != 0)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        value.Append(s[i + 1] switch { 'n' => "\n", 't' => "\t", '\\' => "\\", '\'' => "'", '"' => "\"", '\n' => "", var x => $"\\{x}" });
                        i += 2;
                    }
                    else if (s[i] == '\n' && !triple) break;
                    else value.Append(s[i++]);
                }
                i = Math.Min(s.Length, i + quote.Length);
                yield return new(TokenKind.Str, value.ToString(), start, i);
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
                yield return new(TokenKind.Name, s[start..i], start, i);
            }
            else if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '.')) i++;
                yield return new(TokenKind.Num, s[start..i], start, i);
            }
            else
            {
                var two = i + 1 < s.Length && s.AsSpan(i, 2) is "!=" or "==" or "<=" or ">=";
                var op = s.Substring(i, two ? 2 : 1);
                depth = Math.Max(0, depth + op switch { "(" or "[" or "{" => 1, ")" or "]" or "}" => -1, _ => 0 });
                yield return new(TokenKind.Op, op, i, i + op.Length);
                i += op.Length;
            }
        }
    }

    [GeneratedRegex(@"^[ \t]*(?:import|from)[ \t]+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex Imports();

    [GeneratedRegex(@"\bprefix_info[ \t]*=[ \t]*(['""])\\n\1[ \t]*\.[ \t]*join[ \t]*\([ \t\r\n]*\(")]
    private static partial Regex PrefixInfoAt();

    [GeneratedRegex(@"file_exists\(self\.prefix_dir \+ ""/dosdevices/([a-z]:)"", follow_symlinks=False\)")]
    private static partial Regex Drives();

    [GeneratedRegex(@"\(\s*""([^""]+)""\s*,\s*self\.prefix_dir\s*\+\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\)")]
    private static partial Regex Moves();

    [GeneratedRegex(@"\n[ \t]{0,4}def |\nclass ")]
    private static partial Regex NextDef();
}
