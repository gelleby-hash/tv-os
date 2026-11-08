using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Geolocation;

namespace TvLauncher.Weather;

/// <summary>
/// Windows location through <see cref="Geolocator"/>. <see cref="ReadAsync"/> never throws: any exception, a
/// denied access status or running out of time gives <c>Position = null</c>. Coordinates are rounded, and never logged.
/// While Windows' own setting is off it does not ask at all: asking then shows Windows' "Let Windows and apps access
/// your location?" prompt and waits on it (run-through 2026-11-02), which must not appear on the TV at every start.
/// </summary>
public sealed class WindowsLocation(Func<bool>? settingOn = null) : ILocationSource
{
    private readonly Func<bool> _settingOn = settingOn ?? WindowsLocationSetting.IsOn;

    public async Task<LocationReading> ReadAsync(TimeSpan timeout)
    {
        if (!_settingOn())
            return new LocationReading(false, null);

        var enabled = false;

        async Task<LocationReading> Read()
        {
            enabled = await Geolocator.RequestAccessAsync() == GeolocationAccessStatus.Allowed;
            if (!enabled) return new LocationReading(false, null);

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), timeout);
            var point = position.Coordinate.Point.Position;
            return Coordinates.IsValid(point.Latitude, point.Longitude)
                ? new LocationReading(true, Coordinates.Rounded(point.Latitude, point.Longitude))
                : new LocationReading(true, null);
        }

        // The time limit covers the whole read, the permission check included, so a stuck call cannot hold up the caller.
        try
        {
            return await Read().WaitAsync(timeout + TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            return new LocationReading(enabled, null);
        }
    }
}

/// <summary>
/// Windows' location consent, read from the registry without asking anyone: the whole PC (which only an administrator
/// can change), this user, and desktop apps for this user. On the dev PC and the mini PC the first was "Deny" (2026-11-02).
/// </summary>
public static class WindowsLocationSetting
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";

    public static bool IsOn()
    {
        try
        {
            return AllowsDesktopApps(name => name switch
            {
                "machine" => Registry.LocalMachine.OpenSubKey(Key)?.GetValue("Value") as string,
                "user" => Registry.CurrentUser.OpenSubKey(Key)?.GetValue("Value") as string,
                _ => Registry.CurrentUser.OpenSubKey(Key + @"\NonPackaged")?.GetValue("Value") as string,
            });
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The PC must say "Allow"; this user and desktop apps must not say "Deny" (a missing value does not deny).</summary>
    public static bool AllowsDesktopApps(Func<string, string?> value) =>
        value("machine") == "Allow" && value("user") != "Deny" && value("desktopApps") != "Deny";
}

/// <summary>The country of the Windows region setting (Settings, Time and language, Region), as two capital letters.</summary>
public static class WindowsRegion
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetUserDefaultGeoName([Out] char[] geoName, int geoNameCount);

    /// <summary>Two upper-case letters, or <c>null</c> when Windows gives none (a region such as "001" is not a country).</summary>
    public static string? Code()
    {
        try
        {
            var buffer = new char[16];
            var length = GetUserDefaultGeoName(buffer, buffer.Length);
            if (length != 3) return null; // two letters plus the terminator
            var code = new string(buffer, 0, 2).ToUpperInvariant();
            return code.All(c => c is >= 'A' and <= 'Z') ? code : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
