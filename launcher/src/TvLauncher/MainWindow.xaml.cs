using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TvLauncher.Apps;
using TvLauncher.Backgrounds;
using TvLauncher.Bridge;
using TvLauncher.Config;
using TvLauncher.Diagnostics;
using TvLauncher.Input;
using TvLauncher.Remote;
using TvLauncher.SystemActions;
using TvLauncher.Weather;

namespace TvLauncher;

/// <summary>
/// The full-screen launcher window: the home page in WebView2, the app views stacked above it, the Home key,
/// and the Spotlight photos. Topmost whenever it is shown; only the desktop button hides it, and Home brings it back.
/// </summary>
public partial class MainWindow : Window
{
    private const string HomeHost = "tvhome.test";
    private const string PhotosHost = "photos.test";
    // Under --dev only, app n (0-based in Apps) opens remote debugging port 9300 + n for measuring.
    private const int DevPortBase = 9300;
    private static readonly TimeSpan PhotoRefreshEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FocusCheckEvery = TimeSpan.FromMilliseconds(250);

    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TvLauncher");
    private static readonly string PhotosFolder = Path.Combine(DataFolder, "photos");

    private readonly LauncherConfig _config;
    private readonly TimingLog _timing;
    private readonly bool _dev;
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();

    /// <summary>How many key-downs after the start the log records, with which window had the keyboard (KeyboardHook.ReportKeys).</summary>
    private const int KeysToReport = 10;
    private readonly bool _allowClose;
    private readonly Win32WindowSystem _windows;
    private WebView2? _webView;
    private LauncherState _state = new(BoostOn: true);
    // The home row as the family arranged it: the host's single source of truth, saved in state.json on every change.
    private AppLayout _layout;
    private AppSwitcher? _switcher;
    private HostBridge? _bridge;
    private LocationCoordinator? _location;
    private KeyboardHook? _hook;
    private RemoteServer? _remote;
    private PhoneMicSession? _mic;
    private IKeyPresser? _presser;
    private DispatcherTimer? _photoTimer;
    private DispatcherTimer? _focusTimer;
    private IReadOnlyList<string>? _photoUrls;
    private bool _pageReady;
    private bool _exiting;
    private bool _recovering;
    private readonly RetryBudget _webViewRecovery = new(RetryBudget.WebViewRecoveryTries, RetryBudget.WebViewRecoveryWindow);

    /// <summary>Exit code when WebView2 could not be recovered in-process; the task's watchdog restarts the launcher.</summary>
    private const int ExitWebViewLost = 3;

