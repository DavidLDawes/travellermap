#nullable enable
using Maps.Utilities;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace Maps.Graphics
{
    /// <summary>
    /// PDF rendering with PDFsharp (core build: cross-platform, no GDI+). Text uses the bundled
    /// fonts (see SkiaFonts), laid out exactly as the SkiaSharp bitmap renderer lays it out;
    /// PDFsharp embeds only the glyphs each document uses.
    /// </summary>
    internal class PdfSharpGraphics : AbstractGraphics
    {
        private readonly XGraphics g;
        private readonly XSolidBrush brush;
        private readonly XPen pen;
        private readonly Dictionary<AbstractFont, SkiaFonts.FontSet> fontSets = new Dictionary<AbstractFont, SkiaFonts.FontSet>();
        private readonly Dictionary<(SkiaFonts.Face face, float size), XFont> xfonts = new Dictionary<(SkiaFonts.Face, float), XFont>();

        static PdfSharpGraphics()
        {
            GlobalFontSettings.FontResolver = new BundledFontResolver();
        }

        public PdfSharpGraphics(XGraphics g)
        {
            this.g = g;
            brush = new XSolidBrush();
            pen = new XPen(XColors.Black);
        }

        private static readonly object s_lock = new object();

        /// <summary>Renders a one-page PDF of the given size (in points) to the stream.</summary>
        public static void RenderPdf(Stream output, double width, double height, PdfInfo info, Action<AbstractGraphics> render)
        {
            // PDFsharp's font and image caches are shared, so serialize usage.
            long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (s_lock)
            {
                Maps.Admin.RequestStats.Current.RecordPdfLockWait(Maps.Admin.RequestStats.Since(waitStart));
                using var document = new PdfDocument();
                document.Version = 14; // 1.4 for opacity
                document.Info.Title = info.Title ?? "";
                document.Info.Author = info.Author ?? "";
                document.Info.Creator = info.Creator ?? "";
                document.Info.Subject = info.Subject ?? "";
                document.Info.Keywords = info.Keywords ?? "";

                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);

                using (var graphics = new PdfSharpGraphics(XGraphics.FromPdfPage(page)))
                    render(graphics);

                document.Save(output, closeStream: false);
            }
        }

        /// <summary>PDF document properties.</summary>
        internal sealed class PdfInfo
        {
            public string? Title { get; set; }
            public string? Author { get; set; }
            public string? Creator { get; set; }
            public string? Subject { get; set; }
            public string? Keywords { get; set; }
        }

        #region Conversions

        private static XColor ToXColor(Color c) => XColor.FromArgb(c.A, c.R, c.G, c.B);
        private static XPoint ToXPoint(PointF p) => new XPoint(p.X, p.Y);
        private static XPoint[] ToXPoints(PointF[] points) => points.Select(ToXPoint).ToArray();
        private static XRect ToXRect(RectangleF r) => new XRect(r.X, r.Y, r.Width, r.Height);

        private static XGraphicsPath ToXPath(AbstractPath path)
        {
            var xpath = new XGraphicsPath { FillMode = XFillMode.Winding };
            var points = path.Points;
            var types = path.Types;
            PointF current = PointF.Empty;
            for (int i = 0; i < points.Length; ++i)
            {
                byte type = types[i];
                switch (type & PathPointTypes.PathTypeMask)
                {
                    case PathPointTypes.Start:
                        xpath.StartFigure();
                        break;
                    case PathPointTypes.Line:
                        xpath.AddLine(ToXPoint(current), ToXPoint(points[i]));
                        break;
                    case PathPointTypes.Bezier:
                        // Béziers come in threes: two control points, then the end point.
                        xpath.AddBezier(ToXPoint(current), ToXPoint(points[i]), ToXPoint(points[i + 1]), ToXPoint(points[i + 2]));
                        i += 2;
                        type = types[i];
                        break;
                }
                current = points[i];
                if ((type & PathPointTypes.CloseSubpath) != 0)
                    xpath.CloseFigure();
            }
            return xpath;
        }

        private void Apply(AbstractBrush brush)
        {
            this.brush.Color = ToXColor(brush.Color);
        }
        private void Apply(AbstractPen pen)
        {
            this.pen.Color = ToXColor(pen.Color);
            this.pen.Width = pen.Width;
            this.pen.DashStyle = pen.DashStyle switch
            {
                DashStyle.Dot => XDashStyle.Dot,
                DashStyle.Dash => XDashStyle.Dash,
                DashStyle.DashDot => XDashStyle.DashDot,
                DashStyle.DashDotDot => XDashStyle.DashDotDot,
                DashStyle.Custom => XDashStyle.Custom,
                _ => XDashStyle.Solid,
            };
            if (pen.CustomDashPattern != null)
                this.pen.DashPattern = pen.CustomDashPattern.Select(f => (double)f).ToArray();
        }
        private void Apply(AbstractPen pen, AbstractBrush brush) { Apply(pen); Apply(brush); }

        #endregion

        public bool SupportsWingdings => false;
        public bool IsRaster => false;
        public SmoothingMode SmoothingMode { get => (SmoothingMode)g.SmoothingMode; set => g.SmoothingMode = (XSmoothingMode)value; }
        public bool TextGridFit { set { } }
        public void ScaleTransform(float scaleXY) { g.ScaleTransform(scaleXY); }
        public void ScaleTransform(float scaleX, float scaleY) { g.ScaleTransform(scaleX, scaleY); }
        public void TranslateTransform(float dx, float dy) { g.TranslateTransform(dx, dy); }
        public void RotateTransform(float angle) { g.RotateTransform(angle); }
        public void MultiplyTransform(AbstractMatrix m) { g.MultiplyTransform(new XMatrix(m.M11d, m.M12d, m.M21d, m.M22d, m.OffsetXd, m.OffsetYd)); }

        public void IntersectClip(AbstractPath path) { g.IntersectClip(ToXPath(path)); }
        public void IntersectClip(RectangleF rect) { g.IntersectClip(ToXRect(rect)); }

        public void DrawLine(AbstractPen pen, float x1, float y1, float x2, float y2) { Apply(pen); g.DrawLine(this.pen, x1, y1, x2, y2); }
        public void DrawLine(AbstractPen pen, PointF pt1, PointF pt2) { Apply(pen); g.DrawLine(this.pen, ToXPoint(pt1), ToXPoint(pt2)); }
        public void DrawLines(AbstractPen pen, PointF[] points) { Apply(pen); g.DrawLines(this.pen, ToXPoints(points)); }
        public void DrawPath(AbstractPen pen, AbstractPath path) { Apply(pen); g.DrawPath(this.pen, ToXPath(path)); }
        public void DrawPath(AbstractBrush brush, AbstractPath path) { Apply(brush); g.DrawPath(this.brush, ToXPath(path)); }
        public void DrawCurve(AbstractPen pen, PointF[] points, float tension) { Apply(pen); g.DrawCurve(this.pen, ToXPoints(points), tension); }
        public void DrawClosedCurve(AbstractPen pen, PointF[] points, float tension) { Apply(pen); g.DrawClosedCurve(this.pen, ToXPoints(points), tension); }
        public void DrawClosedCurve(AbstractBrush brush, PointF[] points, float tension) { Apply(brush); g.DrawClosedCurve(this.brush, ToXPoints(points), XFillMode.Alternate, tension); }
        public void DrawRectangle(AbstractPen pen, float x, float y, float width, float height) { Apply(pen); g.DrawRectangle(this.pen, x, y, width, height); }
        public void DrawRectangle(AbstractPen pen, RectangleF rect) { Apply(pen); g.DrawRectangle(this.pen, ToXRect(rect)); }
        public void DrawRectangle(AbstractBrush brush, float x, float y, float width, float height) { Apply(brush); g.DrawRectangle(this.brush, x, y, width, height); }
        public void DrawRectangle(AbstractBrush brush, RectangleF rect) { Apply(brush); g.DrawRectangle(this.brush, ToXRect(rect)); }
        public void DrawEllipse(AbstractPen pen, float x, float y, float width, float height) { Apply(pen); g.DrawEllipse(this.pen, x, y, width, height); }
        public void DrawEllipse(AbstractBrush brush, float x, float y, float width, float height) { Apply(brush); g.DrawEllipse(this.brush, x, y, width, height); }
        public void DrawEllipse(AbstractPen pen, AbstractBrush brush, float x, float y, float width, float height) { Apply(pen, brush); g.DrawEllipse(this.pen, this.brush, x, y, width, height); }
        public void DrawArc(AbstractPen pen, float x, float y, float width, float height, float startAngle, float sweepAngle) { Apply(pen); g.DrawArc(this.pen, x, y, width, height, startAngle, sweepAngle); }

        #region Images

        // PDFsharp can't draw images with partial opacity, so translucent variants are made
        // (quantized to ALPHA_STEPS levels) and kept with the image, keyed by level.
        private const int ALPHA_STEPS = 16;

        private sealed class PdfImages
        {
            public XImage? Image;
            public readonly Dictionary<int, XImage> AlphaVariants = new Dictionary<int, XImage>();
        }

        private static XImage GetXImage(AbstractImage image, int alphaLevel = ALPHA_STEPS)
        {
            var images = image.Natives.Get(() => new PdfImages());
            lock (images)
            {
                if (alphaLevel >= ALPHA_STEPS)
                    return images.Image ??= XImage.FromStream(new MemoryStream(File.ReadAllBytes(image.Path)));
                if (!images.AlphaVariants.TryGetValue(alphaLevel, out XImage? variant))
                {
                    variant = XImage.FromStream(new MemoryStream(MakeTranslucent(image.Path, (float)alphaLevel / ALPHA_STEPS)));
                    images.AlphaVariants[alphaLevel] = variant;
                }
                return variant;
            }
        }

        /// <summary>The image with its alpha scaled, as PNG.</summary>
        private static byte[] MakeTranslucent(string path, float alpha)
        {
            using var source = SKImage.FromEncodedData(path) ?? throw new IOException($"Unable to decode image {path}");
            using var surface = SKSurface.Create(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Unable to allocate image");
            surface.Canvas.Clear(SKColors.Transparent);
            using (var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(alpha * 255)) })
                surface.Canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        public void DrawImage(AbstractImage image, float x, float y, float width, float height)
        {
            XImage ximage = GetXImage(image);
            lock (ximage)
            {
                g.DrawImage(ximage, x, y, width, height);
            }
        }

        public void DrawImageAlpha(float alpha, AbstractImage image, RectangleF targetRect)
        {
            int level = (int)Math.Round(alpha.Clamp(0f, 1f) * ALPHA_STEPS);
            if (level <= 0)
                return;
            XImage ximage = GetXImage(image, level);
            lock (ximage)
            {
                g.DrawImage(ximage, ToXRect(targetRect));
            }
        }

        #endregion

        #region Text

        private SkiaFonts.FontSet Fonts(AbstractFont font)
        {
            if (!fontSets.TryGetValue(font, out var set))
            {
                set = SkiaFonts.Instance.CreateFonts(font);
                fontSets[font] = set;
            }
            return set;
        }

        private XFont GetXFont(SkiaFonts.Face face, AbstractFont font)
        {
            if (!xfonts.TryGetValue((face, font.Size), out XFont? xfont))
            {
                var style = (font.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (font.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
                xfont = new XFont(face.Family, font.Size, style, new XPdfFontOptions(PdfFontEncoding.Unicode));
                xfonts[(face, font.Size)] = xfont;
            }
            return xfont;
        }

        public SizeF MeasureString(string text, AbstractFont font)
        {
            var set = Fonts(font);
            return SkiaFonts.GdiCompatibleSize(set.MeasureAdvance(text), set.LineSpacing, font.Size);
        }

        public FontMetrics GetFontMetrics(AbstractFont font)
        {
            var set = Fonts(font);
            return new FontMetrics(set.Ascent, set.LineSpacing);
        }

        public void DrawString(string s, AbstractFont font, AbstractBrush brush, float x, float y, StringAlignment format)
        {
            Apply(brush);
            var set = Fonts(font);
            var (left, baseline) = set.Layout(s, font.Size, x, y, format);
            foreach (var (text, f) in set.Runs(s))
            {
                g.DrawString(text, GetXFont(set.FaceOf(f), font), this.brush, left, baseline, XStringFormats.BaseLineLeft);
                float advance = set.Advance(f, text);
                if (font.Underline || font.Strikeout)
                    g.DrawRectangle(this.brush, ToXRect(set.Decoration(font, left, baseline, advance)));
                left += advance;
            }
        }

        /// <summary>Serves the bundled font files to PDFsharp, by bundled family name.</summary>
        private sealed class BundledFontResolver : IFontResolver
        {
            private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();

            public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
            {
                if (!SkiaFonts.Instance.IsBundledFamily(familyName))
                    return null;
                var face = SkiaFonts.Instance.GetFace(familyName, bold, italic);
                return new FontResolverInfo(face.Path, face.FakeBold, face.FakeItalic);
            }

            public byte[]? GetFont(string faceName)
            {
                lock (files)
                {
                    if (!files.TryGetValue(faceName, out byte[]? data))
                    {
                        data = File.ReadAllBytes(faceName);
                        files[faceName] = data;
                    }
                    return data;
                }
            }
        }

        #endregion

        public AbstractGraphicsState Save() => new State(this, g.Save());
        public void Restore(AbstractGraphicsState state) { g.Restore(((State)state).state); }

        public void Dispose()
        {
            foreach (var set in fontSets.Values)
                set.Dispose();
            fontSets.Clear();
            g.Dispose();
        }

        private class State : AbstractGraphicsState
        {
            public XGraphicsState state;
            public State(AbstractGraphics g, XGraphicsState state) : base(g) { this.state = state; }
        }
    }
}
