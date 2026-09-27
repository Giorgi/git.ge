// Renders the static site. Run from the repository root, after `gitge.cs prepare`:
//   dotnet run --project site              build _site/ from _build/site-data.json
//   dotnet run --project site -- serve [--port 5080]   build, then serve _site/ on http://localhost:5080

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Xml;
using GitGe.Site;
using GitGe.Site.Pages;
using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

const int PageSize = 100;

var root = Directory.GetCurrentDirectory();
var dataFile = Path.Combine(root, "_build", "site-data.json");
var output = Path.Combine(root, "_site");
var wwwroot = Path.Combine(root, "site", "wwwroot");

if (args.FirstOrDefault() == "selftest")
    return SiteSelfTest();

if (!File.Exists(dataFile))
{
    Console.Error.WriteLine($"Missing {dataFile}. Run `dotnet run gitge.cs -- prepare` from the repository root first.");
    return 1;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
Site.Data = JsonSerializer.Deserialize<SiteData>(File.ReadAllText(dataFile), json)!;
Site.Strings = Strings.Load(Path.Combine(root, "i18n"));

var markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
Site.Roundups = LoadRoundups(Path.Combine(root, "content", "roundups"), markdown);
Site.Pages = LoadPages(Path.Combine(root, "content", "pages"), markdown);

// Start clean, copy static files, then version them for cache busting.
if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
CopyDirectory(wwwroot, output);
// Both languages for app.js: English for the toggle, Georgian for text it renders itself.
Write("assets/i18n.js", "window.GITGE_I18N = " + JsonSerializer.Serialize(new { ka = Site.Strings.Georgian, en = Site.Strings.English }, new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
}) + ";\n");
foreach (var file in Directory.GetFiles(Path.Combine(output, "assets"), "*.*", SearchOption.TopDirectoryOnly))
    Site.AssetVersions[$"assets/{Path.GetFileName(file)}"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))[..10];

// Self-hosted avatars from the cache that `gitge.cs avatars` fills; only people with a page.
var avatarCache = Environment.GetEnvironmentVariable("GITGE_AVATARS") is { Length: > 0 } avatarDir
    ? avatarDir : Path.Combine(root, "_cache", "avatars");
if (Directory.Exists(avatarCache))
{
    Directory.CreateDirectory(Path.Combine(output, "avatars"));
    foreach (var file in Directory.GetFiles(avatarCache))
    {
        var login = Path.GetFileNameWithoutExtension(file);
        if (!Site.Developers.ContainsKey(login)) continue;
        File.Copy(file, Path.Combine(output, "avatars", Path.GetFileName(file)), overwrite: true);
        Site.Avatars[login] = $"/avatars/{Path.GetFileName(file)}";
    }
}

// Razor escapes all text; allow every Unicode range so Georgian stays readable UTF-8
// instead of numeric entities, which would roughly triple its size.
var services = new ServiceCollection()
    .AddLogging()
    .AddSingleton(HtmlEncoder.Create(UnicodeRanges.All))
    .BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
var sitemap = new List<string>();

await Page<Home>("/", []);
foreach (var cat in Site.Data.Categories)
    await Page<Category>($"/c/{cat}/", new() { ["Cat"] = cat });

var listed = Site.DefaultOrder(Site.Listed).ToList();
var pageCount = Math.Max(1, (listed.Count + PageSize - 1) / PageSize);
for (var page = 1; page <= pageCount; page++)
{
    await Page<Projects>(Projects.PagePath(page), new()
    {
        ["Page"] = page,
        ["PageCount"] = pageCount,
        ["Items"] = listed.Skip((page - 1) * PageSize).Take(PageSize).ToList(),
    });
}

await Page<HelpWanted>("/help-wanted/", []);
await Page<Roundups>("/roundups/", []);
foreach (var roundup in Site.Roundups)
    await Page<RoundupPage>($"/roundups/{roundup.Slug}/", new() { ["R"] = roundup });
await Page<About>("/about/", []);
await Page<Developers>("/developers/", []);
foreach (var developer in Site.Data.DeveloperPages)
    await Page<Developer>(developer.Path, new() { ["D"] = developer });
await Page<NotFound>("/404.html", [], inSitemap: false);

Write("index.json", SearchIndex());
Write("roundups/feed.xml", Feed());
Write("sitemap.xml", Sitemap());
Write("robots.txt", $"User-agent: *\nAllow: /\n\nSitemap: {Site.Url("/sitemap.xml")}\n");

Console.Error.WriteLine($"Rendered {sitemap.Count} pages, {listed.Count} listed projects, " +
                        $"{Site.Data.DeveloperPages.Count} developer pages ({Site.Avatars.Count} avatars) → {output}");

