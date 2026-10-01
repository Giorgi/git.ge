// Links that leave git.ge open in a new tab: target="_blank" and rel="noopener" (added to
// any rel already there, such as "nofollow ugc" on submitted URLs). Applied once to
// every rendered HTML file (Program.Write), so templates and Markdown (roundups, About)
// don't carry the attributes themselves; app.js does the same for the cards it builds.
// Same-site links (relative, #fragments, https://git.ge/…) are left alone.
using System.Net;
using System.Text.RegularExpressions;

namespace GitGe.Site;

public static partial class ExternalLinks
{
    [GeneratedRegex("<a\\s[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Anchor();
    [GeneratedRegex("\\shref=\"([^\"]*)\"")]
    private static partial Regex Href();
    [GeneratedRegex("\\srel=\"([^\"]*)\"")]
    private static partial Regex Rel();
    [GeneratedRegex("\\starget=\"")]
    private static partial Regex Target();

    public static bool IsExternal(string href, string siteHost)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        return Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !uri.Host.Equals(siteHost, StringComparison.OrdinalIgnoreCase)
            && !uri.Host.Equals("www." + siteHost, StringComparison.OrdinalIgnoreCase);
    }

    public static string Mark(string html, string siteHost) => Anchor().Replace(html, m =>
    {
        var tag = m.Value;
        var href = Href().Match(tag);
        if (!href.Success || !IsExternal(WebUtility.HtmlDecode(href.Groups[1].Value), siteHost)) return tag;
        var rel = Rel().Match(tag);
        if (rel.Success)
        {
            var parts = rel.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!parts.Contains("noopener")) parts.Add("noopener");
            tag = tag.Remove(rel.Index, rel.Length).Insert(rel.Index, $" rel=\"{string.Join(' ', parts)}\"");
        }
        else tag = tag.Insert(tag.Length - 1, " rel=\"noopener\"");
        if (!Target().IsMatch(tag)) tag = tag.Insert(tag.Length - 1, " target=\"_blank\"");
        return tag;
    });
}
