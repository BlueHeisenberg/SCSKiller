using System.Text;

namespace SCSKiller.Core.Games;

/// <summary>A Steam game's launch options as Proton sees them: Steam runs them as a shell command line with the game's
/// command in place of %command%, so the NAME=value words leading it are the game's environment.</summary>
static class SteamLaunchOptions
{
    /// <summary>The variables the options set in the game's environment, each with the value it ends with, in the order
    /// last set; none when the options hold no %command% (Steam then adds them to the game's arguments). Words are
    /// unquoted as the shell does (nothing expanded) and end at the first %command%. First the shell's assignments (name
    /// and = unquoted), then, when env is the first other word, env's options as GNU env reads them, up to its first
    /// other word or --: -i, --ignore-environment and a lone - after them drop what env inherits, and -u NAME,
    /// --unset=NAME or --unset NAME drops NAME. Then env's own assignments, every word holding an = (after quote
    /// removal). The first other word, a wrapper such as gamemoderun whose arguments the rest are, ends them.
    /// <paramref name="unread"/>: an env option SCSKiller doesn't read, which leaves the variables unknown.</summary>
    internal static List<(string Name, string Value)> Variables(string? options, out string? unread) =>
        [.. Variables(options, out unread, out _, out _).Select(v => (v.Name, v.Value))];

    // each with the index of the word that set it in what comes before %command%: those words, as Words gives them with plain
    static List<(string Name, string Value, int At)> Variables(string? options, out string? unread, out bool plain, out List<(string Word, int QuotedFrom)> words)
    {
        (unread, plain, words) = (null, true, []);
        var at = options?.IndexOf("%command%", StringComparison.Ordinal) ?? -1;
        if (at < 0) return [];
        var vars = new List<(string Name, string Value, int At)>();
        var ws = words = Words(options![..at], out plain);
        var i = 0;
        for (; i < ws.Count && ws[i].Word.IndexOf('=') is var eq and > 0 && eq < ws[i].QuotedFrom && IsName(ws[i].Word.AsSpan(0, eq)); i++)
            Set(ws[i].Word, eq);
        if (i == ws.Count || ws[i].Word is not ("env" or "/usr/bin/env" or "/bin/env")) return vars;
        for (i++; i < ws.Count && ws[i].Word is ['-', _, ..] option; i++)
        {
            if (option == "--")
            {
                i++;
                break;
            }
            if (!Option(option))
            {
                unread = option;
                return vars;
            }
        }
        if (i < ws.Count && ws[i].Word == "-")
        {
            vars.Clear();
            i++;
        }
        // env takes every word holding an = for an assignment, an empty name too
        for (; i < ws.Count && ws[i].Word.IndexOf('=') is var eq and >= 0; i++)
            if (eq > 0) Set(ws[i].Word, eq);
        return vars;

        void Set(string word, int eq)
        {
            vars.RemoveAll(v => v.Name == word[..eq]);
            vars.Add((word[..eq], word[(eq + 1)..], i));
        }

        // a short option's argument is the rest of its word, else the next word
        bool Option(string option)
        {
            if (option == "--ignore-environment") vars.Clear();
            else if (option.StartsWith("--unset=", StringComparison.Ordinal)) return Unset(option["--unset=".Length..]);
            else if (option == "--unset") return i + 1 < ws.Count && Unset(ws[++i].Word);
            else if (option[1] == '-') return false;
            else
                for (var c = 1; c < option.Length; c++)
                    if (option[c] == 'i') vars.Clear();
                    else if (option[c] == 'u') return c + 1 < option.Length ? Unset(option[(c + 1)..]) : i + 1 < ws.Count && Unset(ws[++i].Word);
                    else return false;
            return true;
        }

        // env refuses an empty name or one holding an =
        bool Unset(string name)
        {
            if (name.Length == 0 || name.Contains('=')) return false;
            vars.RemoveAll(v => v.Name == name);
            return true;
        }
    }

    /// <summary>The launch options that set <paramref name="vars"/> in the game's environment: each NAME=value, its value
    /// quoted where the shell would read it otherwise, then %command%.</summary>
    internal static string Line(IEnumerable<(string Name, string Value)> vars) => string.Join(' ', vars.Select(v => $"{v.Name}={Quoted(v.Value)}")) + " %command%";

