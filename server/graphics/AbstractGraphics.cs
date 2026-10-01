#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

// Drawing abstraction shared by all output backends (bitmap, SVG, PDF). Nothing here depends
// on a particular graphics library: backends map these types to their own (e.g. GDI+ fonts,
// PDFsharp matrices) and cache native objects via GetNative().

namespace Maps.Graphics
{
#pragma warning disable IDE1006 // Naming Styles
    internal interface AbstractGraphics : IDisposable
#pragma warning restore IDE1006 // Naming Styles
    {
        SmoothingMode SmoothingMode { get; set; }
        bool SupportsWingdings { get; }

        /// <summary>
        /// Whether text is fitted to the pixel grid (crisper upright text). Turned off for
        /// rotated labels, where grid fitting distorts glyphs. Ignored by vector backends.
        /// Saved and restored with Save()/Restore().
        /// </summary>
        bool TextGridFit { set; }

        void ScaleTransform(float scaleXY);
        void ScaleTransform(float scaleX, float scaleY);
        void TranslateTransform(float dx, float dy);
        void RotateTransform(float angle);
        void MultiplyTransform(AbstractMatrix m);

        void IntersectClip(AbstractPath path);
        void IntersectClip(RectangleF rect);

        void DrawLine(AbstractPen pen, float x1, float y1, float x2, float y2);
        void DrawLine(AbstractPen pen, PointF pt1, PointF pt2);
        void DrawLines(AbstractPen pen, PointF[] points);
        void DrawPath(AbstractPen pen, AbstractPath path);
        void DrawPath(AbstractBrush brush, AbstractPath path);
        void DrawCurve(AbstractPen pen, PointF[] points, float tension = 0.5f);
        void DrawClosedCurve(AbstractPen pen, PointF[] points, float tension = 0.5f);
        void DrawClosedCurve(AbstractBrush brush, PointF[] points, float tension = 0.5f);
        void DrawRectangle(AbstractPen pen, float x, float y, float width, float height);
        void DrawRectangle(AbstractPen pen, RectangleF rect);
        void DrawRectangle(AbstractBrush brush, float x, float y, float width, float height);
        void DrawRectangle(AbstractBrush brush, RectangleF rect);
        void DrawEllipse(AbstractPen pen, float x, float y, float width, float height);
        void DrawEllipse(AbstractBrush brush, float x, float y, float width, float height);
        void DrawEllipse(AbstractPen pen, AbstractBrush brush, float x, float y, float width, float height);
        void DrawArc(AbstractPen pen, float x, float y, float width, float height, float startAngle, float sweepAngle);

        void DrawImage(AbstractImage image, float x, float y, float width, float height);
        void DrawImageAlpha(float alpha, AbstractImage image, RectangleF targetRect);

        SizeF MeasureString(string text, AbstractFont font);
        FontMetrics GetFontMetrics(AbstractFont font);
        void DrawString(string s, AbstractFont font, AbstractBrush brush, float x, float y, StringAlignment format);

        AbstractGraphicsState Save();
        void Restore(AbstractGraphicsState state);
    }

    internal abstract class AbstractGraphicsState : IDisposable
    {

        private AbstractGraphics? g;

        protected AbstractGraphicsState(AbstractGraphics graphics)
        {
            g = graphics;
        }

        public void Restore()
        {
            g!.Restore(this);
            g = null;
        }

        #region IDisposable Members

        public void Dispose()
        {
            g?.Restore(this);
            g = null;
        }

        #endregion
    }

    /// <summary>
    /// 2D affine transform (row-vector convention, like GDI+ and PDF).
    ///
    /// The arithmetic is ported from PDFsharp's XMatrix (MIT License, empira Software GmbH),
    /// which tracks the matrix type (identity/translation/scaling) and uses shortcut formulas
    /// for those cases. Matching it exactly keeps rendered output bit-identical with the
    /// previous XMatrix-based implementation.
    /// </summary>
    internal struct AbstractMatrix
    {
        [Flags]
        private enum MatrixTypes
        {
            Identity = 0,
            Translation = 1,
            Scaling = 2,
            Unknown = 4
        }

