using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using SCSKiller.Core.App;

namespace SCSKiller.App;

public enum ThemeMode { System, Light, Dark }

/// <summary>UI-only preferences. Kept beside the rest of SCSKiller's data so portable builds stay portable.</summary>
public sealed class ThemeSettings : INotifyPropertyChanged
{
    sealed record Stored(string Theme);

    const string FileName = "ui.json";
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ThemeSettings Instance { get; } = Load();
    public static string[] Labels { get; } = ["Use Windows setting", "Light", "Dark"];

    ThemeMode mode;

    ThemeSettings(ThemeMode mode) => this.mode = mode;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ElementTheme RequestedTheme => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public int SelectedIndex
    {
        get => (int)mode;
        set
        {
            if (value is < 0 or > 2 || value == (int)mode) return;
            mode = (ThemeMode)value;
            Save();
            PropertyChanged?.Invoke(this, new(nameof(SelectedIndex)));
            PropertyChanged?.Invoke(this, new(nameof(RequestedTheme)));
        }
    }

    static ThemeSettings Load()
    {
        try
        {
            var path = Path.Combine(AppStore.DefaultDir, FileName);
            if (!File.Exists(path)) return new(ThemeMode.System);
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored != null && Enum.TryParse<ThemeMode>(stored.Theme, ignoreCase: true, out var parsed)
                && Enum.IsDefined(typeof(ThemeMode), parsed))
                return new(parsed);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return new(ThemeMode.System);
    }

    void Save()
    {
        try
        {
            var dir = AppStore.DefaultDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(mode.ToString()), JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
