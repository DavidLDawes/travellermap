#nullable enable
using Maps.Utilities;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace Maps.Graphics
{
    /// <summary>
    /// The fonts the SkiaSharp and PDF renderers draw with: only the files bundled in res/fonts (see its
    /// README), never installed fonts, so output is the same on every machine. Requested family
    /// names (e.g. "Arial", "Calibri,Arial") map to metrically compatible open fonts.
    /// </summary>
    internal sealed class SkiaFonts
    {
        // For characters the requested font lacks, in order.
        private static readonly string[] s_fallbackFamilies = { "DejaVu Sans", "Noto Sans Symbols 2" };
        private const string DefaultFamily = "Liberation Sans";

        // Bundled family -> file name prefix and the styles available as files. Missing styles
        // are synthesized (embolden / skew).
        private static readonly (string family, string prefix, string[] styles)[] s_files =
        {
            ("Liberation Sans", "LiberationSans", new[] { "Regular", "Bold", "Italic", "BoldItalic" }),
            ("Liberation Mono", "LiberationMono", new[] { "Regular", "Bold", "Italic", "BoldItalic" }),
            ("Carlito", "Carlito", new[] { "Regular", "Bold", "Italic", "BoldItalic" }),
            ("Gelasio", "Gelasio", new[] { "Variable" }),
            ("Comic Neue", "ComicNeue", new[] { "Regular", "Bold", "Italic", "BoldItalic" }),
            ("DejaVu Sans", "DejaVuSans", new[] { "", "Bold" }),
            ("Noto Sans Symbols 2", "NotoSansSymbols2", new[] { "Regular" }),
        };

        // Family names used by stylesheets -> bundled family.
        private static readonly IReadOnlyDictionary<string, string> s_aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Arial", "Liberation Sans" },
                { "Helvetica", "Liberation Sans" },
                { "Calibri", "Carlito" },
                { "Courier New", "Liberation Mono" },
                { "Courier", "Liberation Mono" },
                { "Georgia", "Gelasio" },
                { "Comic Sans MS", "Comic Neue" },
                { "Arial Unicode MS", "DejaVu Sans" },
                { "Segoe UI Symbol", "DejaVu Sans" },
                { "Wingdings", "DejaVu Sans" },
            };

        private static readonly Lazy<SkiaFonts> s_instance = new Lazy<SkiaFonts>(
            () => new SkiaFonts(Util.MapPath("~/res/fonts")));
        public static SkiaFonts Instance => s_instance.Value;

        // family -> (bold, italic) -> typeface and its file
        private readonly Dictionary<string, Dictionary<(bool bold, bool italic), (SKTypeface typeface, string path)>> typefaces =
            new Dictionary<string, Dictionary<(bool, bool), (SKTypeface, string)>>(StringComparer.OrdinalIgnoreCase);

        private SkiaFonts(string directory)
        {
            foreach (var (family, prefix, styles) in s_files)
            {
                var byStyle = new Dictionary<(bool, bool), (SKTypeface, string)>();
                foreach (var style in styles)
                {
                    string path = Path.Combine(directory, style.Length == 0 ? prefix + ".ttf" : $"{prefix}-{style}.ttf");
                    var typeface = SKTypeface.FromFile(path)
                        ?? throw new FileNotFoundException("Bundled font missing or unreadable", path);
                    byStyle[(style.Contains("Bold"), style.Contains("Italic"))] = (typeface, path);
                }
                typefaces[family] = byStyle;
            }
        }

        /// <summary>A resolved font: the bundled family, typeface and file, plus any synthesized styling.</summary>
        internal sealed class Face
        {
            public Face(string family, SKTypeface typeface, string path, bool fakeBold, bool fakeItalic)
            {
                Family = family;
                Typeface = typeface;
                Path = path;
                FakeBold = fakeBold;
                FakeItalic = fakeItalic;
            }
            public string Family { get; }
            public SKTypeface Typeface { get; }
            public string Path { get; }
            public bool FakeBold { get; }
            public bool FakeItalic { get; }

            private FontTables? tables;
            /// <summary>Metrics read from the font file (the same on every platform).</summary>
            public FontTables Tables => tables ??= new FontTables(Typeface);

            public SKFont CreateFont(float size)
            {
                var font = new SKFont(Typeface, size)
                {
                    Embolden = FakeBold,
                    SkewX = FakeItalic ? -0.2f : 0,
                    Edging = SKFontEdging.Antialias,
                    Subpixel = true,
                    // Text is drawn under arbitrary transforms at tiny world sizes (e.g. 0.1 units
                    // scaled up 64x), so metrics must not be rounded at the nominal size.
                    LinearMetrics = true,
                };
                return font;
            }
        }

        /// <summary>The primary face for a font: the first requested family that maps to a bundled one.</summary>
        public Face Resolve(AbstractFont font) => font.Natives.Get(() =>
        {
            string family = font.Families.Split(',')
                .Select(f => f.Trim())
                .Select(f => typefaces.ContainsKey(f) ? f : s_aliases.TryGetValue(f, out string? alias) ? alias : null)
                .FirstOrDefault(f => f != null) ?? DefaultFamily;
            return GetFace(family, font.Bold, font.Italic);
        });

        /// <summary>Faces for characters the primary face lacks (symbols, other scripts), in order.</summary>
        public IReadOnlyList<Face> Fallbacks(AbstractFont font) =>
            s_fallbackFamilies.Select(family => GetFace(family, font.Bold, font.Italic)).ToList();

        /// <summary>The primary font and its fallbacks at the font's size.</summary>
        public FontSet CreateFonts(AbstractFont font) =>
            new FontSet(new[] { Resolve(font) }.Concat(Fallbacks(font)).ToArray(), font.Size);

        private readonly Dictionary<(string, bool, bool), Face> faces = new Dictionary<(string, bool, bool), Face>();

        /// <summary>
        /// The face for a bundled family and style. Styles without their own file are
        /// synthesized from the closest one (FakeBold/FakeItalic).
        /// </summary>
        public Face GetFace(string family, bool bold, bool italic)
        {
            lock (faces)
            {
                if (faces.TryGetValue((family, bold, italic), out Face? face))
                    return face;
                var byStyle = typefaces[family];
                if (byStyle.TryGetValue((bold, italic), out var exact))
                    face = new Face(family, exact.typeface, exact.path, false, false);
                else if (bold && byStyle.TryGetValue((true, false), out var boldFace))
                    face = new Face(family, boldFace.typeface, boldFace.path, false, italic);
                else if (italic && byStyle.TryGetValue((false, true), out var italicFace))
                    face = new Face(family, italicFace.typeface, italicFace.path, bold, false);
                else
                {
                    var any = byStyle.Values.First();
                    face = new Face(family, any.typeface, any.path, bold, italic);
                }
                faces[(family, bold, italic)] = face;
                return face;
            }
        }

        /// <summary>Whether the family is one of the bundled ones.</summary>
        public bool IsBundledFamily(string family) => typefaces.ContainsKey(family);

        /// <summary>A primary font plus fallbacks for characters it lacks.</summary>
        internal sealed class FontSet : IDisposable
        {
            private readonly Dictionary<SKFont, Face> faceOf = new Dictionary<SKFont, Face>();
            private readonly Dictionary<(SKFont, ushort), SKPath> glyphPaths = new Dictionary<(SKFont, ushort), SKPath>();

            /// <param name="faces">The primary face, then the fallbacks.</param>
            public FontSet(Face[] faces, float size)
            {
                var fonts = faces.Select(face => face.CreateFont(size)).ToArray();
                for (int i = 0; i < faces.Length; ++i)
                    faceOf[fonts[i]] = faces[i];
                Primary = fonts[0];
                Fallbacks = fonts.Skip(1).ToArray();
            }
            public SKFont Primary { get; }
            public SKFont[] Fallbacks { get; }

            /// <summary>The face a font in this set was created from.</summary>
            public Face FaceOf(SKFont font) => faceOf[font];

            // Vertical metrics of the primary font, in world units, as GDI+ defined them (from
            // the font tables, so the same on every platform).
            public float Ascent => FaceOf(Primary).Tables.Ascent * Primary.Size;
            public float LineSpacing => FaceOf(Primary).Tables.LineSpacing * Primary.Size;

            /// <summary>
            /// Advance width of text in one font, from the font's hmtx table. (SKFont.MeasureText
            /// uses the platform's glyph scaler, DirectWrite or FreeType, whose widths differ.)
            /// </summary>
            public float Advance(SKFont font, string text)
            {
                var tables = FaceOf(font).Tables;
                float units = 0;
                foreach (ushort glyph in font.GetGlyphs(text))
                    units += tables.AdvanceWidth(glyph);
                return units * font.Size;
            }

            /// <summary>
            /// Text in one font as glyph outlines, starting at (x, baseline), placed by hmtx
            /// advances. Filled by Skia's own rasterizer, this draws the same pixels on every OS
            /// (drawing text directly uses the platform's rasterizer, which hints and places
            /// glyphs differently).
            /// </summary>
            public SKPath TextPath(SKFont font, string text, float x, float baseline)
            {
                var tables = FaceOf(font).Tables;
                using var builder = new SKPathBuilder();
                foreach (ushort glyph in font.GetGlyphs(text))
                {
                    if (!glyphPaths.TryGetValue((font, glyph), out SKPath? outline))
                    {
                        outline = font.GetGlyphPath(glyph) ?? new SKPath();
                        glyphPaths[(font, glyph)] = outline;
                    }
                    builder.AddPath(outline, x, baseline);
                    x += tables.AdvanceWidth(glyph) * font.Size;
                }
                return builder.Detach();
            }

            public SKFontHinting Hinting
            {
                set
                {
                    Primary.Hinting = value;
                    foreach (var f in Fallbacks)
                        f.Hinting = value;
                }
            }

            /// <summary>
            /// Splits text into runs drawable with one font each: the primary font, or the first
            /// fallback with glyphs the primary font lacks.
            /// </summary>
            public IEnumerable<(string text, SKFont font)> Runs(string text)
            {
                if (Primary.ContainsGlyphs(text))
                {
                    yield return (text, Primary);
                    yield break;
                }
                int start = 0;
                SKFont? current = null;
                for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
                {
                    int cp = char.ConvertToUtf32(text, i);
                    SKFont font = Primary.ContainsGlyph(cp) ? Primary
                        : Fallbacks.FirstOrDefault(f => f.ContainsGlyph(cp)) ?? Primary;
                    if (current != null && current != font)
                    {
                        yield return (text.Substring(start, i - start), current);
                        start = i;
                    }
                    current = font;
                }
                if (start < text.Length)
                    yield return (text.Substring(start), current ?? Primary);
            }

            /// <summary>Advance width of text, using fallbacks where needed.</summary>
            public float MeasureAdvance(string text) => Runs(text).Sum(run => Advance(run.font, run.text));

            /// <summary>
            /// Where to start drawing text (left edge and baseline) for an AbstractGraphics
            /// DrawString alignment, matching GDI+: near- and far-aligned text is inset by 1/6 em
            /// (its default StringFormat padding), and centered lines center the line box on y.
            /// </summary>
            public (float left, float baseline) Layout(string text, float emSize, float x, float y, StringAlignment format)
            {
                float width = MeasureAdvance(text);
                float ascent = Ascent;
                float lineSpacing = LineSpacing;

                float pad = emSize / 6;
                float left = format switch
                {
                    StringAlignment.Centered or StringAlignment.TopCenter => x - width / 2,
                    StringAlignment.TopRight => x - width - pad,
                    _ => x + pad,
                };
                float baseline = format switch
                {
                    StringAlignment.Baseline => y,
                    StringAlignment.TopLeft or StringAlignment.TopCenter or StringAlignment.TopRight => y + ascent,
                    _ => y - lineSpacing / 2 + ascent, // Centered, CenterLeft: center the line box on y
                };
                return (left, baseline);
            }

            /// <summary>The underline or strikeout bar for a run of text, as a rectangle.</summary>
            public RectangleF Decoration(AbstractFont font, float left, float baseline, float advance)
            {
                float ascent = Ascent;
                float thickness = Math.Max(font.Size / 14, 0);
                float offset = font.Underline ? font.Size / 9 : -ascent * 0.3f;
                return new RectangleF(left, baseline + offset, advance, thickness);
            }

            public void Dispose()
            {
                Primary.Dispose();
                foreach (var f in Fallbacks)
                    f.Dispose();
                foreach (var path in glyphPaths.Values)
                    path.Dispose();
                glyphPaths.Clear();
            }
        }

        /// <summary>
        /// Emulates GDI+ Graphics.MeasureString (default StringFormat), which label layout was
        /// designed around: it pads the advance width by 3% plus 1/6 em on each side, and the
        /// height is the line spacing plus 1/8 em.
        /// </summary>
        public static SizeF GdiCompatibleSize(float advance, float lineSpacing, float emSize) =>
            new SizeF(advance * 1.03f + emSize / 3, lineSpacing + emSize / 8);

        /// <summary>Text measurement with the bundled fonts, for backends without their own (SVG).</summary>
        public static readonly ITextMeasurer TextMeasurer = new Measurer();

        private sealed class Measurer : ITextMeasurer
        {
            public SizeF MeasureString(string text, AbstractFont font)
            {
                using var fonts = Instance.CreateFonts(font);
                return GdiCompatibleSize(fonts.MeasureAdvance(text), fonts.LineSpacing, font.Size);
            }

            public FontMetrics GetFontMetrics(AbstractFont font)
            {
                var tables = Instance.Resolve(font).Tables;
                return new FontMetrics(tables.Ascent * font.Size, tables.LineSpacing * font.Size);
            }
        }
    }
}