    public MainWindow(LauncherConfig config, TimingLog timing, bool dev, bool keyTest)
    {
        InitializeComponent();
        _config = config;
        _layout = AppLayout.Resolve(config.Apps, null);
        _timing = timing;
        _dev = dev;
        _windows = new Win32WindowSystem(Log);
        // The family must never close the home screen by accident (Alt+F4), so only --dev, the key test (closed when
        // done) or a config with AllowClose (the owner's own PC) allow it.
        _allowClose = dev || keyTest || config.AllowClose;
        Closing += OnClosing;
        Closed += (_, _) => Stop();

        if (keyTest)
        {
            ShowMessage("Press a key on the remote");
            // A window hook sees the raw codes in the messages before WPF turns them into its own Key values.
            SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeyTestHook);
        }
        else
        {
            // Started by something other than the viewer (the deploy's scheduled task over SSH), the window opened
            // behind the taskbar until clicked (owner, 2026-11-02; reproduced on the dev PC: started from a background
            // shell, another window kept the foreground). Show() alone never asks for the front; Home's Activate does.
            Loaded += async (_, _) =>
            {
                KeepAskingForFront();
                await StartAsync();
            };
        }
    }

    private async Task StartAsync()
    {
        // The Home key first: if WebView2 cannot start, Home must still bring this window back.
        try
        {
            _state = LauncherState.Load(LauncherState.DefaultPath, Log);
            _layout = AppLayout.Resolve(_config.Apps, _state.Apps);
            // The saved list may name apps no longer in launcher.json; keeping only the resolved choices drops them on the next save.
            _state = _state with { Apps = _layout.Choices };
            _switcher = new AppSwitcher(_config, CreateViewAsync, ShowHome, HideHome, Log, chosen: id => _layout.Chosen(id));
            _bridge = new HostBridge(_switcher, new PowerActions(this, _timing, _dev), _timing);
            _bridge.PageReady += OnPageReady;
            _bridge.BoostToggled += OnBoostToggled;
            _bridge.AppMoveRequested += (id, to) => OnAppsRequest(id, _layout.Move(id, to), closeView: false);
            _bridge.AppShowRequested += (id, on) => OnAppsRequest(id, _layout.Show(id, on), closeView: !on);
            _bridge.AppVersionRequested += OnAppVersionRequested;
            _hook = new KeyboardHook(_config.HomeKeyVks, _config.BackKeyVk, _config.SwallowKeyVks);
            _hook.HomePressed += OnHomePressed;
            _hook.BackPressed += OnBackPressed;
            _hook.KeyReported += (vk, injected) => Log(
                $"key: VK {vk}{(injected ? " injected" : "")} {_sinceStart.Elapsed.TotalSeconds:0.0} s after the start; "
                + _windows.Keyboard(new WindowInteropHelper(this).Handle));
            _hook.ReportKeys(KeysToReport);
            // Hidden by the desktop button (PowerActions.ShowDesktop): the desktop and VLC keep the extra keys.
            IsVisibleChanged += (_, e) => _hook.LauncherInFront = (bool)e.NewValue;
            var location = _location = new LocationCoordinator(
                new WindowsLocation(),
                _config.Location is { } place ? Coordinates.Rounded(place.Latitude, place.Longitude) : null,
                WindowsRegion.Code(),
                () => _state,
                SaveState,
                Post,
                Log);
            _bridge.PlacePicked += place => RunLocation(() => location.PickAsync(place));
            _bridge.WeatherOffChosen += () => RunLocation(() => location.ChooseOffAsync());
            _bridge.WindowsLocationChosen += () => RunLocation(() => location.ChooseWindowsAsync());
            _bridge.TurnOnRequested += OnTurnOnRequested;
        }
        catch (Exception ex)
        {
            Log("start failed: " + ex);
            ShowMessage("The home screen could not start.\n" + ex.Message);
            return;
        }

        StartRemote();
        await StartWebViewAsync();

        _photoTimer = new DispatcherTimer { Interval = PhotoRefreshEvery };
        _photoTimer.Tick += async (_, _) => await RefreshPhotosAsync();
        _photoTimer.Start();
        // An app's page can drop the keyboard focus (AppSwitcher.RestoreFocus); the remote has no mouse to give it back.
        _focusTimer = new DispatcherTimer { Interval = FocusCheckEvery };
        _focusTimer.Tick += (_, _) => CheckFocus();
        _focusTimer.Start();
        await RefreshPhotosAsync();
    }

    // Creates the WebView2 environment and control and opens the home page. Also used to recover after
    // the WebView2 browser process ended, which leaves the old control closed for good.
    private async Task<bool> StartWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataFolder, "WebView2"));
            var webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
            _webView = webView;
            Root.Children.Insert(0, webView);
            await webView.EnsureCoreWebView2Async(environment);
            var core = webView.CoreWebView2;

            PageSettings.For(_dev).ApplyTo(core.Settings);
            core.SetVirtualHostNameToFolderMapping(HomeHost, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
            Directory.CreateDirectory(PhotosFolder);
            // DenyCors: the home page may show the photos as images, nothing else may read them.
            core.SetVirtualHostNameToFolderMapping(PhotosHost, PhotosFolder, CoreWebView2HostResourceAccessKind.DenyCors);

            // The home page is the only page this window ever shows.
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) => e.Cancel = !IsHomePage(e.Uri);
            core.NavigationCompleted += (_, _) => webView.Focus();
            core.ProcessFailed += OnProcessFailed;
            core.WebMessageReceived += OnWebMessage;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(_config.ToPageConfigScript(_layout));

            core.Navigate($"https://{HomeHost}/index.html");
            Message.Visibility = Visibility.Collapsed;
            // Recreated while an app is in front: it stays out of the app's way until Home.
            webView.Visibility = _switcher?.CurrentAppId is null ? Visibility.Visible : Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            // Most likely the WebView2 runtime is missing or broken. Say so on the screen instead of showing black.
            Log("WebView2 start failed: " + ex);
            ShowMessage("The home screen could not start.\n" + ex.Message);
            return false;
        }
    }

    // The hook was created on the UI thread, so it raises this on the UI thread through the dispatcher.
    // The log line uses the time the hook saw the press, so the Home timings include the wait for this thread.
    private void OnHomePressed(object? sender, HomePressedEventArgs e)
    {
        _timing.MarkHomeKey(e.Timestamp);
        GoHome();
    }

    // Raised only while an app is in front (the hook swallows Back then); on the home screen the page gets Back itself.
    private async void OnBackPressed(object? sender, EventArgs e)
    {
        try
        {
            await _switcher!.BackAsync();
        }
        catch (Exception ex)
        {
            Log("Back failed: " + ex.Message);
        }
    }

    private async void GoHome()
    {
        try
        {
            await _switcher!.GoHomeAsync();
        }
        catch (Exception ex)
        {
            Log("Home failed: " + ex.Message);
            ShowHome();
        }
    }

    // The phone remote's listener, started after the hook so a phone's Home or Back always finds the hook
    // in place. Without it (port in use, say) the launcher carries on with the Rii alone.
    private void StartRemote()
    {
        if (!_config.PhoneRemote.Enabled)
            return;
        var remote = new RemoteServer(_config.PhoneRemote.Port, Log);
        var mic = new PhoneMicSession(
            () => _switcher?.CurrentAppId,
            id => _switcher?.Views.FirstOrDefault(v => v.Id == id),
            MicAllowed,
            remote.SendAsync,
            TimeProvider.System,
            Log);
        // Every phone's microphone button follows: greyed while a session runs.
        mic.Changed += () => SendState(null);
        // The server raises these on its own threads and its DisposeAsync waits for those, so the work is
        // queued to this thread (InvokeAsync), never waited on (Invoke would deadlock a dispose from here).
        remote.MessageReceived += (connection, message) =>
        {
            switch (message)
            {
                case PressMessage press:
                    Dispatcher.InvokeAsync(() => OnRemotePress(press.Key));
                    break;
                case MicStartMessage:
                    Dispatcher.InvokeAsync(() => mic.StartAsync(connection));
                    break;
                case MicStopMessage:
                    Dispatcher.InvokeAsync(() => mic.StopAsync(connection));
                    break;
            }
        };
        remote.AudioReceived += (connection, pcm) => Dispatcher.InvokeAsync(() => mic.Audio(connection, pcm));
        remote.Connected += connection => Dispatcher.InvokeAsync(() => SendState(connection));
        remote.Disconnected += connection => Dispatcher.InvokeAsync(() => mic.OnDisconnected(connection));
        try
        {
            remote.Start();
        }
        catch (Exception ex)
        {
            Log($"remote: could not listen on port {_config.PhoneRemote.Port}: {ex.Message}");
            _ = remote.DisposeAsync().AsTask();
            return;
        }
        _presser = new SendInputKeyPresser(Log);
        _mic = mic;
        _remote = remote;
        Log($"remote: listening on port {remote.Port}");
    }

    // The Windows key the Rii sends, so the hook, Back and Home and the app in front treat it as the Rii's.
    private void OnRemotePress(RemoteKey key)
    {
        try
        {
            _presser?.Press(RemoteKeys.VirtualKey(key, _config));
        }
        catch (Exception ex)
        {
            Log($"remote: pressing {key} failed: {ex.Message}");
        }
    }

    private bool MicAllowed(string appId) => _config.Apps.Any(a => a.Id == appId) && _layout.Chosen(appId).PhoneMic;

    // A voice search ends when its app is no longer in front; then every phone hears what is.
    private void OnAppChanged()
    {
        _mic?.OnAppChanged();
        SendState(null);
    }

    // Tells one phone, or every phone when null, which app is in front and whether its mic button works: only
    // in an app with PhoneMic, and only while no voice search runs (one at a time).
    private async void SendState(RemoteConnection? to)
    {
        if (_remote is not { } remote)
            return;
        try
        {
            var id = _switcher?.CurrentAppId;
            var mic = id is not null && MicAllowed(id) && _mic is { IsIdle: true };
            var state = RemoteProtocol.State(id, mic);
            await (to is null ? remote.BroadcastAsync(state) : remote.SendAsync(to, state));
        }
        catch (Exception ex)
        {
            Log("remote: sending the state failed: " + ex.Message);
        }
    }

    // Each app's view, made on its first pick, above the home page in this window.
    private async Task<IAppView> CreateViewAsync(AppEntry entry)
    {
        var gain = entry.BoostGain is { } boost && _state.BoostOn ? boost : 1;
        var index = _config.Apps.ToList().FindIndex(a => a.Id == entry.Id);
        var view = await WebAppView.CreateAsync(entry, Root, _dev, DevPortBase + index, gain, Log);
        view.PhoneMicStopped += () => _mic?.OnPageStopped();
        return view;
    }

    // Called by AppSwitcher once the app in front is hidden. The window may be hidden (desktop button)
    // or minimised (Windows+D on a keyboard).
    private void ShowHome()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowHome);
            return;
        }

        if (_hook is not null)
            _hook.AppInFront = false;
        Topmost = true;
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Maximized;
        _windows.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        // Not over the message that says the home page could not start.
        if (_webView is not null && Message.Visibility != Visibility.Visible)
            _webView.Visibility = Visibility.Visible;
        _webView?.Focus();
        Post(new { type = "home" });
        // Back from Windows' settings (Home): read Windows again and tell the page how the turn on went.
        if (_location is { TurnOnPending: true } location)
            RunLocation(location.FinishTurnOnAsync);
        OnAppChanged();
    }

    // Called by AppSwitcher before it shows an app: a WebView2 is a window of its own, so the home page is
    // collapsed rather than left under the app.
    private void HideHome()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(HideHome);
            return;
        }

        if (_hook is not null)
            _hook.AppInFront = true;
        if (_webView is not null)
            _webView.Visibility = Visibility.Collapsed;
        // AppSwitcher.OpenAsync names the app in front only after this returns, in the same run on this
        // thread, so the state goes once that run is done.
        Dispatcher.InvokeAsync(OnAppChanged);
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsHomePage(e.Source) || _bridge is null)
            return;
        await _bridge.HandleAsync(e.WebMessageAsJson);
    }

    // Asks for the front at the start and, while Windows refuses (during the sign-in at a boot), again every
    // FrontAtStart.AskEvery. One line in the log says how it went.
    private void KeepAskingForFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var front = new FrontAtStart();
        string? firstProblem = null, lastProblem = null;
        var timer = new DispatcherTimer { Interval = FrontAtStart.AskEvery };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();

        void Tick()
        {
            switch (front.Next(_windows.InFront(hwnd), IsVisible))
            {
                case FrontStep.Ask:
                    lastProblem = _windows.TryActivate(hwnd);
                    firstProblem ??= lastProblem;
                    return;
                case FrontStep.Wait:
                    return;
                case FrontStep.Held:
                    Log($"front: in front {front.FirstInFrontAfter?.TotalSeconds:0.0} s after the start, {front.Asks} ask(s)"
                        + (firstProblem is null ? "" : $"; first refusal: {firstProblem}"));
                    break;
                case FrontStep.GiveUp:
                    Log($"front: not in front {front.Elapsed.TotalSeconds:0} s after the start, {front.Asks} ask(s); last: {lastProblem}");
                    break;
                case FrontStep.SteppedAside:
                    Log($"front: stepped aside {front.Elapsed.TotalSeconds:0.0} s after the start, {front.Asks} ask(s)");
                    break;
            }
            timer.Stop();
        }
    }

    // Only while this window is the active one: it never takes the keyboard from another program.
    private void CheckFocus()
    {
        if (!IsActive || _switcher?.CurrentAppId is not { } id || !_windows.KeyboardFocusLost())
            return;
        Log($"focus: no window had the keyboard, given back to {id}");
        _switcher.RestoreFocus();
    }

    private void OnPageReady()
    {
        _pageReady = true;
        _webView?.Focus();
        PostBoost();
        PostApps();
        PostPhotos();
        if (_location is { } location)
            RunLocation(location.RefreshAsync);
    }

    // Keeps _state in step with what the coordinator changed, then writes it. A failed save is logged by Save.
    private void SaveState(LauncherState next)
    {
        _state = next;
        _state.Save(LauncherState.DefaultPath, Log);
    }

    // The "Turn on Windows location" button: open Windows' location settings and step aside as the desktop button
    // does; the viewer comes back with Home (ShowHome finishes the turn on). If Settings cannot be started the
    // launcher stays in front and finishes at once, so the page still hears that location is off.
    private void OnTurnOnRequested()
    {
        if (_location is not { } location)
            return;
        location.BeginTurnOn();
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("location: could not open settings: " + ex.GetType().Name);
            RunLocation(location.FinishTurnOnAsync);
            return;
        }
        Topmost = false;
        Hide();
    }

    // The coordinator's calls come from WebView2 events, where an exception would end the launcher.
    private async void RunLocation(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log("location failed: " + ex.GetType().Name);
        }
    }

    // The boost button: flip and remember the choice, tell the page, then change the apps already open.
    // Apps made later start from the saved state in CreateViewAsync. A failed save is logged by Save and
    // the toggle still holds for this session.
    private async void OnBoostToggled()
    {
        try
        {
            _state = _state with { BoostOn = !_state.BoostOn };
            _state.Save(LauncherState.DefaultPath, Log);
            PostBoost();
            foreach (var view in _switcher!.Views)
            {
                if (_layout.Chosen(view.Id) is not { BoostGain: { } boost })
                    continue;
                try
                {
                    await view.SetBoostGainAsync(_state.BoostOn ? boost : 1);
                }
                catch (Exception ex)
                {
                    Log($"view {view.Id}: changing the boost failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log("Boost toggle failed: " + ex.Message);
        }
    }

    private void PostBoost() => Post(new { type = "boost", on = _state.BoostOn });

    // The page redraws its home row and the boost label from this, after every request, refused or not.
    private void PostApps()
    {
        var page = JsonSerializer.SerializeToElement(_layout.ToPage());
        Post(new { type = "apps", apps = page.GetProperty("apps"), boostApps = page.GetProperty("boostApps") });
    }

    // A version already chosen changes nothing, so its view is left open.
    private void OnAppVersionRequested(string id, string version)
    {
        var next = _layout.ChooseVersion(id, version);
        OnAppsRequest(id, next, closeView: next is not null && next.Chosen(id).VersionId != _layout.Chosen(id).VersionId);
    }

    // One request from the Apps screen: next is the layout it asks for, or null when it was refused (the last shown app
    // cannot be hidden) or the id or version is unknown. A hidden app or one switched to another version loses its view
    // (paused first, as when leaving) and the new version's view is made the next time the tile is opened. The page is
    // always told the truth afterwards, so a refused request redraws it as it was.
    private async void OnAppsRequest(string id, AppLayout? next, bool closeView)
    {
        try
        {
            try
            {
                if (next is not null)
                {
                    _layout = next;
                    SaveState(_state with { Apps = next.Choices });
                    if (closeView)
                        await _switcher!.CloseAppAsync(id);
                }
            }
            catch (Exception ex)
            {
                Log($"Apps change for {id} failed: {ex.Message}");
            }
            PostApps();
        }
        catch (Exception ex)
        {
            Log($"Telling the page about the apps failed: {ex.Message}");
        }
    }

    private async Task RefreshPhotosAsync()
    {
        try
        {
            var folders = _config.SpotlightFolders;
            var names = await Task.Run(() => SpotlightLibrary.Refresh(folders, PhotosFolder));
            _photoUrls = names.Select(n => $"https://{PhotosHost}/{Uri.EscapeDataString(n)}").ToList();
            _timing.Mark($"photos {_photoUrls.Count}");
            PostPhotos();
        }
        catch (Exception ex)
        {
            Log("Spotlight refresh failed: " + ex.Message);
        }
    }

    // Sent once the page is listening and the photo list is known, whichever comes last, and after each refresh.
    private void PostPhotos()
    {
        if (_pageReady && _photoUrls is not null)
            Post(new { type = "backgrounds", urls = _photoUrls });
    }

    private void Post(object message)
    {
        try
        {
            _webView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch (Exception ex)
        {
            Log("Posting to the page failed: " + ex.Message);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log($"WebView2 process failed: {e.ProcessFailedKind} {e.Reason}");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            _pageReady = false;
            _webView?.CoreWebView2?.Reload();
        }
        else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The control is closed for good. Rebuild it after this event handler has returned.
            _pageReady = false;
            Dispatcher.InvokeAsync(RecoverWebViewAsync);
        }
    }

    // Recreates WebView2 in this process, up to 3 times in 5 minutes. After that the launcher exits with a
    // non-zero code and the scheduled task's watchdog starts a fresh one within a minute. The Home key hook
    // lives outside WebView2 and keeps working throughout.
    private async Task RecoverWebViewAsync()
    {
        if (_recovering)
            return;   // a recovery is already under way, and it starts from scratch anyway
        _recovering = true;
        try
        {
            await RecoverWebViewLoopAsync();
        }
        finally
        {
            _recovering = false;
        }
    }

    private async Task RecoverWebViewLoopAsync()
    {
        while (true)
        {
            if (!_webViewRecovery.TryUse())
            {
                Log($"WebView2 browser process ended again: {RetryBudget.WebViewRecoveryTries} tries in "
                    + $"{RetryBudget.WebViewRecoveryWindow.TotalMinutes:0} minutes used, exiting so the task starts a new launcher");
                _exiting = true;
                Application.Current.Shutdown(ExitWebViewLost);
                return;
            }
            Log($"WebView2 browser process ended: recreating it (try {_webViewRecovery.Used} of {RetryBudget.WebViewRecoveryTries})");
            DropWebView();
            if (await StartWebViewAsync())
            {
                Log("WebView2 recreated");
                return;
            }
        }
    }

    private void DropWebView()
    {
        if (_webView is not { } old)
            return;
        _webView = null;
        Root.Children.Remove(old);
        try
        {
            old.Dispose();
        }
        catch (Exception ex)
        {
            Log("Disposing the old WebView2 failed: " + ex.Message);
        }
    }

    private IntPtr KeyTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var (text, stop) = KeyTestMessages.Describe(msg, wParam, lParam);
        if (text is null)
            return IntPtr.Zero;
        ShowMessage(text);
        _timing.Mark("keytest " + text.Replace('\n', ' '));
        handled = stop;
        // A handled WM_APPCOMMAND returns TRUE, so it is not passed on to the shell.
        return msg == KeyTestMessages.WM_APPCOMMAND && stop ? new IntPtr(1) : IntPtr.Zero;
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
        if (_webView is not null)
            _webView.Visibility = Visibility.Collapsed;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && !_exiting)
        {
            e.Cancel = true;
            Log("close ignored (start with --dev, or set AllowClose in launcher.json, to allow it)");
        }
    }

    private void Stop()
    {
        _timing.Mark("stop");
        _photoTimer?.Stop();
        _focusTimer?.Stop();
        // The phone remote goes before the hook: a press already queued to this thread then finds no presser,
        // rather than reaching the shell with no hook (the Rii's Home is Browser Home, which opens a browser).
        _presser = null;
        // Not waited for: the listener frees its port before DisposeAsync first waits, and waiting here, on the
        // thread its handlers queue work to, could hang the close. The connections end with the process.
        _ = _remote?.DisposeAsync().AsTask();
        _hook?.Dispose();
        _webView?.Dispose();
    }

    private static bool IsHomePage(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == HomeHost;

    private void Log(string message) => _timing.Mark("log " + message);
}
