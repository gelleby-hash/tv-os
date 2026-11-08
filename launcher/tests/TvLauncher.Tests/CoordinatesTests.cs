using System.Text.RegularExpressions;
using TvLauncher.Weather;

namespace TvLauncher.Tests;

public class CoordinatesTests
{
    [Fact]
    public void Rounded_goes_to_two_decimals()
    {
        Assert.Equal(new Coordinates(55.95, -3.19), Coordinates.Rounded(55.95324, -3.18827));
    }

    [Fact]
    public void Rounded_sends_midpoints_away_from_zero()
    {
        Assert.Equal(new Coordinates(0.01, -0.01), Coordinates.Rounded(0.005, -0.005));
    }

    [Theory]
    [InlineData(90.1, 0)]
    [InlineData(-90.1, 0)]
    [InlineData(0, 180.1)]
    [InlineData(0, -180.1)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    public void IsValid_rejects_out_of_range_and_non_finite(double latitude, double longitude)
    {
        Assert.False(Coordinates.IsValid(latitude, longitude));
    }

    [Theory]
    [InlineData(90, 180)]
    [InlineData(-90, -180)]
    [InlineData(55.95, -3.19)]
    public void IsValid_accepts_the_edges_and_ordinary_values(double latitude, double longitude)
    {
        Assert.True(Coordinates.IsValid(latitude, longitude));
    }

    [Fact]
    public void WindowsRegion_code_is_null_or_two_capital_letters()
    {
        var code = WindowsRegion.Code();
        Assert.True(code is null || Regex.IsMatch(code, "^[A-Z]{2}$"), $"got '{code}'");
    }
}
