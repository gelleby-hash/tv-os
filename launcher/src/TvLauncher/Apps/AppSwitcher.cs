using TvLauncher.Config;

namespace TvLauncher.Apps;

/// <summary>
/// Decides which app view is in front when the viewer picks a tile or presses Home. Each app is a view in the
/// launcher's window, made on first pick and kept loaded while hidden, so picking it again returns to the
/// same spot. A view is also closed on demand (<see cref="CloseAppAsync"/>, when the Apps panel hides the app or
/// changes its version). Leaving an app pauses it through its own player; coming back never presses a key or plays.
/// </summary>
/// <param name="createView">Makes an app's view; called once per app, and again only if the view's browser process ended.</param>
/// <param name="log">Receives the pause outcomes and failures so the host can write them to its log; may be called from a thread-pool thread.</param>
/// <param name="pauseTimeout">How long to wait for the app being left to pause (default <see cref="DefaultPauseTimeout"/>).</param>
/// <param name="chosen">The version of an app to make a view with (read when a view is made; an open view keeps its own); the app's first version when absent.</param>
public sealed class AppSwitcher(
    LauncherConfig config,
    Func<AppEntry, Task<IAppView>> createView,
    Action showHome,
    Action hideHome,
    Action<string>? log = null,
    TimeProvider? time = null,
    TimeSpan? pauseTimeout = null,
    Func<string, AppEntry>? chosen = null)
{
    /// <summary>The longest Home or another app waits for the app being left to pause.</summary>
    public static readonly TimeSpan DefaultPauseTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _pauseTimeout = pauseTimeout ?? DefaultPauseTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IAppView> _views = new();
    // The entry each view was made with. Back and the pause use it, not what `chosen` says now: a view that was
    // made as one version is left, and paused, as that version until it is closed.
    private readonly Dictionary<string, AppEntry> _entries = new();

    /// <summary>The app currently in front, or null on the home screen (or the desktop and VLC, which are not apps).</summary>
    public string? CurrentAppId { get; private set; }

    /// <summary>The app views made so far, one per app, whether in front or hidden.</summary>
    public IReadOnlyCollection<IAppView> Views => _views.Values.ToList();

    public async Task OpenAsync(string appId)
    {
        var entry = Find(appId);
        await _gate.WaitAsync();
        try
        {
            if (CurrentAppId == appId)
                return;
            await LeaveAsync();
            var view = await ViewForAsync(entry);
            hideHome();
            view.Show();
            CurrentAppId = appId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes the app's view out for good, so the next pick makes a new one (after its version changed, or the app
    /// was hidden). An app in front goes home first, pausing as usual. Nothing if the app has no view.
    /// </summary>
    public async Task CloseAppAsync(string appId)
    {
        await _gate.WaitAsync();
        try
        {
            if (CurrentAppId == appId)
                await LeaveAndShowHomeAsync();
            if (!_views.TryGetValue(appId, out var view))
                return;
            view.Close();
            _views.Remove(appId);
            _entries.Remove(appId);
            log?.Invoke($"view {appId}: closed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task GoHomeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await LeaveAndShowHomeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives the app in front the keyboard again, after its page lost it. Disney+ sometimes drops the keyboard
    /// focus during its first load (seen 2026-10-30: no window focused, the remote's arrows and OK dead until a
    /// mouse click). Nothing on the home screen, while a switch is under way (it focuses the app it shows), or
    /// when the view has no page to focus.
    /// </summary>
    public void RestoreFocus()
    {
        if (_gate.CurrentCount == 0 || CurrentAppId is not { } id || !_views.TryGetValue(id, out var view))
            return;
        if (view.IsAlive && !view.LoadFailed)
            view.RestoreFocus();
    }

    /// <summary>
    /// The remote's Back key, pressed while an app is in front. A <see cref="BackMode.History"/> app goes back
    /// through its history, and home once it has none. A <see cref="BackMode.Page"/> app gets Escape and handles
    /// Back itself, however far back it is: only Home, or the page asking to close, leaves it. Nothing on the home screen.
    /// </summary>
    public async Task BackAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (CurrentAppId is not { } id)
                return;
            var view = _views[id];
            if (_entries[id].Back == BackMode.Page)
                await PressEscapeAsync(view);
            else if (view.CanGoBack)
                view.GoBack();
            else
                await LeaveAndShowHomeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Capped like the pause in LeaveAsync: a key press never returns on a hung page, and this runs inside the
    // lock that Home needs.
    private async Task PressEscapeAsync(IAppView view)
    {
        try
        {
            await view.PressKeyAsync(AppKey.Escape).WaitAsync(_pauseTimeout, _time);
        }
        catch (TimeoutException)
        {
            log?.Invoke($"back {view.Id}: timed out");
        }
    }

    private async Task LeaveAndShowHomeAsync()
    {
        await LeaveAsync();
        showHome();
    }

    // Pauses the app in front through its player, then hides it. A failed or hung pause is logged and never
    // stops Home from showing or the next app from opening.
    private async Task LeaveAsync()
    {
        if (CurrentAppId is not { } id)
            return;
        var view = _views[id];
        CurrentAppId = null;
        // Not disposed: the abandoned pause may still check its token after this method has returned.
        var abandon = new CancellationTokenSource();
        var app = _entries[id];
        var pause = PlayerPause.PauseAsync(view, app.DirectPauseFallback, _time, cancellationToken: abandon.Token, pauseButton: app.PauseButton);
        try
        {
            // WaitAsync caps the whole pause, including a first read or key press that never returns (a hung page).
            var outcome = await pause.WaitAsync(_pauseTimeout, _time);
            log?.Invoke($"pause {id}: {outcome}");
        }
        catch (TimeoutException) when (!pause.IsCompleted)
        {
            log?.Invoke($"pause {id}: timed out");
            // The pause is told to send nothing more (a late, stale "playing" must not press Space, a toggle) and
            // is not waited for. How it ends is logged, so a later failure is never an unobserved exception.
            abandon.Cancel();
            _ = pause.ContinueWith(
                t => log?.Invoke(t.Status switch
                {
                    TaskStatus.Faulted => $"pause {id} failed after timing out: {t.Exception!.InnerException?.Message}",
                    TaskStatus.Canceled => $"pause {id}: stopped after timing out",
                    _ => $"pause {id}: {t.Result} after timing out",
                }),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            log?.Invoke($"pause {id} failed: {ex.Message}");
        }

        try
        {
            view.Hide();
        }
        catch (Exception ex)
        {
            // For example a WebView2 controller whose browser process died. Home or the next app still shows.
            log?.Invoke($"hide {id} failed: {ex.Message}");
        }
    }

    // The app's view, reused once made. A view whose browser process ended is replaced; one whose page
    // did not load is loaded again.
    private async Task<IAppView> ViewForAsync(AppEntry entry)
    {
        if (_views.TryGetValue(entry.Id, out var view))
        {
            if (view.IsAlive)
            {
                if (view.LoadFailed)
                    view.Reload();
                return view;
            }
            log?.Invoke($"view {entry.Id} ended: making a new one");
            _views.Remove(entry.Id);
            _entries.Remove(entry.Id);
        }

        view = await createView(entry);
        view.CloseRequested += () => _ = CloseAsync(view);
        _views[entry.Id] = view;
        _entries[entry.Id] = entry;
        return view;
    }

    // The page asked to close (YouTube's "Exit YouTube?" answered yes, Channel 4's TV app emptying its page): home, if
    // that page is in front. The page is then gone for good, so its view is closed and the next open loads the app
    // afresh in the same profile; a kept view showed Channel 4's dark page again (owner, 2026-11-02).
    // Raised from a view's event handler, so a failure is logged, never thrown.
    private async Task CloseAsync(IAppView view)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (CurrentAppId is { } id && ReferenceEquals(_views[id], view))
                {
                    await LeaveAndShowHomeAsync();
                    _views.Remove(id);
                    _entries.Remove(id);
                    view.Close();
                    log?.Invoke($"view {id}: closed after its page asked to close");
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"closing {view.Id} failed: {ex.Message}");
        }
    }

    private AppEntry Find(string appId) =>
        config.Apps.Any(a => a.Id == appId)
            ? chosen?.Invoke(appId) ?? config.Apps.First(a => a.Id == appId).Default
            : throw new ArgumentException($"No app with Id '{appId}' in the configuration.", nameof(appId));
}