        private double m11, m12, m21, m22, offsetX, offsetY;
        private MatrixTypes type;

        public AbstractMatrix(float m11, float m12, float m21, float m22, float dx, float dy)
            : this((double)m11, m12, m21, m22, dx, dy)
        {
        }

        private AbstractMatrix(double m11, double m12, double m21, double m22, double offsetX, double offsetY)
        {
            this.m11 = m11;
            this.m12 = m12;
            this.m21 = m21;
            this.m22 = m22;
            this.offsetX = offsetX;
            this.offsetY = offsetY;
            type = MatrixTypes.Unknown;
            DeriveMatrixType();
        }

        private static AbstractMatrix Create(double m11, double m12, double m21, double m22, double offsetX, double offsetY, MatrixTypes type)
            => new AbstractMatrix { m11 = m11, m12 = m12, m21 = m21, m22 = m22, offsetX = offsetX, offsetY = offsetY, type = type };

        // Values as doubles, for backends that take them at full precision (PDF).
        public double M11d => type == MatrixTypes.Identity ? 1.0 : m11;
        public double M12d => type == MatrixTypes.Identity ? 0 : m12;
        public double M21d => type == MatrixTypes.Identity ? 0 : m21;
        public double M22d => type == MatrixTypes.Identity ? 1.0 : m22;
        public double OffsetXd => type == MatrixTypes.Identity ? 0 : offsetX;
        public double OffsetYd => type == MatrixTypes.Identity ? 0 : offsetY;

        public float M11 => (float)M11d;
        public float M12 => (float)M12d;
        public float M21 => (float)M21d;
        public float M22 => (float)M22d;
        public float OffsetX => (float)OffsetXd;
        public float OffsetY => (float)OffsetYd;

        private double Determinant => type switch
        {
            MatrixTypes.Identity or MatrixTypes.Translation => 1.0,
            MatrixTypes.Scaling or (MatrixTypes.Scaling | MatrixTypes.Translation) => m11 * m22,
            _ => (m11 * m22) - (m12 * m21),
        };

        public void Invert()
        {
            double determinant = Determinant;
            if (Math.Abs(determinant) < 10.0 * 2.2204460492503131E-16)
                throw new InvalidOperationException("Matrix is not invertible");

            switch (type)
            {
                case MatrixTypes.Identity:
                    break;

                case MatrixTypes.Translation:
                    offsetX = -offsetX;
                    offsetY = -offsetY;
                    return;

                case MatrixTypes.Scaling:
                    m11 = 1.0 / m11;
                    m22 = 1.0 / m22;
                    return;

                case MatrixTypes.Scaling | MatrixTypes.Translation:
                    m11 = 1.0 / m11;
                    m22 = 1.0 / m22;
                    offsetX = -offsetX * m11;
                    offsetY = -offsetY * m22;
                    return;

                default:
                    {
                        double detInverse = 1.0 / determinant;
                        this = Create(m22 * detInverse, -m12 * detInverse, -m21 * detInverse, m11 * detInverse,
                            (m21 * offsetY - offsetX * m22) * detInverse,
                            (offsetX * m12 - m11 * offsetY) * detInverse, MatrixTypes.Unknown);
                        break;
                    }
            }
        }

        public void RotatePrepend(float angle)
        {
            double a = angle % 360.0;
            double radians = a * (Math.PI / 180.0);
            double sin = Math.Sin(radians);
            double cos = Math.Cos(radians);
            this = Multiply(Create(cos, sin, -sin, cos, 0, 0, MatrixTypes.Unknown), this);
        }

        public void ScalePrepend(float sx, float sy) { this = Multiply(Create(sx, 0, 0, sy, 0, 0, MatrixTypes.Scaling), this); }
        public void TranslatePrepend(float dx, float dy) { this = Multiply(Create(1, 0, 0, 1, dx, dy, MatrixTypes.Translation), this); }
        public void Prepend(AbstractMatrix m) { this = Multiply(m, this); }

