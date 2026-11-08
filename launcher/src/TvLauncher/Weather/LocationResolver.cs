namespace TvLauncher.Weather;

/// <summary>What the viewer chose for the weather's position: a town they picked, no location at all, or Windows' position.</summary>
public enum ChoiceKind { Picked, Off, Windows }

/// <summary>The viewer's remembered choice. <see cref="Place"/> is set only for <see cref="ChoiceKind.Picked"/>.</summary>
public sealed record LocationChoice(ChoiceKind Kind, Coordinates? Place);

/// <summary>
/// Where the weather's coordinates come from. <see cref="Source"/> is one of picked, config, windows, lastWindows,
/// off or none; <see cref="Position"/> is null for off and none.
/// </summary>
public sealed record ResolvedLocation(string Source, Coordinates? Position);

public static class LocationResolver
{
    /// <summary>
    /// A picked town wins, then "off", then "use Windows". With no choice the owner's config place wins over
    /// Windows. Windows falls back to the last fix; a Windows choice never falls back to the config place.
    /// </summary>
    public static ResolvedLocation Resolve(LocationChoice? choice, Coordinates? config, LocationReading windows, Coordinates? lastWindows)
    {
        if (choice is { Kind: ChoiceKind.Picked, Place: { } place }) return new("picked", place);
        if (choice is { Kind: ChoiceKind.Off }) return new("off", null);
        if (choice is null && config is not null) return new("config", config);
        if (windows.Position is { } fix) return new("windows", fix);
        if (lastWindows is { } last) return new("lastWindows", last);
        return new("none", null);
    }
}