    /// <summary>The options set each of <paramref name="vars"/> to its value (<see cref="Variables"/>): false too when an env
    /// option SCSKiller doesn't read leaves them unknown, when what comes before %command% is more than words SCSKiller
    /// reads as the shell does (<see cref="Words"/>), such as another command, a comment or an expansion, or when an env
    /// other than the word right before %command% comes after the last of them: its options, or another env's, such as -i,
    /// -u or -S, can clear them before the game starts. A shell of <see cref="Shells"/> or a wrapper of <see cref="Wrappers"/>
    /// anywhere after them, by its file name, can too.</summary>
    internal static bool Hold(string? options, IReadOnlyList<(string Name, string Value)> vars)
    {
        var set = Variables(options, out var unread, out var plain, out var words);
        if (unread != null || !plain || !vars.All(v => set.Any(s => (s.Name, s.Value) == v))) return false;
        var last = set.Where(s => vars.Contains((s.Name, s.Value))).Select(s => s.At).DefaultIfEmpty(-1).Max();
        return !words.Skip(last + 1).SkipLast(1).Any(w => w.Word == "env" || w.Word.EndsWith("/env", StringComparison.Ordinal))
            && !words.Skip(last + 1).Any(w => Path.GetFileName(w.Word) is var name && (Shells.Contains(name) || Wrappers.Contains(name)));
    }

    static readonly string[] Shells = ["sh", "bash", "dash", "zsh", "ksh", "fish"];

    // each can start the game with an environment of its own making
    static readonly string[] Wrappers = ["sudo", "su", "pkexec", "doas", "firejail", "bwrap", "systemd-run", "flatpak-spawn", "steam-run"];

