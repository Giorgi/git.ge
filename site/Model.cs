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
    public List<SiteDeveloper> DeveloperPages { get; set; } = [];
}

// One developer page (/@<login>/). Keep in step with SiteDeveloper in gitge.cs.
public sealed class SiteDeveloper
{
    public string Login { get; set; } = "";
    public string? Name { get; set; }
    public string Type { get; set; } = "User";
    public string Url { get; set; } = "";
    public List<string> Projects { get; set; } = [];
    public List<string> Smaller { get; set; } = [];
    public int Stars { get; set; }
    public List<string> Languages { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public int HelpWanted { get; set; }
    public string? FirstSeenAt { get; set; }
    public DateTimeOffset? LastPush { get; set; }
    public List<SiteRank> Ranks { get; set; } = [];
    public List<string> Spotlight { get; set; } = [];
    public List<string> Roundups { get; set; } = [];
    public List<SiteContribution> ContributesTo { get; set; } = [];
    public List<SiteContributor> Contributors { get; set; } = [];
    public List<string> FollowedBy { get; set; } = [];
    public List<string> Follows { get; set; } = [];
    public List<string> Mutual { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Login : Name!;
    public string Path => $"/@{Login.ToLowerInvariant()}/";
    public bool IsOrg => Type == "Organization";
}

public sealed class SiteRank
{
    public string Project { get; set; } = "";
    public string Category { get; set; } = "";
    public int Rank { get; set; }
    public int Of { get; set; }
}

public sealed class SiteContribution
{
    public string Project { get; set; } = "";
    public int Commits { get; set; }
}

public sealed class SiteContributor
{
    public string Login { get; set; } = "";
    public int Commits { get; set; }
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

    // A URL someone submitted (a project hosted outside GitHub): linked with
    // rel="nofollow ugc", since git.ge doesn't vouch for it.
    public bool IsSubmittedUrl => FullName is null;

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
    public bool Assigned { get; set; }
    public List<string> Labels { get; set; } = [];
    // Ranking score from prepare (popularity × freshness × assigned factor); issues
    // arrive in display order, so the page only needs it for the order of projects.
    public double? Score { get; set; }
}

// One roundup: Georgian, plus English when <slug>.en.md exists (otherwise the
// Georgian text is used for both).
public sealed record RoundupText(string Title, string? Summary, string Html);

public sealed record Roundup(string Slug, DateOnly Date, RoundupText Ka, RoundupText En)
{
    public bool HasEnglish => !ReferenceEquals(Ka, En);
}

// Everything a page needs, loaded once. The renderer is a batch job that runs
// once per build, so a static context is simpler than threading it through.
public static class Site
{
    public static SiteData Data { get; set; } = new();
    public static Strings Strings { get; set; } = new([], []);
    public static List<Roundup> Roundups { get; set; } = [];
    public static Dictionary<string, string> Pages { get; set; } = [];     // name.lang -> HTML
    public static Dictionary<string, string> AssetVersions { get; set; } = [];

    // "New on git.ge" sort: offered once at least this many developers were first seen
    // after the earliest first-seen date (config/site.json developerPages.firstSeenSortMin).
    public static int FirstSeenSortMin { get; set; } = 20;

    static Dictionary<string, SiteProject>? byKey;
    public static SiteProject Project(string key) => (byKey ??= Data.Projects.ToDictionary(p => p.Key))[key];

    public static IEnumerable<SiteProject> Listed => Data.Projects.Where(p => p.Listed);

    // Developer pages by lower-cased login, and self-hosted avatar URLs (/avatars/<login>.<ext>).
    static Dictionary<string, SiteDeveloper>? developers;
    public static Dictionary<string, SiteDeveloper> Developers =>
        developers ??= Data.DeveloperPages.ToDictionary(d => d.Login.ToLowerInvariant());
    public static Dictionary<string, string> Avatars { get; set; } = [];

    public static SiteDeveloper? Developer(string login) => Developers.GetValueOrDefault(login.ToLowerInvariant());

    // Where a project's owner links: their git.ge page when they have one, else GitHub.
    public static string OwnerHref(string owner) =>
        Developer(owner) is { } d ? d.Path : $"https://github.com/{owner}";

    public static string? Avatar(string login) => Avatars.GetValueOrDefault(login.ToLowerInvariant());

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

    // Georgian ordinals written with digits, per grammar.emis.ge (exercise 179):
    // 1 → "1-ელი" (current norm); "მე-N" where the numeral word starts with მე-:
    // 2–20 (მეორე … მეოცე), 40/60/80 (მეორმოცე …), 100, 200 … 900 (მეასე, მეორასე …)
    // and 1000 (მეათასე); everything else ends in -ე: 21 → "21-ე" (ოცდამეერთე),
    // 30 → "30-ე", 101 → "101-ე", 203 → "203-ე". Georgian ordinals are produced only
    // here (rendered server-side); app.js never makes them.
    public static string OrdinalKa(int n) =>
        n == 1 ? "1-ელი"
        : (n >= 2 && n <= 20) || n is 40 or 60 or 80 || (n % 100 == 0 && n >= 100 && n <= 1000) ? $"მე-{n}"
        : $"{n}-ე";

    // English ordinals: 1st, 2nd, 3rd, 4th, … 11th, 12th, 13th, … 21st, 22nd, 23rd, …
    public static string OrdinalEn(int n) =>
        n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
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

// Category and language filters on /developers/. A developer matches a category or a
// language when ANY of their published projects (listed or search-only) has it.
public static class DeveloperFilters
{
    static IEnumerable<SiteProject> Published(SiteDeveloper d) =>
        d.Projects.Concat(d.Smaller).Select(Site.Project);

    // Categories in config order.
    public static List<string> CategoriesOf(SiteDeveloper d)
    {
        var mine = Published(d).Select(p => p.Category).ToHashSet();
        return Site.Data.Categories.Where(mine.Contains).ToList();
    }

    public static List<string> LanguagesOf(SiteDeveloper d) =>
        Published(d).Select(p => p.Language).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

    // Category + language filtering needs ONE project with both. This encodes which
    // languages each category's projects use: "mobile:Kotlin,Swift|web:C#,TypeScript"
    // (category ids have no ':' and GitHub language names contain no ',' ':' or '|').
    // Null when data-cats × data-langs already gives the right answer, which keeps the
    // page small: a single category (every project is in it), or a single language on
    // every project.
    public static string? PairsOf(SiteDeveloper d)
    {
        var projects = Published(d).ToList();
        var categories = projects.Select(p => p.Category).Distinct().ToList();
        var languages = projects.Select(p => p.Language).OfType<string>().Distinct().ToList();
        var allHaveLanguage = projects.All(p => p.Language is not null);
        if (categories.Count <= 1 || languages.Count == 0 || (languages.Count == 1 && allHaveLanguage))
            return null;
        return string.Join("|", Site.Data.Categories
            .Where(categories.Contains)
            .Select(c => (Category: c, Languages: projects.Where(p => p.Category == c).Select(p => p.Language).OfType<string>()
                                                          .Distinct().Order(StringComparer.Ordinal).ToList()))
            .Where(x => x.Languages.Count > 0)
            .Select(x => x.Category + ":" + string.Join(",", x.Languages)));
    }

    // Every category in config order, with the number of developers who have it.
    public static List<(string Key, int Count)> CategoryCounts(IEnumerable<SiteDeveloper> developers)
    {
        var sets = developers.Select(CategoriesOf).ToList();
        return Site.Data.Categories.Select(c => (c, sets.Count(s => s.Contains(c)))).ToList();
    }

    // Options for the filter controls: (value, Georgian label, developer count), with
    // empty categories left out. Languages by count, then alphabetically.
    public static IEnumerable<(string Value, string Label, int Count)> CategoryOptions(IEnumerable<SiteDeveloper> developers) =>
        CategoryCounts(developers).Where(c => c.Count > 0).Select(c => (c.Key, Site.Strings.Ka($"cat.{c.Key}"), c.Count));

    public static IEnumerable<(string Value, string Label, int Count)> LanguageOptions(IEnumerable<SiteDeveloper> developers) =>
        LanguageCounts(developers).Select(l => (l.Language, l.Language, l.Count));

    // "New on git.ge" only sorts something useful once enough developers arrived after
    // the first import (everyone found at launch shares its date): at least `min`
    // developers first seen on a later day than the earliest one.
    public static bool ShowFirstSeenSort(IEnumerable<SiteDeveloper> developers, int? min = null)
    {
        var days = developers.Select(d => d.FirstSeenAt).OfType<string>().Where(s => s.Length >= 10).Select(s => s[..10]).ToList();
        if (days.Count == 0) return false;
        var earliest = days.Min(StringComparer.Ordinal)!;
        return days.Count(d => string.CompareOrdinal(d, earliest) > 0) >= Math.Max(1, min ?? Site.FirstSeenSortMin);
    }

    // Languages by number of developers, most common first.
    public static List<(string Language, int Count)> LanguageCounts(IEnumerable<SiteDeveloper> developers) =>
        developers.SelectMany(LanguagesOf)
            .GroupBy(l => l)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

// Filters on /help-wanted/: category and language of the issue's project, and the
// issue's label. The unit everywhere is an issue (counts, matching); a project section
// shows while it has at least one matching issue.
public static class HelpWantedFilters
{
    // The two labels the page is about, as filter values. Label names vary between repos
    // ("good first issue", "good-first-issue", "Help Wanted"); anything else is ignored.
    public static readonly string[] LabelValues = ["good-first-issue", "help-wanted"];

    public static List<string> LabelsOf(HelpWantedIssue issue) =>
        issue.Labels
            .Select(l => string.Join("-", l.Trim().ToLowerInvariant().Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)))
            .Where(LabelValues.Contains)
            .Distinct()
            .OrderBy(l => Array.IndexOf(LabelValues, l))
            .ToList();

    // "good-first-issue" → "good first issue": GitHub's own wording, the same in both languages.
    public static string LabelText(string value) => value.Replace('-', ' ');

    public static IEnumerable<(string Value, string Label, int Count)> CategoryOptions(IReadOnlyList<HelpWantedIssue> issues) =>
        Site.Data.Categories
            .Select(c => (c, Site.Strings.Ka($"cat.{c}"), issues.Count(i => Site.Project(i.Project).Category == c)))
            .Where(x => x.Item3 > 0);

    public static IEnumerable<(string Value, string Label, int Count)> LanguageOptions(IReadOnlyList<HelpWantedIssue> issues) =>
        issues.Select(i => Site.Project(i.Project).Language).OfType<string>()
            .GroupBy(l => l)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Key, g.Count()));

    public static IEnumerable<(string Value, string Label, int Count)> LabelOptions(IReadOnlyList<HelpWantedIssue> issues) =>
        LabelValues
            .Select(v => (v, LabelText(v), issues.Count(i => LabelsOf(i).Contains(v))))
            .Where(x => x.Item3 > 0);
}
