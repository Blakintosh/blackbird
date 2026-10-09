using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Blackbird.Shots;

#pragma warning disable CA1416 // The harness runs where Blackbird runs: Windows.
internal static class Images
{
    /// <summary>A 16:9 stand-in for Workshop art: a dark gradient with a caption.</summary>
    public static void WriteThumbnail(string path, string caption)
    {
        using var bitmap = new Bitmap(640, 360);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var hue = caption.Sum(ch => ch) % 3; // stable across runs, unlike GetHashCode
        var (from, to) = hue switch
        {
            0 => (Color.FromArgb(38, 20, 16), Color.FromArgb(150, 62, 34)),
            1 => (Color.FromArgb(16, 26, 38), Color.FromArgb(44, 98, 150)),
            _ => (Color.FromArgb(18, 30, 22), Color.FromArgb(70, 120, 64)),
        };
        using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 640, 360), from, to, 35f))
            g.FillRectangle(fill, 0, 0, 640, 360);

        using var font = new Font("Segoe UI", 40, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        var size = g.MeasureString(caption, font);
        g.DrawString(caption, font, text, (640 - size.Width) / 2, (360 - size.Height) / 2);

        bitmap.Save(path, ImageFormat.Png);
    }
}
