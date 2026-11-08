using System.Diagnostics;
using System.IO;
using System.Windows;
using TvLauncher.Diagnostics;

// Not TvLauncher.System: a namespace of that name would hide the real System namespace inside the project.
namespace TvLauncher.SystemActions;

public interface IPowerActions
{
    void Shutdown();
    void Restart();

    /// <summary>Gets the launcher out of the way so the Windows desktop shows (for VLC). Home brings it back.</summary>
    void ShowDesktop();
}

/// <summary>
/// Shut down and restart through shutdown.exe, now (the page has already asked "are you sure").
/// With <paramref name="dryRun"/> (the --dev switch on the dev PC) they are only logged, never run.
/// </summary>
public sealed class PowerActions(Window window, TimingLog timing, bool dryRun) : IPowerActions
{
    public void Shutdown() => RunShutdownExe("/s /t 0");

    public void Restart() => RunShutdownExe("/r /t 0");

    // Hidden rather than minimised: a window without a taskbar button is minimised to a small strip at the
    // bottom left of the desktop (seen on the dev PC), not off screen. Home shows it again.
    public void ShowDesktop()
    {
        window.Topmost = false;
        window.Hide();
    }

    private void RunShutdownExe(string args)
    {
        if (dryRun)
        {
            timing.Mark("dev: not running shutdown.exe " + args);
            return;
        }
        var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        using var _ = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
    }
}
