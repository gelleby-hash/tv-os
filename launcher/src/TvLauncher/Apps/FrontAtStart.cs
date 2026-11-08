namespace TvLauncher.Apps;

/// <summary>What the launcher does on a tick of <see cref="FrontAtStart"/>.</summary>
public enum FrontStep
{
    /// <summary>Not in front: ask for the front again.</summary>
    Ask,
    /// <summary>In front, not yet for <see cref="FrontAtStart.HoldFor"/>: check again on the next tick.</summary>
    Wait,
    /// <summary>In front for <see cref="FrontAtStart.HoldFor"/>: stop.</summary>
    Held,
    /// <summary>Not in front <see cref="FrontAtStart.GiveUpAfter"/> after the start: stop.</summary>
    GiveUp,
    /// <summary>The launcher is hidden (the desktop button, Windows' location settings): stop.</summary>
    SteppedAside,
}

/// <summary>
/// Decides, tick by tick after the start, whether the launcher asks for the front again. At a boot the scheduled
/// task starts it about a second into the sign-in, and Windows refused the one request made then (mini PC log,
/// 2026-11-06 and 2026-11-07: SendInput error 5, SetForegroundWindow refused), so the window stayed behind until
/// clicked. It keeps asking until it has stayed in front for <see cref="HoldFor"/>, and gives up after
/// <see cref="GiveUpAfter"/> so a viewer who has moved on is left alone.
/// </summary>
public sealed class FrontAtStart
{
    public static readonly TimeSpan AskEvery = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly long _started;
    private long? _inFrontSince;

    public FrontAtStart(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _started = _time.GetTimestamp();
    }

    /// <summary>How many times <see cref="Next"/> said to ask.</summary>
    public int Asks { get; private set; }

    /// <summary>Time from the start to the first tick that found the launcher in front, or null before that.</summary>
    public TimeSpan? FirstInFrontAfter { get; private set; }

    public TimeSpan Elapsed => _time.GetElapsedTime(_started);

    public FrontStep Next(bool inFront, bool visible)
    {
        if (!visible)
            return FrontStep.SteppedAside;
        if (inFront)
        {
            FirstInFrontAfter ??= Elapsed;
            _inFrontSince ??= _time.GetTimestamp();
            return _time.GetElapsedTime(_inFrontSince.Value) >= HoldFor ? FrontStep.Held : FrontStep.Wait;
        }
        _inFrontSince = null;
        if (Elapsed >= GiveUpAfter)
            return FrontStep.GiveUp;
        Asks++;
        return FrontStep.Ask;
    }
}
