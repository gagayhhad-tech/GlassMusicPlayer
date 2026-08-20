using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassMusicPlayer.Services;

public class LrcLine
{
    public double Time { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// Loads lyrics from embedded tags (.lrc) or sidecar .lrc files.
/// </summary>
public static class LyricsService
{
    private static readonly Regex LrcLineRegex = new(
        @"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]\s*(.*)",
        RegexOptions.Compiled);

    /// <summary>Parses .lrc content into timestamped lines (sorted). Returns null when nothing parseable.</summary>
    public static List<LrcLine>? ParseLrc(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var lines = new List<LrcLine>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var match = LrcLineRegex.Match(line);
            if (!match.Success) continue;

            if (double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
                double.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                var frac = match.Groups[3].Success
                    ? ParseFraction(match.Groups[3].Value)
                    : 0.0;
                var text = match.Groups[4].Value.Trim();
                lines.Add(new LrcLine
                {
                    Time = minutes * 60 + seconds + frac,
                    Text = text
                });
            }
        }

        if (lines.Count == 0) return null;
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static double ParseFraction(string value)
    {
        return value.Length switch
        {
            1 => int.Parse(value) / 10.0,
            2 => int.Parse(value) / 100.0,
            _ => int.Parse(value) / 1000.0
        };
    }

    /// <summary>Looks for a .lrc sidecar file next to the audio file and parses it.</summary>
    public static List<LrcLine>? LoadLrcFile(string audioPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var baseName = Path.GetFileNameWithoutExtension(audioPath);
            var candidates = new[]
            {
                Path.Combine(dir, baseName + ".lrc"),
                Path.Combine(dir, Path.GetFileName(audioPath) + ".lrc")
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return ParseLrc(File.ReadAllText(candidate));
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>Reads embedded lyrics from the audio file tags.</summary>
    public static string? LoadEmbeddedLyrics(string audioPath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(audioPath);
            var lyrics = tagFile.Tag.Lyrics;
            return string.IsNullOrWhiteSpace(lyrics) ? null : lyrics;
        }
        catch
        {
            return null;
        }
    }

    public class LyricCacheEntry
    {
        public bool synced { get; set; }
        public string content { get; set; } = "";
    }

    public class LyricCacheFile
    {
        public Dictionary<string, LyricCacheEntry> items { get; set; } = new();
    }

    public static string CacheFilePath =>
        Path.Combine(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassMusicPlayer", "lyrics"),
            "cache.json");

    public static string MakeCacheKey(string artist, string title)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((artist + " - " + title).ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static LyricCacheFile LoadCacheFile()
    {
        if (!File.Exists(CacheFilePath)) return new LyricCacheFile();
        try
        {
            return JsonSerializer.Deserialize<LyricCacheFile>(File.ReadAllText(CacheFilePath)) ?? new LyricCacheFile();
        }
        catch
        {
            return new LyricCacheFile();
        }
    }

    /// <summary>Returns cached online lyrics for artist/title, or null when not cached.</summary>
    public static (List<LrcLine> lines, bool synced)? LoadCache(string artist, string title)
    {
        try
        {
            var cache = LoadCacheFile();
            if (!cache.items.TryGetValue(MakeCacheKey(artist, title), out var entry)) return null;
            if (string.IsNullOrWhiteSpace(entry.content)) return null;

            var lines = ParseLrc(entry.content);
            if (lines != null && lines.Count > 0)
                return (lines, entry.synced);

            return (new List<LrcLine> { new() { Time = 0, Text = entry.content } }, false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Caches lyrics fetched online. Synced lines are stored as timestamps, plain ones as [00:00.00] prefixes.</summary>
    public static void SaveCache(string artist, string title, List<LrcLine> lines, bool synced)
    {
        try
        {
            var sb = new StringBuilder();
            if (synced)
            {
                foreach (var line in lines)
                {
                    sb.Append('[').Append(FormatLrcTime(line.Time)).Append(']').Append(line.Text).Append('\n');
                }
            }
            else
            {
                foreach (var line in lines)
                {
                    sb.Append("[00:00.00]").Append(line.Text).Append('\n');
                }
            }

            var cache = LoadCacheFile();
            cache.items[MakeCacheKey(artist, title)] = new LyricCacheEntry { synced = synced, content = sb.ToString() };
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(cache));
        }
        catch
        {
        }
    }

    private static string FormatLrcTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ((int)ts.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" +
               ts.Seconds.ToString("00", CultureInfo.InvariantCulture) + "." +
               (ts.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture);
    }
}