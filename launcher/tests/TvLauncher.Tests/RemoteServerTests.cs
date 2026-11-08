using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using TvLauncher.Remote;

namespace TvLauncher.Tests;

/// <summary>
/// Drives a real <see cref="RemoteServer"/> on a free port (port 0) with <see cref="ClientWebSocket"/> and raw
/// TCP clients. Every wait is an awaited event under <see cref="Timeout"/>, never a sleep.
/// </summary>
public sealed class RemoteServerTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<string> _log = new();
    private readonly RemoteServer _server;
    private readonly List<IDisposable> _clients = new();
    private readonly CancellationTokenSource _cts = new(Timeout);

    public RemoteServerTests()
    {
        // Loopback only: listening on every interface makes Windows ask about testhost.exe in the firewall.
        _server = new RemoteServer(0, _log.Enqueue) { BindAddress = IPAddress.Loopback, HandshakeTimeout = TimeSpan.FromMilliseconds(500) };
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            client.Dispose();
        await _server.DisposeAsync();
        _cts.Dispose();
    }

    private Uri Url(string path = RemoteProtocol.Path) => new($"ws://127.0.0.1:{_server.Port}{path}");

    private ClientWebSocket NewClient()
    {
        var client = new ClientWebSocket();
        _clients.Add(client);
        client.Options.CollectHttpResponseDetails = true;
        return client;
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var client = NewClient();
        await client.ConnectAsync(Url(), _cts.Token);
        return client;
    }

    private Task SendTextAsync(ClientWebSocket client, string text) =>
        client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cts.Token);

    private static string Press(string key) => $$"""{"v":1,"type":"press","key":"{{key}}"}""";

    /// <summary>Completes on the first raise of an event, or fails the test after <see cref="Timeout"/>.</summary>
    private static Task<T> Next<T>(Action<Action<T>> subscribe)
    {
        var raised = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(value => raised.TrySetResult(value));
        return raised.Task.WaitAsync(Timeout);
    }

    private Task<(RemoteConnection From, RemoteMessage Message)> NextMessage() =>
        Next<(RemoteConnection, RemoteMessage)>(done => _server.MessageReceived += (c, m) => done((c, m)));

    private Task<RemoteConnection> NextDisconnect() => Next<RemoteConnection>(done => _server.Disconnected += done);

    /// <summary>Reads the next frame and returns its type, its text and the close status, if it was a close.</summary>
    private async Task<(WebSocketMessageType Type, string Text, WebSocketCloseStatus? Status)> ReceiveAsync(ClientWebSocket client)
    {
        var buffer = new byte[4096];
        var result = await client.ReceiveAsync(buffer, _cts.Token);
        return (result.MessageType, Encoding.UTF8.GetString(buffer, 0, result.Count), result.CloseStatus);
    }

    /// <summary>Sends raw bytes over TCP and returns everything the server answers until it closes.</summary>
    private async Task<string> RawAsync(string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _server.Port, _cts.Token);
        var stream = tcp.GetStream();
        if (request.Length > 0)
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), _cts.Token);
        var answer = new MemoryStream();
        var buffer = new byte[1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
                answer.Write(buffer, 0, read);
        }
        catch (IOException)
        {
            // A reset after the answer counts as closed too.
        }
        return Encoding.ASCII.GetString(answer.ToArray());
    }

    /// <summary>A refused or junk client must never stop the accept loop: a good client still gets through.</summary>
    private async Task AssertStillServesAsync()
    {
        var message = NextMessage();
        var client = await ConnectAsync();
        await SendTextAsync(client, Press("ok"));
        Assert.Equal(new PressMessage(RemoteKey.Ok), (await message).Message);
    }

    [Fact]
    public async Task PressReachesMessageReceived()
    {
        var message = NextMessage();
        var client = await ConnectAsync();

        await SendTextAsync(client, Press("up"));

        Assert.Equal(new PressMessage(RemoteKey.Up), (await message).Message);
    }

    [Fact]
    public async Task TwoClientsBothPress()
    {
        var seen = new ConcurrentBag<(int Id, RemoteMessage Message)>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.MessageReceived += (c, m) =>
        {
            seen.Add((c.Id, m));
            if (seen.Count == 2)
                both.TrySetResult();
        };
        var first = await ConnectAsync();
        var second = await ConnectAsync();

        await SendTextAsync(first, Press("left"));
        await SendTextAsync(second, Press("right"));
        await both.Task.WaitAsync(Timeout);

        Assert.Equal(2, seen.Select(s => s.Id).Distinct().Count());
        Assert.Contains(seen, s => s.Message == new PressMessage(RemoteKey.Left));
        Assert.Contains(seen, s => s.Message == new PressMessage(RemoteKey.Right));
    }

    [Fact]
    public async Task BroadcastReachesEveryClient()
    {
        var connected = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Connected += _ =>
        {
            if (Interlocked.Increment(ref connected) == 2)
                both.TrySetResult();
        };
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        await both.Task.WaitAsync(Timeout);
        var state = RemoteProtocol.State("youtube", true);

        await _server.BroadcastAsync(state);

        Assert.Equal((WebSocketMessageType.Text, state, (WebSocketCloseStatus?)null), await ReceiveAsync(first));
        Assert.Equal((WebSocketMessageType.Text, state, (WebSocketCloseStatus?)null), await ReceiveAsync(second));
    }

    [Fact]
    public async Task SendReachesThatClient()
    {
        var connection = Next<RemoteConnection>(done => _server.Connected += done);
        var client = await ConnectAsync();
        var mic = RemoteProtocol.Mic(false, "timeout");

        await _server.SendAsync(await connection, mic);

        Assert.Equal((WebSocketMessageType.Text, mic, (WebSocketCloseStatus?)null), await ReceiveAsync(client));
    }

    [Fact]
    public async Task RefusesBrowserOrigin()
    {
        var raised = false;
        _server.Connected += _ => raised = true;
        _server.MessageReceived += (_, _) => raised = true;
        var client = NewClient();
        client.Options.SetRequestHeader("Origin", "http://example.com");

        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(Url(), _cts.Token));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, client.HttpStatusCode);
        Assert.Contains("HTTP/1.1 403", await RawAsync($"GET {RemoteProtocol.Path} HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: http://example.com\r\n\r\n"));
        Assert.False(raised);
        await AssertStillServesAsync();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/remote/v2")]
    [InlineData("/remote/v1/")]
    public async Task RefusesOtherPaths(string path)
    {
        var client = NewClient();

        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(Url(path), _cts.Token));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, client.HttpStatusCode);
        await AssertStillServesAsync();
    }

    [Theory]
    [InlineData("GET /remote/v1 HTTP/1.1\r\nHost: x\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n\r\n", "HTTP/1.1 404")]
    [InlineData("POST /remote/v1 HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n", "HTTP/1.1 405")]
    [InlineData("GET /remote/v1 HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: short\r\nSec-WebSocket-Version: 13\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("GET /remote/v1 HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 8\r\n\r\n", "HTTP/1.1 426")]
    [InlineData("hello\r\n\r\n", "HTTP/1.1 400")]
    public async Task RefusesPlainHttpAndBadUpgrades(string request, string answer)
    {
        var response = await RawAsync(request);

        Assert.StartsWith(answer, response);
        await AssertStillServesAsync();
    }

    [Fact]
    public async Task AnswersTheRfcExampleHandshake()
    {
        // RFC 6455 section 1.3: this key must be answered with this accept value.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _server.Port, _cts.Token);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {RemoteProtocol.Path} HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"), _cts.Token);
        var buffer = new byte[1024];
        var read = await stream.ReadAsync(buffer, _cts.Token);
        var answer = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.StartsWith("HTTP/1.1 101", answer);
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n", answer);
    }

    [Fact]
    public async Task RefusesOversizeHandshake()
    {
        var response = await RawAsync($"GET {RemoteProtocol.Path} HTTP/1.1\r\nX-Pad: {new string('a', 9000)}\r\n\r\n");

        Assert.DoesNotContain("101", response);
        await AssertStillServesAsync();
    }

    [Fact]
    public async Task ClosesOnJunk()
    {
        var disconnected = NextDisconnect();
        var client = await ConnectAsync();

        await SendTextAsync(client, "hello");

        var (type, _, status) = await ReceiveAsync(client);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _cts.Token);
        await disconnected;
        await AssertStillServesAsync();
    }

    [Fact]
    public async Task ClosesOnValidJsonItDoesNotKnow()
    {
        var client = await ConnectAsync();

        await SendTextAsync(client, """{"v":1,"type":"shutdown"}""");

        var (type, _, status) = await ReceiveAsync(client);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
    }

    [Fact]
    public void ListensOnEveryInterfaceByDefault()
    {
        // Checked without Start: the tests themselves never listen beyond loopback.
        Assert.Equal(IPAddress.Any, new RemoteServer(0, _ => { }).BindAddress);
    }

    [Fact]
    public async Task ClosesOnALoneSurrogateEscape()
    {
        // Valid JSON whose string cannot be unescaped: refused like any junk, not an abort logged as a failure.
        var disconnected = NextDisconnect();
        var client = await ConnectAsync();

        await SendTextAsync(client, """{"v":1,"type":"\uDC00"}""");

        var (type, _, status) = await ReceiveAsync(client);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _cts.Token);
        await disconnected;
        Assert.DoesNotContain(_log, line => line.Contains("failed"));
        Assert.Contains(_log, line => line.Contains("(unknown message)"));
    }

    [Theory]
    [InlineData(WebSocketMessageType.Text, 1025)]
    [InlineData(WebSocketMessageType.Binary, 32001)]
    public async Task ClosesOnOversizeFrame(WebSocketMessageType frame, int size)
    {
        var disconnected = NextDisconnect();
        var client = await ConnectAsync();
        var payload = new byte[size];
        Array.Fill(payload, (byte)'a');

        await client.SendAsync(payload, frame, true, _cts.Token);

        var (type, _, status) = await ReceiveAsync(client);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, status);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _cts.Token);
        await disconnected;
    }

    [Fact]
    public async Task AcceptsFramesAtTheLimit()
    {
        var audio = Next<ReadOnlyMemory<byte>>(done => _server.AudioReceived += (_, a) => done(a));
        var client = await ConnectAsync();

        await client.SendAsync(new byte[32000], WebSocketMessageType.Binary, true, _cts.Token);

        Assert.Equal(32000, (await audio).Length);
    }

    [Fact]
    public async Task DropsSlowHandshake()
    {
        var raised = false;
        _server.Connected += _ => raised = true;

        // Sends nothing: the server closes it after HandshakeTimeout (500 ms here, 5 s in the launcher).
        Assert.Equal("", await RawAsync(""));

        Assert.False(raised);
        Assert.Equal(TimeSpan.FromSeconds(5), new RemoteServer(0, _ => { }).HandshakeTimeout);
        await AssertStillServesAsync();
    }

    [Fact]
    public async Task AudioReachesAudioReceived()
    {
        var audio = Next<byte[]>(done => _server.AudioReceived += (_, a) => done(a.ToArray()));
        var client = await ConnectAsync();
        var sent = new byte[3200];
        new Random(1).NextBytes(sent);

        await client.SendAsync(sent, WebSocketMessageType.Binary, true, _cts.Token);

        Assert.Equal(sent, await audio);
    }

    [Fact]
    public async Task LogsConnectAndDisconnect()
    {
        var presses = 0;
        var thirdPress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.MessageReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref presses) == 3)
                thirdPress.TrySetResult();
        };
        var disconnected = NextDisconnect();
        var client = await ConnectAsync();

        foreach (var key in new[] { "up", "down", "ok" })
            await SendTextAsync(client, Press(key));
        await thirdPress.Task.WaitAsync(Timeout);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cts.Token);
        await disconnected;

        var lines = _log.ToArray();
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("remote: connected ", lines[0]);
        Assert.StartsWith("remote: disconnected ", lines[1]);
        Assert.All(lines, line => Assert.Contains("127.0.0.1", line));
    }

    [Fact]
    public async Task LogsRefusals()
    {
        await RawAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.Contains(_log, line => line.StartsWith("remote: refused ") && line.Contains("127.0.0.1"));
    }

    /// <summary>A second server whose accepts fail as <paramref name="accept"/> says; its log lines go to <paramref name="log"/>.</summary>
    private static RemoteServer FailingServer(Func<int, TcpListener, CancellationToken, ValueTask<TcpClient>> accept, Action<string> log, TimeSpan retryDelay)
    {
        var calls = 0;
        var server = new RemoteServer(0, log)
        {
            BindAddress = IPAddress.Loopback,
            AcceptRetryDelay = retryDelay,
            Accept = (listener, token) => accept(Interlocked.Increment(ref calls), listener, token),
        };
        server.Start();
        return server;
    }

    [Fact]
    public async Task AcceptCarriesOnAfterSocketErrors()
    {
        var log = new ConcurrentQueue<string>();
        await using var server = FailingServer(
            (call, listener, token) => call <= 3 ? throw new SocketException((int)SocketError.ConnectionReset) : listener.AcceptTcpClientAsync(token),
            log.Enqueue, TimeSpan.FromMilliseconds(10));
        var pressed = Next<RemoteMessage>(done => server.MessageReceived += (_, m) => done(m));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}{RemoteProtocol.Path}"), _cts.Token);
        await client.SendAsync(Encoding.UTF8.GetBytes(Press("ok")), WebSocketMessageType.Text, true, _cts.Token);

        Assert.Equal(new PressMessage(RemoteKey.Ok), await pressed);
        Assert.Equal(3, log.Count(line => line.StartsWith("remote: accept failed: ")));
    }

    [Fact]
    public async Task RepeatedAcceptErrorsWaitAndTheWaitEndsWithTheServer()
    {
        var calls = 0;
        var twoLines = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = 0;
        var server = FailingServer(
            (call, _, _) => { calls = call; throw new SocketException((int)SocketError.NetworkDown); },
            _ => { if (Interlocked.Increment(ref lines) == 2) twoLines.TrySetResult(); },
            TimeSpan.FromHours(1));

        await twoLines.Task.WaitAsync(Timeout);
        // The second failure in a row waits an hour; disposing ends that wait, and no third accept follows.
        await server.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(2, calls);
        Assert.Equal(2, lines);
    }

    [Fact]
    public async Task UnexpectedAcceptErrorIsLogged()
    {
        var logged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = FailingServer(
            (_, _, _) => throw new InvalidOperationException("boom"),
            line => logged.TrySetResult(line), TimeSpan.FromMilliseconds(10));

        Assert.Equal("remote: the listener stopped: InvalidOperationException: boom", await logged.Task.WaitAsync(Timeout));
    }
}
