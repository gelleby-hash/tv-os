using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using TvLauncher.Weather;

namespace TvLauncher.Tests;

public sealed class MetNorwayClientTests : IDisposable
{
    private const string Body = """{"type":"Feature","properties":{"timeseries":[]}}""";
    private static readonly Coordinates Place = new(55.95, -3.19);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvlauncher-tests-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_dir, "metno-cache.json");

    public MetNorwayClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return reply(request);
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpResponseMessage Ok(DateTimeOffset? expires, string lastModified = "Sat, 21 Nov 2026 10:00:00 GMT")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
        if (expires is { } e)
            response.Content.Headers.Expires = e;
        response.Content.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
        return response;
    }

    [Fact]
    public async Task Sends_the_user_agent_and_coordinates()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Ok(DateTimeOffset.UtcNow.AddHours(1))));
        var client = new MetNorwayClient(handler, CachePath, TimeProvider.System);

        var result = await client.GetAsync(Place, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("200", result.Status);
        Assert.Equal(Body, result.RawJson);
        var request = Assert.Single(handler.Requests);
        Assert.Matches(new Regex(@"^TvLauncher/\d+\.\d+ github\.com/gelleby-hash/tv-os$"), MetNorwayClient.UserAgent);
        Assert.Equal(MetNorwayClient.UserAgent, request.Headers.UserAgent.ToString());
        Assert.Equal("https://api.met.no/weatherapi/locationforecast/2.0/complete?lat=55.95&lon=-3.19", request.RequestUri!.ToString());
        var encodings = request.Headers.AcceptEncoding.Select(e => e.Value).ToList();
        Assert.Contains("gzip", encodings);
        Assert.Contains("deflate", encodings);
    }

    [Fact]
    public async Task Before_Expires_returns_the_kept_answer_without_a_call()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Ok(DateTimeOffset.UtcNow.AddHours(1))));
        var client = new MetNorwayClient(handler, CachePath, TimeProvider.System);
        var first = await client.GetAsync(Place, CancellationToken.None);

        var second = await client.GetAsync(Place, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.True(second.Ok);
        Assert.Equal("kept", second.Status);
        Assert.Equal(first.RawJson, second.RawJson);
        Assert.Equal(first.FetchedAtMs, second.FetchedAtMs);
    }

    [Fact]
    public async Task After_Expires_sends_If_Modified_Since_and_304_keeps_the_answer()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => Task.FromResult(++calls == 1
            ? Ok(DateTimeOffset.UtcNow.AddMilliseconds(-1), "Sat, 21 Nov 2026 10:00:00 GMT")
            : new HttpResponseMessage(HttpStatusCode.NotModified)));
        var client = new MetNorwayClient(handler, CachePath, TimeProvider.System);
        var first = await client.GetAsync(Place, CancellationToken.None);

        var second = await client.GetAsync(Place, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[0].Headers.IfModifiedSince);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T10:00:00Z"), handler.Requests[1].Headers.IfModifiedSince);
        Assert.True(second.Ok);
        Assert.Equal("304", second.Status);
        Assert.Equal(first.RawJson, second.RawJson);
        Assert.Equal(first.FetchedAtMs, second.FetchedAtMs);
    }

    [Fact]
    public async Task A_missing_Expires_counts_as_one_hour()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Ok(null)));
        var client = new MetNorwayClient(handler, CachePath, TimeProvider.System);
        await client.GetAsync(Place, CancellationToken.None);

        await client.GetAsync(Place, CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_restart_before_Expires_makes_no_call()
    {
        var first = new FakeHandler(_ => Task.FromResult(Ok(DateTimeOffset.UtcNow.AddHours(1))));
        var original = await new MetNorwayClient(first, CachePath, TimeProvider.System).GetAsync(Place, CancellationToken.None);
        var second = new FakeHandler(_ => throw new InvalidOperationException("no call expected"));

        var again = await new MetNorwayClient(second, CachePath, TimeProvider.System).GetAsync(Place, CancellationToken.None);

        Assert.Empty(second.Requests);
        Assert.True(again.Ok);
        Assert.Equal(original.RawJson, again.RawJson);
        Assert.Equal(original.FetchedAtMs, again.FetchedAtMs);
    }

    [Fact]
    public async Task An_unreadable_cache_file_is_an_empty_cache()
    {
        File.WriteAllText(CachePath, "not json");
        var handler = new FakeHandler(_ => Task.FromResult(Ok(DateTimeOffset.UtcNow.AddHours(1))));

        var result = await new MetNorwayClient(handler, CachePath, TimeProvider.System).GetAsync(Place, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reports_403_without_throwing()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = new MetNorwayClient(handler, CachePath, TimeProvider.System);

        var result = await client.GetAsync(Place, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("403", result.Status);
        Assert.Null(result.RawJson);
    }

    [Fact]
    public async Task Reports_a_network_failure_without_throwing()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("down"));
        var client = new MetNorwayClient(handler, CachePath, TimeProvider.System);

        var result = await client.GetAsync(Place, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("network", result.Status);
    }

    [Fact]
    public async Task Reports_timeout_after_15_seconds_on_the_clock()
    {
        var time = new ManualTime();
        var handler = new HangingHandler();
        var client = new MetNorwayClient(handler, CachePath, time);

        var pending = client.GetAsync(Place, CancellationToken.None);
        await handler.Started.Task;
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Ok);
        Assert.Equal("timeout", result.Status);
    }

    private static FakeHandler Replying(HttpStatusCode code, Action<HttpResponseMessage>? shape = null) => new(_ =>
    {
        var response = new HttpResponseMessage(code);
        shape?.Invoke(response);
        return Task.FromResult(response);
    });

    [Fact]
    public async Task A_403_then_an_immediate_second_ask_makes_one_request()
    {
        var handler = Replying(HttpStatusCode.Forbidden);
        var client = new MetNorwayClient(handler, CachePath, new ManualTime());
        await client.GetAsync(Place, CancellationToken.None);

        var second = await client.GetAsync(Place, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.False(second.Ok);
        Assert.Equal("wait", second.Status);
    }

    [Fact]
    public async Task A_500_waits_15_minutes_before_the_next_request()
    {
        var time = new ManualTime();
        var handler = Replying(HttpStatusCode.InternalServerError);
        var client = new MetNorwayClient(handler, CachePath, time);
        await client.GetAsync(Place, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(14));
        await client.GetAsync(Place, CancellationToken.None);
        Assert.Single(handler.Requests);

        time.Advance(TimeSpan.FromMinutes(1));
        var third = await client.GetAsync(Place, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("500", third.Status);
    }

    [Fact]
    public async Task A_429_with_Retry_After_of_8_hours_waits_8_hours()
    {
        var time = new ManualTime();
        var handler = Replying((HttpStatusCode)429, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(8)));
        var client = new MetNorwayClient(handler, CachePath, time);
        await client.GetAsync(Place, CancellationToken.None);

        time.Advance(TimeSpan.FromHours(7.9));
        await client.GetAsync(Place, CancellationToken.None);
        Assert.Single(handler.Requests);

        time.Advance(TimeSpan.FromHours(0.2));
        await client.GetAsync(Place, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_403_without_Retry_After_waits_6_hours()
    {
        var time = new ManualTime();
        var handler = Replying(HttpStatusCode.Forbidden);
        var client = new MetNorwayClient(handler, CachePath, time);
        await client.GetAsync(Place, CancellationToken.None);

        time.Advance(TimeSpan.FromHours(5.9));
        await client.GetAsync(Place, CancellationToken.None);
        Assert.Single(handler.Requests);

        time.Advance(TimeSpan.FromHours(0.2));
        await client.GetAsync(Place, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }
}