        // Fast multiplication taking matrix type into account (from XMatrix's MatrixHelper).
        private static AbstractMatrix Multiply(AbstractMatrix matrix1, AbstractMatrix matrix2)
        {
            var type1 = matrix1.type;
            var type2 = matrix2.type;
            if (type2 == MatrixTypes.Identity)
                return matrix1;
            if (type1 == MatrixTypes.Identity)
                return matrix2;
            if (type2 == MatrixTypes.Translation)
            {
                matrix1.offsetX += matrix2.offsetX;
                matrix1.offsetY += matrix2.offsetY;
                if (type1 != MatrixTypes.Unknown)
                    matrix1.type |= MatrixTypes.Translation;
                return matrix1;
            }
            if (type1 == MatrixTypes.Translation)
            {
                double num = matrix1.offsetX;
                double num2 = matrix1.offsetY;
                matrix1 = matrix2;
                matrix1.offsetX = num * matrix2.m11 + num2 * matrix2.m21 + matrix2.offsetX;
                matrix1.offsetY = num * matrix2.m12 + num2 * matrix2.m22 + matrix2.offsetY;
                matrix1.type = type2 == MatrixTypes.Unknown ? MatrixTypes.Unknown : MatrixTypes.Scaling | MatrixTypes.Translation;
                return matrix1;
            }

            switch (((int)type1 << 4) | (int)type2)
            {
                case ((int)MatrixTypes.Scaling << 4) | (int)MatrixTypes.Scaling:
                    matrix1.m11 *= matrix2.m11;
                    matrix1.m22 *= matrix2.m22;
                    return matrix1;

                case ((int)MatrixTypes.Scaling << 4) | (int)MatrixTypes.Translation | (int)MatrixTypes.Scaling:
                    matrix1.m11 *= matrix2.m11;
                    matrix1.m22 *= matrix2.m22;
                    matrix1.offsetX = matrix2.offsetX;
                    matrix1.offsetY = matrix2.offsetY;
                    matrix1.type = MatrixTypes.Scaling | MatrixTypes.Translation;
                    return matrix1;

                case (((int)MatrixTypes.Translation | (int)MatrixTypes.Scaling) << 4) | (int)MatrixTypes.Scaling:
                    matrix1.m11 *= matrix2.m11;
                    matrix1.m22 *= matrix2.m22;
                    matrix1.offsetX *= matrix2.m11;
                    matrix1.offsetY *= matrix2.m22;
                    return matrix1;

                case (((int)MatrixTypes.Translation | (int)MatrixTypes.Scaling) << 4) | (int)MatrixTypes.Translation | (int)MatrixTypes.Scaling:
                    matrix1.m11 *= matrix2.m11;
                    matrix1.m22 *= matrix2.m22;
                    matrix1.offsetX = matrix2.m11 * matrix1.offsetX + matrix2.offsetX;
                    matrix1.offsetY = matrix2.m22 * matrix1.offsetY + matrix2.offsetY;
                    return matrix1;

                default:
                    return new AbstractMatrix(
                        matrix1.m11 * matrix2.m11 + matrix1.m12 * matrix2.m21,
                        matrix1.m11 * matrix2.m12 + matrix1.m12 * matrix2.m22,
                        matrix1.m21 * matrix2.m11 + matrix1.m22 * matrix2.m21,
                        matrix1.m21 * matrix2.m12 + matrix1.m22 * matrix2.m22,
                        matrix1.offsetX * matrix2.m11 + matrix1.offsetY * matrix2.m21 + matrix2.offsetX,
                        matrix1.offsetX * matrix2.m12 + matrix1.offsetY * matrix2.m22 + matrix2.offsetY);
            }
        }

        private void DeriveMatrixType()
        {
            type = MatrixTypes.Identity;
            if (m12 != 0 || m21 != 0)
            {
                type = MatrixTypes.Unknown;
            }
            else
            {
                if (m11 != 1 || m22 != 1)
                    type = MatrixTypes.Scaling;
                if (offsetX != 0 || offsetY != 0)
                    type |= MatrixTypes.Translation;
                if ((type & (MatrixTypes.Scaling | MatrixTypes.Translation)) == MatrixTypes.Identity)
                    type = MatrixTypes.Identity;
            }
        }

