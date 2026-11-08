using System.IO;
using TvLauncher.Apps;
using TvLauncher.Config;

namespace TvLauncher.Tests;

public sealed class AppSwitcherTests
{
    // One list for the views, the home screen and creation, so the tests can check the order across them.
    private readonly List<string> _calls = new();
    private readonly List<string> _logs = new();
    private readonly Dictionary<string, List<FakeAppView>> _made = new();
    private readonly List<AppEntry> _entries = new();
    // Pause polls (25 ms) pass at once; the 2 s pause limit waits for the test to advance the clock.
    private readonly ManualTime _time = new() { AutoAdvanceUpTo = TimeSpan.FromSeconds(1) };
    private readonly TaskCompletionSource _homeShown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, creating a view waits for it, as a cold WebView2 does.</summary>
    private Task? _createGate;

    // The repo's launcher.json, so the per-app settings tested are the ones that ship.
    private static LauncherConfig RepoConfig() => LauncherConfig.Load(Path.Combine(AppContext.BaseDirectory, "launcher.json"));

    // A config of its own, for a setting the shipped default version does not have.
    private static LauncherConfig ConfigWithApps(string appsJson)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ \"Apps\": " + appsJson + ", \"HomeKeyVk\": 172, \"SpotlightFolders\": [] }");
            return LauncherConfig.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private AppSwitcher Switcher(TimeSpan? pauseTimeout = null, LauncherConfig? config = null, Func<string, AppEntry>? chosen = null) => new(
        config ?? RepoConfig(),
        CreateViewAsync,
        () =>
        {
            _calls.Add("showHome");
            _homeShown.TrySetResult();
        },
        () => _calls.Add("hideHome"),
        Log,
        _time,
        pauseTimeout,
        chosen);

    // Channel 4's version from the shipped config: "tv" (Back: Page, pauses by Space) or "web" (Back: History, Pause button).
    private static AppEntry Channel4(string versionId) =>
        RepoConfig().Apps.First(a => a.Id == "channel4").Version(versionId)!;

    // An abandoned pause logs from the thread pool.
    private void Log(string line)
    {
        lock (_logs)
            _logs.Add(line);
    }

    private async Task WaitForLogAsync(string text)
    {
        for (var waited = 0; waited < 5000; waited += 10)
        {
            lock (_logs)
                if (_logs.Any(l => l.Contains(text)))
                    return;
            await Task.Delay(10);
        }
        Assert.Fail($"no log line containing '{text}'");
    }

    private async Task<IAppView> CreateViewAsync(AppEntry entry)
    {
        _calls.Add("create " + entry.Id);
        _entries.Add(entry);
        if (_createGate is not null)
            await _createGate;
        var view = new FakeAppView(entry.Id, _calls);
        if (!_made.TryGetValue(entry.Id, out var list))
            _made[entry.Id] = list = new();
        list.Add(view);
        return view;
    }

    // The latest view made for the app.
    private FakeAppView View(string id) => _made[id][^1];

    [Fact]
    public async Task Open_First_CreatesAndShows_HidesHome()
    {
        var sw = Switcher();

        await sw.OpenAsync("youtube");

        Assert.Equal(["create youtube", "hideHome", "youtube.show"], _calls);
        Assert.Equal("youtube", sw.CurrentAppId);
        Assert.Same(View("youtube"), Assert.Single(sw.Views));
    }

    [Fact]
    public async Task RestoreFocus_AppInFront_FocusesThatView()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        await sw.OpenAsync("disney");
        _calls.Clear();

        sw.RestoreFocus();

        Assert.Equal(["disney.restoreFocus"], _calls);
    }

    [Fact]
    public async Task RestoreFocus_OnHome_DoesNothing()
    {
        var sw = Switcher();
        sw.RestoreFocus();
        await sw.OpenAsync("disney");
        await sw.GoHomeAsync();
        _calls.Clear();

        sw.RestoreFocus();

        Assert.Empty(_calls);
    }

    [Fact]
    public async Task RestoreFocus_DeadOrFailedView_DoesNothing()
    {
        var sw = Switcher();
        await sw.OpenAsync("disney");
        _calls.Clear();

        // The "Couldn't open" message is in front, not the page; a dead view has no page to focus.
        View("disney").LoadFailed = true;
        sw.RestoreFocus();
        View("disney").LoadFailed = false;
        View("disney").IsAlive = false;
        sw.RestoreFocus();

        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Open_Again_ReusesView()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        await sw.GoHomeAsync();
        _calls.Clear();

        await sw.OpenAsync("youtube");

        Assert.Single(_made["youtube"]);
        Assert.Single(sw.Views);
        Assert.Equal(["hideHome", "youtube.show"], _calls);
        Assert.Equal("youtube", sw.CurrentAppId);
    }

    [Fact]
    public async Task Open_SameApp_TouchesNothing()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").Playing = true;
        _calls.Clear();

        await sw.OpenAsync("youtube");

        Assert.Empty(_calls);
        Assert.Equal(0, View("youtube").IsPlayingReads);
        Assert.True(View("youtube").Playing);
        Assert.Equal("youtube", sw.CurrentAppId);
    }

    [Fact]
    public async Task Open_Other_PausesHidesThenShows()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").Playing = true;
        _calls.Clear();

        await sw.OpenAsync("disney");

        Assert.Equal(["youtube.key Space", "youtube.hide", "create disney", "hideHome", "disney.show"], _calls);
        Assert.Contains("pause youtube: PausedBySpace", _logs);
        Assert.False(View("youtube").Playing);
        Assert.Equal("disney", sw.CurrentAppId);
        Assert.Equal(2, sw.Views.Count);
    }

    [Fact]
    public async Task Open_Other_PausesChannel4WithItsPauseButton()
    {
        // Channel 4's website version (its TV version pauses by Space), as a single-version app of its own.
        var sw = Switcher(config: ConfigWithApps("""
            [ { "Id": "channel4", "Url": "https://www.channel4.com", "PauseButton": "Pause" },
              { "Id": "youtube", "Url": "https://www.youtube.com/tv" } ]
            """));
        await sw.OpenAsync("channel4");
        View("channel4").Playing = true;
        _calls.Clear();

        await sw.OpenAsync("youtube");

        Assert.Equal(["channel4.button Pause", "channel4.hide", "create youtube", "hideHome", "youtube.show"], _calls);
        Assert.Contains("pause channel4: PausedByButton", _logs);
        Assert.False(View("channel4").Playing);
    }

    [Theory]
    [InlineData("youtube")]
    [InlineData("disney")]
    public async Task Return_NeverPresses_OrPlays(string id)
    {
        var sw = Switcher();
        await sw.OpenAsync(id);
        View(id).Playing = true;
        await sw.GoHomeAsync();
        _calls.Clear();

        await sw.OpenAsync(id);

        Assert.Equal(["hideHome", id + ".show"], _calls);
        Assert.DoesNotContain(_calls, c => c.Contains(".key") || c.Contains(".directPause"));
        Assert.False(View(id).Playing);
    }

    [Fact]
    public async Task Leave_NothingPlaying_SendsNoKey()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        _calls.Clear();

        await sw.GoHomeAsync();

        Assert.Equal(["youtube.hide", "showHome"], _calls);
        Assert.Contains("pause youtube: NothingPlaying", _logs);
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task Home_PauseThrows_StillShowsHome()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").IsPlayingThrows = new InvalidOperationException("page gone");
        _calls.Clear();

        await sw.GoHomeAsync();

        Assert.Equal(["youtube.hide", "showHome"], _calls);
        Assert.Contains(_logs, l => l.Contains("pause youtube failed") && l.Contains("page gone"));
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task Home_HideThrows_StillShowsHome()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").HideThrows = new InvalidOperationException("controller closed");
        _calls.Clear();

        await sw.GoHomeAsync();

        Assert.Equal(["youtube.hide", "showHome"], _calls);
        Assert.Contains(_logs, l => l.Contains("hide youtube failed") && l.Contains("controller closed"));
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task Open_Other_HideThrows_StillShowsOther()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").HideThrows = new InvalidOperationException("controller closed");
        _calls.Clear();

        await sw.OpenAsync("disney");

        Assert.Equal(["youtube.hide", "create disney", "hideHome", "disney.show"], _calls);
        Assert.Contains(_logs, l => l.Contains("hide youtube failed"));
        Assert.Equal("disney", sw.CurrentAppId);
    }

    [Fact]
    public async Task Home_PauseHangs_ShowsHomeAfterTimeout()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").IsPlayingHangs = new TaskCompletionSource<bool>();
        _calls.Clear();

        var home = sw.GoHomeAsync();
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(50);
        Assert.False(home.IsCompleted);
        Assert.Empty(_calls);

        _time.Advance(TimeSpan.FromMilliseconds(2));
        await home.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["youtube.hide", "showHome"], _calls);
        Assert.Contains("pause youtube: timed out", _logs);
        Assert.Null(sw.CurrentAppId);

        // The switcher is free again: the app opens, with the same view.
        await sw.OpenAsync("youtube").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("youtube", sw.CurrentAppId);
        Assert.Single(_made["youtube"]);
    }

    [Fact]
    public async Task Open_Other_PauseHangs_StillShowsOther()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").IsPlayingHangs = new TaskCompletionSource<bool>();
        _calls.Clear();

        var open = sw.OpenAsync("disney");
        _time.Advance(AppSwitcher.DefaultPauseTimeout);
        await open.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["youtube.hide", "create disney", "hideHome", "disney.show"], _calls);
        Assert.Contains("pause youtube: timed out", _logs);
        Assert.Equal("disney", sw.CurrentAppId);
    }

    [Fact]
    public async Task PauseTimeout_IsInjectable()
    {
        var sw = Switcher(pauseTimeout: TimeSpan.FromMilliseconds(300));
        await sw.OpenAsync("youtube");
        View("youtube").IsPlayingHangs = new TaskCompletionSource<bool>();
        _calls.Clear();

        var home = sw.GoHomeAsync();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        await home.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["youtube.hide", "showHome"], _calls);
    }

    [Fact]
    public async Task PauseHangsThenFails_FailureLoggedAndObserved()
    {
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            lock (unobserved)
                unobserved.Add(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            var sw = Switcher();
            await sw.OpenAsync("youtube");
            var hung = new TaskCompletionSource<bool>();
            View("youtube").IsPlayingHangs = hung;
            var home = sw.GoHomeAsync();
            _time.Advance(AppSwitcher.DefaultPauseTimeout);
            await home.WaitAsync(TimeSpan.FromSeconds(5));

            hung.SetException(new InvalidOperationException("renderer gone"));
            await WaitForLogAsync("pause youtube failed after timing out: renderer gone");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobserved)
                Assert.DoesNotContain(unobserved, e => e.ToString().Contains("renderer gone"));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    [Theory]
    [InlineData("disney")]
    [InlineData("youtube")]
    public async Task Home_PauseHangsThenReportsPlaying_SendsNoKey(string id)
    {
        // A stale "playing" answer after the limit must not press Space (a toggle) on a video that has paused since.
        var sw = Switcher();
        await sw.OpenAsync(id);
        var hung = new TaskCompletionSource<bool>();
        View(id).IsPlayingHangs = hung;
        View(id).Playing = true;
        var home = sw.GoHomeAsync();
        _time.Advance(AppSwitcher.DefaultPauseTimeout);
        await home.WaitAsync(TimeSpan.FromSeconds(5));
        _calls.Clear();

        hung.SetResult(true);
        await WaitForLogAsync($"pause {id}: stopped after timing out");

        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Open_LoadFailed_ReloadsOnNextOpen()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").LoadFailed = true;
        await sw.GoHomeAsync();
        _calls.Clear();

        await sw.OpenAsync("youtube");

        Assert.Equal(["youtube.reload", "hideHome", "youtube.show"], _calls);
        Assert.Single(_made["youtube"]);
    }

    [Fact]
    public async Task Open_DeadView_CreatesNewView()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        await sw.GoHomeAsync();
        var dead = View("youtube");
        dead.IsAlive = false;
        _calls.Clear();

        await sw.OpenAsync("youtube");

        Assert.Equal(["create youtube", "hideHome", "youtube.show"], _calls);
        Assert.Equal(2, _made["youtube"].Count);
        Assert.Same(View("youtube"), Assert.Single(sw.Views));
        Assert.NotSame(dead, View("youtube"));
    }

    [Fact]
    public async Task HomeWhileCreating_OneViewAndHomeShown()
    {
        var sw = Switcher();
        var gate = new TaskCompletionSource();
        _createGate = gate.Task;

        var open = sw.OpenAsync("youtube");
        var home = sw.GoHomeAsync();
        var openAgain = sw.OpenAsync("youtube");
        Assert.False(home.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(open, home, openAgain);

        Assert.Single(_made["youtube"]);
        Assert.Equal(
            ["create youtube", "hideHome", "youtube.show", "youtube.hide", "showHome", "hideHome", "youtube.show"],
            _calls);
        Assert.Equal("youtube", sw.CurrentAppId);
    }

    [Fact]
    public async Task CloseRequested_GoesHome()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        _calls.Clear();

        View("youtube").RequestClose();
        await _homeShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("youtube.hide", _calls[0]);
        Assert.Equal("showHome", _calls[1]);
        Assert.Null(sw.CurrentAppId);
    }

    // Channel 4's TV app answers its own "leave Channel 4? Yes" by emptying its page and failing to exit, which left a
    // dark page that the next open showed again (owner, 2026-11-02). A page that asked to close is gone for good: the
    // view is closed after home shows, so the next open loads the app afresh in the same profile.
    [Fact]
    public async Task CloseRequested_ClosesTheView_AndTheNextOpenMakesANewOne()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        _calls.Clear();

        View("youtube").RequestClose();
        await WaitForLogAsync("view youtube: closed after its page asked to close");

        Assert.Equal(["youtube.hide", "showHome", "youtube.close"], _calls);
        Assert.Empty(sw.Views);

        _calls.Clear();
        await sw.OpenAsync("youtube");
        Assert.Equal(["create youtube", "hideHome", "youtube.show"], _calls);
        Assert.Equal(2, _made["youtube"].Count);
    }

    [Fact]
    public async Task CloseRequested_FromAViewNotInFront_IsIgnored()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        await sw.OpenAsync("disney");
        _calls.Clear();

        View("youtube").RequestClose();
        await sw.GoHomeAsync();   // queued behind anything the close started

        Assert.Equal(["disney.hide", "showHome"], _calls);
    }

    [Fact]
    public async Task Disney_SpaceFails_NoDirectPause()
    {
        var sw = Switcher();
        await sw.OpenAsync("disney");
        View("disney").Playing = true;
        View("disney").SpacePauses = false;
        _calls.Clear();

        await sw.GoHomeAsync();

        Assert.Equal(["disney.key Space", "disney.hide", "showHome"], _calls);
        Assert.Contains("pause disney: StillPlaying", _logs);
    }

    [Fact]
    public async Task Youtube_SpaceFails_PausesDirectly()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").Playing = true;
        View("youtube").SpacePauses = false;
        _calls.Clear();

        await sw.GoHomeAsync();

        Assert.Equal(["youtube.key Space", "youtube.directPause", "youtube.hide", "showHome"], _calls);
        Assert.Contains("pause youtube: PausedDirectly", _logs);
    }

    [Fact]
    public async Task CloseApp_WithAView_ClosesItAndTheNextOpenMakesANewOne()
    {
        var version = "tv";
        var sw = Switcher(chosen: _ => Channel4(version));
        await sw.OpenAsync("channel4");
        await sw.GoHomeAsync();
        _calls.Clear();

        await sw.CloseAppAsync("channel4");

        Assert.Equal("channel4.close", _calls[^1]);
        Assert.Empty(sw.Views);
        Assert.Contains("view channel4: closed", _logs);

        version = "web";
        await sw.OpenAsync("channel4");

        Assert.Equal(["create channel4", "hideHome", "channel4.show"], _calls.Skip(_calls.IndexOf("channel4.close") + 1));
        Assert.Equal(2, _made["channel4"].Count);
        Assert.Equal(["channel4-tv", "channel4-web"], _entries.Select(e => e.ProfileId));
        Assert.Same(View("channel4"), Assert.Single(sw.Views));
    }

    [Fact]
    public async Task CloseApp_InFront_GoesHomeFirst()
    {
        var sw = Switcher(chosen: _ => Channel4("tv"));
        await sw.OpenAsync("channel4");
        View("channel4").Playing = true;
        _calls.Clear();

        await sw.CloseAppAsync("channel4");

        Assert.Equal(["channel4.key Space", "channel4.hide", "showHome", "channel4.close"], _calls);
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task CloseApp_NoView_DoesNothing()
    {
        var sw = Switcher(chosen: _ => Channel4("tv"));

        await sw.CloseAppAsync("channel4");

        Assert.Empty(_calls);
        Assert.DoesNotContain(_logs, l => l.Contains("closed"));
    }

    [Fact]
    public async Task CloseApp_AfterTheChosenVersionChanged_PausesAsTheVersionTheViewWasMadeWith()
    {
        var version = "tv";
        var sw = Switcher(chosen: _ => Channel4(version));
        await sw.OpenAsync("channel4");
        View("channel4").Playing = true;
        version = "web";
        _calls.Clear();

        await sw.CloseAppAsync("channel4");

        Assert.Equal(["channel4.key Space", "channel4.hide", "showHome", "channel4.close"], _calls);
    }

    [Fact]
    public async Task Back_AfterTheChosenVersionChanged_FollowsTheVersionTheViewWasMadeWith()
    {
        var version = "tv";
        var sw = Switcher(chosen: _ => Channel4(version));
        await sw.OpenAsync("channel4");
        View("channel4").CanGoBack = true;
        version = "web";
        _calls.Clear();

        await sw.BackAsync();

        Assert.Equal(["channel4.key Escape"], _calls);
    }

    [Theory]
    [InlineData("web", "channel4.goBack")]
    [InlineData("tv", "channel4.key Escape")]
    public async Task Back_FollowsTheChosenVersion(string version, string expected)
    {
        var sw = Switcher(chosen: _ => Channel4(version));
        await sw.OpenAsync("channel4");
        View("channel4").CanGoBack = true;
        _calls.Clear();

        await sw.BackAsync();

        Assert.Equal([expected], _calls);
    }

    [Fact]
    public async Task Back_NoApp_DoesNothing()
    {
        var sw = Switcher();

        await sw.BackAsync();

        Assert.Empty(_calls);
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task Back_History_CanGoBack_GoesBack()
    {
        var sw = Switcher();
        await sw.OpenAsync("disney");
        View("disney").CanGoBack = true;
        _calls.Clear();

        await sw.BackAsync();

        Assert.Equal(["disney.goBack"], _calls);
        Assert.Equal("disney", sw.CurrentAppId);
    }

    [Fact]
    public async Task Back_History_AtStart_GoesHome()
    {
        var sw = Switcher();
        await sw.OpenAsync("disney");
        View("disney").CanGoBack = false;
        _calls.Clear();

        await sw.BackAsync();

        Assert.Equal(["disney.hide", "showHome"], _calls);
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task Back_Page_SendsEscape_NeverHome()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        _calls.Clear();

        // Even with nothing to go back to, and however often: only Home leaves YouTube.
        await sw.BackAsync();
        View("youtube").CanGoBack = true;
        await sw.BackAsync();

        Assert.Equal(["youtube.key Escape", "youtube.key Escape"], _calls);
        Assert.Equal("youtube", sw.CurrentAppId);
    }

    [Fact]
    public async Task Back_Page_PressHangs_TimesOut_HomeStillWorks()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        View("youtube").PressKeyHangs = new TaskCompletionSource();
        _calls.Clear();

        var back = sw.BackAsync();
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(50);
        Assert.False(back.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(2));
        await back.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("back youtube: timed out", _logs);

        // The lock is free: Home is not held up by the hung page.
        await sw.GoHomeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["youtube.key Escape", "youtube.hide", "showHome"], _calls);
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task Back_Page_ThePageClosing_StillGoesHome()
    {
        var sw = Switcher();
        await sw.OpenAsync("youtube");
        _calls.Clear();

        View("youtube").RequestClose();
        await WaitForLogAsync("view youtube: closed after its page asked to close");

        Assert.Equal(["youtube.hide", "showHome", "youtube.close"], _calls);
        Assert.Null(sw.CurrentAppId);
    }

    [Fact]
    public async Task Home_FromHome_ShowsHomeAndTouchesNoView()
    {
        var sw = Switcher();

        await sw.GoHomeAsync();

        Assert.Equal(["showHome"], _calls);
    }

    [Fact]
    public async Task Open_UnknownApp_Throws()
    {
        var sw = Switcher();

        await Assert.ThrowsAsync<ArgumentException>(() => sw.OpenAsync("nope"));
        Assert.Empty(_calls);
    }
}
