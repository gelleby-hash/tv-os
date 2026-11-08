using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TvLauncher.Config;

/// <summary>What Back does inside an app.</summary>
public enum BackMode
{
    /// <summary>Back through the view's own history; home once there is none left.</summary>
    History,

    /// <summary>The page handles Back itself (YouTube's TV interface, which offers "Exit YouTube?").</summary>
    Page,
}

/// <summary>
/// One version of an app: a web page shown in its own view inside the launcher's window. Id and Name are the
/// app's (views, logs and bridge messages go by them); Icon is the version's own, else the app's.
/// </summary>
public sealed record AppEntry(
    string Id,
    string Name,
    string? Icon,
    string Url,
    string? UserAgent,
    string BrowserArgs,
    double? BoostGain,
    BackMode Back,
    bool DirectPauseFallback,
    bool ArrowNav,
    bool PhoneMic,
    bool HidePlayReady,
    string? PauseButton,
    string VersionId,
    string VersionName,
    string ProfileId,
    string? ExitWhenEmpty = null,
    string? HideElements = null);

/// <summary>An app the family picks on the home row, with one or more versions; the first is the default.</summary>
public sealed record AppDefinition(string Id, string Name, string? Icon, IReadOnlyList<AppEntry> Versions)
{
    public AppEntry Default => Versions[0];

    public AppEntry? Version(string versionId) => Versions.FirstOrDefault(v => v.VersionId == versionId);
}

public sealed record WeatherLocation(string? Name, double Latitude, double Longitude);

/// <summary>The listener the phone remote connects to. Off unless launcher.json turns it on.</summary>
public sealed record PhoneRemoteSettings(bool Enabled, int Port);

public sealed class LauncherConfig
{
    private const double MinBoostGain = 1;
    private const double MaxBoostGain = 8;
    private const int DefaultPhoneRemotePort = 47800;
    private const int MinPhoneRemotePort = 1024;
    private const int MaxPhoneRemotePort = 65535;
    private const double MinMotionTime = 0.5;
    private const double MaxMotionTime = 4;

    /// <summary>Where the log goes when the config does not say, or cannot be read.</summary>
    public const string DefaultLogPath = @"%LOCALAPPDATA%\TvLauncher\launcher.log";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Names only: a number would silently pick whichever kind has that position in the enum.
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    // The default encoder escapes <, >, &, ' and non-ASCII, so no name can end the script element.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<AppDefinition> Apps { get; }
    /// <summary>The Home keys: a press of any of them goes home. launcher.json gives a number or a list of numbers.</summary>
    public IReadOnlyList<int> HomeKeyVks { get; }
    /// <summary>Keys the hook keeps from Windows while the launcher is in front (the remote's Search and menu buttons).</summary>
    public IReadOnlyList<int> SwallowKeyVks { get; }
    public int BackKeyVk { get; }
    public bool RequireHdr { get; }
    public WeatherLocation? Location { get; }
    public PhoneRemoteSettings PhoneRemote { get; }
    public string Locale { get; }
    public string Unit { get; }
    public string Clock { get; }
    public int BackgroundRotateSeconds { get; }
    /// <summary>How long the home screen's animations take: 1 is the normal, snappy speed, 2 twice as long.</summary>
    public double MotionTime { get; }
    /// <summary>Whether Alt+F4 may close the launcher. Off unless set: the family must not close the TV's home screen by accident.</summary>
    public bool AllowClose { get; }
    public IReadOnlyList<string> SpotlightFolders { get; }
    public string LogPath { get; }

    private LauncherConfig(Raw raw, IReadOnlyList<int> homeKeys, IReadOnlyList<int> swallowKeys, IReadOnlyList<AppDefinition> apps, WeatherLocation? location, PhoneRemoteSettings phoneRemote)
    {
        Apps = apps;
        HomeKeyVks = homeKeys;
        SwallowKeyVks = swallowKeys;
        BackKeyVk = raw.BackKeyVk;
        RequireHdr = raw.RequireHdr;
        Location = location;
        PhoneRemote = phoneRemote;
        Locale = raw.Locale;
        Unit = raw.Unit;
        Clock = raw.Clock;
        BackgroundRotateSeconds = raw.BackgroundRotateSeconds;
        MotionTime = raw.MotionTime;
        AllowClose = raw.AllowClose;
        SpotlightFolders = raw.SpotlightFolders!.Select(f => Environment.ExpandEnvironmentVariables(f!)).ToList();
        LogPath = Environment.ExpandEnvironmentVariables(raw.LogPath);
    }

