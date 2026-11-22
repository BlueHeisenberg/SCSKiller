using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>The Steam Big Picture page: the SCSKiller companion plugin for Decky Loader, what Decky is, and the plugin's
/// install into Decky from its newest published release. Nothing here needs the engine: the page only reads and writes
/// Decky's plugin folder and fetches the release's zip.</summary>
public sealed class BigPictureVm : Bindable
{
    string status = "";
    string latest = "";
    string installed = "";
    bool busy;

    public BigPictureVm() => ReadInstalled();

    /// <summary>Where Decky keeps the plugin, so a wrong guess is visible on the page.</summary>
    public string PluginsFolder => "Decky's plugin folder: " + Decky.PluginsFolder();
    /// <summary>Decky Loader isn't installed: the page says so and the button stays disabled.</summary>
    public bool DeckyMissing => !Decky.IsInstalled();
    /// <summary>The install button: disabled while an install or a check runs.</summary>
    public bool Idle => !busy;
    public bool Busy => busy;
    /// <summary>"Installed in Decky: 0.9.15", or that it isn't.</summary>
    public string Installed => installed;
    public string Latest => latest;
    public bool HasLatest => latest.Length > 0;
    public string Status => status;
    public bool HasStatus => status.Length > 0;
    /// <summary>The release's zip: what Install downloads, and what Decky's own Developer, Install Plugin from URL takes.</summary>
    public string Url { get; private set; } = "";
    public bool HasUrl => Url.Length > 0;

    /// <summary>Reads the newest published release, so the page shows what Install would get (one small API call).</summary>
    public async Task LoadAsync()
    {
        ReadInstalled();
        Changed();
        if (busy || DeckyMissing) return;
        busy = true;
        status = "Checking the newest release...";
        Changed();
        try
        {
            using var http = Http();
            var release = await Decky.LatestAsync(http);
            latest = "Newest published: " + release.Version;
            Url = release.Zip.ToString();
            status = Decky.InstalledVersion() == release.Version ? "Up to date." : "";
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            status = "Couldn't read the newest release: " + e.Message;
            Host.Log?.Report("Decky plugin check failed: " + e);
        }
        finally
        {
            busy = false;
            Changed();
        }
    }

    /// <summary>Downloads the newest release and unpacks it into Decky's plugin folder. Decky loads it at the next start.</summary>
    public async Task InstallAsync()
    {
        if (busy) return;
        if (DeckyMissing)
        {
            status = "Decky Loader isn't installed in " + Decky.PluginsFolder() + ": install it first.";
            Changed();
            return;
        }
        busy = true;
        status = "Downloading the plugin...";
        Changed();
        try
        {
            using var http = Http();
            var release = await Decky.LatestAsync(http);
            latest = "Newest published: " + release.Version;
            Url = release.Zip.ToString();
            Changed();
            status = "Installing " + release.Version + " into Decky...";
            Changed();
            await Decky.InstallAsync(http, release);
            ReadInstalled();
            status = "Installed " + release.Version + " into Decky. Restart Steam to load it.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            status = "Couldn't install it: " + e.Message;
            Host.Log?.Report("Decky plugin install failed: " + e);
        }
        finally
        {
            busy = false;
            Changed();
        }
    }

    void ReadInstalled() => installed = Decky.InstalledVersion() is { } version
        ? "Installed in Decky: " + version
        : "Not installed in Decky yet";

    // GitHub answers an anonymous request with the release list; RouteFailover passes other hosts through and names the
    // build in the User-Agent, which the API requires.
    static HttpClient Http() => new(RouteFailover.Default, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
}
