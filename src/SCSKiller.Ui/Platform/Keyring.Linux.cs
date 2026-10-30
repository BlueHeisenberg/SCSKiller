using SCSKiller.Core.App;
using Tmds.DBus.Protocol;

namespace SCSKiller.Ui.Platform;

static partial class Keyring
{
    /// <summary>The Secret Service on the session bus (KWallet, GNOME Keyring, KeePassXC). A Flatpak without a talk-name
    /// can't reach it and the token files keep their own file: the Secret portal's per-app key is what would go here.</summary>
    public static IKeyring? System() => For(ThisDevice.SteamOs, DBusAddress.Session);

    /// <summary>On SteamOS only for the items an earlier build saved: a token saved in Desktop Mode's wallet would wait in
    /// Game Mode, which has no unlocked one, so new tokens go to the file.</summary>
    internal static IKeyring For(bool steamOs, string? address) => new SecretService(address, takesNew: !steamOs);
}

/// <summary>The Secret Service API over D-Bus, a connection per call: the "plain" session algorithm (the bus is this
/// user's own socket), items in the default collection, and never a prompt, so a locked keyring is unavailable.</summary>
sealed class SecretService(string? address, TimeSpan? timeout = null, bool takesNew = true) : IKeyring
{
    public bool TakesNew => takesNew;

    const string Bus = "org.freedesktop.secrets", Root = "/org/freedesktop/secrets", Service = "org.freedesktop.Secret.Service",
        Collection = "org.freedesktop.Secret.Collection", Item = "org.freedesktop.Secret.Item", Prompt = "org.freedesktop.Secret.Prompt",
        Properties = "org.freedesktop.DBus.Properties", NoPath = "/";

