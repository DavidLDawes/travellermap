#nullable enable
using SkiaSharp;
using System;

namespace Maps.Graphics
{
    /// <summary>
    /// Horizontal advances and vertical metrics read directly from a TrueType/OpenType font's
    /// tables (head, hhea, hmtx, OS/2), in ems. Unlike the platform's glyph scaler (DirectWrite on
    /// Windows, FreeType on Linux), these are the same everywhere, so text layout is too.
    /// Vertical metrics follow GDI+, which the label layout was designed around:
    /// ascent = usWinAscent, line spacing = usWinAscent + usWinDescent + hhea lineGap.
    /// </summary>
    internal sealed class FontTables
    {
        private readonly ushort[] advances; // font units, per glyph (the last repeats, per hmtx)
        private readonly float unitsPerEm;

        public FontTables(SKTypeface typeface)
        {
            byte[] head = Table(typeface, "head");
            byte[] hhea = Table(typeface, "hhea");
            byte[] hmtx = Table(typeface, "hmtx");
            byte[] os2 = Table(typeface, "OS/2");

            unitsPerEm = UInt16(head, 18);
            short lineGap = Int16(hhea, 8);
            int numberOfHMetrics = UInt16(hhea, 34);
            ushort winAscent = UInt16(os2, 74);
            ushort winDescent = UInt16(os2, 76);

            advances = new ushort[numberOfHMetrics];
            for (int i = 0; i < numberOfHMetrics; ++i)
                advances[i] = UInt16(hmtx, i * 4);

            Ascent = winAscent / unitsPerEm;
            LineSpacing = (winAscent + winDescent + lineGap) / unitsPerEm;
        }

        /// <summary>Ascent, in ems.</summary>
        public float Ascent { get; }
        /// <summary>Line spacing, in ems.</summary>
        public float LineSpacing { get; }

        /// <summary>A glyph's advance width, in ems.</summary>
        public float AdvanceWidth(ushort glyph) =>
            advances.Length == 0 ? 0 : advances[Math.Min(glyph, advances.Length - 1)] / unitsPerEm;

        private static byte[] Table(SKTypeface typeface, string tag)
        {
            uint t = (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);
            return typeface.GetTableData(t) ?? throw new InvalidOperationException($"Font {typeface.FamilyName} has no {tag} table");
        }

        private static ushort UInt16(byte[] data, int offset) => (ushort)(data[offset] << 8 | data[offset + 1]);
        private static short Int16(byte[] data, int offset) => (short)UInt16(data, offset);
    }
}
