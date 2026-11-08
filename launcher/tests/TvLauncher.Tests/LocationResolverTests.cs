using TvLauncher.Weather;

namespace TvLauncher.Tests;

public sealed class LocationResolverTests
{
    private static readonly Coordinates Picked = new(51.50, -0.12);
    private static readonly Coordinates Config = new(55.95, -3.19);
    private static readonly Coordinates Fix = new(53.48, -2.24);
    private static readonly Coordinates Last = new(54.97, -1.61);

    // choice: "picked", "off", "windows" or "" for none; fix and last: whether there is one.
    [Theory]
    [InlineData("picked", true, true, true, "picked", "picked")]
    [InlineData("off", true, true, true, "off", "")]
    [InlineData("windows", true, true, true, "windows", "fix")]
    [InlineData("windows", true, false, true, "lastWindows", "last")]
    [InlineData("windows", true, false, false, "none", "")]
    [InlineData("", true, true, true, "config", "config")]
    [InlineData("", false, true, true, "windows", "fix")]
    [InlineData("", false, false, true, "lastWindows", "last")]
    [InlineData("", false, false, false, "none", "")]
    public void Resolve_PicksTheSourceInOrder(string choice, bool hasConfig, bool hasFix, bool hasLast, string source, string position)
    {
        LocationChoice? chosen = choice switch
        {
            "picked" => new LocationChoice(ChoiceKind.Picked, Picked),
            "off" => new LocationChoice(ChoiceKind.Off, null),
            "windows" => new LocationChoice(ChoiceKind.Windows, null),
            _ => null,
        };

        var result = LocationResolver.Resolve(
            chosen,
            hasConfig ? Config : null,
            new LocationReading(true, hasFix ? Fix : null),
            hasLast ? Last : null);

        Coordinates? expected = position switch
        {
            "picked" => Picked,
            "config" => Config,
            "fix" => Fix,
            "last" => Last,
            _ => null,
        };
        Assert.Equal(source, result.Source);
        Assert.Equal(expected, result.Position);
    }
}
