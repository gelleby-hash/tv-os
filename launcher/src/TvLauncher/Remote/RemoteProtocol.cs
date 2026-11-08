using System.Text.Json;

namespace TvLauncher.Remote;

/// <summary>The keys the phone remote can press: the Rii i25's own, nothing more (spec goal 2).</summary>
public enum RemoteKey { Up, Down, Left, Right, Ok, Back, Home, PlayPause, VolumeUp, VolumeDown }

/// <summary>A message from the phone, parsed from a text frame.</summary>
public abstract record RemoteMessage;

public sealed record PressMessage(RemoteKey Key) : RemoteMessage;

public sealed record MicStartMessage : RemoteMessage;

public sealed record MicStopMessage : RemoteMessage;

/// <summary>
/// The phone remote's wire format, version 1. Phone to launcher: <c>{"v":1,"type":"press","key":"up"}</c>,
/// <c>{"v":1,"type":"micStart"}</c>, <c>{"v":1,"type":"micStop"}</c> (binary frames carry audio). Launcher to
/// phone: <see cref="State"/> and <see cref="Mic"/>. Anything else, including another version, an unknown key
/// or text that is not a JSON object, parses to null: the text comes from the network, so it never throws.
/// </summary>
public static class RemoteProtocol
{
    public const int Version = 1;
    public const string Path = "/remote/v1";

    // Exact, case-sensitive names; an unknown one (say "power") is refused rather than guessed at.
    private static readonly Dictionary<string, RemoteKey> Keys = new(StringComparer.Ordinal)
    {
        ["up"] = RemoteKey.Up,
        ["down"] = RemoteKey.Down,
        ["left"] = RemoteKey.Left,
        ["right"] = RemoteKey.Right,
        ["ok"] = RemoteKey.Ok,
        ["back"] = RemoteKey.Back,
        ["home"] = RemoteKey.Home,
        ["playPause"] = RemoteKey.PlayPause,
        ["volumeUp"] = RemoteKey.VolumeUp,
        ["volumeDown"] = RemoteKey.VolumeDown,
    };

    public static RemoteMessage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version)
                return null;
            return Text(root, "type") switch
            {
                "press" when Text(root, "key") is { } name && Keys.TryGetValue(name, out var key) => new PressMessage(key),
                "micStart" => new MicStartMessage(),
                "micStop" => new MicStopMessage(),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: valid JSON whose string or property name escapes a lone surrogate
            // (say "\uDC00") parses, then throws when it is unescaped for a comparison or GetString.
            return null;
        }
    }

    /// <summary>What is in front (<paramref name="appId"/>, null on the home screen) and whether its mic button works.</summary>
    public static string State(string? appId, bool micAvailable) =>
        $$"""{"v":{{Version}},"type":"state","app":{{Quote(appId)}},"micAvailable":{{Bool(micAvailable)}}}""";

    /// <summary>Whether a listening session runs, and why it ended (for example "timeout"), or null.</summary>
    public static string Mic(bool listening, string? reason) =>
        $$"""{"v":{{Version}},"type":"mic","listening":{{Bool(listening)}},"reason":{{Quote(reason)}}}""";

    private static string Quote(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
