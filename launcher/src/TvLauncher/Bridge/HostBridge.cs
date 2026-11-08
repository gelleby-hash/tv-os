using System.Text.Json;
using TvLauncher.Apps;
using TvLauncher.Diagnostics;
using TvLauncher.SystemActions;
using TvLauncher.Weather;

namespace TvLauncher.Bridge;

/// <summary>
/// Acts on the messages the home page posts (launcher/web/js/host.js). Anything it does not recognise,
/// including text that is not a JSON object, is ignored. A failure while acting is logged, never thrown,
/// because the caller is a WebView2 event handler where an exception would end the launcher.
/// </summary>
public sealed class HostBridge(AppSwitcher switcher, IPowerActions power, TimingLog timing)
{
    /// <summary>The page has loaded and listens for host messages (raised again after a reload).</summary>
    public event Action? PageReady;

    /// <summary>The boost button on the home page was pressed.</summary>
    public event Action? BoostToggled;

    /// <summary>The viewer picked a town in the page's search; the coordinates are already rounded.</summary>
    public event Action<Coordinates>? PlacePicked;

    /// <summary>The viewer turned the weather off.</summary>
    public event Action? WeatherOffChosen;

    /// <summary>The viewer chose to use Windows' location.</summary>
    public event Action? WindowsLocationChosen;

    /// <summary>The viewer asked to turn on Windows' location (the host opens Windows' settings page).</summary>
    public event Action? TurnOnRequested;

    /// <summary>The viewer moved an app to a place in the full list (hidden apps counted).</summary>
    public event Action<string, int>? AppMoveRequested;

    /// <summary>The viewer turned an app on or off the home row.</summary>
    public event Action<string, bool>? AppShowRequested;

    /// <summary>The viewer chose which version of an app it plays.</summary>
    public event Action<string, string>? AppVersionRequested;

    public async Task HandleAsync(string json)
    {
        if (Parse(json) is not { } message)
            return;
        var (type, id, on, place, to, version, code) = message;

        try
        {
            switch (type)
            {
                case "ready":
                    timing.Mark("pageReady");
                    PageReady?.Invoke();
                    break;
                case "launch" when id is not null:
                    timing.Mark("launch " + id);
                    await switcher.OpenAsync(id);
                    break;
                case "desktop":
                    // Only reachable from the home screen, where no app is in front, so the switcher has nothing to do.
                    timing.Mark("desktop");
                    power.ShowDesktop();
                    break;
                case "restart":
                    timing.Mark("restart");
                    power.Restart();
                    break;
                case "shutdown":
                    timing.Mark("shutdown");
                    power.Shutdown();
                    break;
                case "toggleBoost":
                    timing.Mark("toggleBoost");
                    BoostToggled?.Invoke();
                    break;
                case "locationPick" when place is not null:
                    timing.Mark("locationPick");
                    PlacePicked?.Invoke(place);
                    break;
                case "locationOff":
                    timing.Mark("locationOff");
                    WeatherOffChosen?.Invoke();
                    break;
                case "locationWindows":
                    timing.Mark("locationWindows");
                    WindowsLocationChosen?.Invoke();
                    break;
                case "locationTurnOn":
                    timing.Mark("locationTurnOn");
                    TurnOnRequested?.Invoke();
                    break;
                case "appsMove" when id is not null && to is not null:
                    timing.Mark("appsMove " + id);
                    AppMoveRequested?.Invoke(id, to.Value);
                    break;
                case "appsShow" when id is not null && on is not null:
                    timing.Mark("appsShow " + id);
                    AppShowRequested?.Invoke(id, on.Value);
                    break;
                case "appsVersion" when id is not null && version is not null:
                    timing.Mark("appsVersion " + id);
                    AppVersionRequested?.Invoke(id, version);
                    break;
                case "homeShown":
                    timing.Mark("homeShown");
                    break;
                case "hdr" when on is not null:
                    timing.Mark(on.Value ? "hdr on" : "hdr off");
                    break;
                // The page's first keys after it loads, with what had the focus, and its first pointer move.
                case "key" when code is not null:
                    timing.Mark($"page key {code} ({id ?? "?"})");
                    break;
                case "pointer":
                    timing.Mark("page pointer");
                    break;
            }
        }
        catch (Exception ex)
        {
            timing.Mark($"{type} failed{(id is null ? "" : " for " + id)}: {ex.Message}");
        }
    }

    private static (string Type, string? Id, bool? On, Coordinates? Place, int? To, string? Version, int? Code)? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var type = Text(root, "type");
            return type is null ? null : (type, Text(root, "id"), Flag(root, "on"), Place(root), Whole(root, "to"), Text(root, "version"), Whole(root, "code"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Both numbers must be present and a real position; the result is rounded, so nothing finer is ever kept.
    private static Coordinates? Place(JsonElement root) =>
        Number(root, "latitude") is { } lat && Number(root, "longitude") is { } lon && Coordinates.IsValid(lat, lon)
            ? Coordinates.Rounded(lat, lon)
            : null;

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    // A whole number that fits an Int32; 1.5 and a number too big for one are absent.
    private static int? Whole(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
