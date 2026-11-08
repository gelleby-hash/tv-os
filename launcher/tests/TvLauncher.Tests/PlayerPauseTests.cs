using TvLauncher.Apps;

namespace TvLauncher.Tests;

public sealed class PlayerPauseTests
{
    private readonly List<string> _calls = new();
    private readonly ManualTime _time = new() { AutoAdvanceUpTo = TimeSpan.MaxValue };

    private FakeAppView View(bool playing, bool spacePauses = true) =>
        new("app", _calls) { Playing = playing, SpacePauses = spacePauses };

    [Fact]
    public async Task NotPlaying_SendsNothing()
    {
        var view = View(playing: false);

        var outcome = await PlayerPause.PauseAsync(view, directFallback: true, _time);

        Assert.Equal(PauseOutcome.NothingPlaying, outcome);
        Assert.Empty(_calls);
        Assert.Equal(1, view.IsPlayingReads);
        Assert.Equal(TimeSpan.Zero, _time.Elapsed);
    }

    [Fact]
    public async Task Playing_SpacePauses_PausedBySpace()
    {
        var view = View(playing: true);

        var outcome = await PlayerPause.PauseAsync(view, directFallback: true, _time);

        Assert.Equal(PauseOutcome.PausedBySpace, outcome);
        Assert.Equal(["app.key Space"], _calls);
        Assert.Equal(TimeSpan.FromMilliseconds(25), _time.Elapsed);
    }

    [Fact]
    public async Task SpacePausesOnThirdPoll_PausedBySpace_After75Ms()
    {
        var view = View(playing: true);
        view.SpaceTakesReads = 3;

        var outcome = await PlayerPause.PauseAsync(view, directFallback: true, _time);

        Assert.Equal(PauseOutcome.PausedBySpace, outcome);
        Assert.Equal(["app.key Space"], _calls);
        Assert.Equal(TimeSpan.FromMilliseconds(75), _time.Elapsed);
    }

    [Fact]
    public async Task SpaceFails_NoFallback_StillPlaying_NeverDirect()
    {
        var view = View(playing: true, spacePauses: false);

        var outcome = await PlayerPause.PauseAsync(view, directFallback: false, _time);

        Assert.Equal(PauseOutcome.StillPlaying, outcome);
        Assert.Equal(["app.key Space"], _calls);
        // Polled every 25 ms for the default 500 ms, after the first read.
        Assert.Equal(TimeSpan.FromMilliseconds(500), _time.Elapsed);
        Assert.Equal(1 + 20, view.IsPlayingReads);
        Assert.True(view.Playing);
    }

    // Channel 4's player ignores Space; its own Pause button pauses it (measured 2026-11-02).
    [Fact]
    public async Task PauseButton_PressedInsteadOfSpace_PausedByButton()
    {
        var view = View(playing: true);

        var outcome = await PlayerPause.PauseAsync(view, directFallback: false, _time, pauseButton: "Pause");

        Assert.Equal(PauseOutcome.PausedByButton, outcome);
        Assert.Equal(["app.button Pause"], _calls);
        Assert.Equal(TimeSpan.FromMilliseconds(25), _time.Elapsed);
    }

    [Fact]
    public async Task PauseButton_NotPlaying_SendsNothing()
    {
        var view = View(playing: false);

        var outcome = await PlayerPause.PauseAsync(view, directFallback: false, _time, pauseButton: "Pause");

        Assert.Equal(PauseOutcome.NothingPlaying, outcome);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task PauseButton_DoesNotPause_StillPlaying_NoSpace()
    {
        var view = View(playing: true);
        view.ButtonPauses = false;

        var outcome = await PlayerPause.PauseAsync(view, directFallback: false, _time, pauseButton: "Pause");

        Assert.Equal(PauseOutcome.StillPlaying, outcome);
        Assert.Equal(["app.button Pause"], _calls);
        Assert.Equal(TimeSpan.FromMilliseconds(500), _time.Elapsed);
    }

    [Fact]
    public async Task SpaceFails_Fallback_PausedDirectly()
    {
        var view = View(playing: true, spacePauses: false);

        var outcome = await PlayerPause.PauseAsync(view, directFallback: true, _time);

        Assert.Equal(PauseOutcome.PausedDirectly, outcome);
        Assert.Equal(["app.key Space", "app.directPause"], _calls);
        Assert.Equal(TimeSpan.FromMilliseconds(500), _time.Elapsed);
    }

    [Fact]
    public async Task Timeout_IsHonoured()
    {
        var view = View(playing: true, spacePauses: false);

        var outcome = await PlayerPause.PauseAsync(view, directFallback: false, _time, TimeSpan.FromMilliseconds(100));

        Assert.Equal(PauseOutcome.StillPlaying, outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _time.Elapsed);
        Assert.Equal(1 + 4, view.IsPlayingReads);
    }

    // Space is a toggle: a "playing" read that answers after the pause was abandoned may be stale (the video
    // may have paused since), so nothing more may be sent.
    [Fact]
    public async Task Cancelled_AfterPlayingRead_SendsNoKey()
    {
        var view = View(playing: true);
        view.IsPlayingHangs = new TaskCompletionSource<bool>();
        using var cts = new CancellationTokenSource();

        var pause = PlayerPause.PauseAsync(view, directFallback: true, _time, cancellationToken: cts.Token);
        cts.Cancel();
        view.IsPlayingHangs.SetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pause);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Cancelled_DuringPolling_NoDirectPause()
    {
        // YouTube's case: Space did not pause, the fallback is allowed, and the last poll answers after the cancel.
        var view = View(playing: true, spacePauses: false);
        view.IsPlayingHangs = new TaskCompletionSource<bool>();
        view.HangsFromRead = 2;
        using var cts = new CancellationTokenSource();

        var pause = PlayerPause.PauseAsync(view, directFallback: true, _time, TimeSpan.FromMilliseconds(25), cts.Token);
        for (var waited = 0; view.IsPlayingReads < 2 && waited < 5000; waited += 10)
            await Task.Delay(10);
        Assert.Equal(2, view.IsPlayingReads);
        cts.Cancel();
        view.IsPlayingHangs.SetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pause);
        Assert.Equal(["app.key Space"], _calls);
    }

    [Fact]
    public async Task Cancelled_WhileWaitingToPoll_ReadsNoMore()
    {
        var time = new ManualTime();   // the 25 ms poll waits for an Advance that never comes
        var view = View(playing: true, spacePauses: false);
        using var cts = new CancellationTokenSource();

        var pause = PlayerPause.PauseAsync(view, directFallback: true, time, cancellationToken: cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pause).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["app.key Space"], _calls);
        Assert.Equal(1, view.IsPlayingReads);
    }
}
