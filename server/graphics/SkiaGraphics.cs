#nullable enable
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace Maps.Graphics
{
    /// <summary>
    /// Bitmap rendering with SkiaSharp (cross-platform). Text uses only the bundled fonts in
    /// res/fonts (see SkiaFonts). Behavior follows the GDI+ backend (BitmapGraphics) where the
    /// rendering code depends on it: pen dash patterns, minimum one-pixel lines, nonzero fill
    /// rule, cardinal splines, string alignment, and MeasureString padding.
    /// </summary>
    internal sealed class SkiaGraphics : AbstractGraphics
    {
        private readonly SKCanvas canvas;
        private readonly SKPaint paint = new SKPaint();
        private readonly Dictionary<AbstractFont, SkiaFonts.FontSet> fonts =
            new Dictionary<AbstractFont, SkiaFonts.FontSet>();

        // State that SKCanvas.Save() doesn't cover but Graphics.Save() did.
        private SmoothingMode smoothingMode = SmoothingMode.HighSpeed;
        private bool textGridFit = true;

        public SkiaGraphics(SKCanvas canvas)
        {
            this.canvas = canvas;
        }

        public SmoothingMode SmoothingMode { get => smoothingMode; set => smoothingMode = value; }
        public bool TextGridFit { set => textGridFit = value; }
        public bool SupportsWingdings => false;
        public bool IsRaster => true;

        private bool Antialias => smoothingMode == SmoothingMode.AntiAlias || smoothingMode == SmoothingMode.HighQuality;

        #region Transforms and clipping

        public void ScaleTransform(float scaleXY) { canvas.Scale(scaleXY); }
        public void ScaleTransform(float scaleX, float scaleY) { canvas.Scale(scaleX, scaleY); }
        public void TranslateTransform(float dx, float dy) { canvas.Translate(dx, dy); }
        public void RotateTransform(float angle) { canvas.RotateDegrees(angle); }
        public void MultiplyTransform(AbstractMatrix m)
        {
            // AbstractMatrix uses the row-vector convention: x' = M11 x + M21 y + OffsetX.
            var matrix = new SKMatrix(m.M11, m.M21, m.OffsetX, m.M12, m.M22, m.OffsetY, 0, 0, 1);
            canvas.Concat(in matrix);
        }

        public void IntersectClip(AbstractPath path) { using var p = ToSKPath(path); canvas.ClipPath(p, SKClipOperation.Intersect, antialias: true); }
        public void IntersectClip(RectangleF rect) { canvas.ClipRect(SKRect.Create(rect.X, rect.Y, rect.Width, rect.Height), SKClipOperation.Intersect, antialias: false); }

        #endregion

        #region Paints

        private SKPaint Fill(AbstractBrush brush)
        {
            paint.Reset();
            paint.Style = SKPaintStyle.Fill;
            paint.IsAntialias = Antialias;
            paint.Color = ToSKColor(brush.Color);
            return paint;
        }

        private SKPaint Stroke(AbstractPen pen)
        {
            paint.Reset();
            paint.Style = SKPaintStyle.Stroke;
            paint.IsAntialias = Antialias;
            paint.Color = ToSKColor(pen.Color);
            paint.StrokeCap = SKStrokeCap.Butt;
            paint.StrokeJoin = SKStrokeJoin.Miter;
            paint.StrokeMiter = 10; // GDI+ default

            // GDI+ draws pens narrower than a device pixel one pixel wide; Skia would fade them
            // instead, washing out grids and borders at small scales. Use a hairline for those.
            var m = canvas.TotalMatrix;
            float deviceScale = (float)Math.Sqrt(Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY));
            paint.StrokeWidth = pen.Width * deviceScale < 1 ? 0 : pen.Width;

            // GDI+ dash patterns are in multiples of the pen width.
            float[]? pattern = pen.DashStyle switch
            {
                DashStyle.Dot => new float[] { 1, 1 },
                DashStyle.Dash => new float[] { 3, 1 },
                DashStyle.DashDot => new float[] { 3, 1, 1, 1 },
                DashStyle.DashDotDot => new float[] { 3, 1, 1, 1, 1, 1 },
                DashStyle.Custom => pen.CustomDashPattern,
                _ => null,
            };
            if (pattern != null && pattern.Length >= 2)
            {
                float[] intervals = new float[pattern.Length % 2 == 0 ? pattern.Length : pattern.Length * 2];
                for (int i = 0; i < intervals.Length; ++i)
                    intervals[i] = Math.Max(pattern[i % pattern.Length] * pen.Width, 1e-6f);
                paint.PathEffect = SKPathEffect.CreateDash(intervals, 0);
            }
            return paint;
        }

        private static SKColor ToSKColor(Color color) => new SKColor(color.R, color.G, color.B, color.A);

        #endregion

        #region Drawing

        public void DrawLine(AbstractPen pen, float x1, float y1, float x2, float y2) { canvas.DrawLine(x1, y1, x2, y2, Stroke(pen)); }
        public void DrawLine(AbstractPen pen, PointF pt1, PointF pt2) { canvas.DrawLine(pt1.X, pt1.Y, pt2.X, pt2.Y, Stroke(pen)); }
        public void DrawLines(AbstractPen pen, PointF[] points)
        {
            using var builder = new SKPathBuilder();
            builder.MoveTo(points[0].X, points[0].Y);
            for (int i = 1; i < points.Length; ++i)
                builder.LineTo(points[i].X, points[i].Y);
            using var path = builder.Detach();
            canvas.DrawPath(path, Stroke(pen));
        }

        public void DrawPath(AbstractPen pen, AbstractPath path) { using var p = ToSKPath(path); canvas.DrawPath(p, Stroke(pen)); }
        public void DrawPath(AbstractBrush brush, AbstractPath path) { using var p = ToSKPath(path); canvas.DrawPath(p, Fill(brush)); }

        public void DrawCurve(AbstractPen pen, PointF[] points, float tension) { using var p = CardinalSpline(points, tension, closed: false); canvas.DrawPath(p, Stroke(pen)); }
        public void DrawClosedCurve(AbstractPen pen, PointF[] points, float tension) { using var p = CardinalSpline(points, tension, closed: true); canvas.DrawPath(p, Stroke(pen)); }
        public void DrawClosedCurve(AbstractBrush brush, PointF[] points, float tension) { using var p = CardinalSpline(points, tension, closed: true); canvas.DrawPath(p, Fill(brush)); }

        public void DrawRectangle(AbstractPen pen, float x, float y, float width, float height) { canvas.DrawRect(x, y, width, height, Stroke(pen)); }
        public void DrawRectangle(AbstractPen pen, RectangleF rect) { canvas.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, Stroke(pen)); }
        public void DrawRectangle(AbstractBrush brush, float x, float y, float width, float height) { canvas.DrawRect(x, y, width, height, Fill(brush)); }
        public void DrawRectangle(AbstractBrush brush, RectangleF rect) { canvas.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, Fill(brush)); }

        public void DrawEllipse(AbstractPen pen, float x, float y, float width, float height) { canvas.DrawOval(SKRect.Create(x, y, width, height), Stroke(pen)); }
        public void DrawEllipse(AbstractBrush brush, float x, float y, float width, float height) { canvas.DrawOval(SKRect.Create(x, y, width, height), Fill(brush)); }
        public void DrawEllipse(AbstractPen pen, AbstractBrush brush, float x, float y, float width, float height)
        {
            var rect = SKRect.Create(x, y, width, height);
            canvas.DrawOval(rect, Fill(brush));
            canvas.DrawOval(rect, Stroke(pen));
        }
        public void DrawArc(AbstractPen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
        {
            canvas.DrawArc(SKRect.Create(x, y, width, height), startAngle, sweepAngle, useCenter: false, Stroke(pen));
        }

        #endregion

        #region Images

        private static readonly SKSamplingOptions s_sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        private static SKImage GetImage(AbstractImage image) => image.Natives.Get(() =>
            SKImage.FromEncodedData(image.Path) ?? throw new IOException($"Unable to decode image {image.Path}"));

        public void DrawImage(AbstractImage image, float x, float y, float width, float height)
        {
            paint.Reset();
            canvas.DrawImage(GetImage(image), SKRect.Create(x, y, width, height), s_sampling, paint);
        }

        public void DrawImageAlpha(float alpha, AbstractImage image, RectangleF targetRect)
        {
            if (alpha <= 0)
                return;
            paint.Reset();
            // The paint's alpha modulates the image.
            paint.Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Min(alpha, 1) * 255));
            canvas.DrawImage(GetImage(image), SKRect.Create(targetRect.X, targetRect.Y, targetRect.Width, targetRect.Height), s_sampling, paint);
        }

        #endregion

        #region Text

        private SkiaFonts.FontSet Fonts(AbstractFont font)
        {
            if (!fonts.TryGetValue(font, out var set))
            {
                set = SkiaFonts.Instance.CreateFonts(font);
                fonts[font] = set;
            }
            set.Hinting = textGridFit ? SKFontHinting.Slight : SKFontHinting.None;
            return set;
        }

        public SizeF MeasureString(string text, AbstractFont font)
        {
            var set = Fonts(font);
            return SkiaFonts.GdiCompatibleSize(set.MeasureAdvance(text), set.Primary.Spacing, font.Size);
        }

        public FontMetrics GetFontMetrics(AbstractFont font)
        {
            var primary = Fonts(font).Primary;
            return new FontMetrics(-primary.Metrics.Ascent, primary.Spacing);
        }

        public void DrawString(string s, AbstractFont font, AbstractBrush brush, float x, float y, StringAlignment format)
        {
            var set = Fonts(font);
            var (left, baseline) = set.Layout(s, font.Size, x, y, format);

            var p = Fill(brush);
            p.IsAntialias = true;
            foreach (var (text, f) in set.Runs(s))
            {
                canvas.DrawText(text, left, baseline, SKTextAlign.Left, f, p);
                float advance = f.MeasureText(text);
                if (font.Underline || font.Strikeout)
                {
                    var bar = set.Decoration(font, left, baseline, advance);
                    canvas.DrawRect(bar.X, bar.Y, bar.Width, bar.Height, p);
                }
                left += advance;
            }
        }

        #endregion

        #region State

        public AbstractGraphicsState Save() => new State(this, canvas.Save(), smoothingMode, textGridFit);
        public void Restore(AbstractGraphicsState state)
        {
            var s = (State)state;
            canvas.RestoreToCount(s.saveCount);
            smoothingMode = s.smoothingMode;
            textGridFit = s.textGridFit;
        }

        private sealed class State : AbstractGraphicsState
        {
            public readonly int saveCount;
            public readonly SmoothingMode smoothingMode;
            public readonly bool textGridFit;
            public State(AbstractGraphics g, int saveCount, SmoothingMode smoothingMode, bool textGridFit) : base(g)
            {
                this.saveCount = saveCount;
                this.smoothingMode = smoothingMode;
                this.textGridFit = textGridFit;
            }
        }

        #endregion

        #region Paths

        private static SKPath ToSKPath(AbstractPath path)
        {
            using var p = new SKPathBuilder { FillType = SKPathFillType.Winding };
            var points = path.Points;
            var types = path.Types;
            for (int i = 0; i < points.Length; ++i)
            {
                byte type = types[i];
                switch (type & PathPointTypes.PathTypeMask)
                {
                    case PathPointTypes.Start:
                        p.MoveTo(points[i].X, points[i].Y);
                        break;
                    case PathPointTypes.Line:
                        p.LineTo(points[i].X, points[i].Y);
                        break;
                    case PathPointTypes.Bezier:
                        // Béziers come in threes: two control points, then the end point.
                        p.CubicTo(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, points[i + 2].X, points[i + 2].Y);
                        i += 2;
                        type = types[i];
                        break;
                }
                if ((type & PathPointTypes.CloseSubpath) != 0)
                    p.Close();
            }
            return p.Detach();
        }

        /// <summary>
        /// A cardinal spline through the points, as GDI+ DrawCurve/DrawClosedCurve draw it:
        /// Bézier segments whose control points lie along the tangent (next - previous point),
        /// scaled by 0.3 * tension.
        /// </summary>
        private static SKPath CardinalSpline(PointF[] points, float tension, bool closed)
        {
            using var path = new SKPathBuilder { FillType = SKPathFillType.Winding };
            int n = points.Length;
            if (n < 2)
                return path.Detach();
            float t = tension * 0.3f;

            PointF Tangent(int i)
            {
                PointF prev = closed ? points[(i + n - 1) % n] : points[Math.Max(i - 1, 0)];
                PointF next = closed ? points[(i + 1) % n] : points[Math.Min(i + 1, n - 1)];
                return new PointF(next.X - prev.X, next.Y - prev.Y);
            }
            // GDI+ uses the chord to the neighbor, not a tangent, at the ends of an open curve.
            PointF After(int i)
            {
                if (!closed && i == 0)
                    return new PointF(points[0].X + t * (points[1].X - points[0].X), points[0].Y + t * (points[1].Y - points[0].Y));
                var d = Tangent(i);
                return new PointF(points[i].X + t * d.X, points[i].Y + t * d.Y);
            }
            PointF Before(int i)
            {
                if (!closed && i == n - 1)
                    return new PointF(points[i].X + t * (points[i - 1].X - points[i].X), points[i].Y + t * (points[i - 1].Y - points[i].Y));
                var d = Tangent(i);
                return new PointF(points[i].X - t * d.X, points[i].Y - t * d.Y);
            }

            path.MoveTo(points[0].X, points[0].Y);
            int segments = closed ? n : n - 1;
            for (int i = 0; i < segments; ++i)
            {
                int j = (i + 1) % n;
                PointF c1 = After(i), c2 = Before(j);
                path.CubicTo(c1.X, c1.Y, c2.X, c2.Y, points[j].X, points[j].Y);
            }
            if (closed)
                path.Close();
            return path.Detach();
        }

        #endregion

        #region Output

        /// <summary>
        /// Renders into a new 32-bit bitmap and encodes it as PNG or JPEG (by MIME type).
        /// Returns null if the bitmap couldn't be allocated.
        /// </summary>
        public static byte[]? RenderBitmap(int width, int height, string mimeType, Action<AbstractGraphics> render)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null)
                return null;
            surface.Canvas.Clear(SKColors.Transparent);
            using (var graphics = new SkiaGraphics(surface.Canvas))
                render(graphics);

            using var image = surface.Snapshot();
            bool jpeg = mimeType == Utilities.ContentTypes.Image.Jpeg;
            using var data = image.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? 95 : 100);
            return data.ToArray();
        }

        public void Dispose()
        {
            foreach (var set in fonts.Values)
                set.Dispose();
            fonts.Clear();
            paint.Dispose();
        }

        #endregion
    }
}