        public static readonly AbstractMatrix Identity = new AbstractMatrix(1, 0, 0, 1, 0, 0);
    }

    /// <summary>
    /// Thread-safe per-type cache of backend-specific native objects (e.g. a GDI+ Font for an
    /// AbstractFont), so the abstraction itself doesn't depend on any graphics library.
    /// </summary>
    internal sealed class NativeCache
    {
        private readonly Dictionary<Type, object> natives = new Dictionary<Type, object>();

        public T Get<T>(Func<T> create) where T : class
        {
            lock (natives)
            {
                if (natives.TryGetValue(typeof(T), out object? existing))
                    return (T)existing;
                T value = create();
                natives[typeof(T)] = value;
                return value;
            }
        }
    }

    // This is a concrete class (despite the name) since we want static instances held by the server which
    // span different concrete instances.
    internal class AbstractImage
    {
        public string Path { get; }
        public string Url { get; }
        internal NativeCache Natives { get; } = new NativeCache();

        private string? dataUrl;
        public string DataUrl
        {
            get
            {
                if (dataUrl == null)
                {
                    string contentType = Utilities.ContentTypes.TypeForPath(Path);
                    // TODO: Use reader with FileShare.Read
                    byte[] bytes = File.ReadAllBytes(Path);
                    dataUrl = "data:" + contentType + ";base64," + Convert.ToBase64String(bytes, Base64FormattingOptions.None);
                }
                return dataUrl;
            }
        }

        public AbstractImage(string path, string url)
        {
            Path = path;
            Url = url;
        }
    }

    internal class AbstractPen
    {
        public Color Color { get; set; }
        public float Width { get; set; }
        public DashStyle DashStyle { get; set; } = DashStyle.Solid;
        public float[]? CustomDashPattern { get; set; }

        public AbstractPen() { }
        public AbstractPen(Color color, float width = 1)
        {
            Color = color;
            Width = width;
        }
    }

    internal class AbstractBrush
    {
        public Color Color { get; set; }
        public AbstractBrush() { }
        public AbstractBrush(Color color)
        {
            Color = color;
        }
    }

    /// <summary>
    /// A font request: comma-separated family fallbacks (e.g. "Calibri,Arial"), size in world
    /// units, and style. Backends resolve it to a native font (first installed family wins); the
    /// family list is also emitted as-is for remote rendering, e.g. SVG font-family.
    /// </summary>
    internal class AbstractFont
    {
        public AbstractFont(string families, float emSize, FontStyle style)
        {
            Families = families;
            Size = emSize;
            Style = style;
        }

        public string Families { get; }
        public float Size { get; }
        public FontStyle Style { get; }
        public bool Italic => (Style & FontStyle.Italic) != 0;
        public bool Bold => (Style & FontStyle.Bold) != 0;
        public bool Underline => (Style & FontStyle.Underline) != 0;
        public bool Strikeout => (Style & FontStyle.Strikeout) != 0;

        internal NativeCache Natives { get; } = new NativeCache();
    }

    /// <summary>Vertical font metrics in world units (the font's em size).</summary>
    internal struct FontMetrics
    {
        public FontMetrics(float ascent, float lineSpacing)
        {
            Ascent = ascent;
            LineSpacing = lineSpacing;
        }
        public float Ascent { get; }
        public float LineSpacing { get; }
    }

    /// <summary>Same values as System.Drawing.FontStyle.</summary>
    [Flags]
    internal enum FontStyle
    {
        Regular = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strikeout = 8,
    }

    /// <summary>Same values as System.Drawing.Drawing2D.SmoothingMode.</summary>
    internal enum SmoothingMode
    {
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        AntiAlias = 4,
    }

    internal enum StringAlignment
    {
        Baseline,
        Centered,
        TopLeft,
        TopCenter,
        TopRight,
        CenterLeft,
    };

    internal enum DashStyle
    {
        Solid,
        Dot,
        Dash,
        DashDot,
        DashDotDot,
        Custom,
    }
}
