// Not TvLauncher.System: a namespace of that name would hide the real System namespace inside the project.
namespace TvLauncher.SystemActions;

/// <summary>
/// Allows at most a number of tries within a sliding time window. Used to recreate WebView2 after its browser
/// process ends: a crash now and then is recovered every time, a burst gives up so the process can exit and the
/// scheduled task's watchdog starts a fresh launcher.
/// </summary>
public sealed class RetryBudget(int tries, TimeSpan window, TimeProvider? time = null)
{
    public const int WebViewRecoveryTries = 3;
    public static readonly TimeSpan WebViewRecoveryWindow = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Queue<long> _used = new();

    /// <summary>Tries used within the window, counting the latest one.</summary>
    public int Used
    {
        get
        {
            Expire();
            return _used.Count;
        }
    }

    /// <summary>Uses a try and returns true, or returns false when the window already holds all of them.</summary>
    public bool TryUse()
    {
        Expire();
        if (_used.Count >= tries)
            return false;
        _used.Enqueue(_time.GetTimestamp());
        return true;
    }

    private void Expire()
    {
        while (_used.Count > 0 && _time.GetElapsedTime(_used.Peek()) > window)
            _used.Dequeue();
    }
}
