namespace TvLauncher.Apps;

public enum PauseOutcome { NothingPlaying, PausedBySpace, PausedByButton, PausedDirectly, StillPlaying }

/// <summary>
/// Pauses an app through its own player: a real Space press, or a press of the player's own pause button for an
/// app whose player ignores Space (Channel 4), sent only while the video plays, so a paused video is never started.
/// Pausing the video element directly wedges Disney+'s player (spike, 2026-10-25) and puts Channel 4's out of step
/// (2026-11-02), so that is only a fallback, for apps whose player survives it, after the press did not pause.
/// </summary>
public static class PlayerPause
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(25);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    /// <param name="directFallback">Pause the video element directly if Space did not pause it within the timeout.</param>
    /// <param name="timeout">How long to wait for Space to pause it (default 500 ms).</param>
    /// <param name="cancellationToken">Once cancelled, nothing more is sent to the view and the task ends cancelled.</param>
    /// <param name="pauseButton">The label of the player's pause button, pressed instead of Space; null for Space.</param>
    public static async Task<PauseOutcome> PauseAsync(
        IAppView view, bool directFallback, TimeProvider time, TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        string? pauseButton = null)
    {
        var playing = await view.IsPlayingAsync();
        // Once cancelled (the caller stopped waiting), send nothing more: a late "playing" may be stale, and Space toggles.
        cancellationToken.ThrowIfCancellationRequested();
        if (!playing)
            return PauseOutcome.NothingPlaying;

        if (pauseButton is null)
            await view.PressKeyAsync(AppKey.Space);
        else
            await view.PressButtonAsync(pauseButton);
        var limit = timeout ?? DefaultTimeout;
        var started = time.GetTimestamp();
        do
        {
            await Task.Delay(PollEvery, time, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await view.IsPlayingAsync())
                return pauseButton is null ? PauseOutcome.PausedBySpace : PauseOutcome.PausedByButton;
        }
        while (time.GetElapsedTime(started) < limit);

        if (!directFallback)
            return PauseOutcome.StillPlaying;
        cancellationToken.ThrowIfCancellationRequested();
        await view.DirectPauseAsync();
        return PauseOutcome.PausedDirectly;
    }
}
