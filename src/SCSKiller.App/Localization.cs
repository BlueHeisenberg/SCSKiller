using System.Globalization;
using System.Resources;

namespace SCSKiller.App;

public static class I18n
{
    private static readonly ResourceManager Manager = new("SCSKiller.App.Resources.AppResources", typeof(I18n).Assembly);

    public static string T(string key)
    {
        try
        {
            var val = Manager.GetString(key, CultureInfo.CurrentUICulture);
            return string.IsNullOrEmpty(val) ? key : val;
        }
        catch
        {
            return key;
        }
    }

    public static string T(string key, params object[] args)
    {
        var pattern = T(key);
        try
        {
            return string.Format(pattern, args);
        }
        catch
        {
            return pattern;
        }
    }
}