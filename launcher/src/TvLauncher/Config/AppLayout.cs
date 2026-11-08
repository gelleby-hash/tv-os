namespace TvLauncher.Config;

/// <summary>One app's place in the owner's layout: whether it is on the home row and which version it plays.</summary>
public sealed record AppChoice(string Id, bool Shown, string VersionId);

/// <summary>
/// The home row as the family arranged it: every configured app in order, each shown or hidden, each on a chosen
/// version. Immutable; a change returns a new layout, or null when it is refused or the id is unknown.
/// </summary>
public sealed class AppLayout
{
    private readonly IReadOnlyList<AppDefinition> _apps;

    private AppLayout(IReadOnlyList<AppDefinition> apps, IReadOnlyList<AppChoice> choices)
    {
        _apps = apps;
        Choices = choices;
    }

    /// <summary>Every configured app, in the order of the home row, hidden ones included.</summary>
    public IReadOnlyList<AppChoice> Choices { get; }

    /// <summary>
    /// The saved layout made to fit the configuration: ids no longer configured are dropped, an unknown version falls
    /// back to the default, and an app the file has never seen is shown at the end. A file that hides everything
    /// shows the first app, so the home row is never empty.
    /// </summary>
    public static AppLayout Resolve(IReadOnlyList<AppDefinition> apps, IReadOnlyList<AppChoice>? saved)
    {
        var choices = new List<AppChoice>();
        foreach (var s in saved ?? [])
        {
            var app = apps.FirstOrDefault(a => a.Id == s.Id);
            if (app is null || choices.Any(c => c.Id == s.Id)) continue;
            choices.Add(new AppChoice(s.Id, s.Shown, app.Version(s.VersionId)?.VersionId ?? app.Default.VersionId));
        }
        foreach (var app in apps.Where(a => choices.All(c => c.Id != a.Id)))
            choices.Add(new AppChoice(app.Id, true, app.Default.VersionId));
        if (choices.Count > 0 && choices.All(c => !c.Shown))
            choices[0] = choices[0] with { Shown = true };
        return new AppLayout(apps, choices);
    }

    /// <summary>The entry the app plays: its chosen version.</summary>
    public AppEntry Chosen(string appId)
    {
        var choice = Choices.FirstOrDefault(c => c.Id == appId)
                     ?? throw new ArgumentException($"No app with Id '{appId}' in the configuration.", nameof(appId));
        var app = _apps.First(a => a.Id == appId);
        return app.Version(choice.VersionId) ?? app.Default;
    }

    /// <summary>The app moved to a place in <see cref="Choices"/> (clamped to the ends).</summary>
    public AppLayout? Move(string appId, int toIndex)
    {
        var from = IndexOf(appId);
        if (from < 0) return null;
        var list = Choices.ToList();
        var choice = list[from];
        list.RemoveAt(from);
        list.Insert(Math.Clamp(toIndex, 0, list.Count), choice);
        return new AppLayout(_apps, list);
    }

    /// <summary>The app shown or hidden; null when it would hide the last shown app.</summary>
    public AppLayout? Show(string appId, bool shown) =>
        Replace(appId, c => c with { Shown = shown }, list => list.Any(c => c.Shown));

    public AppLayout? ChooseVersion(string appId, string versionId) =>
        _apps.FirstOrDefault(a => a.Id == appId)?.Version(versionId) is null
            ? null
            : Replace(appId, c => c with { VersionId = versionId }, _ => true);

    /// <summary>The names of the shown apps whose chosen version the boost button raises the sound of.</summary>
    public IReadOnlyList<string> BoostAppNames =>
        Choices.Where(c => c.Shown && Chosen(c.Id).BoostGain is not null)
            .Select(c => _apps.First(a => a.Id == c.Id).Name)
            .ToList();

    /// <summary>What the page draws the Apps screen and the home row from.</summary>
    public object ToPage() => new
    {
        apps = Choices.Select(c =>
        {
            var app = _apps.First(a => a.Id == c.Id);
            return new
            {
                id = app.Id,
                name = app.Name,
                icon = Chosen(c.Id).Icon,
                shown = c.Shown,
                version = c.VersionId,
                versions = app.Versions.Select(v => new { id = v.VersionId, name = v.VersionName }).ToList(),
            };
        }).ToList(),
        boostApps = BoostAppNames,
    };

    private int IndexOf(string appId)
    {
        for (var i = 0; i < Choices.Count; i++)
            if (Choices[i].Id == appId) return i;
        return -1;
    }

    private AppLayout? Replace(string appId, Func<AppChoice, AppChoice> change, Func<List<AppChoice>, bool> accept)
    {
        var i = IndexOf(appId);
        if (i < 0) return null;
        var list = Choices.ToList();
        list[i] = change(list[i]);
        return accept(list) ? new AppLayout(_apps, list) : null;
    }
}
