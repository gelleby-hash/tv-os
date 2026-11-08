using System.Globalization;
using System.IO;

namespace TvLauncher.Diagnostics;

/// <summary>
/// Appends one line per event to the launcher log: <c>ISO-time TAB event TAB ms-since-last-home-key</c>
/// ("-" before the first Home key; on the Home key line itself, the time it took to reach the log, as the count
/// starts at the press). Failures and
/// notes go through the same call so every line keeps the three fields. Safe from any thread, and a write
/// that fails is dropped: logging must never break the launcher.
/// </summary>
public sealed class TimingLog
{
    public const string HomeKey = "homeKey";
    public const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private long? _lastHomeKey;

    public TimingLog(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    public void Mark(string evt) => Write(evt, null);

    /// <summary>
    /// Writes the Home key line for a press seen at <paramref name="pressedAt"/>, a timestamp of this log's
    /// clock (Stopwatch.GetTimestamp for the real one) taken on the hook thread. The count to the following
    /// lines starts at the press, not at the moment the UI thread gets round to logging it.
    /// </summary>
    public void MarkHomeKey(long pressedAt) => Write(HomeKey, pressedAt);

    private void Write(string evt, long? at)
    {
        try
        {
            lock (_lock)
            {
                var now = _time.GetTimestamp();
                var when = at ?? now;
                if (evt == HomeKey)
                    _lastHomeKey = when;
                var since = _lastHomeKey is { } key
                    ? ((long)_time.GetElapsedTime(key, now).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                    : "-";
                var wall = _time.GetLocalNow() - _time.GetElapsedTime(when, now);
                var line = wall.ToString(TimeFormat, CultureInfo.InvariantCulture)
                    + "\t" + OneLine(evt) + "\t" + since + Environment.NewLine;

                if (Path.GetDirectoryName(_path) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception)
        {
            // Disk full, folder gone, file locked by a reader: the event is lost, the launcher carries on.
        }
    }

    private static string OneLine(string? evt) =>
        string.Join(' ', (evt ?? "").Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
