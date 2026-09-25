using System.Globalization;
using System.Text.Json;

namespace GitGe.Site;

// The contract with `gitge.cs prepare`: the shape of _build/site-data.json.
// Keep in step with SiteData/SiteProject in gitge.cs.

public sealed class SiteData
{
    public DateTimeOffset GeneratedAt { get; set; }
    public string SiteUrl { get; set; } = "";
    public string RepoUrl { get; set; } = "";
    public string MaintainerLogin { get; set; } = "";
    public int ListingMinStars { get; set; }
    public SiteTrend Trend { get; set; } = new();
    public List<string> Categories { get; set; } = [];
    public int Developers { get; set; }
    public List<SiteProject> Projects { get; set; } = [];
    public List<string> NewThisMonth { get; set; } = [];
    public List<string> RecentlyActive { get; set; } = [];
    public List<string> Spotlight { get; set; } = [];
    public List<HelpWantedIssue> Issues { get; set; } = [];
}

public sealed class SiteTrend
{
    public string Current { get; set; } = "";
    public string? Baseline { get; set; }
    public int WindowDays { get; set; }

    public bool HasHistory => Baseline is not null;

    // First date on which a baseline can exist (window minus tolerance of 7 days).
    public string FirstTrendDate =>
        DateOnly.ParseExact(Current, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(WindowDays - 7).ToString("yyyy-MM-dd");
}

public sealed class SiteProject
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FullName { get; set; }
    public string Owner { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public string? DescriptionKa { get; set; }
    public string? Language { get; set; }
    public List<string> Topics { get; set; } = [];
    public int? Stars { get; set; }
    public int? Trend { get; set; }
    public int? HelpWanted { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string Category { get; set; } = "other";
    public bool Archived { get; set; }
    public bool Featured { get; set; }
    public bool Verified { get; set; }
    public bool Maintainer { get; set; }
    public bool Listed { get; set; }
    public string Source { get; set; } = "";

    // Anchor on /help-wanted/ for this project's issues.
    public string Anchor => "p-" + Key.Replace(':', '-').Replace('/', '-');
}

public sealed class HelpWantedIssue
{
    public string Project { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset? CreatedAt { get; set; }
    public List<string> Labels { get; set; } = [];
}

public sealed record Roundup(string Slug, string Title, DateOnly Date, string? Summary, string Html);

// Everything a page needs, loaded once. The renderer is a batch job that runs
// once per build, so a static context is simpler than threading it through.
public static class Site
{
    public static SiteData Data { get; set; } = new();
    public static Strings Strings { get; set; } = new([], []);
    public static List<Roundup> Roundups { get; set; } = [];
    public static Dictionary<string, string> Pages { get; set; } = [];     // name.lang -> HTML
    public static Dictionary<string, string> AssetVersions { get; set; } = [];

    static Dictionary<string, SiteProject>? byKey;
    public static SiteProject Project(string key) => (byKey ??= Data.Projects.ToDictionary(p => p.Key))[key];

    public static IEnumerable<SiteProject> Listed => Data.Projects.Where(p => p.Listed);

    // Default order: trend when there is history to compute it from, stars otherwise.
    public static IEnumerable<SiteProject> DefaultOrder(IEnumerable<SiteProject> projects) =>
        Data.Trend.HasHistory
            ? projects.OrderByDescending(p => p.Trend ?? int.MinValue).ThenByDescending(p => p.Stars ?? 0)
            : projects.OrderByDescending(p => p.Stars ?? 0).ThenByDescending(p => p.PushedAt);

    // Cache-busting URL for a file under wwwroot.
    public static string Asset(string path) =>
        AssetVersions.TryGetValue(path, out var v) ? $"/{path}?v={v}" : $"/{path}";

    public static string Url(string path) => Data.SiteUrl.TrimEnd('/') + path;
}

public static class Format
{
    public static string Number(int? n) => n is { } v ? v.ToString("#,0", CultureInfo.InvariantCulture) : "—";

    public static string Trend(int? t) => t switch
    {
        null => "—",
        > 0 => "+" + Number(t),
        < 0 => "−" + Number(-t),
        _ => "0",
    };

    public static string Date(DateTimeOffset? d) => d?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
}

// UI strings from i18n/ka.json and i18n/en.json. Pages are rendered in Georgian;
// app.js swaps in English from the same keys.
public sealed class Strings(Dictionary<string, string> ka, Dictionary<string, string> en)
{
    public Dictionary<string, string> Georgian => ka;
    public Dictionary<string, string> English => en;

    public static Strings Load(string dir)
    {
        var ka = Read(Path.Combine(dir, "ka.json"));
        var en = Read(Path.Combine(dir, "en.json"));
        var missing = ka.Keys.Except(en.Keys).Concat(en.Keys.Except(ka.Keys)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"i18n/ka.json and i18n/en.json differ in keys: {string.Join(", ", missing)}");
        return new(ka, en);
    }

    public string Ka(string key, params object?[] args) =>
        ka.TryGetValue(key, out var s)
            ? args.Length == 0 ? s : string.Format(CultureInfo.InvariantCulture, s, args)
            : throw new KeyNotFoundException($"Missing UI string '{key}' in i18n/ka.json");

    static Dictionary<string, string> Read(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"Could not read {path}");
}