    // in single quotes the shell takes every character as it is but a single quote, which ends them and goes in escaped
    static string Quoted(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or ',' or '+' or '@' or '=')
            ? value : "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Why the options run the game on wined3d, without vkd3d-proton or DXVK, as the Proton's script reads them; null when
    /// they don't. Only a variable the script <paramref name="reads"/> counts. PROTON_USE_WINED3D (else PROTON_USE_WINED3D11)
    /// set to anything but empty or 0 does, and set to one of those undoes the rest. wined3d or wined3d11 in
    /// STEAM_COMPAT_CONFIG or GE-Proton's PROTON_ADD_CONFIG does. WINEDLLOVERRIDES can't: Proton adds d3d12=n and
    /// d3d12core=n after it, and Wine takes a dll's last entry. An env option SCSKiller doesn't read refuses it too: the
    /// variables it leaves are unknown.</summary>
    internal static string? OffVkd3d(string? options, Func<string, bool> reads)
    {
        var vars = Variables(options, out var unread);
        if (unread != null) return $"its Steam launch options use env options SCSKiller doesn't read ({unread})";
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in vars)
            if (reads(name)) env[name] = value;
        string? by = null;
        foreach (var name in new[] { "STEAM_COMPAT_CONFIG", "PROTON_ADD_CONFIG" })
            if (by == null && env.TryGetValue(name, out var list) && Config(name, list).Overlaps(["wined3d", "wined3d11"])) by = $"{name}={list}";
        foreach (var name in new[] { "PROTON_USE_WINED3D", "PROTON_USE_WINED3D11" })
            if (env.TryGetValue(name, out var value))
            {
                by = value is "" or "0" ? null : $"{name}={value}";
                break;
            }
        return by == null ? null : $"its Steam launch options run it on wined3d, not vkd3d-proton or DXVK ({by})";
    }

    /// <summary>The DLLs (lower case, no extension) that WINEDLLOVERRIDES as the options set it has Wine load its own of
    /// first, or none of: its ';'-separated entries, each names (',' or ' ' between them) = a load order whose first letter
    /// decides, b builtin and none off. A name's last entry counts, whatever its case, a leading * or a .dll (measured).
    /// Proton's own entries come after it, winmm's for one game alone and native first. Empty when the variables are unknown
    /// (an env option SCSKiller doesn't read).</summary>
    internal static IReadOnlySet<string> BuiltinFirst(string? options)
    {
        var dlls = new HashSet<string>(StringComparer.Ordinal);
        var value = Variables(options, out var unread).LastOrDefault(v => v.Name == "WINEDLLOVERRIDES").Value;
        if (unread != null || value == null) return dlls;
        foreach (var entry in value.Split(';'))
        {
            var eq = entry.IndexOf('=');
            if (eq < 0) continue;
            var order = entry[(eq + 1)..].Trim();
            var builtin = order.Length == 0 || char.ToLowerInvariant(order[0]) == 'b';
            foreach (var word in entry[..eq].Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = word.TrimStart('*').ToLowerInvariant();
                if (name.EndsWith(".dll", StringComparison.Ordinal)) name = name[..^4];
                if (builtin) dlls.Add(name);
                else dlls.Remove(name);
            }
        }
        return dlls;
    }

    /// <summary>The compatibility config names a list sets: STEAM_COMPAT_CONFIG's comma-separated entries (a cmdlineappend:
    /// entry runs on past each backslash-escaped comma), PROTON_ADD_CONFIG's without what follows an =.</summary>
    static HashSet<string> Config(string variable, string list)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (variable == "PROTON_ADD_CONFIG")
        {
            foreach (var entry in list.Split(','))
                if (entry is not ("" or "=")) names.Add(entry.Split('=', 2)[0]);
            return names;
        }
        var parts = list.Split(',');
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i];
            if (entry.StartsWith("cmdlineappend:", StringComparison.Ordinal))
                while (Escaped(entry) && i + 1 < parts.Length) entry = entry[..^1] + "," + parts[++i];
            else if (entry.Length > 0) names.Add(entry);
        }
        return names;

        static bool Escaped(string s)
        {
            var n = 0;
            while (n < s.Length && s[^(n + 1)] == '\\') n++;
            return n % 2 == 1;
        }
    }

    static bool IsName(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || !(char.IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary>The shell's words of <paramref name="line"/>, each with the index of its first quoted or escaped character
    /// (int.MaxValue when none): an assignment's name and = are unquoted. Whitespace and an unquoted ; &amp; | &lt; &gt; ( )
    /// end a word, and an unclosed quote runs to the end. <paramref name="plain"/>: the line is words alone, as the shell
    /// takes them, with none of these: an unquoted ; &amp; | &lt; &gt; ( ) # or whitespace other than a space or tab, a $ or `
    /// outside single quotes, an unquoted ~ * ? [ {, an unclosed quote, or a backslash ending it.</summary>
    static List<(string Word, int QuotedFrom)> Words(string line, out bool plain)
    {
        plain = true;
        var words = new List<(string, int)>();
        var w = new StringBuilder();
        var (any, quotedFrom) = (false, int.MaxValue);
        void Quoted()
        {
            any = true;
            if (quotedFrom == int.MaxValue) quotedFrom = w.Length;
        }
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'')
            {
                Quoted();
                var end = line.IndexOf('\'', i + 1);
                if (end < 0) (end, plain) = (line.Length, false);
                w.Append(line, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                Quoted();
                for (i++; i < line.Length && line[i] != '"'; i++)
                    if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] is '$' or '`' or '"' or '\\' or '\n')
                    {
                        if (line[++i] != '\n') w.Append(line[i]);
                    }
                    else
                    {
                        if (line[i] is '$' or '`') plain = false;
                        w.Append(line[i]);
                    }
                if (i == line.Length) plain = false;
            }
            else if (c == '\\')
            {
                Quoted();
                if (++i == line.Length) plain = false;
                else if (line[i] != '\n') w.Append(line[i]);
            }
            else if (char.IsWhiteSpace(c) || c is ';' or '&' or '|' or '<' or '>' or '(' or ')')
            {
                if (c is not (' ' or '\t')) plain = false;
                if (any) words.Add((w.ToString(), quotedFrom));
                (any, quotedFrom) = (false, int.MaxValue);
                w.Clear();
            }
            else
            {
                if (c is '$' or '`' or '#' or '~' or '*' or '?' or '[' or '{') plain = false;
                any = true;
                w.Append(c);
            }
        }
        if (any) words.Add((w.ToString(), quotedFrom));
        return words;
    }
}
