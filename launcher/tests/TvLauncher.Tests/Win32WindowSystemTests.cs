using TvLauncher.Apps;

namespace TvLauncher.Tests;

public sealed class Win32WindowSystemTests
{
    [Fact]
    public void ActivateProblem_BothCallsWork_NothingToLog()
    {
        Assert.Null(Win32WindowSystem.ActivateProblem(new IntPtr(0x1234), sent: 1, sendError: 0, foreground: true, foregroundError: 0));
    }

    [Fact]
    public void ActivateProblem_SendInputFails_NamesItAndTheError()
    {
        var problem = Win32WindowSystem.ActivateProblem(new IntPtr(0x1234), sent: 0, sendError: 5, foreground: true, foregroundError: 0);

        Assert.NotNull(problem);
        Assert.Contains("SendInput", problem);
        Assert.Contains("error 5", problem);
        Assert.Contains("0x1234", problem);
    }

    [Fact]
    public void ActivateProblem_SetForegroundWindowRefused_NamesItAndTheError()
    {
        var problem = Win32WindowSystem.ActivateProblem(new IntPtr(0x1234), sent: 1, sendError: 0, foreground: false, foregroundError: 0);

        Assert.NotNull(problem);
        Assert.Contains("SetForegroundWindow", problem);
        Assert.Contains("error 0", problem);
        Assert.DoesNotContain("SendInput", problem);
    }

    [Fact]
    public void KeyboardLine_LauncherInFront_NamesTheWindowWithTheKeyboard()
    {
        Assert.Equal("launcher in front, keyboard on Chrome_WidgetWin_1",
            Win32WindowSystem.KeyboardLine(launcherInFront: true, "TvLauncher", "HwndWrapper[TvLauncher]", "Chrome_WidgetWin_1"));
    }

    [Fact]
    public void KeyboardLine_AnotherWindowInFront_NamesItsProgramAndClass()
    {
        Assert.Equal("in front: explorer Shell_TrayWnd, keyboard on (none)",
            Win32WindowSystem.KeyboardLine(launcherInFront: false, "explorer", "Shell_TrayWnd", null));
    }

    [Fact]
    public void KeyboardLine_NothingInFront()
    {
        Assert.Equal("in front: nothing, keyboard on (none)", Win32WindowSystem.KeyboardLine(launcherInFront: false, null, null, null));
    }
}
