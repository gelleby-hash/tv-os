using System.Text.Json.Serialization;
using TvLauncher.Config;

namespace TvLauncher.Weather;

/// <summary>
/// Keeps the weather's position in step with what the viewer chose. Each call saves the state it changed,
/// resolves the position with <see cref="LocationResolver"/> and posts one <c>location</c> message to the page.
/// Only rounded coordinates are saved or posted, and the log names the source, never a position.
/// </summary>
public sealed class LocationCoordinator(
    ILocationSource windows,
    Coordinates? config,
    string? region,
    Func<LauncherState> state,
    Action<LauncherState> save,
    Action<object> post,
    Action<string> log)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // Until the first read Windows is treated as off, so an early choice never posts a position it has not seen.
    private LocationReading _reading = new(false, null);

    /// <summary>
    /// The page has just become ready. What the state, the config place and the last Windows fix already decide
    /// is posted at once, so the home screen never waits for Windows; then Windows is read, a new fix is
    /// remembered and the answer is posted again. With nothing to show yet, nothing is posted before the read.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (Resolve().Source != "none")
            Publish();
        await ReadWindowsAsync();
        Publish();
    }

    public Task PickAsync(Coordinates place)
    {
        var rounded = Coordinates.Rounded(place.Latitude, place.Longitude);
        save(state() with { Location = new LocationChoice(ChoiceKind.Picked, rounded) });
        Publish();
        return Task.CompletedTask;
    }

    public Task ChooseOffAsync()
    {
        save(state() with { Location = new LocationChoice(ChoiceKind.Off, null) });
        Publish();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the choice first, then reads Windows again so a switch turned on since the last read is seen.
    /// A pick or Off made while the read is pending is kept: the post resolves from the state as it is then.
    /// </summary>
    public async Task ChooseWindowsAsync()
    {
        save(state() with { Location = new LocationChoice(ChoiceKind.Windows, null) });
        await ReadWindowsAsync();
        Publish();
    }

    /// <summary>The viewer was sent to Windows' settings to switch location on and has not come back yet.</summary>
    public bool TurnOnPending { get; private set; }

    /// <summary>Windows' settings page has just been opened for the viewer; <see cref="FinishTurnOnAsync"/> runs on the return.</summary>
    public void BeginTurnOn()
    {
        TurnOnPending = true;
        log("location: turn on, settings opened");
    }

    /// <summary>
    /// The viewer is back from Windows' settings (or the page could not be opened). Windows is read again; when it is
    /// now on, the Windows choice is saved, while a picked place or Off is left alone when it is still off. The
    /// answer is posted as a <c>location</c> message, then as <c>locationTurnOn</c> telling the page how it went.
    /// </summary>
    public async Task FinishTurnOnAsync()
    {
        TurnOnPending = false;
        await ReadWindowsAsync();
        if (_reading.Enabled)
            save(state() with { Location = new LocationChoice(ChoiceKind.Windows, null) });
        Publish();
        post(new TurnOnMessage { On = _reading.Enabled });
        log(_reading.Enabled ? "location: turn on, windows on" : "location: turn on, windows off");
    }

    private async Task ReadWindowsAsync()
    {
        _reading = await windows.ReadAsync(ReadTimeout);
        if (_reading.Position is { } fix)
        {
            var rounded = Coordinates.Rounded(fix.Latitude, fix.Longitude);
            if (state().LastWindows != rounded)
                save(state() with { LastWindows = rounded });
        }
    }

    private ResolvedLocation Resolve()
    {
        var current = state();
        return LocationResolver.Resolve(current.Location, config, _reading, current.LastWindows);
    }

    private void Publish()
    {
        var resolved = Resolve();
        post(new LocationMessage
        {
            Source = resolved.Source,
            Latitude = resolved.Position?.Latitude,
            Longitude = resolved.Position?.Longitude,
            WindowsOn = _reading.Enabled,
            Region = region,
            CanTurnOn = !_reading.Enabled,
        });
        log($"location: {resolved.Source}");
    }

    private sealed class TurnOnMessage
    {
        [JsonPropertyName("type")] public string Type => "locationTurnOn";
        [JsonPropertyName("on")] public bool On { get; init; }
    }

    // The contract's shape (host to page): latitude and longitude are left out when there is no position.
    private sealed class LocationMessage
    {
        [JsonPropertyName("type")] public string Type => "location";
        [JsonPropertyName("source")] public required string Source { get; init; }
        [JsonPropertyName("latitude"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Latitude { get; init; }
        [JsonPropertyName("longitude"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Longitude { get; init; }
        [JsonPropertyName("windowsOn")] public bool WindowsOn { get; init; }
        [JsonPropertyName("region")] public string? Region { get; init; }
        [JsonPropertyName("canTurnOn")] public bool CanTurnOn { get; init; }
    }
}
