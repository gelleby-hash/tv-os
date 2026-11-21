using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TvLauncher.Weather;

/// <summary>What MET Norway answered: the forecast as JSON text when Ok, otherwise a short status such as "403", "timeout" or "network".</summary>
public sealed record BackupResult(bool Ok, string Status, string? RawJson, long FetchedAtMs);

/// <summary>
/// Fetches MET Norway's forecast for the page. The service needs an identifying User-Agent, which a page cannot
/// set, and asks clients not to repeat a request before the answer's Expires, then to send If-Modified-Since with
/// the stored Last-Modified. Answers are kept in a file so a restart does not break that rule.
/// </summary>
public sealed class MetNorwayClient(HttpMessageHandler handler, string cachePath, TimeProvider time)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultLife = TimeSpan.FromHours(1);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AfterRefusal = TimeSpan.FromHours(6);

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<BackupResult>> _inFlight = [];
    private Dictionary<string, Entry>? _cache;

    // Per place, the clock reading before which no request is sent after a failed answer. The end of each wait is also
    // written beside the cache as a wall-clock time, so a restart (the watchdog brings the launcher back within a minute)
    // does not ask MET Norway again at once after it refused.
    private readonly Dictionary<string, long> _waitUntil = [];
    private Dictionary<string, DateTimeOffset>? _storedWaits;

    /// <summary>The name MET Norway sees: this program and where to reach its maker, never a browser's.</summary>
    public static string UserAgent { get; } =
        "TvLauncher/" + (typeof(MetNorwayClient).Assembly.GetName().Version ?? new Version(1, 0)).ToString(2) + " github.com/gelleby-hash/tv-os";

    public Task<BackupResult> GetAsync(Coordinates place, CancellationToken ct)
    {
        var key = Key(place);
        lock (_lock)
        {
            // A second ask for the same place while one is out shares its answer rather than sending another.
            if (_inFlight.TryGetValue(key, out var running))
                return running;
            var task = FetchAndForgetAsync(place, key, ct);
            _inFlight[key] = task;
            return task;
        }
    }

    // The place is released before the task completes, so a caller that has its answer can ask again at once.
    private async Task<BackupResult> FetchAndForgetAsync(Coordinates place, string key, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            return await FetchAsync(place, key, ct);
        }
        finally
        {
            lock (_lock)
                _inFlight.Remove(key);
        }
    }

    private async Task<BackupResult> FetchAsync(Coordinates place, string key, CancellationToken ct)
    {
        Entry? kept;
        lock (_lock)
            kept = Cache().GetValueOrDefault(key);
        if (kept is not null && time.GetUtcNow() < kept.Expires)
            return Answer(kept, "kept");

        // After a failure the page may ask again every couple of minutes; MET Norway must not be asked that often.
        lock (_lock)
        {
            StoredWaits();
            if (_waitUntil.TryGetValue(key, out var until) && time.GetTimestamp() < until)
                return Failed("wait");
        }

        try
        {
            using var limit = new CancellationTokenSource(Timeout, time);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(place));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            if (kept?.LastModified is { } since)
                request.Headers.TryAddWithoutValidation("If-Modified-Since", since);

            using var response = await _http.SendAsync(request, both.Token);
            var expires = time.GetUtcNow() + Lifetime(response);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && kept is not null)
            {
                var renewed = kept with { Expires = expires };
                Store(key, renewed);
                Forgive(key);
                return Answer(renewed, "304");
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var wait = code is 403 or 429 ? AfterRefusal : AfterFailure;
                var asked = response.Headers.RetryAfter is { } after
                    ? after.Delta ?? (after.Date is { } date ? date - time.GetUtcNow() : TimeSpan.Zero)
                    : TimeSpan.Zero;
                return Failed(code.ToString(CultureInfo.InvariantCulture), key, asked > wait ? asked : wait);
            }

            var raw = await response.Content.ReadAsStringAsync(both.Token);
            try
            {
                using var _ = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                return Failed("invalid", key, AfterFailure);
            }
            var modified = response.Content.Headers.TryGetValues("Last-Modified", out var values) ? values.FirstOrDefault() : null;
            var fresh = new Entry(raw, expires, modified, time.GetUtcNow().ToUnixTimeMilliseconds());
            Store(key, fresh);
            Forgive(key);
            return Answer(fresh, "200");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("timeout", key, AfterFailure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Failed("network", key, AfterFailure);
        }
    }

    // How long an answer stays fresh, counted on this box's clock. Expires is the server's moment, so it is measured
    // against the server's own Date (the box clock may be wrong). A missing, unparsable or already past Expires gets
    // the default hour rather than an answer that is stale at once and asked for again on every visit.
    private TimeSpan Lifetime(HttpResponseMessage response)
    {
        var expires = response.Content.Headers.Expires;
        if (expires is not { } at || at.Year < 2000)
            return DefaultLife;
        var life = at - (response.Headers.Date ?? time.GetUtcNow());
        return life > TimeSpan.Zero ? life : DefaultLife;
    }

    // At most two decimals (about 1 km): MET Norway allows four, and the page asks with what Coordinates already rounded.
    private static string Key(Coordinates place) =>
        place.Latitude.ToString("0.##", CultureInfo.InvariantCulture) + "," + place.Longitude.ToString("0.##", CultureInfo.InvariantCulture);

    private static Uri Url(Coordinates place) => new(
        "https://api.met.no/weatherapi/locationforecast/2.0/complete?lat="
        + place.Latitude.ToString("0.##", CultureInfo.InvariantCulture)
        + "&lon=" + place.Longitude.ToString("0.##", CultureInfo.InvariantCulture));

    private static BackupResult Answer(Entry entry, string status) => new(true, status, entry.Raw, entry.FetchedAt);

    private static BackupResult Failed(string status) => new(false, status, null, 0);

    private BackupResult Failed(string status, string key, TimeSpan wait)
    {
        lock (_lock)
        {
            _waitUntil[key] = time.GetTimestamp() + (long)(wait.TotalSeconds * time.TimestampFrequency);
            StoredWaits()[key] = time.GetUtcNow() + wait;
            WriteWaits();
        }
        return Failed(status);
    }

    private void Forgive(string key)
    {
        lock (_lock)
        {
            _waitUntil.Remove(key);
            if (StoredWaits().Remove(key))
                WriteWaits();
        }
    }

    private string WaitPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cachePath))!, Path.GetFileNameWithoutExtension(cachePath) + "-wait.json");

    // Read once, under _lock: what is left of each stored wait becomes a wait on this run's clock. A missing or unreadable
    // file means no wait.
    private Dictionary<string, DateTimeOffset> StoredWaits()
    {
        if (_storedWaits is not null)
            return _storedWaits;
        try
        {
            _storedWaits = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(WaitPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _storedWaits = [];
        }
        var now = time.GetUtcNow();
        foreach (var (key, until) in _storedWaits)
        {
            var left = until - now;
            if (left > TimeSpan.Zero && !_waitUntil.ContainsKey(key))
                _waitUntil[key] = time.GetTimestamp() + (long)(left.TotalSeconds * time.TimestampFrequency);
        }
        return _storedWaits;
    }

    // Called under _lock. Waits already over are dropped; a write that fails leaves the wait in memory only.
    private void WriteWaits()
    {
        var now = time.GetUtcNow();
        var waits = StoredWaits();
        foreach (var key in waits.Where(w => w.Value <= now).Select(w => w.Key).ToList())
            waits.Remove(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WaitPath)!);
            var temp = WaitPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(waits));
            File.Move(temp, WaitPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The cache is read once; a missing or unreadable file is an empty cache.
    private Dictionary<string, Entry> Cache()
    {
        if (_cache is not null)
            return _cache;
        try
        {
            _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(cachePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cache = [];
        }
        return _cache;
    }

    // The file is written whole after each new answer; a write that fails leaves the answer kept in memory only.
    private void Store(string key, Entry entry)
    {
        lock (_lock)
        {
            var cache = Cache();
            cache[key] = entry;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
                var temp = cachePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(cache));
                File.Move(temp, cachePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record Entry(string Raw, DateTimeOffset Expires, string? LastModified, long FetchedAt);
}
