using System.IO;
using System.Text.Json;
using TvLauncher.Config;

namespace TvLauncher.Tests;

public sealed class LauncherConfigTests : IDisposable
{
    private const string CobaltUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Cobalt/27.lts.3.1040976-gold (unlike Gecko) v8/13.8.258.54-jit gles Starboard/18, TvLauncher_DESKTOP_x8664_2026/1.0 (TvLauncher, Windows) com.tvos.launcher/1.0";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvlauncher-tests-" + Guid.NewGuid().ToString("N"));

    public LauncherConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "launcher.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string RepoConfigPath => Path.Combine(AppContext.BaseDirectory, "launcher.json");

    private static string ConfigJson(
        string location = """{"Latitude":10,"Longitude":20}""",
        string youtubeName = "YouTube") => $$"""
        {
          "Apps": [
            { "Id": "youtube", "Name": "{{youtubeName}}", "Url": "https://www.youtube.com/tv", "UserAgent": "Cobalt/27",
              "Back": "Page", "DirectPauseFallback": true, "Icon": "icons/youtube.svg" },
            { "Id": "disney", "Name": "Disney+", "Url": "https://www.disneyplus.com", "BrowserArgs": "--enable-spatial-navigation",
              "BoostGain": 4, "ArrowNav": true, "Icon": "icons/disney.svg" }
          ],
          "HomeKeyVk": 172,
          {{(location == "" ? "" : "\"Location\": " + location + ",")}}
          "Locale": "en-GB",
          "Unit": "C",
          "Clock": "24",
          "BackgroundRotateSeconds": 60,
          "SpotlightFolders": [ "%LOCALAPPDATA%\\Packages\\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\\LocalCache\\Microsoft\\IrisService" ],
          "LogPath": "%LOCALAPPDATA%\\TvLauncher\\launcher.log"
        }
        """;

    // The script as the first paint gets it: every app in its default place.
    private static string PageScript(LauncherConfig cfg) => cfg.ToPageConfigScript(AppLayout.Resolve(cfg.Apps, null));

    private static JsonElement ParsedScriptConfig(string script)
    {
        const string prefix = "window.TV_HOME_CONFIG = ";
        Assert.StartsWith(prefix, script);
        Assert.EndsWith(";", script);
        var json = script[prefix.Length..^1];
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Defaults_BackKey166_RequireHdrTrue()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson()));

        Assert.Equal([172], cfg.HomeKeyVks);
        Assert.Empty(cfg.SwallowKeyVks);
        Assert.Equal(166, cfg.BackKeyVk);
        Assert.True(cfg.RequireHdr);
    }

    [Fact]
    public void Load_BackKeyAndRequireHdr_AreRead()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": 172, \"BackKeyVk\": 27, \"RequireHdr\": false,")));

        Assert.Equal(27, cfg.BackKeyVk);
        Assert.False(cfg.RequireHdr);
    }

    [Fact]
    public void Load_HomeKeyVk_SingleNumber_IsAListOfOne()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson()));

        Assert.Equal([172], cfg.HomeKeyVks);
    }

    [Fact]
    public void Load_HomeKeyVk_List_IsRead()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": [172, 36, 255],")));

        Assert.Equal([172, 36, 255], cfg.HomeKeyVks);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("0")]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("172.5")]
    [InlineData("\"172\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[172, 0]")]
    [InlineData("[172, 300]")]
    [InlineData("[172, \"36\"]")]
    [InlineData("[172, null]")]
    [InlineData("[[172]]")]
    public void Load_HomeKeyVk_Invalid_ThrowsNamingTheField(string value)
    {
        var path = Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", $"\"HomeKeyVk\": {value},"));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("HomeKeyVk", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Load_HomeKeyVk_Missing_ThrowsNamingTheField()
    {
        var path = Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", ""));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("HomeKeyVk", ex.Message);
    }

    [Fact]
    public void Load_SwallowKeyVks_MissingIsEmpty_ListIsRead()
    {
        Assert.Empty(LauncherConfig.Load(Write(ConfigJson())).SwallowKeyVks);
        var cfg = LauncherConfig.Load(Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": 172, \"SwallowKeyVks\": [170, 93],")));
        Assert.Equal([170, 93], cfg.SwallowKeyVks);
        var empty = LauncherConfig.Load(Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": 172, \"SwallowKeyVks\": [],")));
        Assert.Empty(empty.SwallowKeyVks);
    }

    [Theory]
    [InlineData("170")]
    [InlineData("\"170\"")]
    [InlineData("[0]")]
    [InlineData("[256]")]
    [InlineData("[170, -1]")]
    [InlineData("[170.5]")]
    [InlineData("[\"93\"]")]
    [InlineData("[null]")]
    [InlineData("[[93]]")]
    public void Load_SwallowKeyVks_Invalid_ThrowsNamingTheField(string value)
    {
        var path = Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", $"\"HomeKeyVk\": 172, \"SwallowKeyVks\": {value},"));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("SwallowKeyVks", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Load_ShippedDefault_HasBackKeyAndRequireHdr()
    {
        var cfg = LauncherConfig.Load(RepoConfigPath);

        Assert.Equal([172], cfg.HomeKeyVks);
        Assert.Equal([170, 93], cfg.SwallowKeyVks);
        Assert.Equal(166, cfg.BackKeyVk);
        Assert.True(cfg.RequireHdr);
    }

    [Fact]
    public void PageConfig_HasRequireHdr()
    {
        var on = ParsedScriptConfig(PageScript(LauncherConfig.Load(Write(ConfigJson()))));
        var off = ParsedScriptConfig(PageScript(LauncherConfig.Load(
            Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": 172, \"RequireHdr\": false,")))));

        Assert.True(on.GetProperty("requireHdr").GetBoolean());
        Assert.False(off.GetProperty("requireHdr").GetBoolean());
    }

    [Fact]
    public void PageConfig_HasTheLayoutsAppsAndBoostApps_AndNoneOfTheOldFields()
    {
        var cfg = LauncherConfig.Load(RepoConfigPath);
        var layout = AppLayout.Resolve(cfg.Apps, null);

        var page = ParsedScriptConfig(cfg.ToPageConfigScript(layout));

        var apps = page.GetProperty("apps").EnumerateArray().ToList();
        Assert.Equal(6, apps.Count);
        var c4 = apps[2];
        Assert.Equal("channel4", c4.GetProperty("id").GetString());
        Assert.Equal("tv", c4.GetProperty("version").GetString());
        Assert.Equal(["tv", "web"], c4.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("id").GetString()));
        Assert.Equal(["Disney+"], page.GetProperty("boostApps").EnumerateArray().Select(b => b.GetString()));
        Assert.False(page.TryGetProperty("homeApps", out _));
        Assert.False(page.TryGetProperty("favoriteOrder", out _));
        Assert.False(page.GetProperty("defaults").TryGetProperty("hidden", out _));
    }

    [Fact]
    public void PageConfig_FollowsTheLayoutsOrderAndHiding()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson()));
        var layout = AppLayout.Resolve(cfg.Apps, null).Move("disney", 0)!.Show("youtube", false)!;

        var page = ParsedScriptConfig(cfg.ToPageConfigScript(layout));

        var apps = page.GetProperty("apps").EnumerateArray().ToList();
        Assert.Equal(["disney", "youtube"], apps.Select(a => a.GetProperty("id").GetString()));
        Assert.Equal([true, false], apps.Select(a => a.GetProperty("shown").GetBoolean()));
    }

    [Fact]
    public void AllowClose_LeftOut_IsOff()
    {
        // The TV's launcher must not close on Alt+F4 by accident; only a config that says so allows it.
        Assert.False(LauncherConfig.Load(Write(ConfigJson())).AllowClose);
    }

    [Fact]
    public void AllowClose_Set_IsRead()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": 172, \"AllowClose\": true,")));

        Assert.True(cfg.AllowClose);
    }

    [Fact]
    public void AllowClose_RepoConfig_IsOff()
    {
        // The repo's launcher.json is the mini PC's starting point.
        Assert.False(LauncherConfig.Load(RepoConfigPath).AllowClose);
    }

    [Fact]
    public void MotionTime_LeftOut_IsTheNormalSpeed()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson()));

        Assert.Equal(1, cfg.MotionTime);
        Assert.Equal(1, ParsedScriptConfig(PageScript(cfg)).GetProperty("motionTime").GetDouble());
    }

    [Fact]
    public void MotionTime_Set_ReachesThePage()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": 172, \"MotionTime\": 1.5,")));

        Assert.Equal(1.5, cfg.MotionTime);
        Assert.Equal(1.5, ParsedScriptConfig(PageScript(cfg)).GetProperty("motionTime").GetDouble());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.4")]
    [InlineData("4.5")]
    [InlineData("-1")]
    public void MotionTime_OutOfRange_ThrowsNamingTheField(string value)
    {
        var path = Write(ConfigJson().Replace("\"HomeKeyVk\": 172,", "\"HomeKeyVk\": 172, \"MotionTime\": " + value + ","));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("MotionTime", ex.Message);
        Assert.Contains("0.5 to 4", ex.Message);
    }

    [Fact]
    public void Load_ValidFile_ReadsBothApps()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson()));

        Assert.Equal(2, cfg.Apps.Count);
        var yt = cfg.Apps[0].Default;
        Assert.Equal("youtube", yt.Id);
        Assert.Equal("YouTube", yt.Name);
        Assert.Equal("https://www.youtube.com/tv", yt.Url);
        Assert.Equal("Cobalt/27", yt.UserAgent);
        Assert.Equal("", yt.BrowserArgs);
        Assert.Null(yt.BoostGain);
        Assert.Equal(BackMode.Page, yt.Back);
        Assert.True(yt.DirectPauseFallback);
        Assert.False(yt.ArrowNav);   // left out: off
        Assert.Equal("icons/youtube.svg", yt.Icon);
        var disney = cfg.Apps[1].Default;
        Assert.Equal("https://www.disneyplus.com", disney.Url);
        Assert.Null(disney.UserAgent);
        Assert.Equal("--enable-spatial-navigation", disney.BrowserArgs);
        Assert.True(disney.ArrowNav);
        Assert.Equal(4, disney.BoostGain);
        // Back and DirectPauseFallback left out: the defaults.
        Assert.Equal(BackMode.History, disney.Back);
        Assert.False(disney.DirectPauseFallback);
        Assert.Equal([172], cfg.HomeKeyVks);
        Assert.NotNull(cfg.Location);
        Assert.Null(cfg.Location.Name);
        Assert.Equal(10, cfg.Location.Latitude);
        Assert.Equal(20, cfg.Location.Longitude);
        Assert.Equal("en-GB", cfg.Locale);
        Assert.Equal("C", cfg.Unit);
        Assert.Equal("24", cfg.Clock);
        Assert.Equal(60, cfg.BackgroundRotateSeconds);
    }

    [Fact]
    public void Load_RepoConfig_ReadsTheSixApps()
    {
        var cfg = LauncherConfig.Load(RepoConfigPath);

        Assert.Equal(["youtube", "disney", "channel4", "netflix", "prime", "plex"], cfg.Apps.Select(a => a.Id));
        var ytApp = cfg.Apps[0];
        var disneyApp = cfg.Apps[1];
        // One version each, in the profile folders the family is already signed in to (WebView2-youtube, WebView2-disney).
        var yt = Assert.Single(ytApp.Versions);
        var disney = Assert.Single(disneyApp.Versions);
        Assert.Equal("youtube", yt.ProfileId);
        Assert.Equal("disney", disney.ProfileId);
        Assert.Equal("default", yt.VersionId);
        Assert.Equal("YouTube", yt.VersionName);
        Assert.Same(yt, ytApp.Default);
        Assert.Equal("YouTube", yt.Name);
        Assert.Equal("https://www.youtube.com/tv", yt.Url);
        Assert.Equal(CobaltUserAgent, yt.UserAgent);
        Assert.Equal("", yt.BrowserArgs);
        Assert.Null(yt.BoostGain);
        Assert.Equal(BackMode.Page, yt.Back);
        Assert.True(yt.DirectPauseFallback);
        Assert.Equal("icons/youtube.svg", yt.Icon);
        Assert.Equal("Disney+", disney.Name);
        Assert.Equal("https://www.disneyplus.com", disney.Url);
        Assert.Null(disney.UserAgent);
        Assert.Equal("--enable-spatial-navigation --autoplay-policy=no-user-gesture-required", disney.BrowserArgs);
        Assert.Equal(4, disney.BoostGain);
        Assert.Equal(BackMode.History, disney.Back);
        Assert.False(disney.DirectPauseFallback);
        Assert.True(disney.ArrowNav);
        Assert.False(yt.ArrowNav);
        Assert.Equal("icons/disney.svg", disney.Icon);
        Assert.True(yt.PhoneMic);
        Assert.False(disney.PhoneMic);
        Assert.Null(yt.PauseButton);
        Assert.Null(disney.PauseButton);
        Assert.False(yt.HidePlayReady);
        Assert.False(disney.HidePlayReady);

        var c4App = cfg.Apps[2];
        Assert.Equal("Channel 4", c4App.Name);
        Assert.Equal(["tv", "web"], c4App.Versions.Select(v => v.VersionId));
        Assert.Equal(["TV", "Website"], c4App.Versions.Select(v => v.VersionName));
        // Each version has its own profile, so its own sign-in: the TV app refused the Freeview-device sign-in (2026-11-02).
        Assert.Equal(["channel4-tv", "channel4-web"], c4App.Versions.Select(v => v.ProfileId));
        // A version's Icon wins over the app's: the TV version the green "4", the website the white one.
        Assert.Equal(["icons/channel4-tv.svg", "icons/channel4-web.svg"], c4App.Versions.Select(v => v.Icon));
        Assert.All(c4App.Versions, v => Assert.Equal("channel4", v.Id));
        Assert.All(c4App.Versions, v => Assert.Equal("Channel 4", v.Name));
        Assert.Same(c4App.Versions[0], c4App.Default);
        Assert.Same(c4App.Versions[1], c4App.Version("web"));
        Assert.Null(c4App.Version("nope"));

        var tv = c4App.Versions[0];
        Assert.Equal("https://freeview.bsd.client.streaming.channel4.com/rc-26.3.0/index.html?device=google", tv.Url);
        // The "google" value of https://assets.bsd.client.streaming.channel4.com/common/uaInjector.js (read 2026-11-02).
        Assert.Equal("Mozilla/5.0 (Linux; Android 12; Chromecast Build/STTL.240508.005; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/128.0.6613.146 Mobile Safari/537.36 WebAppWrapper/1.0.1 (Google TV; Channel 4; google; ; )", tv.UserAgent);
        Assert.Equal(BackMode.Page, tv.Back);
        Assert.Equal("", tv.BrowserArgs);
        Assert.False(tv.HidePlayReady);
        Assert.False(tv.ArrowNav);
        Assert.Null(tv.PauseButton);
        Assert.Null(tv.BoostGain);
        // Its exit empties <div id="appRoot"> and then fails, leaving a dark page (TV app code, read 2026-11-02).
        Assert.Equal("appRoot", tv.ExitWhenEmpty);
        Assert.False(tv.PhoneMic);

        var c4 = c4App.Versions[1];
        Assert.Equal("https://www.channel4.com", c4.Url);
        // Seen as Edge, Channel 4 held at 1024x576; seen as Chrome, 1920x1080 within 6 s (2026-11-02).
        Assert.StartsWith("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", c4.UserAgent);
        Assert.Contains(" Chrome/", c4.UserAgent);
        Assert.DoesNotContain("Edg", c4.UserAgent);
        Assert.Null(c4.BoostGain);
        Assert.Equal(BackMode.History, c4.Back);
        Assert.False(c4.DirectPauseFallback);
        Assert.False(c4.PhoneMic);
        Assert.Null(c4.ExitWhenEmpty);
        // Channel 4 plays through Widevine: with PlayReady it refuses a PC with screens Edge counts as unsafe (2026-11-02).
        Assert.True(c4.HidePlayReady);
        // Spatial navigation alone stuck scrolling 40 px a press and parked on the rows' arrows (2026-11-02).
        Assert.True(c4.ArrowNav);
        Assert.Equal("--enable-spatial-navigation", c4.BrowserArgs);
        // Its player ignores Space; its own Pause button pauses it, where a direct pause put it out of step (2026-11-02).
        Assert.Equal("Pause", c4.PauseButton);
        // Shipped off: on each PC it is switched on by hand once the firewall rule for its exe exists, because
        // listening first makes Windows show its firewall dialog, whose Cancel adds Block rules (ruling R15).
        Assert.False(cfg.PhoneRemote.Enabled);
        Assert.Equal(47800, cfg.PhoneRemote.Port);
    }

    [Fact]
    public void Load_ExpandsLocalAppData()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson()));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.All(cfg.SpotlightFolders, f => Assert.StartsWith(local, f));
        Assert.StartsWith(local, cfg.LogPath);
        Assert.DoesNotContain("%", cfg.LogPath);
    }

    [Fact]
    public void Load_NoLocation_LoadsWithLocationNull()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson(location: "")));

        Assert.Null(cfg.Location);
    }

    [Fact]
    public void Load_LocationWithoutName_Loads()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson(location: """{"Latitude":10,"Longitude":20}""")));

        Assert.NotNull(cfg.Location);
        Assert.Null(cfg.Location.Name);
        Assert.Equal(10, cfg.Location.Latitude);
        Assert.Equal(20, cfg.Location.Longitude);
    }

    [Theory]
    [InlineData("""{"Latitude":91,"Longitude":20}""", "Location.Latitude")]
    [InlineData("""{"Longitude":20}""", "Location.Latitude")]
    [InlineData("""{"Latitude":10,"Longitude":181}""", "Location.Longitude")]
    [InlineData("""{"Latitude":10}""", "Location.Longitude")]
    public void Load_BadLocation_ThrowsNamingTheField(string location, string field)
    {
        var path = Write(ConfigJson(location: location));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Load_ShippedDefault_ParsesAndItsIconsExistInTheWebFolder()
    {
        var cfg = LauncherConfig.Load(RepoConfigPath);

        Assert.Null(cfg.Location);
        Assert.Equal(new[] { "youtube", "disney", "channel4", "netflix", "prime", "plex" }, cfg.Apps.Select(a => a.Id));
        var web = new DirectoryInfo(AppContext.BaseDirectory);
        while (web != null && !Directory.Exists(Path.Combine(web.FullName, "web", "icons"))) web = web.Parent;
        Assert.NotNull(web);
        foreach (var app in cfg.Apps)
        {
            Assert.True(File.Exists(Path.Combine(web!.FullName, "web", app.Icon!)), app.Icon);
            foreach (var version in app.Versions)
                Assert.True(File.Exists(Path.Combine(web!.FullName, "web", version.Icon!)), $"{app.Id}/{version.VersionId}: {version.Icon}");
        }
    }

    [Fact]
    public void RepoConfig_EveryAppIcon_IsInTheWebFolder()
    {
        var web = Path.Combine(AppContext.BaseDirectory, "web");
        var missing = new List<string>();
        foreach (var app in LauncherConfig.Load(RepoConfigPath).Apps)
        {
            if (app.Icon != null && !File.Exists(Path.Combine(web, app.Icon))) missing.Add(app.Icon);
            foreach (var version in app.Versions)
                if (version.Icon != null && !File.Exists(Path.Combine(web, version.Icon))) missing.Add(version.Icon);
        }

        Assert.Empty(missing);
    }

    // One app entry with the given fields, in an otherwise valid file.
    private static string OneApp(string appFields, string spotlight = "[]") => $$"""
        {
          "Apps": [ { "Id": "youtube", "Name": "YouTube", {{appFields}} } ],
          "HomeKeyVk": 172,
          "Location": {"Latitude":10,"Longitude":20},
          "SpotlightFolders": {{spotlight}}
        }
        """;

    private const string GoodFields = "\"Url\": \"https://www.youtube.com/tv\"";

    [Fact]
    public void Load_GoodSingleApp_Loads()
    {
        var cfg = LauncherConfig.Load(Write(OneApp(GoodFields)));

        var def = cfg.Apps.Single();
        var app = Assert.Single(def.Versions);
        Assert.Same(app, def.Default);
        Assert.Equal("default", app.VersionId);
        Assert.Equal("YouTube", app.VersionName);
        Assert.Equal("youtube", app.ProfileId);
        Assert.Equal("https://www.youtube.com/tv", app.Url);
        Assert.Null(app.UserAgent);
        Assert.Equal("", app.BrowserArgs);
        Assert.Null(app.BoostGain);
        Assert.Equal(BackMode.History, app.Back);
        Assert.False(app.DirectPauseFallback);
        Assert.False(app.ArrowNav);
        Assert.False(app.HidePlayReady);
        Assert.Null(app.PauseButton);
        Assert.Null(app.ExitWhenEmpty);
        Assert.Null(app.Icon);
        Assert.Empty(cfg.SpotlightFolders);
    }

    [Theory]
    [InlineData("\"Url\": \"\"", "Url")]
    [InlineData("\"Url\": \"  \"", "Url")]
    [InlineData("\"Icon\": \"icons/youtube.svg\"", "Url")]
    [InlineData("\"Url\": \"www.youtube.com/tv\"", "Url")]
    [InlineData("\"Url\": \"file:///C:/youtube.html\"", "Url")]
    [InlineData("\"Url\": \"https://www.youtube.com/tv\", \"BoostGain\": 0", "BoostGain")]
    [InlineData("\"Url\": \"https://www.youtube.com/tv\", \"BoostGain\": -4", "BoostGain")]
    public void Load_BadAppField_ThrowsNamingFileAndField(string appFields, string field)
    {
        var path = Write(OneApp(appFields));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("app 'youtube'", ex.Message);
        Assert.Contains(field, ex.Message);
    }

    [Theory]
    // The entry the launcher that opened separate windows shipped, and parts of it.
    [InlineData("\"Kind\": \"Exe\", \"Target\": \"C:\\\\VacuumTube.exe\", \"Args\": \"--remote-debugging-port=9223\", \"ProcessName\": \"VacuumTube\", \"CdpPort\": 9223")]
    [InlineData("\"Kind\": \"EdgeApp\"")]
    [InlineData("\"Kind\": 1")]
    [InlineData("\"Target\": \"C:\\\\VacuumTube.exe\"")]
    public void Load_MissingUrl_OldShape_ExplainsReplacement(string appFields)
    {
        var path = Write(OneApp(appFields));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Equal(
            $"{path}: app 'youtube': the Url field is missing. This launcher.json is from the launcher that opened separate windows; "
            + "replace its Apps section with the one in the repo's launcher.json.",
            ex.Message);
    }

    [Fact]
    public void Load_MissingUrl_NewShape_DoesNotMentionTheOldLauncher()
    {
        var path = Write(OneApp("\"Icon\": \"icons/youtube.svg\""));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("Url", ex.Message);
        Assert.DoesNotContain("separate windows", ex.Message);
    }

    [Fact]
    public void Load_HttpUrl_Rejected()
    {
        var path = Write(OneApp("\"Url\": \"http://www.youtube.com/tv\""));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("Url", ex.Message);
        Assert.Contains("https", ex.Message);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("9")]
    public void Load_BoostGainOutOfRange_Rejected(string gain)
    {
        var path = Write(OneApp(GoodFields + ", \"BoostGain\": " + gain));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("BoostGain", ex.Message);
        Assert.Contains("1 to 8", ex.Message);
    }

    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("8", 8.0)]
    [InlineData("2.5", 2.5)]
    public void Load_BoostGainInRange_Read(string gain, double expected)
    {
        var cfg = LauncherConfig.Load(Write(OneApp(GoodFields + ", \"BoostGain\": " + gain)));

        Assert.Equal(expected, cfg.Apps.Single().Default.BoostGain);
    }

    [Fact]
    public void Load_BackModeByNumber_Rejected()
    {
        // A number would silently pick whichever mode has that position in the enum.
        var path = Write(OneApp(GoodFields + ", \"Back\": 1"));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("Back", ex.Message);
    }

    [Fact]
    public void Load_UnknownBackMode_Rejected()
    {
        var path = Write(OneApp(GoodFields + ", \"Back\": \"Escape\""));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("Back", ex.Message);
    }

    [Fact]
    public void Load_NullAppEntry_ThrowsNamingFileAndField()
    {
        var path = Write("""
            { "Apps": [ null ], "Location": {"Latitude":10,"Longitude":20} }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("Apps", ex.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[ null ]")]
    public void Load_NullSpotlightFolders_ThrowsNamingFileAndField(string spotlight)
    {
        var path = Write(OneApp(GoodFields, spotlight));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("SpotlightFolders", ex.Message);
    }

    private const string VersionsFile = """
        {
          "Apps": [ { "Id": "c4", "Name": "Channel 4", "Icon": "icons/app.svg", "Versions": [
            { "Id": "tv", "Name": "TV", "Url": "https://tv.example.com", "Icon": "icons/tv.svg", "Back": "Page" },
            { "Id": "web", "Url": "https://web.example.com", "BoostGain": 2 }
          ] } ],
          "HomeKeyVk": 172,
          "Location": {"Latitude":10,"Longitude":20},
          "SpotlightFolders": []
        }
        """;

    [Fact]
    public void Load_AppWithVersions_ReadsEachVersionAsAFullEntry()
    {
        var app = LauncherConfig.Load(Write(VersionsFile)).Apps.Single();

        Assert.Equal("c4", app.Id);
        Assert.Equal("Channel 4", app.Name);
        Assert.Equal("icons/app.svg", app.Icon);
        Assert.Equal(["tv", "web"], app.Versions.Select(v => v.VersionId));
        var tv = app.Versions[0];
        Assert.Same(tv, app.Default);
        Assert.Equal("c4", tv.Id);
        Assert.Equal("Channel 4", tv.Name);
        Assert.Equal("TV", tv.VersionName);
        Assert.Equal("c4-tv", tv.ProfileId);
        Assert.Equal("https://tv.example.com", tv.Url);
        Assert.Equal(BackMode.Page, tv.Back);
        var web = app.Versions[1];
        Assert.Same(web, app.Version("web"));
        Assert.Equal("c4", web.Id);
        Assert.Equal("web", web.VersionName);   // no Name: the version Id
        Assert.Equal("c4-web", web.ProfileId);
        Assert.Equal(BackMode.History, web.Back);   // each version is complete: nothing is inherited from its sibling
        Assert.Equal(2, web.BoostGain);
        Assert.Null(app.Version("missing"));
    }

    [Fact]
    public void Load_AppWithVersions_VersionIconWinsOverTheApps_ElseTheAppsIsUsed()
    {
        var app = LauncherConfig.Load(Write(VersionsFile)).Apps.Single();

        Assert.Equal("icons/tv.svg", app.Versions[0].Icon);
        Assert.Equal("icons/app.svg", app.Versions[1].Icon);
    }

    [Fact]
    public void PageConfig_AppWithVersions_AppsUseTheDefaultVersionsIcon()
    {
        var root = ParsedScriptConfig(PageScript(LauncherConfig.Load(Write(VersionsFile))));

        var apps = root.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("c4", apps[0].GetProperty("id").GetString());
        Assert.Equal("Channel 4", apps[0].GetProperty("name").GetString());
        Assert.Equal("icons/tv.svg", apps[0].GetProperty("icon").GetString());
        Assert.Equal(0, root.GetProperty("boostApps").GetArrayLength());   // the default version has no BoostGain
    }

    private const string TvVersion = "{ \"Id\": \"tv\", \"Url\": \"https://tv.example.com\" }";

    [Theory]
    // The app's own Url and Versions together: which would win?
    [InlineData("\"Url\": \"https://www.youtube.com/tv\", \"Versions\": [ " + TvVersion + " ]", "Versions")]
    [InlineData("\"Versions\": []", "Versions")]
    [InlineData("\"Versions\": [ " + TvVersion + ", " + TvVersion + " ]", "Id")]
    [InlineData("\"Versions\": [ { \"Url\": \"https://tv.example.com\" } ]", "Id")]
    [InlineData("\"Versions\": [ { \"Id\": \"  \", \"Url\": \"https://tv.example.com\" } ]", "Id")]
    [InlineData("\"Versions\": [ { \"Id\": \"tv\" } ]", "Url")]
    [InlineData("\"Versions\": [ { \"Id\": \"tv\", \"Url\": \"http://tv.example.com\" } ]", "Url")]
    [InlineData("\"Versions\": [ { \"Id\": \"tv\", \"Url\": \"https://tv.example.com\", \"BoostGain\": 0 } ]", "BoostGain")]
    [InlineData("\"Versions\": [ null ]", "Versions")]
    public void Load_AppWithVersions_Invalid_ThrowsNamingAppAndField(string appFields, string field)
    {
        var path = Write(OneApp(appFields));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("app 'youtube'", ex.Message);
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Load_AppWithVersions_VersionWithoutUrl_NamesTheVersion()
    {
        var path = Write(OneApp("\"Versions\": [ { \"Id\": \"tv\" } ]"));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("version 'tv'", ex.Message);
    }

    [Fact]
    public void PhoneRemoteMissingMeansOff()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson()));

        Assert.False(cfg.PhoneRemote.Enabled);
        Assert.Equal(47800, cfg.PhoneRemote.Port);
    }

    [Fact]
    public void PhoneRemoteReadsEnabledAndPort()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson().Replace("\"HomeKeyVk\": 172,",
            "\"HomeKeyVk\": 172, \"PhoneRemote\": { \"Enabled\": true, \"Port\": 47811 },")));

        Assert.True(cfg.PhoneRemote.Enabled);
        Assert.Equal(47811, cfg.PhoneRemote.Port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("80")]
    [InlineData("1023")]
    [InlineData("65536")]
    public void PhoneRemotePortOutOfRangeIsRejected(string port)
    {
        var path = Write(ConfigJson().Replace("\"HomeKeyVk\": 172,",
            $"\"HomeKeyVk\": 172, \"PhoneRemote\": {{ \"Enabled\": true, \"Port\": {port} }},"));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.Contains("PhoneRemote.Port", ex.Message);
        Assert.Contains("1024 to 65535", ex.Message);
    }

    [Fact]
    public void PhoneMicDefaultsToFalseAndReadsTrue()
    {
        Assert.False(LauncherConfig.Load(Write(OneApp(GoodFields))).Apps.Single().Default.PhoneMic);
        Assert.True(LauncherConfig.Load(Write(OneApp(GoodFields + ", \"PhoneMic\": true"))).Apps.Single().Default.PhoneMic);
    }

    [Fact]
    public void HideElements_MissingOrBlankIsNull_ASelectorIsReadTrimmed()
    {
        Assert.Null(LauncherConfig.Load(Write(OneApp(GoodFields))).Apps.Single().Default.HideElements);
        Assert.Null(LauncherConfig.Load(Write(OneApp(GoodFields + ", \"HideElements\": \"  \""))).Apps.Single().Default.HideElements);
        Assert.Equal("[data-testid=\"x\"]:has(a[href*=\"privacyprefs\"])",
            LauncherConfig.Load(Write(OneApp(GoodFields + ", \"HideElements\": \" [data-testid=\\\"x\\\"]:has(a[href*=\\\"privacyprefs\\\"]) \""))).Apps.Single().Default.HideElements);
    }

    [Theory]
    [InlineData("div { color: red }")]
    [InlineData("div}")]
    [InlineData("</style><script>")]
    public void HideElements_ThatCouldEndTheRule_ThrowsNamingTheField(string selector)
    {
        var path = Write(OneApp(GoodFields + ", \"HideElements\": " + JsonSerializer.Serialize(selector)));

        var ex = Assert.Throws<InvalidDataException>(() => LauncherConfig.Load(path));
        Assert.Contains("HideElements", ex.Message);
        Assert.Contains("youtube", ex.Message);
    }

    [Fact]
    public void PageConfig_HasBoostApps()
    {
        var root = ParsedScriptConfig(PageScript(LauncherConfig.Load(RepoConfigPath)));

        Assert.Equal(["Disney+"], root.GetProperty("boostApps").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void PageConfig_NoBoostGain_EmptyBoostApps()
    {
        var root = ParsedScriptConfig(PageScript(LauncherConfig.Load(Write(OneApp(GoodFields)))));

        Assert.Equal(0, root.GetProperty("boostApps").GetArrayLength());
    }

    [Fact]
    public void ToPageConfigScript_EscapesNamesAndMatchesCustomTvShape()
    {
        var cfg = LauncherConfig.Load(Write(ConfigJson(youtubeName: "</script><b>\\\"Tube\\\"")));
        var script = PageScript(cfg);

        Assert.DoesNotContain("</script", script, StringComparison.OrdinalIgnoreCase);
        var root = ParsedScriptConfig(script);

        Assert.False(root.TryGetProperty("location", out _));
        Assert.Equal("en-GB", root.GetProperty("locale").GetString());
        Assert.Equal(15, root.GetProperty("weatherRefreshMinutes").GetInt32());
        Assert.Equal(60, root.GetProperty("backgroundRotateSeconds").GetInt32());

        var apps = root.GetProperty("apps");
        Assert.Equal(2, apps.GetArrayLength());
        Assert.Equal("youtube", apps[0].GetProperty("id").GetString());
        Assert.Equal("</script><b>\"Tube\"", apps[0].GetProperty("name").GetString());
        Assert.Equal("icons/youtube.svg", apps[0].GetProperty("icon").GetString());
        Assert.Equal("disney", apps[1].GetProperty("id").GetString());

        var d = root.GetProperty("defaults");
        Assert.Equal("en-GB", d.GetProperty("language").GetString());
        Assert.Equal("C", d.GetProperty("unit").GetString());
        Assert.Equal("24", d.GetProperty("clock").GetString());
        Assert.Equal("auto", d.GetProperty("scenery").GetString());
        Assert.True(d.GetProperty("effects").GetBoolean());
    }
}
