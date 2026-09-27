using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace GitGe.Site;

// Checks over the built _site: every indexable page has a unique <title> and meta
// description, and every JSON-LD block parses as JSON. Run after each build and by
// `dotnet run --project site -- selftest` when _site exists.
public static class BuiltSiteChecks
{
    static readonly Regex Title = new("<title[^>]*>(.*?)</title>", RegexOptions.Singleline);
    static readonly Regex Description = new("<meta name=\"description\" content=\"([^\"]*)\"");
    static readonly Regex NoIndex = new("<meta name=\"robots\" content=\"[^\"]*noindex");
    static readonly Regex JsonLd = new("<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline);

    public static List<string> Run(string siteDir)
    {
        var problems = new List<string>();
        var titles = new Dictionary<string, string>();
        var descriptions = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(siteDir, "*.html", SearchOption.AllDirectories))
        {
            var page = "/" + Path.GetRelativePath(siteDir, file).Replace('\\', '/');
            var html = File.ReadAllText(file);
            foreach (Match ld in JsonLd.Matches(html))
            {
                try { using var _ = JsonDocument.Parse(ld.Groups[1].Value); }
                catch (JsonException e) { problems.Add($"{page}: JSON-LD doesn't parse ({e.Message})"); }
            }
            if (NoIndex.IsMatch(html)) continue;

            var title = Title.Match(html) is { Success: true } t ? t.Groups[1].Value : "";
            var description = Description.Match(html) is { Success: true } d ? d.Groups[1].Value : "";
            if (title.Length == 0) problems.Add($"{page}: no <title>");
            else if (titles.TryGetValue(title, out var other)) problems.Add($"{page}: same <title> as {other} ({title})");
            else titles[title] = page;
            if (description.Length == 0) problems.Add($"{page}: no meta description");
            else if (descriptions.TryGetValue(description, out var other2)) problems.Add($"{page}: same description as {other2}");
            else descriptions[description] = page;
        }
        return problems;
    }
}

// Structured data (schema.org JSON-LD) for search engines. Every page gets one
// <script type="application/ld+json"> block holding an @graph: the page's own
// entity (WebSite, ProfilePage, BlogPosting, ItemList) plus a BreadcrumbList.
// It's a data block, not a script: browsers don't execute it, so the CSP's
// script-src 'self' doesn't apply to it.
public static class Seo
{
    // JavaScriptEncoder (unlike UnsafeRelaxedJsonEscaping) always escapes the
    // HTML-sensitive characters < > & ' " as \uXXXX, so no value can close the
    // <script> element ("</script>" can't appear). Georgian stays readable.
    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static string Serialize(IEnumerable<object> nodes) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = nodes.ToList(),
        }, Options);

    // Items are (name, site path); the last one is the current page.
    public static object Breadcrumbs(IEnumerable<(string Name, string Path)> items) => new Dictionary<string, object?>
    {
        ["@type"] = "BreadcrumbList",
        ["itemListElement"] = items.Select((item, i) => new Dictionary<string, object?>
        {
            ["@type"] = "ListItem",
            ["position"] = i + 1,
            ["name"] = item.Name,
            ["item"] = Site.Url(item.Path),
        }).ToList(),
    };

    public static object WebSite() => new Dictionary<string, object?>
    {
        ["@type"] = "WebSite",
        ["name"] = "git.ge",
        ["alternateName"] = Site.Strings.English["site.tagline"],
        ["description"] = Site.Strings.Ka("site.description"),
        ["url"] = Site.Url("/"),
        ["inLanguage"] = "ka",
    };

    // A developer page: ProfilePage whose main entity is the Person or Organization.
    // Only public GitHub facts: name, login, avatar (self-hosted), GitHub profile,
    // main languages. Never a location.
    public static object Profile(SiteDeveloper d)
    {
        var entity = new Dictionary<string, object?>
        {
            ["@type"] = d.IsOrg ? "Organization" : "Person",
            ["name"] = d.DisplayName,
            ["alternateName"] = d.Login,
            ["url"] = Site.Url(d.Path),
            ["sameAs"] = new[] { d.Url },
        };
        if (Site.Avatar(d.Login) is { } avatar) entity[d.IsOrg ? "logo" : "image"] = Site.Url(avatar);
        if (!d.IsOrg && d.Languages.Count > 0) entity["knowsAbout"] = d.Languages;
        return new Dictionary<string, object?>
        {
            ["@type"] = "ProfilePage",
            ["url"] = Site.Url(d.Path),
            ["inLanguage"] = "ka",
            ["mainEntity"] = entity,
        };
    }

    public static object Article(Roundup r) => new Dictionary<string, object?>
    {
        ["@type"] = "BlogPosting",
        ["headline"] = r.Ka.Title,
        ["description"] = r.Ka.Summary,
        ["datePublished"] = r.Date.ToString("yyyy-MM-dd"),
        ["inLanguage"] = "ka",
        ["url"] = Site.Url($"/roundups/{r.Slug}/"),
        ["author"] = Maintainer(),
        ["publisher"] = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = "git.ge", ["url"] = Site.Url("/") },
    };

    // Category pages list their projects. The URLs point to GitHub for now; they
    // should switch to git.ge project pages once those exist.
    public static object ItemList(string name, IEnumerable<SiteProject> projects) => new Dictionary<string, object?>
    {
        ["@type"] = "ItemList",
        ["name"] = name,
        ["itemListElement"] = projects.Select((p, i) => new Dictionary<string, object?>
        {
            ["@type"] = "ListItem",
            ["position"] = i + 1,
            ["name"] = p.FullName ?? p.Name,
            ["url"] = p.Url,
        }).ToList(),
    };

    // "a, b და c" for descriptions (Georgian "and" is და).
    public static string JoinKa(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list[..^1]) + " და " + list[^1];
    }

    static object Maintainer()
    {
        var login = Site.Data.MaintainerLogin;
        var person = new Dictionary<string, object?> { ["@type"] = "Person", ["name"] = login };
        if (Site.Developer(login) is { } d)
        {
            person["name"] = d.DisplayName;
            person["url"] = Site.Url(d.Path);
        }
        else person["url"] = $"https://github.com/{login}";
        return person;
    }
}
