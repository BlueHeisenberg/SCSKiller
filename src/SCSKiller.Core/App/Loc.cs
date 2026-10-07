using System.Globalization;
using System.Resources;

namespace SCSKiller.Core.App;

/// <summary>Native .NET resources for display text. Core state, protocol strings and logs stay invariant.</summary>
public static class Loc
{
    static readonly ResourceManager Resources = new("SCSKiller.Core.App.Strings", typeof(Loc).Assembly);

    public static IReadOnlyList<CultureInfo> Languages { get; } =
        [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("zh-Hans")];

    public static string Text(string source) => Resources.GetString(source, CultureInfo.CurrentUICulture) ?? source;

    /// <summary>The same English that needs another translation in <paramref name="context"/> (resource "context|source").</summary>
    public static string Text(string source, string context) => Resources.GetString(context + "|" + source, CultureInfo.CurrentUICulture) ?? source;

    public static string Format(FormattableString source) =>
        string.Format(CultureInfo.CurrentCulture, Text(source.Format), source.GetArguments());

    /// <summary><see cref="Format(FormattableString)"/> for a template that needs <see cref="Text(string, string)"/>'s context.</summary>
    public static string Format(FormattableString source, string context) =>
        string.Format(CultureInfo.CurrentCulture, Text(source.Format, context), source.GetArguments());

    public static string NormalizeLanguage(string? language) =>
        Languages.FirstOrDefault(c => c.Name.Equals(language?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name ?? "";

    /// <summary>The language to show: <paramref name="setting"/> when it names a supported one, else the first of
    /// <paramref name="preferred"/> (Windows' preferred languages, in order) that has one (zh-CN and zh-Hans-CN: zh-Hans;
    /// en-GB: en-US; zh-TW: none, it is another script), else English. Never the invariant culture: an empty, blank or
    /// unknown tag (setting or preferred) is skipped, so callers always get a non-empty language tag.</summary>
    public static CultureInfo Resolve(string? setting, IEnumerable<string?>? preferred)
    {
        if (NormalizeLanguage(setting) is { Length: > 0 } chosen) return CultureInfo.GetCultureInfo(chosen);
        foreach (var tag in preferred ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            CultureInfo c;
            try { c = CultureInfo.GetCultureInfo(tag.Trim()); }
            catch (CultureNotFoundException) { continue; }
            for (; c.Name.Length > 0; c = c.Parent)
            {
                var name = c.Name;
                if (Languages.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
                // a bare language (en) takes its supported regional variant (en-US), never one in a script of its own (zh-Hans)
                if (c.Parent.Name.Length == 0
                    && Languages.FirstOrDefault(l => l.Parent.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && l.Name.Split('-') is not [_, { Length: 4 }, ..]) is { } regional)
                    return regional;
            }
        }
        return Languages[0];
    }
}
