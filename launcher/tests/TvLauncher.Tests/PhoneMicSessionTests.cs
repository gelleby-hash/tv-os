using System.IO;
using System.Net.WebSockets;
using TvLauncher.Apps;
using TvLauncher.Remote;

namespace TvLauncher.Tests;

public sealed class PhoneMicSessionTests
{
    private readonly List<string> _calls = new();
    private readonly List<(int Phone, string Json)> _sent = new();
    private readonly List<string> _logs = new();
    private readonly ManualTime _time = new();
    private readonly Dictionary<string, FakeAppView> _views = new();
    private readonly RemoteConnection _phone = Phone(1);
    private readonly RemoteConnection _other = Phone(2);
    private string? _current = "youtube";

    public PhoneMicSessionTests()
    {
        _views["youtube"] = new FakeAppView("youtube", _calls);
        _views["disney"] = new FakeAppView("disney", _calls);
    }

    private FakeAppView YouTube => _views["youtube"];

    // The session never touches the socket; it only hands the connection back to send.
    private static RemoteConnection Phone(int id) =>
        new(id, "192.168.0.20", WebSocket.CreateFromStream(Stream.Null, new WebSocketCreationOptions { IsServer = true }));

    private PhoneMicSession Session() => new(
        () => _current,
        id => _views.GetValueOrDefault(id),
        id => id == "youtube",
        Send,
        _time,
        Log);

    // The timeout sends from the thread pool.
    private Task Send(RemoteConnection to, string json)
    {
        lock (_sent)
            _sent.Add((to.Id, json));
        return Task.CompletedTask;
    }

    private void Log(string line)
    {
        lock (_logs)
            _logs.Add(line);
    }

    private (int Phone, string Json)[] Sent()
    {
        lock (_sent)
            return _sent.ToArray();
    }

    private async Task WaitForSentAsync(int count)
    {
        for (var waited = 0; waited < 5000; waited += 10)
        {
            if (Sent().Length >= count)
                return;
            await Task.Delay(10);
        }
        Assert.Fail($"fewer than {count} messages sent");
    }

    private static string Listening => RemoteProtocol.Mic(true, null);

    private static string Ended(string reason) => RemoteProtocol.Mic(false, reason);

