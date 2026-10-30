using System.ComponentModel;

namespace SCSKiller.Ui.Models;

// View models are computed views over IScsKiller: Refresh() re-reads the facade and tells the bindings to update everything.
public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
    protected void Changed(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new(n)); }
}

/// <summary>Folds a burst of Core events into one run on the UI thread: a scan raises GameChanged once per game (62 at
/// once on a real library), and a page refresh per event kept the UI thread busy for seconds. Request from any thread.</summary>
public sealed class Coalesced(IUiThread ui, Action run)
{
    int pending;

    public void Request()
    {
        if (Interlocked.Exchange(ref pending, 1) == 0 && !ui.TryEnqueue(() => { Volatile.Write(ref pending, 0); run(); }))
            Volatile.Write(ref pending, 0);   // the window is closing
    }
}

/// <summary>A read that must not run on the UI thread, such as the driver cache's usage and limit (NVIDIA's limit loads the
/// driver's profile database: ~130 ms measured; the usage walks the cache folder). <paramref name="read"/> runs on a
/// background thread, <paramref name="apply"/> on the UI thread. One read at a time: requests meanwhile fold into one more
/// read, so results apply in order. A read that throws changes nothing and is logged once until it reads again: thrown
/// from this async void, it would end the app. Request on the UI thread.</summary>
public sealed class BackgroundRead<T>(string name, Func<T> read, Action<T> apply)
{
    bool busy, again;
    string? failed;

    public async void Request()
    {
        if (busy) { again = true; return; }
        busy = true;
        try
        {
            do
            {
                again = false;
                T value;
                try { value = await Task.Run(read); }
                catch (OperationCanceledException) { continue; }
                catch (Exception e)
                {
                    if (e.Message != failed) Host.Log?.Report($"{name} read failed: {e.Message}");
                    failed = e.Message;
                    continue;
                }
                failed = null;
                apply(value);
            } while (again);
        }
        finally { busy = false; }
    }
}
