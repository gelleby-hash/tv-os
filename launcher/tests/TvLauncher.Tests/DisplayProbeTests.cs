using TvLauncher.Apps;

namespace TvLauncher.Tests;

public sealed class DisplayProbeTests
{
    [Fact]
    public void Line_OneHdrScreen_NamesSizeRateModeAndBits()
    {
        Assert.Equal("3840x2160 60.00 Hz HDR 10-bit",
            DisplayProbe.Line([new DisplayProbe.Screen(3840, 2160, 60.0, "HDR", 10)]));
    }

    [Fact]
    public void Line_TwoScreens_JoinedAndRateRounded()
    {
        Assert.Equal("1920x1080 239.96 Hz SDR 8-bit; 2560x1440 59.94 Hz WCG 10-bit",
            DisplayProbe.Line([
                new DisplayProbe.Screen(1920, 1080, 239.9572, "SDR", 8),
                new DisplayProbe.Screen(2560, 1440, 60000.0 / 1001, "WCG", 10),
            ]));
    }

    [Fact]
    public void Line_ModeNotRead_NoBits()
    {
        Assert.Equal("3840x2160 60.00 Hz colour not read (error 87)",
            DisplayProbe.Line([new DisplayProbe.Screen(3840, 2160, 60.0, "colour not read (error 87)", 0)]));
    }

    [Fact]
    public void Line_NoScreen()
    {
        Assert.Equal("no active display", DisplayProbe.Line([]));
    }

    [Fact]
    public void Read_ThisPc_GivesALineWithoutThrowing()
    {
        Assert.False(string.IsNullOrWhiteSpace(DisplayProbe.Read()));
    }
}
