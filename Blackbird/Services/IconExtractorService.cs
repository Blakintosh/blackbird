using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace Blackbird.Services;

#pragma warning disable CA1416 // Windows-only app targeting BO3 mod tools
public static class IconExtractorService
{
    private static readonly string IconCacheDir = AppPaths.IconsFolder;

    /// <summary>The exe's icon, from the cache when it has a readable copy. Null when the exe has none or can't be read.</summary>
    public static Bitmap? ExtractIcon(string exePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath)))[..16];
        var cachePath = Path.Combine(IconCacheDir, $"{hash}.png");

        if (File.Exists(cachePath))
        {
            try
            {
                return new Bitmap(cachePath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A truncated or damaged copy: extract it again rather than go iconless for good.
                DeleteCachedIcon(cachePath);
            }
        }

        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null)
                return null;

            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            TryCache(cachePath, ms.ToArray());

            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>Writes beside the target and moves it in, so a crash mid-write never leaves a half PNG.</summary>
    private static void TryCache(string cachePath, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(IconCacheDir);
            var temp = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The icon still shows this session; it's extracted again next time.
        }
    }

    public static string? GetCachePath(string exePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath)))[..16];
        var cachePath = Path.Combine(IconCacheDir, $"{hash}.png");
        return File.Exists(cachePath) ? cachePath : null;
    }

    public static void DeleteCachedIcon(string? cachePath)
    {
        try
        {
            if (cachePath is not null && File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next extraction to overwrite.
        }
    }
}
