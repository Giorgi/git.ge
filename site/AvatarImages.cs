using System.Security.Cryptography;
using SkiaSharp;

namespace GitGe.Site;

// Avatars as served: each cached GitHub avatar (fetched at 256px by `gitge.cs avatars`)
// becomes two WebP files, 96px for every small face and 224px for the large ones (the
// developer page, share images). Names carry a hash of the bytes, so /avatars/ can be
// cached forever: a changed avatar gets a new URL. Square crops from the centre; an
// animated GIF keeps its first frame; a source smaller than a size is not enlarged.
static class AvatarImages
{
    public const int Small = 96, Large = 224;
    const int Quality = 80;

    // Writes <output>/avatars/ and fills Site.Avatars / Site.AvatarsLarge (lower-cased login → path).
    public static void Build(string cache, string output)
    {
        var dir = Path.Combine(output, "avatars");
        Directory.CreateDirectory(dir);
        var files = Directory.GetFiles(cache)
            .Select(f => (File: f, Login: Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
            .Where(x => Site.Developers.ContainsKey(x.Login))
            .ToList();
        var small = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var large = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        Parallel.ForEach(files, x =>
        {
            using var source = SKBitmap.Decode(x.File);
            if (source is null) return;
            small[x.Login] = Write(dir, x.Login, source, Small);
            large[x.Login] = Write(dir, x.Login, source, Large);
        });
        Site.Avatars = new(small);
        Site.AvatarsLarge = new(large);
    }

    static string Write(string dir, string login, SKBitmap source, int size)
    {
        var side = Math.Min(source.Width, source.Height);
        var target = Math.Min(size, side);
        using var square = new SKBitmap(target, target, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(square))
        {
            var crop = SKRect.Create((source.Width - side) / 2f, (source.Height - side) / 2f, side, side);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, crop, SKRect.Create(target, target), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        using var data = SKImage.FromBitmap(square).Encode(SKEncodedImageFormat.Webp, Quality);
        var bytes = data.ToArray();
        var name = $"{login}-{size}-{Convert.ToHexStringLower(SHA256.HashData(bytes))[..8]}.webp";
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
        return $"/avatars/{name}";
    }
}
