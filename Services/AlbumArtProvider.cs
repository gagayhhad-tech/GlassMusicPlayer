using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlassMusicPlayer.Services;

public static class AlbumArtProvider
{
    private static readonly HttpClient _http = new();
    private static readonly object _lock = new();
    private static readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds(200);
    private static DateTime _lastRequest = DateTime.MinValue;

    private static string CacheDir => Path.Combine(Path.GetTempPath(), "GlassMusicPlayer", "covers");

    /// <summary>
    /// Returns a covers.localhost URL for the album of the given artist/album,
    /// fetching it from the iTunes Search API on first use and caching on disk.
    /// Returns null when no cover could be found or resolved.
    /// </summary>
    public static string? GetCoverUrl(string artist, string album)
    {
        if (string.IsNullOrWhiteSpace(artist) || artist.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(album) || album.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = artist.Trim() + " - " + album.Trim();
        var hash = ComputeSha1(key.ToLowerInvariant());
        var file = Path.Combine(CacheDir, $"art-{hash}.jpg");

        if (File.Exists(file))
            return $"https://covers.localhost/art-{hash}.jpg";

        lock (_lock)
        {
            if (_attempted.Contains(key))
                return null;
            _attempted.Add(key);

            // Throttle requests to stay within iTunes rate limits
            var wait = _minInterval - (DateTime.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero)
                System.Threading.Thread.Sleep(wait);
        }

        try
        {
            var artwork = SearchItunes(artist.Trim(), album.Trim())
                ?? SearchDeezer(artist.Trim(), album.Trim())
                ?? SearchYandexMusic(artist.Trim(), album.Trim());
            if (string.IsNullOrEmpty(artwork))
                return null;

            var bytes = _http.GetByteArrayAsync(artwork).Result;
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(file, bytes);
            return $"https://covers.localhost/art-{hash}.jpg";
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (_lock) { _lastRequest = DateTime.UtcNow; }
        }
    }

    private static string? SearchItunes(string artist, string album)
    {
        foreach (var country in new[] { "US", "RU" })
        {
            var term = Uri.EscapeDataString($"{artist} {album}");
            var url = $"https://itunes.apple.com/search?term={term}&entity=album&limit=1&country={country}";

            var json = _http.GetStringAsync(url).Result;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("resultCount", out var countEl) || countEl.GetInt32() == 0)
                continue;

            var first = doc.RootElement.GetProperty("results")[0];
            if (!first.TryGetProperty("artworkUrl100", out var artEl))
                continue;

            var artwork = artEl.GetString();
            if (string.IsNullOrEmpty(artwork))
                continue;

            return artwork.Replace("100x100", "600x600");
        }
        return null;
    }

    private static string? SearchDeezer(string artist, string album)
    {
        var term = Uri.EscapeDataString($"{artist} {album}");
        var url = $"https://api.deezer.com/search?q={term}&limit=1";

        var json = _http.GetStringAsync(url).Result;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            return null;

        var first = data[0];
        if (!first.TryGetProperty("album", out var albumObj))
            return null;
        if (!albumObj.TryGetProperty("cover_big", out var coverEl))
            return null;

        return coverEl.GetString();
    }

    private static string? SearchYandexMusic(string artist, string album)
    {
        var url = "https://api.music.yandex.net/search?text=" + Uri.EscapeDataString($"{artist} {album}") + "&type=album&page=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", "YandexMusicAndroid/5.36.2 (Android 13)");
        request.Headers.TryAddWithoutValidation("User-Agent", "Yandex-Music-API/0.0.1");

        var json = _http.SendAsync(request).Result.Content.ReadAsStringAsync().Result;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("albums", out var albums) ||
            !albums.TryGetProperty("results", out var results) ||
            results.GetArrayLength() == 0)
            return null;

        var first = results[0];
        if (!first.TryGetProperty("coverUri", out var coverEl))
            return null;

        var coverUri = coverEl.GetString();
        if (string.IsNullOrEmpty(coverUri))
            return null;

        return "https://" + coverUri.Replace("%%", "600x600");
    }

    private static string ComputeSha1(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
