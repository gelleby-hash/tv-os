using System.IO;
using Windows.Networking.Connectivity;
using TvLauncher.Diagnostics;
using TvLauncher.SystemActions;

namespace TvLauncher.Tests;

public sealed class NetworkWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvlauncher-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly List<object> _posts = new();
    private readonly FakeSource _source = new();
    private readonly NetworkWatcher _watcher;

    public NetworkWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _watcher = new NetworkWatcher(_source, _posts.Add, new TimingLog(LogPath, _time));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LogPath => Path.Combine(_dir, "launcher.log");

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private int LogLines(string text) => File.ReadAllLines(LogPath).Count(l => l.Contains(text));

    [Fact]
    public void Maps_windows_levels()
    {
        Assert.Equal(NetState.Internet, WindowsNetworkSource.FromLevel(NetworkConnectivityLevel.InternetAccess));
        Assert.Equal(NetState.Local, WindowsNetworkSource.FromLevel(NetworkConnectivityLevel.ConstrainedInternetAccess));
        Assert.Equal(NetState.Local, WindowsNetworkSource.FromLevel(NetworkConnectivityLevel.LocalAccess));
        Assert.Equal(NetState.None, WindowsNetworkSource.FromLevel(NetworkConnectivityLevel.None));
        Assert.Equal(NetState.None, WindowsNetworkSource.FromLevel(null));
    }

    [Fact]
    public void PostCurrent_posts_the_state()
    {
        _source.State = NetState.Local;
        _watcher.PostCurrent();
        Assert.Equal("{\"type\":\"net\",\"state\":\"local\"}", Json(Assert.Single(_posts)));
    }

    [Fact]
    public void A_change_posts_and_logs_once()
    {
        _source.State = NetState.Local;
        _source.Raise();
        _source.Raise();
        Assert.Single(_posts);
        Assert.Equal(1, LogLines("net: local"));

        _source.State = NetState.Internet;
        _source.Raise();
        Assert.Equal(2, _posts.Count);
        Assert.Equal("{\"type\":\"net\",\"state\":\"internet\"}", Json(_posts[1]));
        Assert.Equal(1, LogLines("net: internet"));
    }

    [Fact]
    public void Overlapping_changes_end_on_the_state_the_source_ended_on()
    {
        // The first read is slow after it has taken its answer, as a thread-pool thread can be; a second change
        // arrives meanwhile and reads the newer state. The last post must still be the newer one.
        _source.State = NetState.Local;
        _source.SlowFirstRead = true;
        var a = Task.Run(_source.Raise);
        Thread.Sleep(50);
        _source.State = NetState.Internet;
        var b = Task.Run(_source.Raise);
        Task.WaitAll(a, b);
        Assert.Equal("{\"type\":\"net\",\"state\":\"internet\"}", Json(_posts[^1]));
    }

    private sealed class FakeSource : INetworkSource
    {
        private int _reads;
        public bool SlowFirstRead { get; set; }
        public NetState State { get; set; } = NetState.Internet;
        public event Action? Changed;
        public NetState Read()
        {
            var state = State;
            if (SlowFirstRead && Interlocked.Increment(ref _reads) == 1)
                Thread.Sleep(300);
            return state;
        }
        public void Raise() => Changed?.Invoke();
    }
}
