#nullable enable
using System;
using System.Drawing;
using System.IO;

namespace Maps.Graphics
{
    /// <summary>
    /// GDI+ (System.Drawing, Windows-only) implementations of the abstract font, image, and
    /// transform types, shared by the GDI-based backends (bitmap, PDF, and SVG text measurement).
    /// </summary>
    internal static class GdiSupport
    {
        /// <summary>The first family in the font's fallback list that's installed.</summary>
        public static Font Font(AbstractFont font) => font.Natives.Get(() =>
        {
            foreach (var family in font.Families.Split(','))
            {
                var gdiFont = new Font(family, font.Size, (System.Drawing.FontStyle)font.Style, GraphicsUnit.World);
                if (gdiFont.Name == family)
                    return gdiFont;
                gdiFont.Dispose();
            }
            throw new ApplicationException("No matching font family");
        });

        public static FontMetrics Metrics(AbstractFont font)
        {
            Font f = Font(font);
            float fontUnitsToWorldUnits = f.Size / f.FontFamily.GetEmHeight(f.Style);
            return new FontMetrics(
                ascent: f.FontFamily.GetCellAscent(f.Style) * fontUnitsToWorldUnits,
                lineSpacing: f.FontFamily.GetLineSpacing(f.Style) * fontUnitsToWorldUnits);
        }

        private static readonly Lazy<System.Drawing.Graphics> s_measureGraphics =
            new Lazy<System.Drawing.Graphics>(() => System.Drawing.Graphics.FromImage(new Bitmap(1, 1)));

        /// <summary>Measures text for backends without their own GDI+ surface (SVG).</summary>
        public static SizeF MeasureString(string text, AbstractFont font)
        {
            var g = s_measureGraphics.Value;
            lock (g)
            {
                return g.MeasureString(text, Font(font));
            }
        }

        public static Image Image(AbstractImage image) => image.Natives.Get(() =>
        {
            // Use a stream since Image.FromFile(path) locks the file on disk.
            using var stream = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return System.Drawing.Image.FromStream(stream);
        });

        public static System.Drawing.Drawing2D.Matrix Matrix(AbstractMatrix m) =>
            new System.Drawing.Drawing2D.Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);

        public static System.Drawing.Drawing2D.SmoothingMode Convert(SmoothingMode mode) => (System.Drawing.Drawing2D.SmoothingMode)mode;
        public static SmoothingMode Convert(System.Drawing.Drawing2D.SmoothingMode mode) => (SmoothingMode)mode;
    }
}
