using System.Globalization;
using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>One language <see cref="Loc"/> has: its code, as its Strings file is named, and its name in itself (the Settings list).</summary>
public sealed record Language(string Code, string NativeName);

/// <summary>The app's texts (<c>App/Strings/*.json</c>, one flat key→text table per language). <see cref="Load"/> runs once at
/// start, for the setting's language or, null, the system's; every later <see cref="T"/> reads that table, and a key it lacks
/// falls to English, and a key English lacks is the key itself: a missing text is seen, not hidden. Until a load, and anywhere
/// that never loads one (the CLI, ScsKiller's own logs), it is English.</summary>
public static class Loc
{
    public static readonly IReadOnlyList<Language> Languages =
    [
        new("en", "English"),
        new("zh-Hans", "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("ru", "Русский"),
        new("pt-BR", "Português (Brasil)"),
    ];

    static readonly Dictionary<string, string> English = Read("en");
    static Dictionary<string, string> _table = English;

    /// <summary>The loaded language's code; "en" until a load.</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>Reads <paramref name="code"/>'s texts for every later <see cref="T"/>: the code SCSKiller has, or, null, the
    /// system UI culture's language (zh-Hant too reads Simplified Chinese: Chinese over English), or "en" when neither is had.
    /// Only the app calls it, before its first window.</summary>
    public static void Load(string? code)
    {
        var resolved = Resolve(code);
        if (resolved != "en") _table = Read(resolved);   // English is already in place
        Current = resolved;
    }

    /// <summary>The code <see cref="Load"/> would read: <paramref name="code"/> itself when SCSKiller has it.</summary>
    public static string Resolve(string? code)
    {
        if (code is { } set && Languages.Any(l => l.Code == set)) return set;
        var name = CultureInfo.CurrentUICulture.Name;   // "pt-BR", "zh-CN", "de-DE", "en-US", "" (invariant)
        if (Languages.Any(l => l.Code == name)) return name;
        var tag = name.Split('-')[0];   // the language alone: de-AT reads German, pt-PT reads pt-BR
        return Languages.FirstOrDefault(l => l.Code == tag || l.Code.StartsWith(tag + "-", StringComparison.Ordinal))?.Code ?? "en";
    }

    /// <summary>The text of <paramref name="key"/> in the loaded language.</summary>
    public static string T(string key) => _table.GetValueOrDefault(key, English.GetValueOrDefault(key, key));

    /// <summary>The text of <paramref name="key"/> in the loaded language, with <paramref name="args"/> in its {0} places.</summary>
    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    static Dictionary<string, string> Read(string code)
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream($"SCSKiller.Core.App.Strings.{code}.json")
            ?? throw new FileNotFoundException($"the embedded strings of {code}");
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }
}