    // a keyring daemon that waits on its own dialog must not hold a sign-in or an upload
    readonly TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);

    public Task<bool> StoreAsync(string label, IReadOnlyDictionary<string, string> attributes, byte[] secret, CancellationToken ct) => Run(false, async c =>
    {
        var collection = await c.CallMethodAsync(Call(c, Root, Service, "ReadAlias", "s", "default"), (m, _) => m.GetBodyReader().ReadObjectPathAsString());
        if (collection == NoPath || await Locked(c, collection)) return false;
        var session = await OpenSession(c);
        var (item, prompt) = await c.CallMethodAsync(CreateItem(c, collection, label, attributes, session, secret), (m, _) =>
        {
            var r = m.GetBodyReader();
            return (r.ReadObjectPathAsString(), r.ReadObjectPathAsString());
        });
        if (prompt != NoPath) await Dismiss(c, prompt);
        return item != NoPath;
    }, ct);

    public Task<KeyringRead> ReadAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct) => Run(new KeyringRead(KeyringState.Unavailable), async c =>
    {
        var (unlocked, locked) = await Search(c, attributes);
        if (unlocked.Length > 0)
        {
            var session = await OpenSession(c);
            var secret = await c.CallMethodAsync(Call(c, unlocked[0], Item, "GetSecret", "o", session, path: true), (m, _) =>
            {
                var r = m.GetBodyReader();
                r.AlignStruct();
                r.ReadObjectPath();
                r.ReadArrayOfByte();   // parameters: none with "plain"
                return r.ReadArrayOfByte();
            });
            return new KeyringRead(KeyringState.Found, secret);
        }
        // a closed wallet may not list its items at all: none found is known only with every collection open
        return locked.Length > 0 || await AnyLocked(c) ? new KeyringRead(KeyringState.Unavailable) : new KeyringRead(KeyringState.Missing);
    }, ct);

    public Task<bool> DeleteAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct) => Run(false, async c =>
    {
        var (unlocked, locked) = await Search(c, attributes);
        foreach (var item in unlocked)
        {
            var prompt = await c.CallMethodAsync(Call(c, item, Item, "Delete"), (m, _) => m.GetBodyReader().ReadObjectPathAsString());
            if (prompt != NoPath)
            {
                await Dismiss(c, prompt);
                return false;
            }
        }
        // a wallet may hide its items: open checks before and after the re-search, so a re-lock either way keeps the id
        return locked.Length == 0 && !await AnyLocked(c) && await Search(c, attributes) is ([], []) && !await AnyLocked(c);
    }, ct);

    /// <summary><paramref name="unavailable"/> when there's no bus, no Secret Service on it, an error or no answer in time.</summary>
    async Task<T> Run<T>(T unavailable, Func<DBusConnection, Task<T>> body, CancellationToken ct)
    {
        if (address is not { Length: > 0 }) return unavailable;
        var c = new DBusConnection(address);
        var call = Go();
        try { return await call.WaitAsync(limit, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return unavailable; }
        finally
        {
            c.Dispose();
            // a call cut short ends with its connection: observed, so it isn't logged as a crash
            _ = call.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        async Task<T> Go()
        {
            await c.ConnectAsync();
            return await body(c);
        }
    }

    static Task<(string[] Unlocked, string[] Locked)> Search(DBusConnection c, IReadOnlyDictionary<string, string> attributes)
    {
        var w = c.GetMessageWriter();
        MessageBuffer call;
        try
        {
            w.WriteMethodCallHeader(Bus, Root, Service, "SearchItems", "a{ss}", MessageFlags.None);
            WriteAttributes(ref w, attributes);
            call = w.CreateMessage();
        }
        finally { w.Dispose(); }
        return c.CallMethodAsync(call, (m, _) =>
        {
            var r = m.GetBodyReader();
            return (Paths(r.ReadArrayOfObjectPath()), Paths(r.ReadArrayOfObjectPath()));
        });
    }

    static string[] Paths(ObjectPath[] paths) => [.. paths.Select(p => p.ToString())];

    static Task<string> OpenSession(DBusConnection c)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, Root, Service, "OpenSession", "sv", MessageFlags.None);
        w.WriteString("plain");
        w.WriteVariantString("");
        return c.CallMethodAsync(w.CreateMessage(), (m, _) =>
        {
            var r = m.GetBodyReader();
            r.ReadVariantValue();
            return r.ReadObjectPathAsString();
        });
    }

    static MessageBuffer CreateItem(DBusConnection c, string collection, string label, IReadOnlyDictionary<string, string> attributes, string session, byte[] secret)
    {
        var w = c.GetMessageWriter();
        try
        {
            w.WriteMethodCallHeader(Bus, collection, Collection, "CreateItem", "a{sv}(oayays)b", MessageFlags.None);
            var props = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("org.freedesktop.Secret.Item.Label");
            w.WriteVariantString(label);
            w.WriteDictionaryEntryStart();
            w.WriteString("org.freedesktop.Secret.Item.Attributes");
            w.WriteSignature("a{ss}");   // a variant: its signature, then the value
            WriteAttributes(ref w, attributes);
            w.WriteDictionaryEnd(props);
            w.WriteStructureStart();
            w.WriteObjectPath(session);
            w.WriteArray(Array.Empty<byte>());
            w.WriteArray(secret);
            w.WriteString("text/plain");
            w.WriteBool(true);   // replace an item with the same attributes
            return w.CreateMessage();
        }
        finally { w.Dispose(); }
    }

    static void WriteAttributes(ref MessageWriter w, IReadOnlyDictionary<string, string> attributes)
    {
        var dict = w.WriteDictionaryStart();
        foreach (var (key, value) in attributes)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(key);
            w.WriteString(value);
        }
        w.WriteDictionaryEnd(dict);
    }

    static async Task<bool> Locked(DBusConnection c, string collection) =>
        await c.CallMethodAsync(Call(c, collection, Properties, "Get", "ss", Collection, "Locked"), (m, _) =>
        {
            var r = m.GetBodyReader();
            r.ReadSignature("b");
            return r.ReadBool();
        });

    static async Task<bool> AnyLocked(DBusConnection c)
    {
        var all = await c.CallMethodAsync(Call(c, Root, Properties, "Get", "ss", Service, "Collections"), (m, _) =>
        {
            var r = m.GetBodyReader();
            r.ReadSignature("ao");
            return Paths(r.ReadArrayOfObjectPath());
        });
        foreach (var collection in all)
            if (await Locked(c, collection)) return true;
        return false;
    }

    static Task Dismiss(DBusConnection c, string prompt) => c.CallMethodAsync(Call(c, prompt, Prompt, "Dismiss"));

    /// <summary>A call with string arguments, or one object path (<paramref name="path"/>).</summary>
    static MessageBuffer Call(DBusConnection c, string objectPath, string iface, string member, string? signature = null, string? a = null,
        string? b = null, bool path = false)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, objectPath, iface, member, signature, MessageFlags.None);
        if (a != null)
        {
            if (path) w.WriteObjectPath(a);
            else w.WriteString(a);
        }
        if (b != null) w.WriteString(b);
        return w.CreateMessage();
    }
}