    public static LauncherConfig Load(string path)
    {
        Raw raw;
        try
        {
            raw = JsonSerializer.Deserialize<Raw>(File.ReadAllText(path), ReadOptions)
                  ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid: {ex.Message}", ex);
        }

        if (raw.Apps is null || raw.Apps.Count == 0)
            throw new InvalidDataException($"{path}: the Apps field must list at least one app.");
        if (raw.Apps.Any(a => a is null))
            throw new InvalidDataException($"{path}: an entry in Apps is null; every entry must be an app object.");
        if (raw.Apps.Any(a => string.IsNullOrWhiteSpace(a!.Id)))
            throw new InvalidDataException($"{path}: every entry in Apps needs an Id.");
        if (raw.Apps.Select(a => a!.Id).Distinct().Count() != raw.Apps.Count)
            throw new InvalidDataException($"{path}: the Id values in Apps must be unique.");
        var apps = raw.Apps.Select(a => ReadApp(path, a!)).ToList();
        if (raw.MotionTime is not (>= MinMotionTime and <= MaxMotionTime))
            throw new InvalidDataException($"{path}: the MotionTime field must be a number from {MinMotionTime} to {MaxMotionTime} (1 is the normal speed, 2 makes every animation take twice as long), or left out.");
        if (raw.SpotlightFolders is null || raw.SpotlightFolders.Any(f => f is null))
            throw new InvalidDataException($"{path}: the SpotlightFolders field must be a list of folder paths (it may be empty, not null).");

        return new LauncherConfig(raw, ReadHomeKeys(path, raw.HomeKeyVk), ReadSwallowKeys(path, raw.SwallowKeyVks), apps, ReadLocation(path, raw.Location), ReadPhoneRemote(path, raw.PhoneRemote));
    }

