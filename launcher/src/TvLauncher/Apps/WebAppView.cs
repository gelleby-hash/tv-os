using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TvLauncher.Config;

namespace TvLauncher.Apps;

/// <summary>
/// An app's web page in its own WebView2, stacked in the launcher's window above the home screen. Each app has
/// its own environment: its own profile and sign-in, browser flags and browser processes, so one app's crash
/// takes down neither the home screen nor the other app. Used on the UI thread only.
/// </summary>
public sealed class WebAppView : IAppView
{
    /// <summary>The longest a WebView2 start may take. Opening an app holds the switcher meanwhile, Home included.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The page's end of the phone's sound (<see cref="PageScripts.PhoneMic"/>): the stream YouTube has ends.</summary>
    internal const string PhoneMicEndMessage = """{"tvMic":"end"}""";

    // Arms PageScripts.PhoneMic for YouTube's next audio request; "true" only when the script is in the page.
    private const string ArmPhoneMic = "window[Symbol.for('tvMic.arm')]?.()";

    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TvLauncher");

    private readonly AppEntry _entry;
    private readonly Panel _host;
    private readonly Action<string> _log;
    private readonly Grid _root;
    private readonly WebView2 _webView;
    private readonly TextBlock _failure;
    private readonly SemaphoreSlim _boostGate = new(1, 1);
    private CoreWebView2? _core;
    private string? _boostScriptId;
    private bool _dropped;

