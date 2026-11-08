using System.Text.Json;
using TvLauncher.Config;

namespace TvLauncher.Tests;

public sealed class AppLayoutTests
{
    private static AppEntry Entry(string app, string appName, string versionId, string versionName, string? icon, double? gain = null) =>
        new(app, appName, icon, "https://" + app + ".test", null, "", gain, BackMode.Page, false, false, false, false, null,
            versionId, versionName, versionId == "default" ? app : $"{app}-{versionId}");

    // a (one version), b (versions x and y, y boosted), c (one version, boosted).
    private static readonly IReadOnlyList<AppDefinition> Apps =
    [
        new("a", "App A", "a.png", [Entry("a", "App A", "default", "App A", "a.png")]),
        new("b", "App B", "b.png", [Entry("b", "App B", "x", "Version X", "bx.png"), Entry("b", "App B", "y", "Version Y", "by.png", 2)]),
        new("c", "App C", "c.png", [Entry("c", "App C", "default", "App C", "c.png", 3)]),
    ];

    private static IEnumerable<string> Ids(AppLayout layout) => layout.Choices.Select(c => c.Id);

    [Fact]
    public void Resolve_NothingSaved_AllShownInConfigOrderOnTheirDefault()
    {
        var layout = AppLayout.Resolve(Apps, null);

        Assert.Equal(new[] { "a", "b", "c" }, Ids(layout));
        Assert.All(layout.Choices, c => Assert.True(c.Shown));
        Assert.Equal(new[] { "default", "x", "default" }, layout.Choices.Select(c => c.VersionId));
    }

    [Fact]
    public void Resolve_KeepsSavedOrderShownAndVersion()
    {
        var layout = AppLayout.Resolve(Apps, [new("c", true, "default"), new("b", false, "y"), new("a", true, "default")]);

        Assert.Equal(new[] { "c", "b", "a" }, Ids(layout));
        Assert.False(layout.Choices[1].Shown);
        Assert.Equal("y", layout.Chosen("b").VersionId);
    }

    [Fact]
    public void Resolve_DropsSavedIdsNoLongerConfigured()
    {
        var layout = AppLayout.Resolve(Apps, [new("gone", true, "default"), new("b", true, "x"), new("a", true, "default"), new("c", true, "default")]);

        Assert.Equal(new[] { "b", "a", "c" }, Ids(layout));
    }

    [Fact]
    public void Resolve_NewAppIsShownAtTheEndOnItsDefault()
    {
        var layout = AppLayout.Resolve(Apps, [new("b", true, "y"), new("a", false, "default")]);

        Assert.Equal(new[] { "b", "a", "c" }, Ids(layout));
        Assert.Equal(new AppChoice("c", true, "default"), layout.Choices[2]);
    }

    [Fact]
    public void Resolve_UnknownSavedVersion_UsesTheDefault()
    {
        var layout = AppLayout.Resolve(Apps, [new("b", true, "gone")]);

        Assert.Equal("x", layout.Chosen("b").VersionId);
    }

    [Fact]
    public void Resolve_SavedAllHidden_ShowsTheFirst()
    {
        var layout = AppLayout.Resolve(Apps, [new("c", false, "default"), new("a", false, "default"), new("b", false, "x")]);

        Assert.Equal(new[] { true, false, false }, layout.Choices.Select(c => c.Shown));
        Assert.Equal("c", layout.Choices[0].Id);
    }

    [Fact]
    public void Move_ToIndex_ReordersAndClamps()
    {
        var layout = AppLayout.Resolve(Apps, null);

        Assert.Equal(new[] { "b", "c", "a" }, Ids(layout.Move("a", 2)!));
        Assert.Equal(new[] { "b", "c", "a" }, Ids(layout.Move("a", 99)!));
        Assert.Equal(new[] { "c", "a", "b" }, Ids(layout.Move("c", -5)!));
        Assert.Equal(new[] { "a", "b", "c" }, Ids(layout));
    }

    [Fact]
    public void Move_UnknownId_IsNull() => Assert.Null(AppLayout.Resolve(Apps, null).Move("nope", 0));

    [Fact]
    public void Show_HidingTheLastShown_IsNull()
    {
        var layout = AppLayout.Resolve(Apps, null).Show("a", false)!.Show("b", false)!;

        Assert.Null(layout.Show("c", false));
        Assert.Null(layout.Show("nope", true));
    }

    [Fact]
    public void Show_HideAndShowAgain()
    {
        var layout = AppLayout.Resolve(Apps, null);

        var hidden = layout.Show("b", false)!;
        var back = hidden.Show("b", true)!;

        Assert.False(hidden.Choices[1].Shown);
        Assert.True(layout.Choices[1].Shown);
        Assert.True(back.Choices[1].Shown);
    }

    [Fact]
    public void ChooseVersion_SetsIt_AndChosenReturnsThatEntry()
    {
        var layout = AppLayout.Resolve(Apps, null).ChooseVersion("b", "y")!;

        Assert.Equal("y", layout.Choices[1].VersionId);
        Assert.Equal("Version Y", layout.Chosen("b").VersionName);
        Assert.Throws<ArgumentException>(() => layout.Chosen("nope"));
    }

    [Fact]
    public void ChooseVersion_UnknownVersion_IsNull()
    {
        var layout = AppLayout.Resolve(Apps, null);

        Assert.Null(layout.ChooseVersion("b", "nope"));
        Assert.Null(layout.ChooseVersion("nope", "x"));
    }

    [Fact]
    public void BoostAppNames_FollowShownAndChosenVersion()
    {
        var layout = AppLayout.Resolve(Apps, null);

        Assert.Equal(new[] { "App C" }, layout.BoostAppNames);
        var y = layout.ChooseVersion("b", "y")!;
        Assert.Equal(new[] { "App B", "App C" }, y.BoostAppNames);
        Assert.Equal(new[] { "App C" }, y.Show("b", false)!.BoostAppNames);
    }

    [Fact]
    public void ToPage_ListsEveryAppWithItsVersions()
    {
        var layout = AppLayout.Resolve(Apps, null).Show("b", false)!;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(layout.ToPage(), options));
        var root = doc.RootElement;
        var apps = root.GetProperty("apps");

        Assert.Equal(3, apps.GetArrayLength());
        var b = apps[1];
        Assert.Equal("b", b.GetProperty("id").GetString());
        Assert.Equal("App B", b.GetProperty("name").GetString());
        Assert.Equal("bx.png", b.GetProperty("icon").GetString());
        Assert.False(b.GetProperty("shown").GetBoolean());
        Assert.Equal("x", b.GetProperty("version").GetString());
        var versions = b.GetProperty("versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal("x", versions[0].GetProperty("id").GetString());
        Assert.Equal("Version X", versions[0].GetProperty("name").GetString());
        Assert.Equal("y", versions[1].GetProperty("id").GetString());
        Assert.Equal("Version Y", versions[1].GetProperty("name").GetString());
        Assert.Equal(new[] { "App C" }, root.GetProperty("boostApps").EnumerateArray().Select(e => e.GetString()));

        using var after = JsonDocument.Parse(JsonSerializer.Serialize(layout.ChooseVersion("b", "y")!.ToPage(), options));
        Assert.Equal("by.png", after.RootElement.GetProperty("apps")[1].GetProperty("icon").GetString());
    }
}
