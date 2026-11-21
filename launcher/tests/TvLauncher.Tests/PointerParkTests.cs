using TvLauncher.Apps;

namespace TvLauncher.Tests;

public sealed class PointerParkTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 1.0)]     // the dev PC's screen at 100%
    [InlineData(0, 0, 3840, 2160, 3.0)]     // the mini PC: a 4K picture at 300%
    [InlineData(-1920, 0, 0, 1080, 1.0)]    // a screen left of the main one
    public void Spot_SitsInTheScreensBottomRightCorner(int left, int top, int right, int bottom, double scale)
    {
        var spot = PointerPark.Spot(left, top, right, bottom, scale);

        var size = (int)Math.Round(PointerPark.Size * scale);
        Assert.Equal((right - size, bottom - size, size, size), spot);
    }

    [Fact]
    public void Target_IsInsideTheSpot()
    {
        var (x, y, width, height) = PointerPark.Spot(0, 0, 3840, 2160, 3.0);

        var (px, py) = PointerPark.Target(x, y, width, height);

        Assert.InRange(px, x, x + width - 1);
        Assert.InRange(py, y, y + height - 1);
    }

    [Fact]
    public void AClickOnTheSpot_NeverActivatesIt_AndIsEaten()
    {
        // With the air mouse on, OK and Back are clicks on the parked pointer: a click made the spot the active
        // window, holding the keyboard (seen on the box, 2026-11-15). AirMouse turns the click into OK or Back.
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATEANDEAT = 4;

        Assert.Equal(MA_NOACTIVATEANDEAT, PointerPark.Answer(WM_MOUSEACTIVATE));
        Assert.Null(PointerPark.Answer(0x0201));   // WM_LBUTTONDOWN: left to Windows
    }
}
