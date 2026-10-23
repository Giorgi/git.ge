using System.Security.Cryptography;
using SkiaSharp;

namespace GitGe.Site;

// Share images (og:image, 1200×630 PNG) drawn at build time.
// - Roundups: the month at poster size, the faces of the people it features, its top
//   three new projects and its numbers, all from the roundup's own Markdown, so the
//   image matches the text and never changes after publication.
// - Developers: the face, name and login, project count and stars, the top three
//   projects, and the best top-three category rank with its medal. Only slow-changing
//   facts (stars of 1,000+ rounded down, no weekly ring), so most images stay the same
//   from one night to the next. The file name carries a hash
// of the PNG, so a changed image gets a new URL and link previews fetch it again.
// Text uses static instances of the site's own Noto fonts (site/og-fonts/); Georgian
// and Latin come from separate files, so each string is drawn in runs per script.
static class OgImages
{
    const int W = 1200, H = 630, Pad = 72;
    const int FaceSize = 64, FaceGap = 12, MaxFaces = 12;

    static readonly SKColor Bg = SKColor.Parse("#fdfdfc"), Fg = SKColor.Parse("#1c1c1a"),
        Muted = SKColor.Parse("#5f5f5c"), Accent = SKColor.Parse("#b3261e"), Line = SKColor.Parse("#e4e4e0"),
        Up = SKColor.Parse("#1b7536");   // the site's --up: growth

    static SKTypeface latin = null!, latinBlack = null!, georgian = null!, georgianBlack = null!;

    // Draws every roundup's image into <output>/og/ and returns slug → site path.
    static void LoadFonts(string root)
    {
        if (latin is not null) return;
        var fonts = Path.Combine(root, "site", "og-fonts");
        latin = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-latin-400.ttf"));
        latinBlack = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-latin-900.ttf"));
        georgian = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-georgian-400.ttf"));
        georgianBlack = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-georgian-900.ttf"));
    }

    // The site's own image (home and every page without one of its own): the home page's
    // headline, "438 დეველოპერი. 1,083 ღია კოდის პროექტი.", with the same counts.
    public static string SiteImage(string root, string output)
    {
        LoadFonts(root);
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);
        // The homepage image: a bigger wordmark than the others, and a bold tagline.
        // One block: wordmark, the two counts, the tagline and its English line.
        var x = Text(c, "git", Pad, 150, 64, true, Fg);
        Text(c, ".ge", x, 150, 64, true, Accent);

        var devs = Format.Number(Site.Data.Developers);
        var projects = Format.Number(Site.Listed.Count());
        var line1 = $"{devs} {Site.Strings.Ka("home.heroDevs")}";
        var line2 = $"{projects} {Site.Strings.Ka("home.heroProjects")}";
        var size = 104f;
        while (size > 48 && Math.Max(Measure(line1, size, true), Measure(line2, size, true)) > W - 2 * Pad) size -= 2;
        Text(c, line1, Pad, 292, size, true, Fg);
        var px = Text(c, projects, Pad, 292 + size * 1.08f, size, true, Accent);
        Text(c, " " + Site.Strings.Ka("home.heroProjects"), px, 292 + size * 1.08f, size, true, Fg);
        var tagline = Site.Strings.Ka("site.tagline");
        var tsize = 38f;
        while (tsize > 26 && Measure(tagline, tsize, true) > W - 2 * Pad) tsize -= 1;
        var line2Y = 292 + size * 1.08f;
        Text(c, tagline, Pad, line2Y + 92, tsize, true, Fg);
        // In English too: of all the images this one is the likeliest to reach non-Georgian readers.
        Text(c, Site.Strings.English["site.tagline"], Pad, line2Y + 140, 30, false, Muted);