    private WebAppView(AppEntry entry, Panel host, Action<string> log)
    {
        _entry = entry;
        _host = host;
        _log = log;
        _webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
        _failure = new TextBlock
        {
            Text = $"Couldn't open {entry.Name}. Press Home to go back.",
            Visibility = Visibility.Collapsed,
            Foreground = Brushes.White,
            FontSize = 96,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _root = new Grid { Background = Brushes.Black, Children = { _webView, _failure } };
        // Added on screen, above the home screen: the switcher shows it as soon as it has started. A WebView2 is
        // a Win32 window inside WPF, and this does not rely on a hidden one starting.
        host.Children.Add(_root);
    }

    public string Id => _entry.Id;

    public bool IsAlive { get; private set; } = true;

    public bool LoadFailed { get; private set; }

    public bool CanGoBack => IsAlive && _core is { } core && core.CanGoBack;

    public event Action? CloseRequested;

    public event Action? PhoneMicStopped;

    /// <summary>
    /// Adds the app's view to <paramref name="host"/> and opens its page. Throws <see cref="TimeoutException"/> if
    /// WebView2 has not started within <see cref="StartTimeout"/>; on any failure the view is removed again.
    /// </summary>
    /// <param name="devPort">The remote debugging port this app opens under <paramref name="dev"/>; none otherwise.</param>
    /// <param name="boostGain">The starting gain of the volume boost, used only when the entry has a <c>BoostGain</c>.</param>
    public static async Task<WebAppView> CreateAsync(AppEntry entry, Panel host, bool dev, int devPort, double boostGain, Action<string> log)
    {
        var view = new WebAppView(entry, host, log);
        var start = view.StartAsync(dev, devPort, boostGain);
        try
        {
            await start.WaitAsync(StartTimeout);
            log($"view {entry.Id}: started");
            return view;
        }
        catch (TimeoutException) when (!start.IsCompleted)
        {
            // The start is abandoned. The control is disposed below, so it fails if it ever goes on; that is not news.
            _ = start.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            view.Drop();
            throw new TimeoutException($"{entry.Name} did not start within {StartTimeout.TotalSeconds:0} s.");
        }
        catch
        {
            view.Drop();
            throw;
        }
    }

    private async Task StartAsync(bool dev, int devPort, double boostGain)
    {
        var options = new CoreWebView2EnvironmentOptions(BrowserArguments(_entry.BrowserArgs, dev, devPort));
        var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder(_entry.ProfileId), options);
        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2;
        _core = core;

        if (_entry.UserAgent is { } userAgent)
            core.Settings.UserAgent = userAgent;
        PageSettings.For(dev).ApplyTo(core.Settings);
        // A page asking for full screen is left to fill this view: the window already fills the screen.
        core.NewWindowRequested += OnNewWindowRequested;
        core.WindowCloseRequested += OnWindowCloseRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.HideScrollbars);
        if (_entry.HideElements is { } hide)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.HideElements(hide));
        if (_entry.HidePlayReady)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.HidePlayReady);
        if (_entry.ExitWhenEmpty is { } root)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.ExitWhenEmpty(root));
        if (_entry.ArrowNav)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.ArrowNav);
        if (_entry.PhoneMic)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.PhoneMic);
        if (_entry.PhoneMic || _entry.ExitWhenEmpty is not null)
            core.WebMessageReceived += OnWebMessageReceived;
        if (_entry.BoostGain is not null)
            _boostScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.Boost(boostGain));

        core.Navigate(_entry.Url);
    }

    public async Task<bool> IsPlayingAsync() => await Core.ExecuteScriptAsync(PageScripts.IsPlaying) == "true";

    // In-process DevTools, no debugging port: the page sees a real key press, as from the remote.
    public async Task PressKeyAsync(AppKey key)
    {
        var core = Core;
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", KeyEvent(key, down: true));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", KeyEvent(key, down: false));
    }

    public Task PressButtonAsync(string label) => Core.ExecuteScriptAsync(PageScripts.PressButton(label));

    public Task DirectPauseAsync() => Core.ExecuteScriptAsync(PageScripts.DirectPause);

    // Armed first and the key pressed only once that has returned: the arming and the key take different routes
    // into the page, and YouTube asks for the microphone as soon as it has the key. Without the script (no
    // PhoneMic, or a page that is not YouTube's, such as a sign-in page) nothing is pressed.
    public async Task StartPhoneMicAsync()
    {
        var armed = await Core.ExecuteScriptAsync(ArmPhoneMic);
        if (armed != "true")
            throw new InvalidOperationException($"the {_entry.Name} page's microphone script did not answer (it returned {armed})");
        await PressKeyAsync(AppKey.Microphone);
    }

    public void PostPhoneMicAudio(ReadOnlyMemory<byte> pcm) => Core.PostWebMessageAsJson(PhoneMicAudioMessage(pcm.Span));

    public void EndPhoneMic() => Core.PostWebMessageAsJson(PhoneMicEndMessage);

    public void GoBack() => Core.GoBack();

    // After a failed load the view holds the browser's error page for whatever address failed, so it opens the
    // app's own address again rather than reloading that.
    public void Reload()
    {
        if (LoadFailed)
            Core.Navigate(_entry.Url);
        else
            Core.Reload();
    }

    public void Show()
    {
        if (!IsAlive)
        {
            _log($"view {Id}: not shown, its browser process has ended");
            return;
        }
        _root.Visibility = Visibility.Visible;
        if (!LoadFailed)
            _webView.Focus();
    }

    // WPF still counts the control as focused when Windows has lost the focus, so a plain Focus() would change
    // nothing: clearing first makes the control take it again, which hands the keyboard to the page.
    public void RestoreFocus()
    {
        Keyboard.ClearFocus();
        _webView.Focus();
    }

    public void Hide()
    {
        try
        {
            _root.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // WebView2 tells its browser process it is hidden; a process that has ended can make that throw.
            _log($"view {Id}: hiding failed: {ex.Message}");
        }
    }

    // Run on the view's dispatcher, as after a browser process failure: the caller may be in the middle of
    // a switch. Safe to call twice (and after the process failure), since Drop does nothing the second time.
    public void Close() => _webView.Dispatcher.InvokeAsync(Drop);

    // The script for pages loaded from now on carries the new gain, and the page already loaded changes at once.
    // One change at a time, so a quick second toggle cannot leave two boost scripts behind.
    public async Task SetBoostGainAsync(double gain)
    {
        await _boostGate.WaitAsync();
        try
        {
            if (_boostScriptId is not { } old)
                return;   // this app has no boost
            var core = Core;
            _boostScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.Boost(gain));
            core.RemoveScriptToExecuteOnDocumentCreated(old);
            await core.ExecuteScriptAsync(PageScripts.SetBoostGain(gain));
        }
        finally
        {
            _boostGate.Release();
        }
    }

    /// <summary>The DevTools <c>Input.dispatchKeyEvent</c> parameters for a press or release, as in the spike.</summary>
    internal static string KeyEvent(AppKey key, bool down)
    {
        var type = down ? "keyDown" : "keyUp";
        return key switch
        {
            AppKey.Space when down => JsonSerializer.Serialize(new { type, key = " ", code = "Space", text = " ", windowsVirtualKeyCode = 32, nativeVirtualKeyCode = 32 }),
            AppKey.Space => JsonSerializer.Serialize(new { type, key = " ", code = "Space", windowsVirtualKeyCode = 32, nativeVirtualKeyCode = 32 }),
            AppKey.Escape => JsonSerializer.Serialize(new { type, key = "Escape", code = "Escape", windowsVirtualKeyCode = 27, nativeVirtualKeyCode = 27 }),
            // YouTube's MICROPHONE key, which opens its voice search (measured in a spike, 2026-10-31).
            AppKey.Microphone => JsonSerializer.Serialize(new { type = down ? "rawKeyDown" : "keyUp", windowsVirtualKeyCode = 12290, nativeVirtualKeyCode = 12290 }),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
    }

    /// <summary>A piece of the phone's sound for the page, base64 in one web message.</summary>
    internal static string PhoneMicAudioMessage(ReadOnlySpan<byte> pcm) =>
        $$"""{"tvMic":"audio","pcm":"{{Convert.ToBase64String(pcm)}}"}""";

    /// <summary>Whether a message from the page is its <c>{"tvMic":"stopped"}</c>. Never throws, whatever the page sent.</summary>
    internal static bool IsPhoneMicStopped(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tvMic", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.ValueEquals("stopped");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string escaping a lone surrogate (say "\uDC00") throws when compared.
            return false;
        }
    }

    /// <summary>The page says the app emptied itself, its own exit (<see cref="PageScripts.ExitWhenEmpty"/>).</summary>
    internal static bool IsExitRequested(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tvExit", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.ValueEquals("empty");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The app's browser flags; a remote debugging port only under --dev, for measuring.</summary>
    internal static string BrowserArguments(string browserArgs, bool dev, int devPort) =>
        dev ? $"{browserArgs} --remote-debugging-port={devPort}".TrimStart() : browserArgs;

    /// <summary>Under <c>%LOCALAPPDATA%\TvLauncher</c>, so deploy's stop pattern <c>*\TvLauncher\WebView2*</c> covers it.</summary>
    internal static string UserDataFolder(string id) => Path.Combine(DataFolder, "WebView2-" + id);

    /// <summary>
    /// Only a navigation the server never answered (HTTP 0: network, DNS, TLS) is a failed load. WebView2 also reports
    /// failure for a page the service rendered itself (4xx, 5xx), a stopped load and a download, none of which should blank the page.
    /// </summary>
    internal static bool ShowsLoadFailure(bool isSuccess, CoreWebView2WebErrorStatus status, int httpStatusCode) =>
        !isSuccess && status != CoreWebView2WebErrorStatus.OperationCanceled && httpStatusCode == 0;

    private CoreWebView2 Core =>
        _core ?? throw new InvalidOperationException($"The {_entry.Name} view has no browser (not started, or its browser process ended).");

    // Pop-ups, such as a sign-in page, open in this view: there is no other window to show them in.
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            _core?.Navigate(e.Uri);
        else
            _log($"view {Id}: a new window that is not a web address was ignored");
    }

    // For example YouTube's "Exit YouTube?" answered yes.
    private void OnWindowCloseRequested(object? sender, object e)
    {
        _log($"view {Id}: the page asked to close");
        CloseRequested?.Invoke();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _log($"view {Id}: page loaded");
            if (LoadFailed)
            {
                LoadFailed = false;
                _failure.Visibility = Visibility.Collapsed;
                _webView.Visibility = Visibility.Visible;
                if (_root.IsVisible)
                    _webView.Focus();
            }
            return;
        }
        // Another navigation took over, or the page stopped this one: nothing failed.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            return;

        _log($"view {Id}: page did not load ({e.WebErrorStatus}, HTTP {e.HttpStatusCode})");
        if (!ShowsLoadFailure(e.IsSuccess, e.WebErrorStatus, e.HttpStatusCode))
            return;
        LoadFailed = true;
        // The browser's error page is hidden: a WebView2 is a window of its own, which no WPF text can cover.
        _webView.Visibility = Visibility.Collapsed;
        _failure.Visibility = Visibility.Visible;
    }

    // Any page in this view can post; only the microphone script's "stopped" means anything here.
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            _log($"view {Id}: reading a page message failed: {ex.Message}");
            return;
        }
        if (IsPhoneMicStopped(json))
            PhoneMicStopped?.Invoke();
        else if (IsExitRequested(json))
        {
            _log($"view {Id}: the page emptied itself, its own exit");
            CloseRequested?.Invoke();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log($"view {Id}: WebView2 process failed: {e.ProcessFailedKind} {e.Reason}");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            Reload();
        }
        else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The control is closed for good. The switcher makes a new view on the next pick; this one is
            // removed from the window after this event handler has returned.
            IsAlive = false;
            _webView.Dispatcher.InvokeAsync(Drop);
        }
    }

    // Takes the view out of the window for good: no events, no control, nothing left behind.
    private void Drop()
    {
        if (_dropped)
            return;
        _dropped = true;
        IsAlive = false;
        CloseRequested = null;
        PhoneMicStopped = null;
        if (_core is { } core)
        {
            _core = null;
            try
            {
                core.NewWindowRequested -= OnNewWindowRequested;
                core.WindowCloseRequested -= OnWindowCloseRequested;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.ProcessFailed -= OnProcessFailed;
                core.WebMessageReceived -= OnWebMessageReceived;
            }
            catch (Exception ex)
            {
                _log($"view {Id}: removing its event handlers failed: {ex.Message}");
            }
        }
        _host.Children.Remove(_root);
        try
        {
            _webView.Dispose();
        }
        catch (Exception ex)
        {
            _log($"view {Id}: disposing its WebView2 failed: {ex.Message}");
        }
        _log($"view {Id}: removed");
    }
}
