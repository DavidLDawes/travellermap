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
    /// The fonts the SkiaSharp renderer draws with: only the files bundled in res/fonts (see its
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

        // family -> (bold, italic) -> typeface
        private readonly Dictionary<string, Dictionary<(bool bold, bool italic), SKTypeface>> typefaces =
            new Dictionary<string, Dictionary<(bool, bool), SKTypeface>>(StringComparer.OrdinalIgnoreCase);

        private SkiaFonts(string directory)
        {
            foreach (var (family, prefix, styles) in s_files)
            {
                var byStyle = new Dictionary<(bool, bool), SKTypeface>();
                foreach (var style in styles)
                {
                    string path = Path.Combine(directory, style.Length == 0 ? prefix + ".ttf" : $"{prefix}-{style}.ttf");
                    var typeface = SKTypeface.FromFile(path)
                        ?? throw new FileNotFoundException("Bundled font missing or unreadable", path);
                    byStyle[(style.Contains("Bold"), style.Contains("Italic"))] = typeface;
                }
                typefaces[family] = byStyle;
            }
        }

        /// <summary>A resolved font: the typeface plus any synthesized styling.</summary>
        internal sealed class Face
        {
            public Face(SKTypeface typeface, bool fakeBold, bool fakeItalic)
            {
                Typeface = typeface;
                FakeBold = fakeBold;
                FakeItalic = fakeItalic;
            }
            public SKTypeface Typeface { get; }
            public bool FakeBold { get; }
            public bool FakeItalic { get; }

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
            new FontSet(Resolve(font).CreateFont(font.Size), Fallbacks(font).Select(f => f.CreateFont(font.Size)).ToArray());

        private readonly Dictionary<(string, bool, bool), Face> faces = new Dictionary<(string, bool, bool), Face>();
        private Face GetFace(string family, bool bold, bool italic)
        {
            lock (faces)
            {
                if (faces.TryGetValue((family, bold, italic), out Face? face))
                    return face;
                var byStyle = typefaces[family];
                if (byStyle.TryGetValue((bold, italic), out SKTypeface? exact))
                    face = new Face(exact, false, false);
                else if (bold && byStyle.TryGetValue((true, false), out SKTypeface? boldFace))
                    face = new Face(boldFace, false, italic);
                else if (italic && byStyle.TryGetValue((false, true), out SKTypeface? italicFace))
                    face = new Face(italicFace, bold, false);
                else
                    face = new Face(byStyle.Values.First(), bold, italic);
                faces[(family, bold, italic)] = face;
                return face;
            }
        }

        /// <summary>A primary font plus fallbacks for characters it lacks.</summary>
        internal sealed class FontSet : IDisposable
        {
            public FontSet(SKFont primary, SKFont[] fallbacks)
            {
                Primary = primary;
                Fallbacks = fallbacks;
            }
            public SKFont Primary { get; }
            public SKFont[] Fallbacks { get; }

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
            public float MeasureAdvance(string text) => Runs(text).Sum(run => run.font.MeasureText(run.text));

            public void Dispose()
            {
                Primary.Dispose();
                foreach (var f in Fallbacks)
                    f.Dispose();
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
                return GdiCompatibleSize(fonts.MeasureAdvance(text), fonts.Primary.Spacing, font.Size);
            }

            public FontMetrics GetFontMetrics(AbstractFont font)
            {
                using var primary = Instance.Resolve(font).CreateFont(font.Size);
                return new FontMetrics(-primary.Metrics.Ascent, primary.Spacing);
            }
        }
    }
}
