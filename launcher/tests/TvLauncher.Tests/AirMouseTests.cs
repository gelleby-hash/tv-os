using TvLauncher.Input;
using TvLauncher.Remote;

namespace TvLauncher.Tests;

public sealed class AirMouseTests
{
    // The Rii i25's air mouse and a desktop mouse, as raw input names them (recorded on the dev PC, 2026-11-15).
    private const string Rii = @"\\?\HID#VID_1997&PID_2433&MI_01&Col01#8&2a7e3c1&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
    private const string Desk = @"\\?\HID#VID_1E7D&PID_2E24&MI_00&Col01#7&1b0c5d2&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
    private const ushort LeftDown = 0x0001, LeftUp = 0x0002, RightDown = 0x0004, RightUp = 0x0008;

    private sealed class Presser : IKeyPresser
    {
        public List<int> Pressed { get; } = [];
        public void Press(int vk) => Pressed.Add(vk);
    }

    private readonly Presser _presser = new();
    private int _holds;

    private AirMouse Make() => new("VID_1997&PID_2433", _presser, backVk: 166, hold: () => _holds++) { LauncherInFront = true };

    [Fact]
    public void Matches_TheConfiguredDongle_InAnyCase()
    {
        Assert.True(AirMouse.Matches(Rii, "VID_1997&PID_2433"));
        Assert.True(AirMouse.Matches(Rii, "vid_1997&pid_2433"));
        Assert.False(AirMouse.Matches(Desk, "VID_1997&PID_2433"));
        Assert.False(AirMouse.Matches(null, "VID_1997&PID_2433"));
    }

    [Fact]
    public void AMoveOfTheAirMouse_HoldsThePointer_AnotherMouseDoesNot()
    {
        var air = Make();

        air.OnReport(Rii, 3, -2, 0);
        air.OnReport(Desk, 40, 10, 0);

        Assert.Equal(1, _holds);
        Assert.Empty(_presser.Pressed);
    }

    [Fact]
    public void ItsClicks_AreOkAndBack_OnceEach()
    {
        // With the air mouse on, the Rii sends OK as a left click and Back as a right click (measured 2026-10-26).
        var air = Make();

        air.OnReport(Rii, 0, 0, LeftDown);
        air.OnReport(Rii, 0, 0, LeftUp);
        air.OnReport(Rii, 0, 0, RightDown);
        air.OnReport(Rii, 0, 0, RightUp);
        air.OnReport(Desk, 0, 0, LeftDown);

        Assert.Equal([13, 166], _presser.Pressed);
    }

    [Fact]
    public void SetFree_TheAirMouseIsAnOrdinaryMouse()
    {
        // The emergency key (the Rii's Search): the pointer is needed in a page, and the clicks are clicks.
        var air = Make();
        air.Free = true;

        air.OnReport(Rii, 5, 5, LeftDown);

        Assert.Equal(0, _holds);
        Assert.Empty(_presser.Pressed);
    }

    [Fact]
    public void WithTheLauncherAside_NothingIsDone()
    {
        // The desktop button: the owner may use the remote's pointer on the desktop.
        var air = Make();
        air.LauncherInFront = false;

        air.OnReport(Rii, 5, 5, LeftDown);

        Assert.Equal(0, _holds);
        Assert.Empty(_presser.Pressed);
    }
}
