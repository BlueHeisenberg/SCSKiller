using SCSKiller.Core.App;
using Tmds.DBus.Protocol;

namespace SCSKiller.Ui.Platform;

/// <summary>org.freedesktop.portal.Background on the session bus: RequestBackground, then the Response signal of its
/// request object, which comes once the user answered the desktop's question (if it asks one).</summary>
sealed class BackgroundPortal : LinuxStartup.IBackgroundPortal
{
    const string Portal = "org.freedesktop.portal.Desktop", PortalPath = "/org/freedesktop/portal/desktop";
    static readonly TimeSpan AnswerWait = TimeSpan.FromMinutes(5);

    public async Task<bool?> RequestAsync(bool autostart, IReadOnlyList<string> commandline, string reason)
    {
        if (DBusAddress.Session is not { } address) return null;
        try
        {
            using var bus = new DBusConnection(address);
            await bus.ConnectAsync();
            // the request object's path is known before the call: watching it first can't miss a fast answer
            var token = "scskiller" + Guid.NewGuid().ToString("N");
            var request = $"{PortalPath}/request/{bus.UniqueName![1..].Replace('.', '_')}/{token}";
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = await bus.WatchSignalAsync(null, request, "org.freedesktop.portal.Request", "Response",
                (m, _) =>
                {
                    var r = m.GetBodyReader();
                    var code = r.ReadUInt32();   // 0 granted, 1 cancelled, 2 other
                    return code == 0 && r.ReadDictionaryOfStringToVariantValue().TryGetValue("autostart", out var a) && a.GetBool();
                },
                n =>
                {
                    if (n.HasValue) answer.TrySetResult(n.Value);
                    else answer.TrySetException(n.Exception ?? new IOException("the bus closed before the portal answered"));
                },
                ObserverFlags.EmitOnConnectionClosed, emitOnCapturedContext: false);
            var handle = await bus.CallMethodAsync(Request(bus, token, autostart, commandline, reason), (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null);
            if (handle != request) return null;   // a portal too old for handle_token
            return await answer.Task.WaitAsync(AnswerWait);
        }
        catch (Exception) { return null; }   // no portal, no bus, an error reply or no answer in time: not known to start
    }

    static MessageBuffer Request(DBusConnection bus, string token, bool autostart, IReadOnlyList<string> commandline, string reason)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(destination: Portal, path: PortalPath, @interface: "org.freedesktop.portal.Background", member: "RequestBackground",
            signature: "sa{sv}", flags: MessageFlags.None);
        w.WriteString("");   // no parent window: the question may come before or without one
        w.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token, ["reason"] = reason, ["autostart"] = autostart, ["commandline"] = VariantValue.Array(commandline.ToArray()),
        });
        return w.CreateMessage();
    }
}
