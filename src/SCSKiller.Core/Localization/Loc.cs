using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace SCSKiller.Core.Localization;

public sealed record LanguageInfo(string Code, string NativeName, string EnglishName)
{
    public override string ToString() => NativeName;
}

/// <summary>Localization provider for UI and Core: embedded JSON language dictionaries with fallback to English.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    const string ResourcePrefix = "SCSKiller.Core.Localization.";
    const string DefaultLanguageCode = "en";

    public static Loc Instance { get; } = new();

    public static void Initialize(string? language = null) => Instance.SetLanguage(language);

    public static string T(string key) => Instance[key];
    public static string F(string key, params object[] args) => Instance.Format(key, args);

    readonly Dictionary<string, string> fallbackStrings = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> currentStrings = new(StringComparer.OrdinalIgnoreCase);
    readonly List<LanguageInfo> availableLanguages = [];

    string currentLanguage = DefaultLanguageCode;
    CultureInfo currentCulture = CultureInfo.InvariantCulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    Loc()
    {
        DiscoverLanguages();
        LoadDictionary(DefaultLanguageCode, fallbackStrings);
        SetLanguage(null);
    }

    public string CurrentLanguage => currentLanguage;

    public CultureInfo CurrentCulture => currentCulture;

    public IReadOnlyList<LanguageInfo> AvailableLanguages => availableLanguages;

    public LanguageInfo CurrentLanguageInfo =>
        availableLanguages.FirstOrDefault(l => l.Code.Equals(currentLanguage, StringComparison.OrdinalIgnoreCase))
        ?? availableLanguages.FirstOrDefault(l => l.Code.Equals(DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
        ?? new(DefaultLanguageCode, "English", "English");

    public string this[string key]
    {
        get
        {
            if (currentStrings.TryGetValue(key, out var translated) && !string.IsNullOrEmpty(translated))
                return translated;
            if (fallbackStrings.TryGetValue(key, out var fallback) && !string.IsNullOrEmpty(fallback))
                return fallback;
            return key;
        }
    }

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        try
        {
            return string.Format(currentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public void SetLanguage(string? code)
    {
        var resolved = ResolveLanguageCode(code);
        if (resolved == currentLanguage && currentStrings.Count > 0 && code != null) return;

        var newStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!resolved.Equals(DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
            LoadDictionary(resolved, newStrings);

        currentLanguage = resolved;
        currentStrings = newStrings;

        try
        {
            currentCulture = CultureInfo.GetCultureInfo(resolved);
        }
        catch (CultureNotFoundException)
        {
            currentCulture = CultureInfo.InvariantCulture;
        }

        PropertyChanged?.Invoke(this, new(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new(nameof(CurrentLanguageInfo)));
        PropertyChanged?.Invoke(this, new("Item"));
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    string ResolveLanguageCode(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = availableLanguages.FirstOrDefault(l => l.Code.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Code;
        }

        // Automatic OS system language detection
        var uiCulture = CultureInfo.CurrentUICulture;
        var exact = availableLanguages.FirstOrDefault(l => l.Code.Equals(uiCulture.Name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Code;

        var twoLetter = availableLanguages.FirstOrDefault(l =>
            l.Code.StartsWith(uiCulture.TwoLetterISOLanguageName + "-", StringComparison.OrdinalIgnoreCase) ||
            l.Code.Equals(uiCulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
        if (twoLetter != null) return twoLetter.Code;

        return DefaultLanguageCode;
    }

    void DiscoverLanguages()
    {
        availableLanguages.Clear();
        var assembly = typeof(Loc).Assembly;

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = name[ResourcePrefix.Length..];
            var code = fileName[..^5]; // drop ".json"

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                string native = code, english = code;
                if (root.TryGetProperty("_meta", out var meta))
                {
                    if (meta.TryGetProperty("nativeName", out var n) && n.GetString() is { } ns) native = ns;
                    if (meta.TryGetProperty("englishName", out var e) && e.GetString() is { } es) english = es;
                }

                availableLanguages.Add(new(code, native, english));
            }
            catch (Exception)
            {
                availableLanguages.Add(new(code, code, code));
            }
        }

        // Always ensure English is in the list
        if (!availableLanguages.Any(l => l.Code.Equals(DefaultLanguageCode, StringComparison.OrdinalIgnoreCase)))
            availableLanguages.Insert(0, new(DefaultLanguageCode, "English", "English"));
    }

    void LoadDictionary(string code, Dictionary<string, string> target)
    {
        var assembly = typeof(Loc).Assembly;
        var resourceName = $"{ResourcePrefix}{code}.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return;

        try
        {
            using var doc = JsonDocument.Parse(stream);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith('_')) continue; // Skip metadata
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } val)
                    target[prop.Name] = val;
            }
        }
        catch (Exception)
        {
            // Keep existing keys if parse fails
        }
    }
}

