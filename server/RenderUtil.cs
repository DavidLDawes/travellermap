#nullable enable
using Maps.Graphics;
using Maps.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace Maps.Rendering
{
    internal static class RenderUtil
    {
        /*
                    1
            +-*------------*x+
            |/              \|
            /                \
           /|                |\
          * |                +x*  x = tan( pi / 6 ) / 4
           \|                |/
            \                /
            |\              /|
            +-*------------*-+
        */
        // Hex geometry lives in PathUtil (Maps.Core); these forward for rendering code.
        public static readonly float HEX_EDGE = PathUtil.HEX_EDGE;

        public static void HexEdges(PathUtil.PathType type, out float[] edgeX, out float[] edgeY)
            => PathUtil.HexEdges(type, out edgeX, out edgeY);

        public static readonly AbstractPath HexPath = new AbstractPath(
            new PointF[] {
                new PointF(-0.5f + RenderUtil.HEX_EDGE, -0.5f),
                new PointF( 0.5f - RenderUtil.HEX_EDGE, -0.5f),
                new PointF( 0.5f + RenderUtil.HEX_EDGE, 0),
                new PointF( 0.5f - RenderUtil.HEX_EDGE, 0.5f),
                new PointF(-0.5f + RenderUtil.HEX_EDGE, 0.5f),
                new PointF(-0.5f - RenderUtil.HEX_EDGE, 0),
                new PointF(-0.5f + RenderUtil.HEX_EDGE, -0.5f),
            },
            new byte[] {
                PathPointTypes.Start,
                PathPointTypes.Line,
                PathPointTypes.Line,
                PathPointTypes.Line,
                PathPointTypes.Line,
                PathPointTypes.Line,
                (byte)(PathPointTypes.Line | PathPointTypes.CloseSubpath),
            });

        // NOTE: Wingdings are often used instead of UNICODE equivalents in a common font
        // because the glyphs are much higher quality.
        // See http://www.alanwood.net/demos/wingdings.html for a good mapping

        private static readonly IReadOnlyDictionary<char, char> DING_MAP =
            new Dictionary<char, char> {
            { '\x2666', '\x74' }, // U+2666 (BLACK DIAMOND SUIT)
            { '\x2756', '\x76' }, // U+2756 (BLACK DIAMOND MINUS WHITE X)
            { '\x2726', '\xAA' }, // U+2726 (BLACK FOUR POINTED STAR)
            { '\x2605', '\xAB' }, // U+2605 (BLACK STAR)
            { '\x2736', '\xAC' }, // U+2736 (BLACK SIX POINTED STAR)
        };

        public static void DrawGlyph(AbstractGraphics g, Glyph glyph, FontCache styleRes, AbstractBrush brush, PointF pt)
        {
            AbstractFont font;
            string s = glyph.Characters;
            if (g.SupportsWingdings && s.All(c => DING_MAP.ContainsKey(c)))
            {
                s = string.Join("", s.Select(c => DING_MAP[c]));
                font = styleRes.WingdingFont;
            }
            else
            {
                font = styleRes.GlyphFont;
            }

            g.DrawString(s, font, brush, pt.X, pt.Y, Maps.Graphics.StringAlignment.Centered);
        }

        //               |          |
        //           Top |   Top    | Top
        //          Left |  Center  | Right
        // --------------+----------+----------------
        //   Middle Left |  Center  | Middle Right
        // --------------+----------+-----------------
        //        Bottom |  Bottom  | Bottom
        //          Left |  Center  | Right
        //               |          |
        public enum TextFormat
        {
            TopLeft,
            TopCenter,
            TopRight,
            MiddleLeft,
            Center,
            MiddleRight,
            BottomLeft,
            BottomCenter,
            BottomRight
        }

        // String is positioned relative to x,y according to TextFormat.
        // TextFormat also controls text alignment (left, center, right).
        // Handles embedded newlines.
        public static void DrawString(AbstractGraphics g, string text, AbstractFont font, AbstractBrush brush, float x, float y, TextFormat format = TextFormat.Center)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var lines = text.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            var sizes = lines.Select(s => g.MeasureString(s, font)).ToList();

            float lineSpacing = g.GetFontMetrics(font).LineSpacing;

            SizeF boundingSize = new SizeF(sizes.Max(s => s.Width), lineSpacing * sizes.Count());

            // Offset from baseline to top-left.
            y += lineSpacing / 2;

            float widthFactor = 0;
            switch (format)
            {
                case TextFormat.MiddleLeft:
                case TextFormat.Center:
                case TextFormat.MiddleRight:
                    y -= boundingSize.Height / 2;
                    break;
                case TextFormat.BottomLeft:
                case TextFormat.BottomCenter:
                case TextFormat.BottomRight:
                    y -= boundingSize.Height;
                    break;
            }

            switch (format)
            {
                case TextFormat.TopCenter:
                case TextFormat.Center:
                case TextFormat.BottomCenter:
                    widthFactor = -0.5f;
                    break;
                case TextFormat.TopRight:
                case TextFormat.MiddleRight:
                case TextFormat.BottomRight:
                    widthFactor = -1;
                    break;
            }

            lines.ForEachZip(sizes, (line, sz) =>
            {
                g.DrawString(line, font, brush, x + widthFactor * sz.Width + sz.Width / 2, y, Graphics.StringAlignment.Centered);
                y += lineSpacing;
            });
        }

        // Used for Sector/Subsector names and labels (borders and explicit).
        // Always centered on given coordinates.
        public static void DrawLabel(AbstractGraphics g, string text, PointF center, AbstractFont font, AbstractBrush brush, LabelStyle labelStyle)
        {
            using (g.Save())
            {
                if (labelStyle.Uppercase)
                    text = text.ToUpper();
                if (labelStyle.Wrap)
                    text = text.Replace(' ', '\n');

                g.TranslateTransform(center.X, center.Y);
                g.ScaleTransform(1.0f / Astrometrics.ParsecScaleX, 1.0f / Astrometrics.ParsecScaleY);

                g.TranslateTransform(labelStyle.Translation.X, labelStyle.Translation.Y);
                g.RotateTransform(labelStyle.Rotation);
                g.ScaleTransform(labelStyle.Scale.Width, labelStyle.Scale.Height);

                if (labelStyle.Rotation != 0)
                    g.TextGridFit = false;

                DrawString(g, text, font, brush, 0, 0);
            }
        }
    }

    internal struct Glyph
    {
        public enum GlyphBias
        {
            None,
            Top,
            Bottom
        }
        public string Characters { get; }
        public GlyphBias Bias { get; }
        public bool IsHighlighted { get; }

        public Glyph(string chars, bool highlight = false)
        {
            Characters = chars;
            Bias = GlyphBias.None;
            IsHighlighted = highlight;
        }
        public Glyph(Glyph other, bool highlight = false, GlyphBias bias = GlyphBias.None)
        {
            Characters = other.Characters;
            IsHighlighted = highlight;
            Bias = bias;
        }

        public bool IsPrintable => Characters.Length > 0;
        public static readonly Glyph None = new Glyph("");
        public static readonly Glyph Diamond = new Glyph("\x2666"); // U+2666 (BLACK DIAMOND SUIT)
        public static readonly Glyph DiamondX = new Glyph("\x2756"); // U+2756 (BLACK DIAMOND MINUS WHITE X)
        public static readonly Glyph Circle = new Glyph("\x2022"); // U+2022 (BULLET); alternate:  U+25CF (BLACK CIRCLE)
        public static readonly Glyph Triangle = new Glyph("\x25B2"); // U+25B2 (BLACK UP-POINTING TRIANGLE)
        public static readonly Glyph Square = new Glyph("\x25A0"); // U+25A0 (BLACK SQUARE)
        public static readonly Glyph Star4Point = new Glyph("\x2726"); // U+2726 (BLACK FOUR POINTED STAR)
        public static readonly Glyph Star5Point = new Glyph("\x2605"); // U+2605 (BLACK STAR)
        public static readonly Glyph StarStar = new Glyph("**"); // Would prefer U+2217 (ASTERISK OPERATOR) but font coverage is poor

        // Research Stations
        public static readonly Glyph Alpha = new Glyph("\x0391", highlight: true);
        public static readonly Glyph Beta = new Glyph("\x0392", highlight: true);
        public static readonly Glyph Gamma = new Glyph("\x0393", highlight: true);
        public static readonly Glyph Delta = new Glyph("\x0394", highlight: true);
        public static readonly Glyph Epsilon = new Glyph("\x0395", highlight: true);
        public static readonly Glyph Zeta = new Glyph("\x0396", highlight: true);
        public static readonly Glyph Eta = new Glyph("\x0397", highlight: true);
        public static readonly Glyph Theta = new Glyph("\x0398", highlight: true);
        public static readonly Glyph Omicron = new Glyph("\x039F", highlight: true);

        // Other Textual
        public static readonly Glyph Prison = new Glyph("P", highlight: true);
        public static readonly Glyph Reserve = new Glyph("R");
        public static readonly Glyph ExileCamp = new Glyph("X");

        // TNE
        public static readonly Glyph HiverSupplyBase = new Glyph("\x2297");
        public static readonly Glyph Terminus = new Glyph("\x2297");
        public static readonly Glyph Interface = new Glyph("\x2297");

        public static Glyph FromResearchCode(string rs)
        {
            Glyph glyph = Glyph.Gamma;

            if (rs.Length == 3)
            {
                char c = rs[2];
                glyph = c switch
                {
                    'A' => Glyph.Alpha,
                    'B' => Glyph.Beta,
                    'G' => Glyph.Gamma,
                    'D' => Glyph.Delta,
                    'E' => Glyph.Epsilon,
                    'Z' => Glyph.Zeta,
                    'H' => Glyph.Eta,
                    'T' => Glyph.Theta,
                    'O' => Glyph.Omicron,
                    _ => glyph
                };
            }
            return glyph;
        }

        private static readonly RegexMap<Glyph> s_baseGlyphTable =
            new GlobMap<Glyph> {
            { "*.C", new Glyph(Glyph.StarStar, bias:GlyphBias.Bottom) }, // Vargr Corsair Base
            { "Im.D", new Glyph(Glyph.Square, bias:GlyphBias.Bottom) }, // Imperial Depot
            { "*.D", new Glyph(Glyph.Square, highlight:true)}, // Depot
            { "*.E", new Glyph(Glyph.StarStar, bias:GlyphBias.Bottom) }, // Hiver Embassy
            { "*.K", new Glyph(Glyph.Star5Point, highlight:true, bias:GlyphBias.Top) }, // Naval Base
            { "*.M", new Glyph(Glyph.Star4Point, bias:GlyphBias.Bottom) }, // Military Base
            { "*.N", new Glyph(Glyph.Star5Point, bias:GlyphBias.Top) }, // Imperial Naval Base
            { "*.O", new Glyph(Glyph.Square, highlight:true, bias:GlyphBias.Top) }, // K'kree Naval Outpost (non-standard)
            { "*.R", new Glyph(Glyph.StarStar, bias:GlyphBias.Bottom) }, // Aslan Clan Base
            { "*.S", new Glyph(Glyph.Triangle, bias:GlyphBias.Bottom) }, // Imperial Scout Base
            { "*.T", new Glyph(Glyph.Star5Point, highlight:true, bias:GlyphBias.Top) }, // Aslan Tlaukhu Base
            { "*.V", new Glyph(Glyph.Circle, bias:GlyphBias.Bottom) }, // Exploration Base
            { "Zh.W", new Glyph(Glyph.Diamond, highlight:true)}, // Zhodani Relay Station
            { "*.W", new Glyph(Glyph.Triangle, highlight:true, bias:GlyphBias.Bottom) }, // Imperial Scout Waystation
            { "Zh.Z", Glyph.Diamond }, // Zhodani Base (Special case for "Zh.KM")
            { "*.*", Glyph.Circle }, // Independent Base
            // For TNE
            { "Sc.H", Glyph.HiverSupplyBase }, // Hiver Supply Base
            { "*.I", Glyph.Interface }, // Interface
            { "*.T", Glyph.Terminus }, // Terminus
        };

        public static Glyph FromBaseCode(string allegiance, char code) => s_baseGlyphTable.Match(allegiance + "." + code);
    }

    #region Stellar Rendering
    internal struct StarProps
    {
        public StarProps(Color color, Color border, float radius) { this.color = color; borderColor = border; this.radius = radius; }
        public Color color;
        public Color borderColor;
        public float radius;
    }

    internal static class StellarRendering
    {
        // Additions to radius based on luminosity.
        private static readonly IReadOnlyDictionary<string, float> LUM =
            new Dictionary<string, float> {
                { "Ia", 7 },
                { "Ib", 5 },
                { "II", 3 },
                { "III", 2 },
                { "IV", 1 },
                { "V", 0 },
                { "VI", -0.1f }
            };

        // Base radius for spectral class.
        private static readonly IReadOnlyDictionary<char, float> RAD =
            new Dictionary<char, float> { 
                { 'O', 4 }, { 'B', 3 }, { 'A', 2 }, { 'F', 1.5f }, { 'G', 1 }, { 'K', 0.7f }, { 'M', 0.5f }
            };

        // Maps spectral class to color.
        private static readonly IReadOnlyDictionary<char, Color> COLOR =
            new Dictionary<char, Color> {
                { 'O', Color.FromArgb(0x9d, 0xb4, 0xff) },
                { 'B', Color.FromArgb(0xbb, 0xcc, 0xff) },
                { 'A', Color.FromArgb(0xfb, 0xf8, 0xff) },
                { 'F', Color.FromArgb(0xff, 0xff, 0xed) },
                { 'G', Color.FromArgb(0xff, 0xff, 0x00) },
                { 'K', Color.FromArgb(0xff, 0x98, 0x33) },
                { 'M', Color.FromArgb(0xff, 0x00, 0x00) },
            };

        public static StarProps StarToProps(StellarData.Star star)
        {
            if (star.classification == "D")
                return new StarProps(Color.White, Color.Black, 0.3f);

            // TODO: Distinct rendering for black holes, neutron stars, pulsars
            if (star.classification == "NS" || star.classification == "PSR" || star.classification == "BH")
                return new StarProps(Color.Black, Color.White, 0.8f);

            if (star.classification == "BD")
                return new StarProps(Color.Brown, Color.Black, 0.3f);

            return new StarProps(COLOR[star.type], Color.Black, RAD[star.type] + LUM[star.luminosity ?? "V"]);
        }

        private static float SinF(double r) => (float)Math.Sin(r);
        private static float CosF(double r) => (float)Math.Cos(r);

        private static float[] dx = new float[] {
                    0.0f,
                    CosF(Math.PI * 1 / 3), CosF(Math.PI * 2 / 3), CosF(Math.PI * 3 / 3),
                    CosF(Math.PI * 4 / 3), CosF(Math.PI * 5 / 3), CosF(Math.PI * 6 / 3)
        };
        private static float[] dy = new float[] {
                    0.0f,
                    SinF(Math.PI * 1 / 3), SinF(Math.PI * 2 / 3), SinF(Math.PI * 3 / 3),
                    SinF(Math.PI * 4 / 3), SinF(Math.PI * 5 / 3), SinF(Math.PI * 6 / 3)
        };
        public static PointF Offset(int index)
        {
            if (index >= dx.Length)
                index = (index % (dx.Length - 1)) + 1;
            return new PointF(dx[index], dy[index]);
        }
    }
    #endregion
}
