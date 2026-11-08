using TvLauncher.Apps;

namespace TvLauncher.Remote;

/// <summary>
/// YouTube's voice search with a phone's microphone: one listening session at a time, owned by the phone that
/// started it. Only that phone's sound reaches the page, and the session ends on whichever comes first: the page
/// saying YouTube stopped listening ("done"), the phone's stop ("stopped"), <see cref="MaxListening"/>
/// ("timeout"), the app in front changing ("left-app") or the phone going (nothing sent). Whether YouTube itself
/// closes the stream when it stops listening was not checked in the spike (2026-10-31), so the page's "stopped"
/// is only one of these, and the others need no help from the page. A start is refused while another runs
/// ("busy"), when YouTube is not in front ("not-youtube"), or when the page cannot take the sound ("failed").
/// Used on the UI thread; the only timer is the limit, while a session runs, and it comes back on the thread
/// (the synchronisation context) that started the session. Logs one line per session; never the sound.
/// </summary>
/// <param name="currentAppId">The app in front, or null on the home screen.</param>
/// <param name="viewFor">The view of an app, or null if it has none.</param>
/// <param name="micAllowed">Whether an app takes the phone's microphone (its <c>PhoneMic</c>).</param>
/// <param name="send">Sends a message to one phone.</param>
public sealed class PhoneMicSession(
    Func<string?> currentAppId,
    Func<string, IAppView?> viewFor,
    Func<string, bool> micAllowed,
    Func<RemoteConnection, string, Task> send,
    TimeProvider time,
    Action<string> log)
{
    /// <summary>The longest one session listens. The recorded search listened for about 5.5 s (spec, 2026-10-31).</summary>
    public static readonly TimeSpan MaxListening = TimeSpan.FromSeconds(15);

    private Live? _live;

    /// <summary>True when no session runs, so a phone's microphone button can start one.</summary>
    public bool IsIdle => _live is null;

    /// <summary>A session began or ended: <see cref="IsIdle"/> changed.</summary>
    public event Action? Changed;

    public async Task StartAsync(RemoteConnection phone)
    {
        if (_live is not null)
        {
            log($"mic: phone {phone.Id} refused, phone {_live.Phone.Id} is listening");
            await SendAsync(phone, RemoteProtocol.Mic(false, "busy"));
            return;
        }
        if (currentAppId() is not { } appId || !micAllowed(appId))
        {
            log($"mic: phone {phone.Id} refused, {currentAppId() ?? "the home screen"} is in front");
            await SendAsync(phone, RemoteProtocol.Mic(false, "not-youtube"));
            return;
        }
        if (viewFor(appId) is not { } view)
        {
            log($"mic: phone {phone.Id} could not start: {appId} has no view");
            await SendAsync(phone, RemoteProtocol.Mic(false, "failed"));
            return;
        }

        // Taken before the page is asked, so a second start meanwhile is busy.
        var live = new Live(phone, appId, view, time.GetTimestamp());
        _live = live;
        Changed?.Invoke();
        try
        {
            await view.StartPhoneMicAsync();
        }
        catch (Exception ex)
        {
            log($"mic: phone {phone.Id} could not start: {ex.Message}");
            if (_live == live)
                await EndAsync("failed", tellPhone: true, tellPage: true);
            return;
        }

        if (_live != live)
        {
            // Ended while the page was opening the voice search (the phone went, or YouTube was left). The
            // end may have reached the page before it armed, so it is told again.
            EndPage(view);
            return;
        }
        live.Listening = true;
        log($"mic: phone {phone.Id} started");
        _ = LimitAsync(live);
        await SendAsync(phone, RemoteProtocol.Mic(true, null));
    }

    /// <summary>Sound from a phone: passed on only while that phone owns the session.</summary>
    public void Audio(RemoteConnection phone, ReadOnlyMemory<byte> pcm)
    {
        if (_live is not { } live || live.Phone != phone)
            return;
        try
        {
            live.View.PostPhoneMicAudio(pcm);
        }
        catch (Exception ex)
        {
            log($"mic: passing the sound to {live.AppId} failed: {ex.Message}");
        }
    }

    /// <summary>The phone's stop; a stop from a phone that does not own the session changes nothing.</summary>
    public Task StopAsync(RemoteConnection phone) =>
        _live is { } live && live.Phone == phone ? EndAsync("stopped", tellPhone: true, tellPage: true) : Task.CompletedTask;

    /// <summary>
    /// The page says YouTube stopped listening. Ignored with no session, and while one is still opening: the
    /// page answers the launcher's own end with "stopped" too, and that one can come in late.
    /// </summary>
    public void OnPageStopped()
    {
        if (_live is { Listening: true })
            _ = EndAsync("done", tellPhone: true, tellPage: false);
    }

    /// <summary>A phone went; if it owned the session, the session ends (there is nobody to tell).</summary>
    public void OnDisconnected(RemoteConnection phone)
    {
        if (_live is { } live && live.Phone == phone)
            _ = EndAsync("disconnected", tellPhone: false, tellPage: true);
    }

    /// <summary>The app in front may have changed; leaving the app that listens ends the session.</summary>
    public void OnAppChanged()
    {
        if (_live is { } live && currentAppId() != live.AppId)
            _ = EndAsync("left-app", tellPhone: true, tellPage: true);
    }

    private async Task LimitAsync(Live live)
    {
        try
        {
            await Task.Delay(MaxListening, time, live.Limit.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // the session ended first
        }
        if (_live == live)
            await EndAsync("timeout", tellPhone: true, tellPage: true);
    }

    // Never throws: it is called from event handlers and fire-and-forget.
    private async Task EndAsync(string reason, bool tellPhone, bool tellPage)
    {
        if (_live is not { } live)
            return;
        _live = null;
        live.Limit.Cancel();
        live.Limit.Dispose();
        if (tellPage)
            EndPage(live.View);
        log($"mic: phone {live.Phone.Id} ended ({reason}) after {time.GetElapsedTime(live.StartedAt).TotalSeconds:0.0} s");
        Changed?.Invoke();
        if (tellPhone)
            await SendAsync(live.Phone, RemoteProtocol.Mic(false, reason));
    }

    private void EndPage(IAppView view)
    {
        try
        {
            view.EndPhoneMic();
        }
        catch (Exception ex)
        {
            // For example a view whose browser process ended; its stream went with it.
            log($"mic: ending the sound in {view.Id} failed: {ex.Message}");
        }
    }

    private async Task SendAsync(RemoteConnection phone, string json)
    {
        try
        {
            await send(phone, json);
        }
        catch (Exception ex)
        {
            log($"mic: telling phone {phone.Id} failed: {ex.Message}");
        }
    }

    private sealed class Live(RemoteConnection phone, string appId, IAppView view, long startedAt)
    {
        public RemoteConnection Phone { get; } = phone;
        public string AppId { get; } = appId;
        public IAppView View { get; } = view;
        public long StartedAt { get; } = startedAt;
        public CancellationTokenSource Limit { get; } = new();

        /// <summary>True once the page has the voice search open; false while it is still opening.</summary>
        public bool Listening { get; set; }
    }
}
