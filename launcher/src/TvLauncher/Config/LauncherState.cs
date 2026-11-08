using System.IO;
using System.Text.Json;
using TvLauncher.Weather;

namespace TvLauncher.Config;

/// <summary>
/// What the launcher remembers between runs, in <c>%LOCALAPPDATA%\TvLauncher\state.json</c> (not the config,
/// which the owner edits). A missing or unreadable file gives the defaults; a failed save is logged, never thrown.
/// </summary>
public sealed record LauncherState(bool BoostOn, LocationChoice? Location = null, Coordinates? LastWindows = null, IReadOnlyList<AppChoice>? Apps = null)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TvLauncher", "state.json");

    public static LauncherState Load(string path, Action<string>? log = null)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Raw>(File.ReadAllText(path));
            if (raw is { BoostOn: { } boostOn })
                return new LauncherState(boostOn, ReadChoice(raw.Location), ReadPoint(raw.LastWindows), ReadApps(raw.Apps));
            log?.Invoke($"state: {path} has no BoostOn value, using the defaults");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"state: could not read {path} ({ex.GetType().Name}: {ex.Message}), using the defaults");
        }
        return new LauncherState(true);
    }

    public bool Save(string path, Action<string>? log = null)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(new Raw
            {
                BoostOn = BoostOn,
                Location = Location is null ? null : new RawChoice { Kind = Location.Kind.ToString(), Place = WritePoint(Location.Place) },
                LastWindows = WritePoint(LastWindows),
                Apps = Apps is null ? null : JsonSerializer.SerializeToElement(Apps.Select(a => new RawAppChoice { Id = a.Id, Shown = a.Shown, VersionId = a.VersionId }).ToList()),
            }));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"state: could not save {path} ({ex.GetType().Name}: {ex.Message})");
            try { File.Delete(temp); } catch (Exception) { /* best effort */ }
            return false;
        }
    }

    // A stored point is rounded on the way in and read as absent when it is not a real position.
    private static Coordinates? ReadPoint(RawPoint? raw) =>
        raw is { Latitude: { } lat, Longitude: { } lon } && Coordinates.IsValid(lat, lon) ? Coordinates.Rounded(lat, lon) : null;

    private static RawPoint? WritePoint(Coordinates? point) =>
        point is null ? null : new RawPoint { Latitude = point.Latitude, Longitude = point.Longitude };

    // An unknown kind, or a Picked choice without a valid place, reads as no choice.
    private static LocationChoice? ReadChoice(RawChoice? raw)
    {
        if (raw?.Kind is not { } name || !Enum.TryParse<ChoiceKind>(name, out var kind) || !Enum.IsDefined(kind)) return null;
        if (kind != ChoiceKind.Picked) return new LocationChoice(kind, null);
        return ReadPoint(raw.Place) is { } place ? new LocationChoice(kind, place) : null;
    }

    // Read by hand so a bad Apps value loses only the layout, never the boost or the location. Apps that is not an
    // array reads as nothing saved; an element that is not an object with a text Id is skipped; a Shown that is not a
    // boolean reads as shown and a VersionId that is not text as the empty id, which Resolve turns into the default.
    private static List<AppChoice>? ReadApps(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Array } array) return null;
        var choices = new List<AppChoice>();
        foreach (var e in array.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var shown = !e.TryGetProperty("Shown", out var s) || s.ValueKind != JsonValueKind.False;
            var version = e.TryGetProperty("VersionId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            choices.Add(new AppChoice(id.GetString()!, shown, version));
        }
        return choices;
    }

    private sealed class Raw
    {
        public bool? BoostOn { get; set; }
        public RawChoice? Location { get; set; }
        public RawPoint? LastWindows { get; set; }
        public JsonElement? Apps { get; set; }
    }

    private sealed class RawAppChoice
    {
        public string? Id { get; set; }
        public bool? Shown { get; set; }
        public string? VersionId { get; set; }
    }

    private sealed class RawChoice
    {
        public string? Kind { get; set; }
        public RawPoint? Place { get; set; }
    }

    private sealed class RawPoint
    {
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }
}
