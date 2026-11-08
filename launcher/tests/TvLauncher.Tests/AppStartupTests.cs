using System.IO;
using TvLauncher.Config;

namespace TvLauncher.Tests;

public sealed class AppStartupTests
{
    private sealed class Token : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Fact]
    public void Begin_AnotherLauncherRunning_ExitsBeforeLoadingConfigOrLogging()
    {
        // The watchdog starts a copy every minute: it must leave at once, without reading the config or the log.
        var loaded = false;
        var reported = false;

        var outcome = App.Begin(
            keyTest: false,
            acquireInstance: () => null,
            loadConfig: () => { loaded = true; throw new InvalidOperationException("must not load"); },
            configProblem: _ => reported = true,
            out var instance,
            out var config);

        Assert.Equal(App.StartupOutcome.AlreadyRunning, outcome);
        Assert.False(loaded);
        Assert.False(reported);
        Assert.Null(instance);
        Assert.Null(config);
    }

    [Theory]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(NotSupportedException))]
    public void Begin_ConfigLoadThrowsAnything_ReportsInsteadOfCrashing(Type exceptionType)
    {
        Exception? reported = null;
        var thrown = (Exception)Activator.CreateInstance(exceptionType)!;

        var outcome = App.Begin(
            keyTest: false,
            acquireInstance: () => new Token(),
            loadConfig: () => throw thrown,
            configProblem: ex => reported = ex,
            out var instance,
            out var config);

        Assert.Equal(App.StartupOutcome.BadConfig, outcome);
        Assert.Same(thrown, reported);
        Assert.NotNull(instance);   // still held, so a watchdog copy does not show the same error again
        Assert.Null(config);
    }

    [Fact]
    public void Begin_KeyTest_TakesNoInstanceLock()
    {
        var acquired = false;

        var outcome = App.Begin(
            keyTest: true,
            acquireInstance: () => { acquired = true; return new Token(); },
            loadConfig: () => throw new InvalidDataException("no config here"),
            configProblem: _ => { },
            out var instance,
            out _);

        Assert.False(acquired);
        Assert.Null(instance);
        Assert.Equal(App.StartupOutcome.BadConfig, outcome);
    }

    [Fact]
    public void Begin_FirstLauncherWithGoodConfig_Runs()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "launcher.json");
        var token = new Token();

        var outcome = App.Begin(false, () => token, () => LauncherConfig.Load(shipped), _ => { }, out var instance, out var config);

        Assert.Equal(App.StartupOutcome.Run, outcome);
        Assert.Same(token, instance);
        Assert.NotNull(config);
    }
}
