#nullable enable 
using Maps.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Linq;

namespace Maps.Serialization
{
    internal abstract class SectorMetadataFileParser
    {
        public const int BUFFER_SIZE = 32768;

        public abstract Encoding Encoding { get; }

        public virtual Sector Parse(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: BUFFER_SIZE);
            return Parse(reader);
        }
        public abstract Sector Parse(TextReader reader);

        public static SectorMetadataFileParser ForType(string mediaType) =>
            mediaType switch
            {
                "MSEC" => new MSECParser(),
                "XML" => new XmlSectorMetadataParser(),
                _ => new XmlSectorMetadataParser(),
            };

        private static readonly Regex SNIFF_XML_REGEX = new Regex(@"<\?xml", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string SniffType(Stream stream)
        {
            long pos = stream.Position;
            try
            {
                using (var reader = new NoCloseStreamReader(stream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: true, bufferSize: BUFFER_SIZE))
                {
                    string? line = reader.ReadLine();
                    if (line != null && SNIFF_XML_REGEX.IsMatch(line))
                        return "XML";
                }
                return "MSEC";
            }
            finally
            {
                stream.Position = pos;
            }
        }
    }

    internal class XmlSectorMetadataParser : SectorMetadataFileParser
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Sector Parse(Stream stream)
        {
            try
            {
                XmlDocument xd = new XmlDocument();
                xd.Load(stream);
                return Parse(xd);
            }
            catch (System.InvalidOperationException ex) when (ex.InnerException is XmlException)
            {
                throw ex.InnerException;
            }
        }