    // A single key code or a list of them, each a virtual-key code from 1 to 255.
    private static IReadOnlyList<int> ReadHomeKeys(string path, JsonElement? value)
    {
        InvalidDataException Problem(string what) => new($"{path}: the HomeKeyVk field {what}");
        const string Expected = "must be a key code from 1 to 255, or a list of them, such as 172 or [172, 36].";
        if (value is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw Problem("is missing. It " + Expected);

        var items = element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToList() : [element];
        if (items.Count == 0)
            throw Problem("is an empty list. It " + Expected);
        var keys = new List<int>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var vk) || vk is < 1 or > 255)
                throw Problem($"has the value {item.GetRawText()}. It " + Expected);
            keys.Add(vk);
        }
        return keys;
    }

    // A list of virtual-key codes, each from 1 to 255; missing or empty means none.
    private static IReadOnlyList<int> ReadSwallowKeys(string path, JsonElement? value)
    {
        const string Expected = "must be a list of key codes from 1 to 255, such as [170, 93], or left out.";
        if (value is not { } element || element.ValueKind == JsonValueKind.Undefined)
            return [];
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: the SwallowKeyVks field has the value {element.GetRawText()}. It " + Expected);
        var keys = new List<int>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var vk) || vk is < 1 or > 255)
                throw new InvalidDataException($"{path}: the SwallowKeyVks field has the value {item.GetRawText()}. It " + Expected);
            keys.Add(vk);
        }
        return keys;
    }

    // An app with a Url is a single version; an app with Versions lists them, each a complete entry.
    private static AppDefinition ReadApp(string path, RawApp a)
    {
        string Problem(string what) => $"{path}: app '{a.Id}': {what}";
        if (a.Versions is null)
            return new AppDefinition(a.Id!, a.Name ?? a.Id!, a.Icon, [ReadEntry(path, a, a, "default", a.Name ?? a.Id!, a.Id!, $"app '{a.Id}': ")]);

        if (a.Url is not null)
            throw new InvalidDataException(Problem("has both a Url field and a Versions field. Give the Url to each version in Versions, or drop Versions."));
        if (a.Versions.Count == 0)
            throw new InvalidDataException(Problem("the Versions field is empty; list at least one version."));
        if (a.Versions.Any(v => v is null))
            throw new InvalidDataException(Problem("an entry in the Versions field is null; every entry must be a version object."));
        if (a.Versions.Any(v => string.IsNullOrWhiteSpace(v!.Id)))
            throw new InvalidDataException(Problem("every entry in the Versions field needs an Id."));
        if (a.Versions.Select(v => v!.Id).Distinct().Count() != a.Versions.Count)
            throw new InvalidDataException(Problem("the Id values in the Versions field must be unique."));
        // Each version has its own profile folder, so its own sign-in (measured 2026-11-02: Channel 4's TV app as a Google TV device, client=googletv, refused a sign-in made as a Freeview device, 401 on api.channel4.com/online/v2/auth/token).
        var versions = a.Versions
            .Select(v => ReadEntry(path, a, v!, v!.Id!, v.Name ?? v.Id!, $"{a.Id}-{v.Id}", $"app '{a.Id}': version '{v.Id}': "))
            .ToList();
        return new AppDefinition(a.Id!, a.Name ?? a.Id!, a.Icon, versions);
    }

    // The fields of one version, or of a single-version app. The prefix leads every message.
    private static AppEntry ReadEntry(string path, RawApp app, RawApp a, string versionId, string versionName, string profileId, string where)
    {
        string Problem(string what) => $"{path}: {where}{what}";
        if (string.IsNullOrWhiteSpace(a.Url))
            throw new InvalidDataException(Problem(a.Kind is not null || a.Target is not null
                ? "the Url field is missing. This launcher.json is from the launcher that opened separate windows; "
                  + "replace its Apps section with the one in the repo's launcher.json."
                : "the Url field (the web address the app opens) is missing or blank."));
        if (!Uri.TryCreate(a.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException(Problem($"the Url field must be a full https address, such as https://www.youtube.com/tv, not '{a.Url}'."));
        if (a.BoostGain is { } gain && gain is not (>= MinBoostGain and <= MaxBoostGain))
            throw new InvalidDataException(Problem($"the BoostGain field must be a number from {MinBoostGain} to {MaxBoostGain}, or left out for no boost."));
        // The selector goes into a style rule as it is, so nothing in it may end the rule or the style.
        if (a.HideElements is { } hide && hide.IndexOfAny(['{', '}', '<']) >= 0)
            throw new InvalidDataException(Problem("the HideElements field must be a CSS selector only, without {, } or <."));
        return new AppEntry(
            app.Id!,
            app.Name ?? app.Id!,
            a.Icon ?? app.Icon,
            a.Url,
            string.IsNullOrWhiteSpace(a.UserAgent) ? null : a.UserAgent,
            a.BrowserArgs ?? "",
            a.BoostGain,
            a.Back ?? BackMode.History,
            a.DirectPauseFallback ?? false,
            a.ArrowNav ?? false,
            a.PhoneMic ?? false,
            a.HidePlayReady ?? false,
            string.IsNullOrWhiteSpace(a.PauseButton) ? null : a.PauseButton.Trim(),
            versionId,
            versionName,
            profileId,
            string.IsNullOrWhiteSpace(a.ExitWhenEmpty) ? null : a.ExitWhenEmpty.Trim(),
            string.IsNullOrWhiteSpace(a.HideElements) ? null : a.HideElements.Trim());
    }

    // Missing means no configured place: the viewer's choice or Windows location decides (LocationResolver).
    private static WeatherLocation? ReadLocation(string path, RawLocation? loc)
    {
        if (loc is null) return null;
        if (loc.Latitude is not { } lat || lat is < -90 or > 90)
            throw new InvalidDataException($"{path}: Location.Latitude must be a number from -90 to 90.");
        if (loc.Longitude is not { } lon || lon is < -180 or > 180)
            throw new InvalidDataException($"{path}: Location.Longitude must be a number from -180 to 180.");
        return new WeatherLocation(string.IsNullOrWhiteSpace(loc.Name) ? null : loc.Name.Trim(), lat, lon);
    }

    // Missing means off. The port is checked even when the remote is off, so a typo shows up before it is turned on.
    private static PhoneRemoteSettings ReadPhoneRemote(string path, RawPhoneRemote? phone)
    {
        var port = phone?.Port ?? DefaultPhoneRemotePort;
        if (port is < MinPhoneRemotePort or > MaxPhoneRemotePort)
            throw new InvalidDataException($"{path}: the PhoneRemote.Port field must be a port number from {MinPhoneRemotePort} to {MaxPhoneRemotePort}, or left out for {DefaultPhoneRemotePort}.");
        return new PhoneRemoteSettings(phone?.Enabled ?? false, port);
    }

    // The page reads this as window.TV_HOME_CONFIG, in the shape the vendored custom-tv web app expects.
    // The apps and boostApps are the layout's, so the first paint already has the home row as the family arranged it.
    public string ToPageConfigScript(AppLayout layout)
    {
        var fromLayout = JsonSerializer.SerializeToElement(layout.ToPage(), WriteOptions);
        var page = new
        {
            locale = Locale,
            weatherRefreshMinutes = 15,
            apps = fromLayout.GetProperty("apps"),
            boostApps = fromLayout.GetProperty("boostApps"),
            defaults = new
            {
                language = Locale,
                unit = Unit,
                clock = Clock,
                scenery = "auto",
                effects = true,
            },
            backgroundRotateSeconds = BackgroundRotateSeconds,
            requireHdr = RequireHdr,
            motionTime = MotionTime,
        };
        return "window.TV_HOME_CONFIG = " + JsonSerializer.Serialize(page, WriteOptions) + ";";
    }

    private sealed class Raw
    {
        public List<RawApp?>? Apps { get; set; }
        // A number or a list of numbers, checked by ReadHomeKeys.
        public JsonElement? HomeKeyVk { get; set; }
        // A list of numbers, checked by ReadSwallowKeys.
        public JsonElement? SwallowKeyVks { get; set; }
        // VK_BROWSER_BACK: what the Rii i25's Back key sends (measured 2026-10-26).
        public int BackKeyVk { get; set; } = 166;
        public bool RequireHdr { get; set; } = true;
        public RawLocation? Location { get; set; }
        public RawPhoneRemote? PhoneRemote { get; set; }
        public string Locale { get; set; } = "en-GB";
        public string Unit { get; set; } = "C";
        public string Clock { get; set; } = "24";
        public int BackgroundRotateSeconds { get; set; } = 60;
        public double MotionTime { get; set; } = 1;
        public bool AllowClose { get; set; }
        public List<string?>? SpotlightFolders { get; set; } = new();
        public string LogPath { get; set; } = DefaultLogPath;
    }

    // Every field nullable, so a missing one is told apart from a default value.
    private sealed class RawApp
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public string? Url { get; set; }
        public string? UserAgent { get; set; }
        public string? BrowserArgs { get; set; }
        public double? BoostGain { get; set; }
        public BackMode? Back { get; set; }
        public bool? DirectPauseFallback { get; set; }
        // The launcher moves focus on the remote's arrows (PageScripts.ArrowNav), for a site built for a mouse.
        public bool? ArrowNav { get; set; }
        // This app takes the phone's microphone as its voice search (YouTube).
        public bool? PhoneMic { get; set; }
        // The page is told PlayReady is missing, so it plays through Widevine (PageScripts.HidePlayReady; Channel 4).
        public bool? HidePlayReady { get; set; }
        // The label of the player's pause button, pressed instead of Space when leaving the app (Channel 4: "Pause").
        public string? PauseButton { get; set; }
        // The id of the page's root element: once it has had content and is empty again, the app has exited and the
        // launcher goes home (PageScripts.ExitWhenEmpty; Channel 4's TV app: "appRoot").
        public string? ExitWhenEmpty { get; set; }
        // A CSS selector: what it matches is never shown (PageScripts.HideElements; Prime Video's cookie notice, which
        // the remote's arrows cannot reach).
        public string? HideElements { get; set; }
        // The versions of this app, each read with the rules of an app's own fields (a Url and the rest).
        public List<RawApp?>? Versions { get; set; }

        // Fields of the launcher that opened separate windows, read only to recognise its launcher.json.
        public JsonElement? Kind { get; set; }
        public JsonElement? Target { get; set; }
    }

    private sealed class RawPhoneRemote
    {
        public bool? Enabled { get; set; }
        public int? Port { get; set; }
    }

    private sealed class RawLocation
    {
        public string? Name { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }
}
