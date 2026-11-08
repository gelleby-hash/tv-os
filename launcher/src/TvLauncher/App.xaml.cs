using System.IO;
using System.Windows;
using TvLauncher.Config;
using TvLauncher.Diagnostics;
using TvLauncher.SystemActions;

namespace TvLauncher;

/// <summary>
/// Command line: <c>--dev</c> (dev PC: Alt+F4 closes, shut down and restart are only logged),
/// <c>--keytest</c> (show the VK code of each key instead of the home screen, for setting up a remote; stop the
/// launcher first: disable the TvLauncher task so its watchdog does not start one, then stop the TvLauncher
/// process, because a running launcher's hook swallows the Home key and its topmost window covers the test),
/// <c>--config &lt;path&gt;</c> (default: %ProgramData%\TvLauncher\launcher.json if present, else the one next to the exe).
/// </summary>
public partial class App : Application
{
    // Held in a field for the life of the process so the single-instance mutex is never collected and released.
    private IDisposable? _instance;

    internal enum StartupOutcome { AlreadyRunning, BadConfig, Run }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var dev = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
        var keyTest = args.Contains("--keytest", StringComparer.OrdinalIgnoreCase);
        var configPath = ConfigPath(args);

        var outcome = Begin(
            keyTest,
            () => SingleInstance.TryAcquire(SingleInstance.MutexName),
            () => LauncherConfig.Load(configPath),
            ShowConfigProblem,
            out _instance,
            out var config);
        if (outcome != StartupOutcome.Run)
        {
            // Exit code 0 for a second copy: not a failure, just the watchdog finding the launcher already up.
            Shutdown(outcome == StartupOutcome.AlreadyRunning ? 0 : 1);
            return;
        }

        var timing = new TimingLog(config!.LogPath);
        timing.Mark($"start{(dev ? " --dev" : "")}{(keyTest ? " --keytest" : "")} config {configPath}");
        // Logged, then left to end the process (exit code non-zero): the scheduled task's one-minute
        // watchdog starts it again.
        DispatcherUnhandledException += (_, ex) => timing.Mark("crash: " + ex.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => timing.Mark("crash: " + ex.ExceptionObject);

        new MainWindow(config, timing, dev, keyTest).Show();
    }

    /// <summary>
    /// The first steps of a start, in this order: the single-instance check comes before anything else
    /// (no config read, no log line), because the scheduled task's watchdog starts a copy every minute and
    /// that copy must leave within milliseconds while the launcher runs. Any exception from the config load
    /// goes to <paramref name="configProblem"/> rather than ending the process unhandled.
    /// The key test takes no lock: it is run by hand with the launcher stopped (see <see cref="App"/>).
    /// </summary>
    internal static StartupOutcome Begin(
        bool keyTest,
        Func<IDisposable?> acquireInstance,
        Func<LauncherConfig> loadConfig,
        Action<Exception> configProblem,
        out IDisposable? instance,
        out LauncherConfig? config)
    {
        instance = null;
        config = null;
        if (!keyTest)
        {
            instance = acquireInstance();
            if (instance is null)
                return StartupOutcome.AlreadyRunning;
        }

        try
        {
            config = loadConfig();
            return StartupOutcome.Run;
        }
        catch (Exception ex)
        {
            configProblem(ex);
            return StartupOutcome.BadConfig;
        }
    }

    // There is no configured log path without a config, so the problem goes to the default log file.
    private static void ShowConfigProblem(Exception ex)
    {
        new TimingLog(Environment.ExpandEnvironmentVariables(LauncherConfig.DefaultLogPath)).Mark("config problem: " + ex);
        MessageBox.Show(ex.Message, "TV launcher: configuration problem", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string ConfigPath(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Length)
            return Path.GetFullPath(args[at + 1]);
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TvLauncher", "launcher.json");
        return File.Exists(shared) ? shared : Path.Combine(AppContext.BaseDirectory, "launcher.json");
    }
}
