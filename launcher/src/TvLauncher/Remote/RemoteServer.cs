using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace TvLauncher.Remote;

/// <summary>One phone connected to the listener.</summary>
public sealed class RemoteConnection
{
    internal RemoteConnection(int id, string remoteAddress, WebSocket socket)
    {
        Id = id;
        RemoteAddress = remoteAddress;
        Socket = socket;
    }

    public int Id { get; }
    public string RemoteAddress { get; }

    internal WebSocket Socket { get; }

    // A WebSocket allows one send at a time; a broadcast and a reply can meet.
    internal SemaphoreSlim SendLock { get; } = new(1, 1);
}

/// <summary>
/// The phone remote's listener: a WebSocket server on one TCP port, all interfaces (the firewall rule keeps it to
/// the home network). A <see cref="TcpListener"/> with the upgrade read by hand needs no administrator rights or
/// URL reservation, where <c>HttpListener</c> would (spec, 2026-10-31). It answers only <c>GET /remote/v1</c>
/// with a valid WebSocket upgrade and no <c>Origin</c> header, so a web page in a browser on the network cannot
/// drive it; anything else gets a plain HTTP error and is closed. A frame that is not a known message, or is
/// too big, closes that connection only, and nothing a client sends ends the accept loop. While nobody is
/// connected the only cost is the pending accept: no timers, no polling. Events are raised on the thread pool;
/// the window marshals them.
/// </summary>
public sealed class RemoteServer(int port, Action<string> log) : IAsyncDisposable
{
    /// <summary>The upgrade request must fit in this many bytes.</summary>
    internal const int MaxHandshakeBytes = 8 * 1024;

    /// <summary>A text frame (a message) larger than this closes the connection; the largest real one is ~40 bytes.</summary>
    internal const int MaxTextBytes = 1024;

    /// <summary>A binary frame (audio) larger than this closes the connection: one second of 16 kHz 16-bit mono.</summary>
    internal const int MaxAudioBytes = 32000;

    // RFC 6455 section 1.3.
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    // How long a close waits for the phone's own close frame before the socket is dropped.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, RemoteConnection> _connections = new();
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _nextId;
    private int _disposed;

    /// <summary>A client that has not sent its whole upgrade request by then is dropped. Tests shorten it.</summary>
    internal TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The interface listened on: every one in the launcher, so the phone can reach it. Tests use loopback,
    /// because listening on every interface made Windows ask about testhost.exe in the firewall (2026-10-31).
    /// </summary>
    internal IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>
    /// How long the accept loop waits after a second accept failure in a row before trying again, so a fault that
    /// repeats (the network stack in trouble) cannot spin. Never used while accepts succeed. Tests shorten it.
    /// </summary>
    internal TimeSpan AcceptRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Takes the next connection from the listener. Tests replace it to make accepts fail.</summary>
    internal Func<TcpListener, CancellationToken, ValueTask<TcpClient>> Accept { get; init; } =
        static (listener, token) => listener.AcceptTcpClientAsync(token);

    /// <summary>The port listened on: after <see cref="Start"/>, the one the system chose if 0 was asked for.</summary>
    public int Port { get; private set; } = port;

    /// <summary>A well-formed message arrived (a press, mic start or stop).</summary>
    public event Action<RemoteConnection, RemoteMessage>? MessageReceived;

    /// <summary>A binary frame arrived: PCM audio. The memory is the handler's to keep.</summary>
    public event Action<RemoteConnection, ReadOnlyMemory<byte>>? AudioReceived;

    public event Action<RemoteConnection>? Connected;

    public event Action<RemoteConnection>? Disconnected;

    public void Start()
    {
        if (_listener is not null)
            throw new InvalidOperationException("The remote server is already started.");
        _listener = new TcpListener(BindAddress, Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_listener);
    }

