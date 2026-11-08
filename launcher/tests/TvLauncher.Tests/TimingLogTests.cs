using System.Globalization;
using System.IO;
using TvLauncher.Diagnostics;

namespace TvLauncher.Tests;

public sealed class TimingLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvlauncher-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public TimingLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LogPath => Path.Combine(_dir, "logs", "launcher.log");

    [Fact]
    public void Mark_WritesIsoTimeEventAndMsSinceLastHomeKey()
    {
        var log = new TimingLog(LogPath, _time);

        log.Mark(TimingLog.HomeKey);
        _time.Advance(TimeSpan.FromMilliseconds(120));
        log.Mark("homeShown");
        _time.Advance(TimeSpan.FromMilliseconds(5000));
        log.Mark(TimingLog.HomeKey);
        _time.Advance(TimeSpan.FromMilliseconds(80));
        log.Mark("homeShown");

        var lines = File.ReadAllLines(LogPath).Select(l => l.Split('\t')).ToList();
        Assert.Equal(["homeKey", "homeShown", "homeKey", "homeShown"], lines.Select(l => l[1]));
        Assert.Equal(["0", "120", "0", "80"], lines.Select(l => l[2]));
        Assert.All(lines, l => Assert.Equal(3, l.Length));
        Assert.All(lines, l => DateTimeOffset.ParseExact(l[0], TimingLog.TimeFormat, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MarkHomeKey_CountsFromTheMomentOfTheKeyNotTheMomentItIsLogged()
    {
        // The hook thread takes the time of the press; the UI thread logs it a little later.
        var log = new TimingLog(LogPath, _time);
        var pressed = _time.GetTimestamp();
        _time.Advance(TimeSpan.FromMilliseconds(40));

        log.MarkHomeKey(pressed);
        _time.Advance(TimeSpan.FromMilliseconds(60));
        log.Mark("homeShown");

        var lines = File.ReadAllLines(LogPath).Select(l => l.Split('	')).ToList();
        Assert.Equal(["homeKey", "homeShown"], lines.Select(l => l[1]));
        Assert.Equal(["40", "100"], lines.Select(l => l[2]));
        // The wall-clock time on the Home key line is the time of the press, 40 ms before it was written
        // (wall-clock time is real in this test, so the gap to the next line is at least those 40 ms).
        var keyAt = DateTimeOffset.ParseExact(lines[0][0], TimingLog.TimeFormat, CultureInfo.InvariantCulture);
        var shownAt = DateTimeOffset.ParseExact(lines[1][0], TimingLog.TimeFormat, CultureInfo.InvariantCulture);
        Assert.InRange((shownAt - keyAt).TotalMilliseconds, 40, 1000);
    }

    [Fact]
    public void Mark_BeforeAnyHomeKey_WritesDash()
    {
        var log = new TimingLog(LogPath, _time);

        log.Mark("pageReady");

        Assert.Equal("-", File.ReadAllLines(LogPath).Single().Split('\t')[2]);
    }

    [Fact]
    public void Mark_TabsAndNewlinesInTheEvent_StayOnOneLine()
    {
        var log = new TimingLog(LogPath, _time);

        log.Mark("log Pause failed:\tline one\r\nline two");

        var line = File.ReadAllLines(LogPath).Single().Split('\t');
        Assert.Equal(3, line.Length);
        Assert.Equal("log Pause failed: line one line two", line[1]);
    }

    [Fact]
    public void Mark_UnwritablePath_DoesNotThrow()
    {
        // The log path is an existing folder, so every write fails.
        var log = new TimingLog(_dir, _time);

        log.Mark(TimingLog.HomeKey);
        log.Mark("homeShown");
    }

    [Fact]
    public async Task Mark_FromManyThreads_KeepsEveryLine()
    {
        var log = new TimingLog(LogPath, _time);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
                log.Mark($"t{t} e{i}");
        })));

        Assert.Equal(200, File.ReadAllLines(LogPath).Length);
    }
}