        private static string? ParseString(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static bool? ParseBool(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
#pragma warning disable IDE0075 // Simplify conditional expression
            return s == "true" ? true : s == "false" ? false : throw new Exception($"'{s}' is not a valid boolean");
#pragma warning restore IDE0075 // Simplify conditional expression
        }
        private static int? ParseInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return int.Parse(s); }
            catch (Exception) { throw new Exception($"'{s}' is not a valid integer"); }
        }
        private static float? ParseFloat(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return float.Parse(s); }
            catch (Exception) { throw new Exception($"'{s}' is not a valid number"); }
        }
        private static TEnum? ParseEnum<TEnum>(string s) where TEnum : struct
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;
            try { return (TEnum)Enum.Parse(typeof(TEnum), s); }
            catch { throw new Exception($"'{s}' is not a valid {typeof(TEnum).Name}"); }
        }

        private static void ParseErrorAppender(XmlElement elem, Action<XmlElement> action)
        {
            try { action(elem); }
            catch (Exception ex) { throw new Exception($"{ex.Message}\n in {elem.OuterXml}"); }
        }

        // XmlNode.SelectNodes is annotated as possibly null; treat that as no matches.
        private static IEnumerable<XmlElement> Elements(XmlDocument xd, string xpath)
            => xd.SelectNodes(xpath)?.OfType<XmlElement>() ?? Enumerable.Empty<XmlElement>();

        private Sector Parse(XmlDocument xd)
        {
            Sector sector = new Sector();

            foreach (var e in Elements(xd, "/Sector/Name"))
            {
                ParseErrorAppender(e, name => sector.Names.Add(new Name()
                {
                    Lang = ParseString(name.GetAttribute("lang")),
                    Text = name.InnerText,
                }));
            }
            foreach (var e in Elements(xd, "/Sector/Subsectors/Subsector"))
            {
                ParseErrorAppender(e, subsector => sector.Subsectors.Add(new Subsector()
                {
                    Index = subsector.GetAttribute("Index"),
                    Name = subsector.InnerText,
                }));
            }
            foreach (var e in Elements(xd, "/Sector/Routes/Route"))
            {
                ParseErrorAppender(e, route =>
                {
                    var r = new Route()
                    {
                        Allegiance = ParseString(route.GetAttribute("Allegiance")),
                        ColorHtml = ParseString(route.GetAttribute("Color")),
                        Style = ParseEnum<LineStyle>(route.GetAttribute("Style")),
                        Type = ParseString(route.GetAttribute("Type")),
                        Width = ParseFloat(route.GetAttribute("Width")),

                        // These assignments must precede Start/EndHex as the latter may
                        // adjust Start/EndOffsetX/Y (e.g. if 0000/3341).
                        StartOffsetX = ParseInt(route.GetAttribute("StartOffsetX")) ?? 0,
                        StartOffsetY = ParseInt(route.GetAttribute("StartOffsetY")) ?? 0,
                        EndOffsetX = ParseInt(route.GetAttribute("EndOffsetX")) ?? 0,
                        EndOffsetY = ParseInt(route.GetAttribute("EndOffsetY")) ?? 0,

                        StartHex = ParseString(route.GetAttribute("Start")) ?? throw new ParseException("Route missing Start"),
                        EndHex = ParseString(route.GetAttribute("End")) ?? throw new ParseException("Route missing Start"),
                    };
                    sector.Routes.Add(r);
                });
            }
            foreach (var e in Elements(xd, "/Sector/Borders/Border"))
            {
                ParseErrorAppender(e, border => sector.Borders.Add(new Border()
                {
                    Allegiance = ParseString(border.GetAttribute("Allegiance")),
                    ColorHtml = ParseString(border.GetAttribute("Color")),
                    Label = ParseString(border.GetAttribute("Label")),
                    LabelPositionHex = ParseString(border.GetAttribute("LabelPosition")) ?? string.Empty,
                    LabelOffsetX = ParseFloat(border.GetAttribute("LabelOffsetX")) ?? 0,
                    LabelOffsetY = ParseFloat(border.GetAttribute("LabelOffsetY")) ?? 0,
                    PathString = border.InnerText,
                    ShowLabel = ParseBool(border.GetAttribute("ShowLabel")) ?? true,
                    Style = ParseEnum<LineStyle>(border.GetAttribute("Style")),
                    WrapLabel = ParseBool(border.GetAttribute("WrapLabel")) ?? false,
                }));
            }
            foreach (var e in Elements(xd, "/Sector/Regions/Region"))
            {
                ParseErrorAppender(e, region => sector.Regions.Add(new Region()
                {
                    Allegiance = ParseString(region.GetAttribute("Allegiance")),
                    ColorHtml = ParseString(region.GetAttribute("Color")),
                    Label = ParseString(region.GetAttribute("Label")),
                    LabelPositionHex = ParseString(region.GetAttribute("LabelPosition")) ?? string.Empty,
                    LabelOffsetX = ParseFloat(region.GetAttribute("LabelOffsetX")) ?? 0,
                    LabelOffsetY = ParseFloat(region.GetAttribute("LabelOffsetY")) ?? 0,
                    PathString = region.InnerText,
                    ShowLabel = ParseBool(region.GetAttribute("ShowLabel")) ?? true,
                    Style = ParseEnum<LineStyle>(region.GetAttribute("Style")),
                    WrapLabel = ParseBool(region.GetAttribute("WrapLabel")) ?? false,
                }));
            }
            foreach (var e in Elements(xd, "/Sector/Allegiances/Allegiance"))
            {
                ParseErrorAppender(e, alleg => sector.Allegiances.Add(new Allegiance()
                {
                    Base = ParseString(alleg.GetAttribute("Base")),
                    T5Code = ParseString(alleg.GetAttribute("Code")) ?? string.Empty,
                    Name = alleg.InnerText,
                }));
            }
            foreach (var e in Elements(xd, "/Sector/Labels/Label"))
            {
                ParseErrorAppender(e, label => sector.Labels.Add(new Label()
                {
                    Allegiance = ParseString(label.GetAttribute("Allegiance")),
                    ColorHtml = ParseString(label.GetAttribute("Color")),
                    Hex = new Hex(ParseString(label.GetAttribute("Hex")) ?? string.Empty),
                    OffsetX = ParseFloat(label.GetAttribute("OffsetX")) ?? 0,
                    OffsetY = ParseFloat(label.GetAttribute("OffsetY")) ?? 0,
                    Size = ParseString(label.GetAttribute("Size")),
                    Wrap = ParseBool(label.GetAttribute("Wrap")) ?? false,
                    Text = label.InnerText,
                }));
            }
            if (xd.SelectSingleNode("/Sector/Stylesheet") is XmlElement stylesheet)
                ParseErrorAppender(stylesheet, e => sector.StylesheetText = e.InnerText);

            if (xd.SelectSingleNode("/Sector/Credits") is XmlElement credits)
                ParseErrorAppender(credits, e => sector.Credits = e.InnerText);

            if (xd.SelectSingleNode("/Sector/DataFile") is XmlElement dataFile)
            {
                ParseErrorAppender(dataFile, elem =>
                {
                    sector.DataFile = new DataFile()
                    {
                        Milieu = ParseString(elem.GetAttribute("Milieu"))
                    };
                });
            }

            return sector;
        }

        public override Sector Parse(TextReader reader)
        {
            try
            {
                XmlDocument xd = new XmlDocument();
                xd.Load(reader);
                return Parse(xd);
            }
            catch (System.InvalidOperationException ex) when (ex.InnerException is System.Xml.XmlException)
            {
                throw ex.InnerException;
            }
        }
    }
}
