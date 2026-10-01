using Maps.Graphics;
using Maps.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Drawing;
using SkiaSharp;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using FontStyle = Maps.Graphics.FontStyle;
using StringAlignment = Maps.Graphics.StringAlignment;

namespace UnitTests
{
    [TestClass]
    public class RenderingTest
    {
        // AbstractMatrix is a port of PDFsharp's XMatrix (including its shortcuts for identity,
        // translation, and scaling matrices). Rendered output depends on matching it exactly.
        [TestMethod]
        public void AbstractMatrixMatchesXMatrix()
        {
            var random = new Random(1234);
            for (int trial = 0; trial < 500; ++trial)
            {
                var m = AbstractMatrix.Identity;
                var x = XMatrix.Identity;
                int steps = random.Next(1, 8);
                for (int step = 0; step < steps; ++step)
                {
                    // Small integers make identity/translation/scaling shortcuts likely.
                    float a = random.Next(-3, 4), b = random.Next(-3, 4);
                    if (random.Next(3) == 0) { a += (float)random.NextDouble(); b += (float)random.NextDouble(); }
                    switch (random.Next(5))
                    {
                        case 0: m.TranslatePrepend(a, b); x.TranslatePrepend(a, b); break;
                        case 1:
                            if (a == 0 || b == 0) continue;
                            m.ScalePrepend(a, b); x.ScalePrepend(a, b); break;
                        case 2: m.RotatePrepend(a * 30); x.RotatePrepend(a * 30); break;
                        case 3:
                            var other = new AbstractMatrix(a, 0, 0, b, b, a);
                            m.Prepend(other); x.Prepend(new XMatrix(a, 0, 0, b, b, a)); break;
                        case 4:
                            if (Math.Abs(x.Determinant) < 1e-6) continue;
                            m.Invert(); x.Invert(); break;
                    }
                    Assert.AreEqual(x.M11, m.M11d, $"trial {trial} step {step}");
                    Assert.AreEqual(x.M12, m.M12d, $"trial {trial} step {step}");
                    Assert.AreEqual(x.M21, m.M21d, $"trial {trial} step {step}");
                    Assert.AreEqual(x.M22, m.M22d, $"trial {trial} step {step}");
                    Assert.AreEqual(x.OffsetX, m.OffsetXd, $"trial {trial} step {step}");
                    Assert.AreEqual(x.OffsetY, m.OffsetYd, $"trial {trial} step {step}");
                }
            }
        }

        // Every glyph and overlay symbol the renderer draws must exist in the bundled fonts,
        // or the Skia renderer would draw blank boxes.
        [TestMethod]
        public void BundledFontsCoverRenderedSymbols()
        {
            var glyphs = typeof(Glyph).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(Glyph))
                .Select(f => ((Glyph)f.GetValue(null)!).Characters);
            string[] overlays = { "★☆", "✻", "☀", "⌖", "*" };

            var font = new AbstractFont("Arial Unicode MS,Segoe UI Symbol,Arial", 1, FontStyle.Bold);
            using var fonts = SkiaFonts.Instance.CreateFonts(font);
            var missing = glyphs.Concat(overlays).SelectMany(s => s)
                .Where(c => !fonts.Primary.ContainsGlyph(c) && !fonts.Fallbacks.Any(f => f.ContainsGlyph(c)))
                .Select(c => $"U+{(int)c:X4}");
            Assert.AreEqual("", string.Join(" ", missing), "Characters missing from the bundled fonts");
        }

        [TestMethod]
        public void FontFamiliesResolveToBundledFonts()
        {
            string Family(string families, FontStyle style = FontStyle.Regular) =>
                SkiaFonts.Instance.Resolve(new AbstractFont(families, 1, style)).Typeface.FamilyName;

            Assert.AreEqual("Liberation Sans", Family("Arial"));
            Assert.AreEqual("Liberation Sans", Family("Arial", FontStyle.Bold | FontStyle.Italic));
            Assert.AreEqual("Carlito", Family("Calibri,Arial"));
            Assert.AreEqual("Liberation Mono", Family("Courier New"));
            Assert.AreEqual("Gelasio", Family("Georgia"));
            Assert.AreEqual("Comic Neue", Family("Comic Sans MS"));
            Assert.AreEqual("DejaVu Sans", Family("Arial Unicode MS,Segoe UI Symbol"));
            // Unknown families fall through to the next in the list, then to the default.
            Assert.AreEqual("Carlito", Family("No Such Font,Calibri"));
            Assert.AreEqual("Liberation Sans", Family("No Such Font"));

            var bold = SkiaFonts.Instance.Resolve(new AbstractFont("Arial", 1, FontStyle.Bold));
            Assert.IsTrue(bold.Typeface.IsBold);
            Assert.IsFalse(bold.FakeBold);
        }

        [TestMethod]
        public void SkiaRendersAndEncodes()
        {
            byte[] png = SkiaGraphics.RenderBitmap(40, 20, Maps.Utilities.ContentTypes.Image.Png, g =>
            {
                g.DrawRectangle(new AbstractBrush(Color.Red), 0, 0, 20, 20);
                g.ScaleTransform(2);
                g.DrawRectangle(new AbstractBrush(Color.Blue), 10, 0, 10, 10);
                g.DrawString("Hi", new AbstractFont("Arial", 6, FontStyle.Bold), new AbstractBrush(Color.White), 15, 5, StringAlignment.Centered);
            });
            Assert.IsNotNull(png);
            using var bitmap = SKBitmap.Decode(png);
            Assert.AreEqual(40, bitmap.Width);
            Assert.AreEqual(20, bitmap.Height);
            Assert.AreEqual(new SKColor(255, 0, 0), bitmap.GetPixel(5, 10));
            Assert.AreEqual(new SKColor(0, 0, 255), bitmap.GetPixel(21, 1));
            // Text was drawn in the blue square.
            bool white = Enumerable.Range(20, 20).Any(x => Enumerable.Range(0, 20).Any(y => bitmap.GetPixel(x, y).Red > 200 && bitmap.GetPixel(x, y).Blue > 200));
            Assert.IsTrue(white, "Expected white text pixels");
        }

        // Text measurement emulates GDI+ MeasureString (label layout was designed around it):
        // the advance width + 3% + 1/3 em, and the line spacing + 1/8 em.
        [TestMethod]
        public void SkiaMeasureStringIsGdiCompatible()
        {
            var font = new AbstractFont("Arial", 100, FontStyle.Regular);
            var size = SkiaFonts.TextMeasurer.MeasureString("Hello", font);
            // GDI+ with Arial measures 268.05 x 124.22; Liberation Sans is metrically compatible.
            Assert.AreEqual(268.05, size.Width, 1.0);
            Assert.AreEqual(124.22, size.Height, 4.0);
            var metrics = SkiaFonts.TextMeasurer.GetFontMetrics(font);
            Assert.AreEqual(90.5, metrics.Ascent, 0.5);      // 1854/2048 em
            Assert.AreEqual(115.0, metrics.LineSpacing, 0.5); // 2355/2048 em
        }
    }
}