        using var rule = new SKPaint { Color = Line, StrokeWidth = 2 };
        c.DrawLine(0, H - 1, W, H - 1, rule);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        var dir = Path.Combine(output, "og");
        Directory.CreateDirectory(dir);
        var name = $"site-{Convert.ToHexStringLower(SHA256.HashData(png))[..8]}.png";
        File.WriteAllBytes(Path.Combine(dir, name), png);
        return $"/og/{name}";
    }

    // Draws each developer page's image (JPEG: photos make PNGs twice the size) into
    // <output>/og/ and returns login → site path.
    public static Dictionary<string, string> Developers(string root, string output, IEnumerable<SiteDeveloper> developers)
    {
        LoadFonts(root);
        var dir = Path.Combine(output, "og");
        Directory.CreateDirectory(dir);
        var paths = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        Parallel.ForEach(developers, d =>
        {
            var png = DrawDeveloper(d, output);
            var login = d.Login.ToLowerInvariant();
            var name = $"dev-{login}-{Convert.ToHexStringLower(SHA256.HashData(png))[..8]}.jpg";
            File.WriteAllBytes(Path.Combine(dir, name), png);
            paths[login] = $"/og/{name}";
        });
        return new(paths);
    }

    public static Dictionary<string, string> Roundups(string root, string output, IEnumerable<Roundup> roundups)
    {
        LoadFonts(root);

        var dir = Path.Combine(output, "og");
        Directory.CreateDirectory(dir);
        var paths = new Dictionary<string, string>();
        foreach (var r in roundups)
        {
            var png = DrawRoundup(r, output);
            var name = $"roundup-{r.Slug}-{Convert.ToHexStringLower(SHA256.HashData(png))[..8]}.png";
            File.WriteAllBytes(Path.Combine(dir, name), png);
            paths[r.Slug] = $"/og/{name}";
        }
        return paths;
    }

    static byte[] DrawRoundup(Roundup r, string output)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);

        // Top line: the wordmark, and what this is.
        var x = Text(c, "git", Pad, 108, 40, true, Fg);
        Text(c, ".ge", x, 108, 40, true, Accent);
        var kind = Site.Strings.Ka("roundups.ogKind");
        Text(c, kind, W - Pad - Measure(kind, 30, false), 106, 30, false, Muted);

        // The month, as large as fits.
        var title = r.Ka.Title;
        var size = 132f;
        while (size > 60 && Measure(title, size, true) > W - 2 * Pad) size -= 4;
        Text(c, title, Pad, 268, size, true, Fg);

        // Faces in order of mention (only those with a saved avatar), then "+N".
        var faces = r.Owners.Where(o => Site.Avatar(o) is not null).ToList();
        var fx = (float)Pad;
        foreach (var login in faces.Take(MaxFaces))
        {
            Face(c, Path.Combine(output, Site.AvatarLarge(login)!.TrimStart('/')), fx, 330);
            fx += FaceSize + FaceGap;
        }
        if (faces.Count > MaxFaces) Text(c, $"+{faces.Count - MaxFaces}", fx + 6, 330 + FaceSize / 2f + 12, 32, false, Muted);

        // The top new projects in bold with their stars (as written in the roundup), in the
        // site's growth green: a project created this month earned every one of them. Then
        // the month's numbers, quieter. Both from the Markdown.
        var top = r.Ka.Sections.SelectMany(s => s.Parts).OfType<RoundupEntry>().Take(3).ToList();
        var nameSize = 38f;
        while (nameSize > 26 && TopWidth(top, nameSize) > W - 2 * Pad) nameSize -= 2;
        var nx = (float)Pad;
        for (var i = 0; i < top.Count; i++)
        {
            if (i > 0) nx = Text(c, "  ·  ", nx, 492, nameSize, false, Muted);
            nx = Text(c, top[i].Repo.Name, nx, 492, nameSize, true, Fg);
            if (Stars(top[i]) is { } stars)
            {
                nx += nameSize * .35f;
                Star(c, nx, 492, nameSize * .62f, Up);
                nx = Text(c, stars, nx + nameSize * .72f, 492, nameSize * .8f, true, Up);
            }
        }
        var stats = Stats(r.Ka.Sections);
        if (stats.Length > 0) Text(c, stats, Pad, 552, 30, false, Muted);

        using var rule = new SKPaint { Color = Line, StrokeWidth = 2 };
        c.DrawLine(0, H - 1, W, H - 1, rule);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static byte[] DrawDeveloper(SiteDeveloper d, string output)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);

        var x = Text(c, "git", Pad, 108, 40, true, Fg);
        Text(c, ".ge", x, 108, 40, true, Accent);
        // Everyone here is a developer; only an organisation gets a label.
        if (d.IsOrg)
        {
            var kind = Site.Strings.Ka("dev.org");
            Text(c, kind, W - Pad - Measure(kind, 30, false), 106, 30, false, Muted);
        }

        // The face, large; the letter circle when there is no saved photo.
        const int face = 208, fy = 156;
        if (Site.AvatarLarge(d.Login) is { } avatar) Face(c, Path.Combine(output, avatar.TrimStart('/')), Pad, fy, face);
        else Initial(c, d.Login, Pad, fy, face);

        // Name as large as fits beside the face, then the login and the counts.
        var tx = Pad + face + 44f;
        var width = W - Pad - tx;
        var size = 76f;
        while (size > 36 && Measure(d.DisplayName, size, true) > width) size -= 2;
        Text(c, Fit(d.DisplayName, size, width, true), tx, fy + 74, size, true, Fg);
        if (!string.Equals(d.DisplayName, d.Login, StringComparison.OrdinalIgnoreCase))
            Text(c, "@" + d.Login, tx, fy + 122, 32, false, Muted);
        var projects = d.Projects.Select(Site.Project).OrderByDescending(p => p.Stars ?? 0).ToList();
        var sx = Text(c, $"{projects.Count} {Site.Strings.Ka("dev.statProjects")}", tx, fy + 186, 38, false, Fg);
        if (d.Stars > 0)
        {
            sx = Text(c, "  ·  ", sx, fy + 186, 38, false, Muted);
            sx = Text(c, StarCount(d.Stars), sx, fy + 186, 38, true, Fg);
            Star(c, sx + 10, fy + 186, 26, Fg);
        }

        // Top three projects, names in bold with their (all-time) stars in grey.
        var top = projects.Take(3).ToList();
        var nameSize = 36f;
        while (nameSize > 24 && ProjectsWidth(top, nameSize) > W - 2 * Pad) nameSize -= 2;
        var nx = (float)Pad;
        for (var i = 0; i < top.Count; i++)
        {
            if (i > 0) nx = Text(c, "  ·  ", nx, 476, nameSize, false, Muted);
            nx = Text(c, top[i].Name, nx, 476, nameSize, true, Fg);
            if (top[i].Stars is > 0 and var stars)
            {
                nx += nameSize * .35f;
                Star(c, nx, 476, nameSize * .62f, Muted);
                nx = Text(c, StarCount(stars), nx + nameSize * .72f, 476, nameSize * .8f, false, Muted);
            }
        }

        // The best top-three category rank, with its medal; shorter than the page's sentence.
        if (d.Ranks.Where(r => r.Rank <= 3).OrderBy(r => r.Rank).ThenByDescending(r => r.Of).FirstOrDefault() is { } best)
        {
            // The disc's centre on the middle of the lowercase letters, so medal and text line up.
            using var metrics = Font(false, 30, false);
            var mid = 553 - metrics.Metrics.XHeight / 2;
            Medal(c, Pad, mid, best.Rank, 46);
            var line = Site.Strings.Ka("og.rank", Site.Project(best.Project).Name, OrdinalKa(best.Rank), Site.Strings.Ka($"cat.{best.Category}"));
            Text(c, Fit(line, 30, W - 2 * Pad - 56), Pad + 56, 553, 30, false, Fg);
        }

        using var rule = new SKPaint { Color = Line, StrokeWidth = 2 };
        c.DrawLine(0, H - 1, W, H - 1, rule);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }

    static string OrdinalKa(int n) => Format.OrdinalKa(n);

    // Exact below 1,000; above, rounded down so the image changes rarely: 1.8k, 12k.
    static string StarCount(int n) => n switch
    {
        < 1000 => n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture),
        < 10000 => (Math.Floor(n / 100.0) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k",
        _ => (n / 1000) + "k",
    };

    static float ProjectsWidth(List<SiteProject> top, float size) =>
        top.Select((p, i) => (i > 0 ? Measure("  ·  ", size, false) : 0) + Measure(p.Name, size, true)
                             + (p.Stars is > 0 and var s ? size * 1.07f + Measure(StarCount(s), size * .8f, false) : 0)).Sum();

    // The site's medal (Medal.razor): ribbon, disc and rim in the place's metal, the number on it.
    // (x, discCentreY): the medal is placed by its disc, size is the SVG box height.
    static void Medal(SKCanvas c, float x, float discY, int rank, float size)
    {
        var (disc, rim, ink) = rank switch
        {
            1 => ("#e3b23c", "#b8871a", "#4a3300"),
            2 => ("#c3c7cc", "#8d939a", "#2c3035"),
            _ => ("#d08a52", "#9c5a2a", "#3d1d06"),
        };
        var k = size / 40;   // the SVG's 32×40 box, scaled
        var y = discY - 26 * k;
        using var fill = new SKPaint { IsAntialias = true, Color = SKColor.Parse(rim) };
        var ribbon = new SKPathBuilder();
        ribbon.MoveTo(x + 7 * k, y); ribbon.LineTo(x + 14 * k, y); ribbon.LineTo(x + 19 * k, y + 14 * k); ribbon.LineTo(x + 12 * k, y + 14 * k); ribbon.Close();
        ribbon.MoveTo(x + 25 * k, y); ribbon.LineTo(x + 18 * k, y); ribbon.LineTo(x + 13 * k, y + 14 * k); ribbon.LineTo(x + 20 * k, y + 14 * k); ribbon.Close();
        using (var path = ribbon.Detach()) c.DrawPath(path, fill);
        fill.Color = SKColor.Parse(disc);
        c.DrawCircle(x + 16 * k, y + 26 * k, 12 * k, fill);
        using var stroke = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 1.2f, Color = SKColor.Parse(rim) };
        c.DrawCircle(x + 16 * k, y + 26 * k, 9 * k, stroke);
        var n = rank.ToString();
        using var font = Font(false, 13 * k, true);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColor.Parse(ink) };
        c.DrawText(n, x + 16 * k, y + 30.5f * k, SKTextAlign.Center, font, paint);
    }

    // The site's letter fallback: the login's first letter in a neutral circle.
    static void Initial(SKCanvas c, string login, float x, float y, int size)
    {
        using var bg = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#efefec") };
        c.DrawCircle(x + size / 2f, y + size / 2f, size / 2f, bg);
        using var ring = new SKPaint { Color = Line, IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true };
        c.DrawCircle(x + size / 2f, y + size / 2f, size / 2f, ring);
        var letter = login.Length > 0 ? login[..1].ToUpperInvariant() : "?";
        using var font = Font(false, size * .45f, true);
        using var paint = new SKPaint { IsAntialias = true, Color = Muted };
        c.DrawText(letter, x + size / 2f, y + size / 2f + size * .16f, SKTextAlign.Center, font, paint);
    }

    // "22 ახალი რეპოზიტორია · 10 ახალი ვერსია": the new-repository figure from the
    // numbers section (or the entry count), and the number of release rows.
    static string Stats(List<RoundupSection> sections)
    {
        var parts = sections.SelectMany(s => s.Parts).ToList();
        var created = parts.OfType<RoundupFigures>().SelectMany(f => f.Figures)
            .Where(f => f.Text.StartsWith("ახალი რეპოზიტორია")).Select(f => f.Number).FirstOrDefault();
        var entries = parts.OfType<RoundupEntry>().Count();
        var releases = parts.OfType<RoundupRows>().SelectMany(r => r.Rows).Count(row => row.Text is not null);
        var bits = new List<string>();
        if (created is not null) bits.Add(Site.Strings.Ka("roundups.ogCreated", created));
        else if (entries > 0) bits.Add(Site.Strings.Ka("roundups.ogCreated", entries));
        if (releases > 0) bits.Add(Site.Strings.Ka("roundups.ogReleases", releases));
        return string.Join(" · ", bits);
    }

    // "★ 203 · JavaScript · …" → "203".
    static string? Stars(RoundupEntry e) =>
        e.Meta is { } meta && System.Text.RegularExpressions.Regex.Match(meta, @"^★\s*([\d,]+)") is { Success: true } m ? m.Groups[1].Value : null;

    static float TopWidth(List<RoundupEntry> top, float size) =>
        top.Select((e, i) => (i > 0 ? Measure("  ·  ", size, false) : 0) + Measure(e.Repo.Name, size, true)
                             + (Stars(e) is { } st ? size * 1.07f + Measure(st, size * .8f, true) : 0)).Sum();

    // A five-pointed star drawn as a path (the font subset has no ★), sitting on the baseline.
    static void Star(SKCanvas c, float x, float baseline, float size, SKColor color)
    {
        var builder = new SKPathBuilder();
        float cx = x + size / 2, cy = baseline - size * .42f, outer = size / 2, inner = outer * .45f;
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = -MathF.PI / 2 + i * MathF.PI / 5;
            var pt = new SKPoint(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
            if (i == 0) builder.MoveTo(pt); else builder.LineTo(pt);
        }
        builder.Close();
        using var path = builder.Detach();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawPath(path, paint);
    }

    static void Face(SKCanvas c, string file, float x, float y, int size = FaceSize)
    {
        using var bitmap = SKBitmap.Decode(file);
        if (bitmap is null) return;
        var rect = new SKRect(x, y, x + size, y + size);
        c.Save();
        c.ClipRoundRect(new SKRoundRect(rect, size / 2f), antialias: true);
        using var paint = new SKPaint { IsAntialias = true };
        using var img = SKImage.FromBitmap(bitmap);
        c.DrawImage(img, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        c.Restore();
        using var ring = new SKPaint { Color = Line, IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true };
        c.DrawOval(rect, ring);
    }

    // Text in runs: Georgian letters from the Georgian font, everything else from Latin.
    static IEnumerable<(string Run, bool Georgian)> Runs(string text)
    {
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
            if (i == text.Length || IsGeorgian(text[i]) != IsGeorgian(text[start]))
            {
                yield return (text[start..i], IsGeorgian(text[start]));
                start = i;
            }
    }

    static bool IsGeorgian(char ch) => ch is >= 'Ⴀ' and <= 'ჿ' or >= 'Ა' and <= 'Ჿ' or >= 'ⴀ' and <= '⴯';

    static SKFont Font(bool isGeorgian, float size, bool black) =>
        new(isGeorgian ? (black ? georgianBlack : georgian) : (black ? latinBlack : latin), size) { Subpixel = true, Edging = SKFontEdging.Antialias };

    static float Measure(string text, float size, bool black) =>
        Runs(text).Sum(r => { using var f = Font(r.Georgian, size, black); return f.MeasureText(r.Run); });

    // Returns the x after the text.
    static float Text(SKCanvas c, string text, float x, float y, float size, bool black, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        foreach (var (run, isGeorgian) in Runs(text))
        {
            using var font = Font(isGeorgian, size, black);
            c.DrawText(run, x, y, SKTextAlign.Left, font, paint);
            x += font.MeasureText(run);
        }
        return x;
    }

    static string Fit(string text, float size, float width, bool black = false)
    {
        if (Measure(text, size, black) <= width) return text;
        while (text.Length > 1 && Measure(text + "…", size, black) > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }
}