    [Fact]
    public async Task StartOpensVoiceSearchAndTellsPhone()
    {
        var session = Session();

        await session.StartAsync(_phone);

        Assert.Equal(["youtube.micStart"], _calls);
        Assert.Equal([(1, Listening)], Sent());
        Assert.False(session.IsIdle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("disney")]
    public async Task StartRefusedWhenYouTubeNotInFront(string? inFront)
    {
        _current = inFront;
        var session = Session();

        await session.StartAsync(_phone);

        Assert.Equal([(1, Ended("not-youtube"))], Sent());
        Assert.Empty(_calls);
        Assert.True(session.IsIdle);
    }

    [Fact]
    public async Task SecondStartIsRefusedWhileListening()
    {
        var session = Session();
        await session.StartAsync(_phone);

        await session.StartAsync(_other);

        Assert.Equal([(1, Listening), (2, Ended("busy"))], Sent());
        Assert.Equal(["youtube.micStart"], _calls);
        session.Audio(_phone, new byte[320]);
        Assert.Equal(["youtube.micStart", "youtube.micAudio 320"], _calls);
    }

    [Fact]
    public async Task SecondStartIsRefusedWhileStarting()
    {
        YouTube.StartPhoneMicHangs = new TaskCompletionSource();
        var session = Session();
        var first = session.StartAsync(_phone);

        await session.StartAsync(_other);
        YouTube.StartPhoneMicHangs.SetResult();
        await first;

        Assert.Equal([(2, Ended("busy")), (1, Listening)], Sent());
        Assert.Equal(["youtube.micStart"], _calls);
    }

    [Fact]
    public async Task AudioOnlyFromTheListeningPhone()
    {
        var session = Session();
        session.Audio(_phone, new byte[100]);   // no session yet
        await session.StartAsync(_phone);

        session.Audio(_phone, new byte[320]);
        session.Audio(_other, new byte[640]);

        Assert.Equal(["youtube.micStart", "youtube.micAudio 320"], _calls);
    }

    [Fact]
    public async Task PageStopEndsSession()
    {
        var session = Session();
        await session.StartAsync(_phone);

        session.OnPageStopped();
        session.Audio(_phone, new byte[320]);

        Assert.Equal([(1, Listening), (1, Ended("done"))], Sent());
        Assert.Equal(["youtube.micStart"], _calls);
        Assert.True(session.IsIdle);
    }

    [Fact]
    public async Task PageStopWithNoSessionIsIgnored()
    {
        var session = Session();

        session.OnPageStopped();

        Assert.Empty(Sent());
        Assert.Empty(_calls);
    }

    // A late "stopped" (from the session before, whose stream the page closed on "end") must not end a new one
    // that is still opening.
    [Fact]
    public async Task PageStopWhileStartingIsIgnored()
    {
        YouTube.StartPhoneMicHangs = new TaskCompletionSource();
        var session = Session();
        var start = session.StartAsync(_phone);

        session.OnPageStopped();
        YouTube.StartPhoneMicHangs.SetResult();
        await start;

        Assert.Equal([(1, Listening)], Sent());
        Assert.False(session.IsIdle);
    }

    [Fact]
    public async Task PhoneStopEndsSession()
    {
        var session = Session();
        await session.StartAsync(_phone);

        await session.StopAsync(_phone);

        Assert.Equal(["youtube.micStart", "youtube.micEnd"], _calls);
        Assert.Equal([(1, Listening), (1, Ended("stopped"))], Sent());
        Assert.True(session.IsIdle);
    }

    [Fact]
    public async Task StopFromAnotherPhoneIsIgnored()
    {
        var session = Session();
        await session.StartAsync(_phone);

        await session.StopAsync(_other);

        Assert.Equal(["youtube.micStart"], _calls);
        Assert.Equal([(1, Listening)], Sent());
        Assert.False(session.IsIdle);
    }

    [Fact]
    public async Task TimeoutEndsSession()
    {
        var session = Session();
        await session.StartAsync(_phone);

        _time.Advance(PhoneMicSession.MaxListening - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.Single(Sent());

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForSentAsync(2);

        Assert.Equal(["youtube.micStart", "youtube.micEnd"], _calls);
        Assert.Equal([(1, Listening), (1, Ended("timeout"))], Sent());
        Assert.True(session.IsIdle);
    }

    [Fact]
    public async Task EndedSessionLeavesNoTimeout()
    {
        var session = Session();
        await session.StartAsync(_phone);
        await session.StopAsync(_phone);
        await session.StartAsync(_phone);

        _time.Advance(PhoneMicSession.MaxListening - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        // The first session's limit would have passed by now; the second one listens on.
        Assert.Equal([(1, Listening), (1, Ended("stopped")), (1, Listening)], Sent());
        Assert.False(session.IsIdle);
    }

    [Fact]
    public async Task PhoneDisconnectEndsSession()
    {
        var session = Session();
        await session.StartAsync(_phone);

        session.OnDisconnected(_other);
        Assert.False(session.IsIdle);
        session.OnDisconnected(_phone);

        Assert.Equal(["youtube.micStart", "youtube.micEnd"], _calls);
        Assert.Equal([(1, Listening)], Sent());
        Assert.True(session.IsIdle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("disney")]
    public async Task LeavingYouTubeEndsSession(string? nowInFront)
    {
        var session = Session();
        await session.StartAsync(_phone);

        session.OnAppChanged();   // still YouTube: nothing changes
        Assert.False(session.IsIdle);
        _current = nowInFront;
        session.OnAppChanged();

        Assert.Equal(["youtube.micStart", "youtube.micEnd"], _calls);
        Assert.Equal([(1, Listening), (1, Ended("left-app"))], Sent());
        Assert.True(session.IsIdle);
    }

    [Fact]
    public async Task FailedStartTellsPhone()
    {
        YouTube.StartPhoneMicThrows = new InvalidOperationException("the page did not arm");
        var session = Session();

        await session.StartAsync(_phone);

        Assert.Equal([(1, Ended("failed"))], Sent());
        Assert.Contains(_logs, l => l.Contains("the page did not arm"));
        Assert.True(session.IsIdle);

        YouTube.StartPhoneMicThrows = null;
        await session.StartAsync(_phone);
        Assert.Equal([(1, Ended("failed")), (1, Listening)], Sent());
    }

    [Fact]
    public async Task ChangedOnStartAndEnd()
    {
        var session = Session();
        var idle = new List<bool>();
        session.Changed += () => idle.Add(session.IsIdle);

        await session.StartAsync(_phone);
        await session.StartAsync(_other);   // busy: no change
        await session.StopAsync(_phone);

        Assert.Equal([false, true], idle);
    }

    [Fact]
    public async Task LogsEachSessionWithoutAudio()
    {
        var session = Session();
        await session.StartAsync(_phone);
        session.Audio(_phone, new byte[320]);

        session.OnPageStopped();

        Assert.Contains(_logs, l => l.Contains("mic") && l.Contains("phone 1") && l.Contains("started"));
        Assert.Contains(_logs, l => l.Contains("mic") && l.Contains("phone 1") && l.Contains("done"));
        Assert.DoesNotContain(_logs, l => l.Contains("320"));
    }
}