if (args.FirstOrDefault() == "serve")
{
    var portIndex = Array.IndexOf(args, "--port");
    Serve(output, portIndex > 0 && portIndex + 1 < args.Length ? int.Parse(args[portIndex + 1]) : 5080);
}
return 0;

async Task Page<TPage>(string path, Dictionary<string, object?> parameters, bool inSitemap = true) where TPage : IComponent
{
    var html = await renderer.Dispatcher.InvokeAsync(async () =>
        (await renderer.RenderComponentAsync<TPage>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    Write(path.EndsWith('/') ? path.TrimStart('/') + "index.html" : path.TrimStart('/'), "<!doctype html>\n" + html);
    if (inSitemap) sitemap.Add(path);
}

void Write(string relativePath, string content)
{
    var path = Path.Combine(output, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(false));
}

// Compact index for client-side search: short keys keep it small. The browser
// fetches it only when someone starts searching. Keep in step with app.js.
string SearchIndex()
{
    var items = Site.DefaultOrder(Site.Data.Projects).Select(p => new Dictionary<string, object?>
    {
        ["n"] = p.FullName ?? p.Name,
        ["u"] = p.FullName is not null && p.Url == $"https://github.com/{p.FullName}" ? null : p.Url,   // GitHub URLs are rebuilt from "n" by app.js
        ["d"] = p.Description,
        ["k"] = p.DescriptionKa,
        ["l"] = p.Language,
        ["g"] = p.Topics.Count > 0 ? p.Topics : null,
        ["c"] = p.Category,
        ["s"] = p.Stars,
        ["t"] = p.Trend,
        ["p"] = p.PushedAt is null ? null : Format.Date(p.PushedAt),
        ["r"] = p.CreatedAt is null ? null : Format.Date(p.CreatedAt),
        ["h"] = p.HelpWanted > 0 ? p.HelpWanted : null,
        ["a"] = p.Archived ? 1 : null,
        ["v"] = p.Verified ? 1 : null,
        ["x"] = p.HelpWanted > 0 ? p.Anchor : null,
        ["w"] = Site.Developer(p.Owner) is not null ? 1 : null,   // owner has a developer page (/@login/)
    }.Where(kv => kv.Value is not null).ToDictionary());

    return JsonSerializer.Serialize(new
    {
        trend = Site.Data.Trend.HasHistory,
        window = Site.Data.Trend.WindowDays,
        projects = items,
    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
}

string Feed()
{
    using var writer = new StringWriter();
    using (var xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
    {
        xml.WriteStartElement("rss");
        xml.WriteAttributeString("version", "2.0");
        xml.WriteStartElement("channel");
        xml.WriteElementString("title", "git.ge");
        xml.WriteElementString("link", Site.Url("/roundups/"));
        xml.WriteElementString("description", Site.Strings.Ka("roundups.intro"));
        xml.WriteElementString("language", "ka");
        foreach (var r in Site.Roundups)
        {
            xml.WriteStartElement("item");
            xml.WriteElementString("title", r.Ka.Title);
            xml.WriteElementString("link", Site.Url($"/roundups/{r.Slug}/"));
            xml.WriteElementString("guid", Site.Url($"/roundups/{r.Slug}/"));
            xml.WriteElementString("pubDate", r.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture));
            xml.WriteElementString("description", r.Ka.Html);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
    }
    return writer.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"") + "\n";
}

string Sitemap()
{
    var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
    foreach (var path in sitemap)
        sb.Append($"  <url><loc>{SecurityElement(Site.Url(path))}</loc></url>\n");
    return sb.Append("</urlset>\n").ToString();
}

static string SecurityElement(string s) => System.Security.SecurityElement.Escape(s);

// content/roundups/YYYY-MM-<slug>.md with a small front matter block:
//   ---
//   title: September 2026
//   date: 2026-10-01
//   summary: One line for the list and the RSS feed.
//   ---
static List<Roundup> LoadRoundups(string dir, MarkdownPipeline markdown)
{
    if (!Directory.Exists(dir)) return [];
    // <slug>.ka.md (or plain <slug>.md) is required; <slug>.en.md is optional.
    return Directory.GetFiles(dir, "*.md")
        .Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
        .GroupBy(f => Regex.Replace(Path.GetFileNameWithoutExtension(f), @"\.(ka|en)$", ""))
        .Select(g =>
        {
            var kaFile = g.FirstOrDefault(f => f.EndsWith(".ka.md")) ?? g.FirstOrDefault(f => !f.EndsWith(".en.md"))
                ?? throw new InvalidOperationException($"Roundup '{g.Key}' has no Georgian file ({g.Key}.ka.md)");
            var enFile = g.FirstOrDefault(f => f.EndsWith(".en.md"));
            var (ka, date) = Read(kaFile);
            return new Roundup(g.Key, date, ka, enFile is null ? ka : Read(enFile).Text);
        })
        .OrderByDescending(r => r.Date)
        .ToList();

    (RoundupText Text, DateOnly Date) Read(string file)
    {
        var (meta, body) = FrontMatter(File.ReadAllText(file));
        var title = meta.GetValueOrDefault("title") ?? throw new InvalidOperationException($"{file}: missing 'title' in front matter");
        var date = DateOnly.ParseExact(meta.GetValueOrDefault("date") ?? throw new InvalidOperationException($"{file}: missing 'date' in front matter"),
                                       "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (new RoundupText(title, meta.GetValueOrDefault("summary"), Markdown.ToHtml(body, markdown)), date);
    }
}

static (Dictionary<string, string> Meta, string Body) FrontMatter(string text)
{
    text = text.Replace("\r\n", "\n");
    var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!text.StartsWith("---\n")) return (meta, text);
    var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
    if (end < 0) return (meta, text);
    foreach (var line in text[4..end].Split('\n'))
    {
        var colon = line.IndexOf(':');
        if (colon > 0) meta[line[..colon].Trim()] = line[(colon + 1)..].Trim();
    }
    return (meta, text[(end + 5)..]);
}

// content/pages/<name>.<lang>.md. {{placeholders}} are filled from site data so the
// text can't drift from the actual rules.
static Dictionary<string, string> LoadPages(string dir, MarkdownPipeline markdown)
{
    var values = new Dictionary<string, string>
    {
        ["listingMinStars"] = Site.Data.ListingMinStars.ToString(CultureInfo.InvariantCulture),
        ["maintainerLogin"] = Site.Data.MaintainerLogin,
        ["repoUrl"] = Site.Data.RepoUrl,
        ["trendWindowDays"] = Site.Data.Trend.WindowDays.ToString(CultureInfo.InvariantCulture),
    };
    return Directory.GetFiles(dir, "*.md").ToDictionary(
        f => Path.GetFileNameWithoutExtension(f),
        f => Markdown.ToHtml(values.Aggregate(File.ReadAllText(f), (text, kv) => text.Replace($"{{{{{kv.Key}}}}}", kv.Value)), markdown));
}

// `dotnet run --project site -- selftest`: checks for helpers used in rendering.
static int SiteSelfTest()
{
    var failures = 0;
    void Check(bool ok, string what)
    {
        if (!ok) failures++;
        Console.Error.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    }

    var en = new Dictionary<int, string>
    {
        [1] = "1st", [2] = "2nd", [3] = "3rd", [4] = "4th", [10] = "10th", [11] = "11th", [12] = "12th", [13] = "13th",
        [21] = "21st", [22] = "22nd", [23] = "23rd", [101] = "101st", [111] = "111th", [112] = "112th",
    };
    foreach (var (n, expected) in en)
        Check(Format.OrdinalEn(n) == expected, $"English ordinal {n} → {expected} (got {Format.OrdinalEn(n)})");
    var ka = new Dictionary<int, string>
    {
        // grammar.emis.ge, exercise 179
        [1] = "1-ელი", [2] = "მე-2", [4] = "მე-4", [12] = "მე-12", [20] = "მე-20", [21] = "21-ე", [22] = "22-ე",
        [30] = "30-ე", [40] = "მე-40", [41] = "41-ე", [80] = "მე-80", [99] = "99-ე", [100] = "მე-100", [101] = "101-ე",
        [107] = "107-ე", [120] = "120-ე", [200] = "მე-200", [203] = "203-ე", [1000] = "მე-1000",
    };
    foreach (var (n, expected) in ka)
        Check(Format.OrdinalKa(n) == expected, $"Georgian ordinal {n} → {expected} (got {Format.OrdinalKa(n)})");

    Console.Error.WriteLine(failures == 0 ? "All site checks passed" : $"{failures} site check(s) FAILED");
    return failures == 0 ? 0 : 1;
}

static void CopyDirectory(string from, string to)
{
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

static void Serve(string dir, int port)
{
    var app = WebApplication.CreateBuilder().Build();
    app.Urls.Add($"http://localhost:{port}");
    var files = new PhysicalFileProvider(dir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    // Terminal middleware, not MapFallback: an endpoint chosen by routing would stop
    // the static file middleware from serving index.html for directory URLs.
    app.Run(async context =>
    {
        context.Response.StatusCode = 404;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(dir, "404.html"));
    });
    Console.Error.WriteLine($"Serving _site/ on http://localhost:{port} (Ctrl+C to stop)");
    app.Run();
}
