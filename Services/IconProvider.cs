using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace GlassMusicPlayer.Services;

/// <summary>
/// Renders the user-provided SVG artwork into PNGs of all needed sizes and
/// builds a multi-resolution app.ico / tray.ico. Cached under
/// %APPDATA%\GlassMusicPlayer\icons so rendering happens only once.
/// </summary>
public static class IconProvider
{
    private static readonly object Sync = new();
    private static bool _generated;

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassMusicPlayer", "icons");

    public static string AppIcoPath => Path.Combine(CacheDir, "app.ico");
    public static string TrayIcoPath => Path.Combine(CacheDir, "tray.ico");

    private static string SourcePath(string name) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", name);

    public static void EnsureGenerated()
    {
        lock (Sync)
        {
            if (_generated) return;
            _generated = true;
            try
            {
                Directory.CreateDirectory(CacheDir);

                RenderPng("GlassPlayer Logo.svg", "logo", new[] { 256, 128, 64, 48, 32, 24, 16 },
                    new SvgContentBox(312, 216, 1080, 1080));
                RenderPng("GlassPlayer Tray.svg", "tray", new[] { 32, 16 },
                    new SvgContentBox(512, 352, 680, 804));

                BuildIco(AppIcoPath, new[] { 256, 128, 64, 48, 32, 24, 16 }, "logo");
                BuildIco(TrayIcoPath, new[] { 32, 16 }, "tray");
            }
            catch
            {
            }
        }
    }

    private readonly record struct SvgContentBox(double Left, double Top, double Width, double Height);

    private static void RenderPng(string svgName, string prefix, int[] sizes, SvgContentBox box)
    {
        var svgPath = SourcePath(svgName);
        if (!File.Exists(svgPath)) return;

        using var svg = new SKSvg();
        var picture = svg.Load(svgPath);
        if (picture == null) return;

        foreach (var size in sizes)
        {
            var outPath = Path.Combine(CacheDir, $"{prefix}-{size}.png");
            if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(svgPath)) continue;

            using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                var scale = (float)(size / Math.Max(box.Width, box.Height));
                var cx = (float)(size / 2.0 - (box.Left + box.Width / 2.0) * scale);
                var cy = (float)(size / 2.0 - (box.Top + box.Height / 2.0) * scale);
                canvas.Translate(cx, cy);
                canvas.Scale(scale);
                canvas.DrawPicture(picture);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(outPath);
            data.SaveTo(stream);
        }
    }

    private static void BuildIco(string icoPath, int[] sizes, string prefix)
    {
        var frames = new List<byte[]>();
        foreach (var size in sizes)
        {
            var pngPath = Path.Combine(CacheDir, $"{prefix}-{size}.png");
            if (File.Exists(pngPath))
                frames.Add(File.ReadAllBytes(pngPath));
        }
        if (frames.Count == 0) return;

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)frames.Count);

            var offset = 6 + 16 * frames.Count;
            foreach (var frame in frames)
            {
                var dim = ReadPngDimension(frame);
                bw.Write((byte)(dim >= 256 ? 0 : dim));
                bw.Write((byte)(dim >= 256 ? 0 : dim));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)frame.Length);
                bw.Write((uint)offset);
                offset += frame.Length;
            }

            foreach (var frame in frames)
                bw.Write(frame);
        }

        File.WriteAllBytes(icoPath, ms.ToArray());
    }

    private static int ReadPngDimension(byte[] png)
    {
        if (png.Length < 24) return 16;
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        return w;
    }

    public static System.Drawing.Icon LoadAppIcon()
    {
        EnsureGenerated();
        try
        {
            using var fs = new FileStream(AppIcoPath, FileMode.Open, FileAccess.Read);
            return new Icon(fs, 32, 32);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public static System.Drawing.Icon LoadTrayIcon()
    {
        EnsureGenerated();
        try
        {
            using var fs = new FileStream(TrayIcoPath, FileMode.Open, FileAccess.Read);
            return new Icon(fs, 32, 32);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public static BitmapSource LoadWindowIconSource()
    {
        EnsureGenerated();
        try
        {
            var decoder = new IconBitmapDecoder(
                new Uri(AppIcoPath),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch
        {
            return null;
        }
    }
}