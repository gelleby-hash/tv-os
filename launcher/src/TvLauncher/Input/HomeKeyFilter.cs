namespace TvLauncher.Input;

/// <summary>
/// Decides whether a key event is a press of one of the configured keys: key down, at most once per 300 ms
/// across all of them, so a held or bouncing key (or a button that sends two keys) fires once. Whether Windows
/// flagged the event as injected is not asked: the remote's Home and Back are consumer keys, which Windows
/// flags as injected although they are real, and the launcher never sends these keys itself.
/// </summary>
public sealed class HomeKeyFilter(IReadOnlyCollection<int> vks)
{
    public const long DebounceMs = 300;

    private long? _lastTriggerMs;

    public bool ShouldTrigger(int vkCode, bool keyDown, long timestampMs)
    {
        if (!keyDown || !vks.Contains(vkCode))
            return false;

        // A clock that went backwards (should not happen with a monotonic source) does not block the key.
        if (_lastTriggerMs is { } last && timestampMs >= last && timestampMs - last < DebounceMs)
            return false;

        _lastTriggerMs = timestampMs;
        return true;
    }
}
