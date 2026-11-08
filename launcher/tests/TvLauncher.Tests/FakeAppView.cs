using TvLauncher.Apps;

namespace TvLauncher.Tests;

/// <summary>An app view that records what is done to it in a shared list, as "&lt;id&gt;.&lt;call&gt;".</summary>
internal sealed class FakeAppView(string id, List<string> calls) : IAppView
{
    private int _readsUntilPaused;

    public string Id => id;
    public bool IsAlive { get; set; } = true;
    public bool LoadFailed { get; set; }
    public bool CanGoBack { get; set; }

    /// <summary>Whether its video plays.</summary>
    public bool Playing { get; set; }

    /// <summary>Whether a Space press pauses the video.</summary>
    public bool SpacePauses { get; set; } = true;

    /// <summary>How many reads after a Space press the video still plays before it stops (0: at once).</summary>
    public int SpaceTakesReads { get; set; }

    public Exception? IsPlayingThrows { get; set; }

    /// <summary>When set, reading whether it plays waits for this, as a hung page does.</summary>
    public TaskCompletionSource<bool>? IsPlayingHangs { get; set; }

    /// <summary>The first read (1-based) that hangs when <see cref="IsPlayingHangs"/> is set; earlier reads answer.</summary>
    public int HangsFromRead { get; set; } = 1;

    public int IsPlayingReads { get; private set; }

    public Task<bool> IsPlayingAsync()
    {
        IsPlayingReads++;
        if (IsPlayingHangs is not null && IsPlayingReads >= HangsFromRead)
            return IsPlayingHangs.Task;
        if (IsPlayingThrows is not null)
            return Task.FromException<bool>(IsPlayingThrows);
        if (_readsUntilPaused > 0 && --_readsUntilPaused == 0)
            Playing = false;
        return Task.FromResult(Playing);
    }

    /// <summary>When set, pressing a key waits for this, as a hung page does.</summary>
    public TaskCompletionSource? PressKeyHangs { get; set; }

    public Task PressKeyAsync(AppKey key)
    {
        calls.Add($"{id}.key {key}");
        if (PressKeyHangs is not null)
            return PressKeyHangs.Task;
        if (key == AppKey.Space && SpacePauses)
        {
            if (SpaceTakesReads == 0)
                Playing = false;
            else
                _readsUntilPaused = SpaceTakesReads;
        }
        return Task.CompletedTask;
    }

    /// <summary>Whether pressing the player's pause button pauses the video.</summary>
    public bool ButtonPauses { get; set; } = true;

    public Task PressButtonAsync(string label)
    {
        calls.Add($"{id}.button {label}");
        if (ButtonPauses)
            Playing = false;
        return Task.CompletedTask;
    }

    public Task DirectPauseAsync()
    {
        calls.Add($"{id}.directPause");
        Playing = false;
        return Task.CompletedTask;
    }

    public void GoBack() => calls.Add($"{id}.goBack");
    public void Reload() => calls.Add($"{id}.reload");
    public void Show() => calls.Add($"{id}.show");
    public void RestoreFocus() => calls.Add($"{id}.restoreFocus");
    /// <summary>When set, Hide throws it, as a WebView2 controller can after its browser process died.</summary>
    public Exception? HideThrows { get; set; }

    public void Close() => calls.Add($"{id}.close");

    public void Hide()
    {
        calls.Add($"{id}.hide");
        if (HideThrows is not null)
            throw HideThrows;
    }

    public Task SetBoostGainAsync(double gain)
    {
        calls.Add($"{id}.boost {gain}");
        return Task.CompletedTask;
    }

    public event Action? CloseRequested;

    /// <summary>What the page does when it asks to close (YouTube's "Exit YouTube?" answered yes).</summary>
    public void RequestClose() => CloseRequested?.Invoke();

    /// <summary>When set, opening voice search throws it, as when the page script did not arm.</summary>
    public Exception? StartPhoneMicThrows { get; set; }

    /// <summary>When set, opening voice search waits for this, as a slow page does.</summary>
    public TaskCompletionSource? StartPhoneMicHangs { get; set; }

    public Task StartPhoneMicAsync()
    {
        calls.Add($"{id}.micStart");
        if (StartPhoneMicHangs is not null)
            return StartPhoneMicHangs.Task;
        return StartPhoneMicThrows is not null ? Task.FromException(StartPhoneMicThrows) : Task.CompletedTask;
    }

    public void PostPhoneMicAudio(ReadOnlyMemory<byte> pcm) => calls.Add($"{id}.micAudio {pcm.Length}");

    public void EndPhoneMic() => calls.Add($"{id}.micEnd");

    public event Action? PhoneMicStopped;

    /// <summary>What the page does when YouTube stops listening.</summary>
    public void StopPhoneMic() => PhoneMicStopped?.Invoke();
}
