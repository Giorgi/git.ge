using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// A roundup's Markdown cut into the pieces RoundupPage lays out in the site's own
// grammar: the opening paragraphs, then one section per "##" heading. Inside a
// section, "### [owner/repo](https://github.com/…)" starts a project entry (meta line,
// the author's quoted description, the prose), a list whose items open with a GitHub
// repo link becomes rows with the owner's face, and a list whose items open with a
// number becomes big figures. Anything else stays plain Markdown HTML, so hand-written
// roundups still render.

public sealed record RoundupSection(string? Title, List<RoundupPart> Parts);

public abstract record RoundupPart;
public sealed record RoundupHtml(string Html) : RoundupPart;
public sealed record RoundupEntry(RepoLink Repo, string? Meta, string? Quote, string? Body) : RoundupPart;
public sealed record RoundupRows(List<(RepoLink Repo, string? Text)> Rows) : RoundupPart;
public sealed record RoundupFigures(List<(string Number, string Text)> Figures) : RoundupPart;

// A link to a GitHub repository: the owner drives the face, Name the title.
public sealed record RepoLink(string Owner, string Name, string Url)
{
    static readonly Regex GitHubRepo = new(@"^https://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+)/?$");

    public static RepoLink? From(Inline? inline) =>
        inline is LinkInline { IsImage: false } link && link.Url is { } url && GitHubRepo.Match(url) is { Success: true } m
            ? new RepoLink(m.Groups[1].Value, m.Groups[2].Value, url)
            : null;
}

public static class RoundupView
{
    public static List<RoundupSection> Parse(string markdown, MarkdownPipeline pipeline)
    {
        var sections = new List<RoundupSection> { new(null, []) };
        RoundupEntryBuilder? entry = null;

        void Flush()
        {
            if (entry is null) return;
            sections[^1].Parts.Add(entry.Build());
            entry = null;
        }

        foreach (var block in Markdown.Parse(markdown, pipeline))
        {
            switch (block)
            {
                case HeadingBlock { Level: 2 } h2:
                    Flush();
                    sections.Add(new RoundupSection(Inner(h2, pipeline), []));
                    break;
                case HeadingBlock { Level: 3 } h3 when RepoLink.From(h3.Inline?.FirstChild) is { } repo && h3.Inline!.FirstChild!.NextSibling is null:
                    Flush();
                    entry = new RoundupEntryBuilder(repo);
                    break;
                case HeadingBlock:
                    Flush();
                    sections[^1].Parts.Add(new RoundupHtml(Html(block, pipeline)));
                    break;
                case ParagraphBlock p when entry is not null:
                    entry.Paragraph(Inner(p, pipeline), p);
                    break;
                case QuoteBlock q when entry is not null && entry.Quote is null:
                    entry.Quote = string.Concat(q.OfType<ParagraphBlock>().Select(p => Inner(p, pipeline)));
                    break;
                case ListBlock list when Rows(list, pipeline) is { } rows:
                    Flush();
                    sections[^1].Parts.Add(rows);
                    break;
                case ListBlock list when Figures(list, pipeline) is { } figures:
                    Flush();
                    sections[^1].Parts.Add(figures);
                    break;
                default:
                    Flush();
                    sections[^1].Parts.Add(new RoundupHtml(Html(block, pipeline)));
                    break;
            }
        }
        Flush();
        return sections.Where(s => s.Title is not null || s.Parts.Count > 0).ToList();
    }

    // Every GitHub owner a roundup links to, in order of first mention.
    public static List<string> Owners(IEnumerable<RoundupSection> sections) => sections
        .SelectMany(s => s.Parts)
        .SelectMany(p => p switch
        {
            RoundupEntry e => [e.Repo.Owner],
            RoundupRows r => r.Rows.Select(x => x.Repo.Owner),
            _ => [],
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    // "- [owner/repo](https://github.com/owner/repo): the rest" in every item.
    static RoundupRows? Rows(ListBlock list, MarkdownPipeline pipeline)
    {
        var rows = new List<(RepoLink, string?)>();
        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (item.Count != 1 || item[0] is not ParagraphBlock p || RepoLink.From(p.Inline?.FirstChild) is not { } repo) return null;
            var rest = InlinesHtml(p.Inline!.FirstChild!.NextSibling, pipeline).Trim();
            rest = Regex.Replace(rest, @"^[:·—–-]\s*", "");
            rows.Add((repo, rest.Length == 0 ? null : rest));
        }
        return rows.Count > 0 ? new RoundupRows(rows) : null;
    }

    // "- 1,083 listed projects, …" in every item: the leading number, then the rest.
    static RoundupFigures? Figures(ListBlock list, MarkdownPipeline pipeline)
    {
        var figures = new List<(string, string)>();
        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (item.Count != 1 || item[0] is not ParagraphBlock p) return null;
            var html = Inner(p, pipeline);
            if (Regex.Match(html, @"^(\d[\d,.]*)\s+(.+)$", RegexOptions.Singleline) is not { Success: true } m) return null;
            figures.Add((m.Groups[1].Value, m.Groups[2].Value));
        }
        return figures.Count > 0 ? new RoundupFigures(figures) : null;
    }

    static string Html(MarkdownObject node, MarkdownPipeline pipeline)
    {
        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.Render(node);
        writer.Flush();
        return writer.ToString();
    }

    // A paragraph or heading without its own tag.
    static string Inner(LeafBlock block, MarkdownPipeline pipeline)
    {
        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { ImplicitParagraph = true };
        pipeline.Setup(renderer);
        if (block.Inline is { } inline) renderer.WriteChildren(inline);
        writer.Flush();
        return writer.ToString().Trim();
    }

    static string InlinesHtml(Inline? from, MarkdownPipeline pipeline)
    {
        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        for (var i = from; i is not null; i = i.NextSibling) renderer.Render(i);
        writer.Flush();
        return writer.ToString();
    }

    sealed class RoundupEntryBuilder(RepoLink repo)
    {
        string? meta, body;
        public string? Quote { get; set; }

        // A first paragraph starting with ★ or + is the meta line ("★ 203 · JavaScript · …",
        // "+40 ★ (total 210) · Go"); everything else is the prose.
        public void Paragraph(string html, ParagraphBlock _)
        {
            if (meta is null && Quote is null && body is null && (html.StartsWith('★') || html.StartsWith('+'))) meta = html;
            else body = body is null ? $"<p>{html}</p>" : body + $"<p>{html}</p>";
        }

        public RoundupEntry Build() => new(repo, meta, Quote, body);
    }
}
