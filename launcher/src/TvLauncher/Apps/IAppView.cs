namespace TvLauncher.Apps;

/// <summary>A key the launcher can press inside an app's page, as if it came from the remote.</summary>
public enum AppKey { Space, Escape, Microphone }

/// <summary>
/// One app's web page, shown in its own view inside the launcher's window. Created on first pick and kept
/// loaded while hidden, so the app keeps its place, profile and sign-in.
/// </summary>
public interface IAppView
{
    string Id { get; }

    /// <summary>False once the view's browser process has ended; the view cannot be used again.</summary>
    bool IsAlive { get; }

    /// <summary>True when the app's page did not load (no network, the service down).</summary>
    bool LoadFailed { get; }

    bool CanGoBack { get; }

    /// <summary>Whether a video in the page is playing.</summary>
    Task<bool> IsPlayingAsync();

    /// <summary>Presses and releases the key inside the page, through the app's own key handling.</summary>
    Task PressKeyAsync(AppKey key);

    /// <summary>Presses the page's button with this label (its aria-label or its text), if there is one.</summary>
    Task PressButtonAsync(string label);

    /// <summary>Pauses the page's video element directly. Only for apps whose player survives it (YouTube).</summary>
    Task DirectPauseAsync();

    void GoBack();
    void Reload();
    void Show();

    /// <summary>Gives the page the keyboard again, as a mouse click on it would.</summary>
    void RestoreFocus();
    void Hide();

    /// <summary>Takes the view out of the window for good, so the next pick of the app makes a new one.</summary>
    void Close();
    Task SetBoostGainAsync(double gain);

    /// <summary>The page asked to close (for example YouTube's "Exit YouTube?" answered yes).</summary>
    event Action? CloseRequested;

    /// <summary>
    /// Opens the app's voice search with the phone's sound as its microphone (YouTube with <c>PhoneMic</c> only).
    /// Throws if the page's microphone script is not there to take the sound.
    /// </summary>
    Task StartPhoneMicAsync();

    /// <summary>Hands the page a piece of the phone's sound: PCM, 16-bit little-endian, mono, 16000 Hz.</summary>
    void PostPhoneMicAudio(ReadOnlyMemory<byte> pcm);

    /// <summary>Ends the sound the page gives the voice search, so it stops listening.</summary>
    void EndPhoneMic();

    /// <summary>The page says the voice search stopped listening.</summary>
    event Action? PhoneMicStopped;
}
