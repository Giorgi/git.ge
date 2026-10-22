using System.Security.Cryptography;
using SkiaSharp;

namespace GitGe.Site;

// Share images (og:image, 1200×630 PNG) drawn at build time. For now only roundups:
// the month at poster size, the faces of the people it features, its numbers and its
// top three new projects, all taken from the roundup's own Markdown so the image
// matches the text and never changes after publication. The file name carries a hash
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
    public static Dictionary<string, string> Roundups(string root, string output, IEnumerable<Roundup> roundups)
    {
        var fonts = Path.Combine(root, "site", "og-fonts");
        latin = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-latin-400.ttf"));
        latinBlack = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-latin-900.ttf"));
        georgian = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-georgian-400.ttf"));
        georgianBlack = SKTypeface.FromFile(Path.Combine(fonts, "noto-sans-georgian-900.ttf"));

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

    static void Face(SKCanvas c, string file, float x, float y)
    {
        using var bitmap = SKBitmap.Decode(file);
        if (bitmap is null) return;
        var rect = new SKRect(x, y, x + FaceSize, y + FaceSize);
        c.Save();
        c.ClipRoundRect(new SKRoundRect(rect, FaceSize / 2f), antialias: true);
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

    static string Fit(string text, float size, float width)
    {
        if (Measure(text, size, false) <= width) return text;
        while (text.Length > 1 && Measure(text + "…", size, false) > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }
}