    public async Task SendAsync(RemoteConnection to, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await to.SendLock.WaitAsync(_stop.Token);
            try
            {
                if (to.Socket.State == WebSocketState.Open)
                    await to.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token);
            }
            finally
            {
                to.SendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is going; its receive loop logs why and raises Disconnected.
        }
    }

    public Task BroadcastAsync(string json) => Task.WhenAll(_connections.Values.Select(c => SendAsync(c, json)));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _stop.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop;
        // Every handler watches _stop: pending handshakes and receive loops end, raising Disconnected.
        await Task.WhenAll(_handlers.Keys);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        var failedBefore = false;
        while (true)
        {
            TcpClient client;
            try
            {
                client = await Accept(listener, _stop.Token);
                failedBefore = false;
            }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // A client that resets before it is accepted shows up here; the listener itself is fine, so the
                // next accept follows at once. A second failure in a row waits first, so a lasting fault does not spin.
                log($"remote: accept failed: {ex.Message}");
                if (failedBefore)
                {
                    try
                    {
                        await Task.Delay(AcceptRetryDelay, _stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                failedBefore = true;
                continue;
            }
            catch (Exception ex)
            {
                // Not expected; logged so the phone remote never stops without a trace. The launcher carries on.
                log($"remote: the listener stopped: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var handler = Task.Run(() => HandleAsync(client));
            _handlers.TryAdd(handler, 0);
            _ = handler.ContinueWith(done => _handlers.TryRemove(done, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var address = "unknown";
            try
            {
                address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? address;
                client.NoDelay = true; // a key press is one small frame: send it now, not with the next one
                var stream = client.GetStream();
                if (!await HandshakeAsync(stream, address))
                    return;
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(30),
                });
                await ServeAsync(new RemoteConnection(Interlocked.Increment(ref _nextId), address, socket));
            }
            catch (Exception ex)
            {
                // Whatever went wrong, it stays inside this one connection.
                if (!_stop.IsCancellationRequested)
                    log($"remote: connection from {address} failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads the upgrade request (at most <see cref="MaxHandshakeBytes"/> within <see cref="HandshakeTimeout"/>)
    /// and answers it: 101 if it is the app's own, else a plain HTTP error. True when the WebSocket is open.
    /// </summary>
    private async Task<bool> HandshakeAsync(NetworkStream stream, string address)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(HandshakeTimeout);
        string? request;
        try
        {
            request = await ReadRequestAsync(stream, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_stop.IsCancellationRequested)
                log($"remote: refused {address} (no request within {HandshakeTimeout.TotalSeconds:0.#} s)");
            return false;
        }
        if (request is null)
            return false; // closed before a whole request: nothing to answer

        var (status, key) = Check(request);
        if (key is null)
        {
            log($"remote: refused {address} ({status})");
            var extra = status.StartsWith("426") ? "Sec-WebSocket-Version: 13\r\n" : "";
            await WriteAsync(stream, $"HTTP/1.1 {status}\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n", timeout.Token);
            stream.Socket.Shutdown(SocketShutdown.Send);
            return false;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
        await WriteAsync(stream, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n", timeout.Token);
        return true;
    }

    /// <summary>
    /// The request up to its blank line, or null if the client closed first. One byte per read so nothing after
    /// the blank line is taken from the WebSocket; a request is a few hundred bytes, once per connection.
    /// Too long a request comes back cut short, with no blank line, and is refused by <see cref="Check"/>.
    /// </summary>
    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new byte[MaxHandshakeBytes];
        var count = 0;
        while (count < bytes.Length)
        {
            if (await stream.ReadAsync(bytes.AsMemory(count, 1), token) == 0)
                return null;
            count++;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' && bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                break;
        }
        return Encoding.Latin1.GetString(bytes, 0, count);
    }

    /// <summary>Checks an upgrade request: the HTTP status to refuse it with, or the client's key to accept it.</summary>
    internal static (string Status, string? Key) Check(string request)
    {
        const string BadRequest = "400 Bad Request";
        if (!request.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            return (BadRequest, null);

        var lines = request[..^4].Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return (BadRequest, null);
        if (parts[1] != RemoteProtocol.Path)
            return ("404 Not Found", null);
        if (parts[0] != "GET")
            return ("405 Method Not Allowed", null);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                return (BadRequest, null);
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var earlier) ? earlier + ", " + value : value;
        }

        // Browsers always send Origin on a WebSocket; the app never does. This is what keeps web pages out.
        if (headers.ContainsKey("Origin"))
            return ("403 Forbidden", null);
        if (!HasToken(headers, "Upgrade", "websocket") || !HasToken(headers, "Connection", "Upgrade"))
            return (BadRequest, null);
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || !Convert.TryFromBase64String(key, new byte[16], out var length) || length != 16)
            return (BadRequest, null);
        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13")
            return ("426 Upgrade Required", null);
        return ("101 Switching Protocols", key);
    }

    private static bool HasToken(Dictionary<string, string> headers, string name, string token) =>
        headers.TryGetValue(name, out var value)
        && value.Split(',').Any(part => part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

    private static Task WriteAsync(NetworkStream stream, string text, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    private async Task ServeAsync(RemoteConnection connection)
    {
        _connections[connection.Id] = connection;
        log($"remote: connected {connection.Id} from {connection.RemoteAddress}");
        var reason = "lost";
        try
        {
            Raise(() => Connected?.Invoke(connection));
            reason = await ReceiveLoopAsync(connection);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            reason = "launcher stopping";
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or SocketException)
        {
            reason = "lost: " + ex.Message;
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            log($"remote: disconnected {connection.Id} from {connection.RemoteAddress} ({reason})");
            Raise(() => Disconnected?.Invoke(connection));
        }
    }

    /// <summary>Receives frames until the connection ends, and returns why it ended (for the log).</summary>
    private async Task<string> ReceiveLoopAsync(RemoteConnection connection)
    {
        var socket = connection.Socket;
        var buffer = new byte[MaxAudioBytes + 1];
        while (true)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(count), _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null);
                    return "closed by the phone";
                }
                count += result.Count;
                if (count > (result.MessageType == WebSocketMessageType.Text ? MaxTextBytes : MaxAudioBytes))
                {
                    await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "too big");
                    return "frame too big";
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var audio = buffer.AsMemory(0, count).ToArray();
                Raise(() => AudioReceived?.Invoke(connection, audio));
                continue;
            }

            if (RemoteProtocol.Parse(Encoding.UTF8.GetString(buffer, 0, count)) is not { } message)
            {
                await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "unknown message");
                return "unknown message";
            }
            Raise(() => MessageReceived?.Invoke(connection, message));
        }
    }

    /// <summary>
    /// Closes the WebSocket, waiting a short while for the phone's answer. After the phone's own close frame
    /// only ours is left to send; otherwise ours goes first and theirs is awaited.
    /// </summary>
    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, description, timeout.Token);
            else
                await socket.CloseAsync(status, description, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            // The phone did not answer in time or the link is gone; the socket is dropped either way.
        }
    }

    /// <summary>Runs an event handler; one that throws is logged, and the connection carries on.</summary>
    private void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            log($"remote: event handler failed: {ex.Message}");
        }
    }
}
