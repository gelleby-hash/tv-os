namespace TvLauncher.Weather;

/// <summary>A place on Earth in degrees. Windows readings are rounded to two decimals (about 1 km) before use.</summary>
public sealed record Coordinates(double Latitude, double Longitude)
{
    public static Coordinates Rounded(double latitude, double longitude) =>
        new(Math.Round(latitude, 2, MidpointRounding.AwayFromZero), Math.Round(longitude, 2, MidpointRounding.AwayFromZero));

    public static bool IsValid(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude)
        && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
}

/// <summary>What Windows location gave: whether the app may use it, and the position if one was read in time.</summary>
public sealed record LocationReading(bool Enabled, Coordinates? Position);

/// <summary>Where the host reads the device's position from (Windows location; a fake in tests).</summary>
public interface ILocationSource
{
    Task<LocationReading> ReadAsync(TimeSpan timeout);
}
