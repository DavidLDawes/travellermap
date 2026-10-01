#nullable enable
using Maps.Graphics;
using Maps.Rendering;
using Maps.Serialization;
using Maps.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using Maps.Web;

namespace Maps.API
{
    internal abstract class ImageHandlerBase : DataHandlerBase
    {
        public const double MinScale = 0.0078125; // Math.Pow(2, -7);
        public const double MaxScale = 512; // Math.Pow(2, 9);

        // The largest legitimate bitmap is roughly a full sector at 128 px/parsec with dpr=2
        // (~7200x10300, ~74M pixels). Allow headroom, but refuse requests that would
        // allocate many gigabytes or render for minutes.
        public const long MaxBitmapPixels = 1L << 27; // ~134M pixels, 512MB at 32bpp

        // Vector output (SVG/PDF) is cheap per unit of size, but dimensions must still fit in
        // an int; this also rejects nonsensical requests.
        public const int MaxVectorDimension = 1 << 20;

        /// <summary>
        /// Checks requested output dimensions (in output pixels/points). Uses doubles so
        /// callers can check sizes before converting to int, avoiding overflow.
        /// </summary>
        internal static bool IsImageSizeAllowed(double width, double height, bool bitmap)
        {
            if (double.IsNaN(width) || double.IsNaN(height) || width < 1 || height < 1)
                return false;
            if (width > MaxVectorDimension || height > MaxVectorDimension)
                return false;
            return !bitmap || width * height <= MaxBitmapPixels;
        }

        /// <summary>
        /// Bitmap renderer: "skia" (SkiaSharp with the fonts bundled in res/fonts; same output on
        /// every OS) or "gdi" (System.Drawing with installed fonts; Windows only). Set by the
        /// "Renderer" app setting; the "renderer" query option overrides it per request, for
        /// comparing the two. SVG text measurement follows the same choice.
        /// </summary>
        private static string DefaultRenderer => AppSettings.Get("Renderer") ?? "skia";

        /// <summary>The GDI+ renderer for renderer=gdi; registered by hosts that have it (IIS).</summary>
        public static ILegacyBitmapRenderer? GdiRenderer { get; set; }

        protected abstract class ImageResponder : DataResponder
        {
            protected ImageResponder(HttpContext context) : base(context) { }
            public override string DefaultContentType => ContentTypes.Image.Png;
            protected void ProduceResponse(HttpContext context, string title, RenderContext ctx, Size tileSize,
                AbstractMatrix transform,
                bool transparent = false)
            {
                ProduceResponse(context, this, title, ctx, tileSize, transform, transparent,
                    context.RouteValues);
            }

            protected void ProduceResponse(HttpContext context, ITypeAccepter accepter, string title, RenderContext ctx, Size tileSize,
                AbstractMatrix transform,
                bool transparent, IDictionary<string, object> queryDefaults)
            {
                ApplyStyleOptions(ctx.Styles, queryDefaults);
                double devicePixelRatio = GetDevicePixelRatio(queryDefaults);
                bool dataURI = GetBoolOption("datauri", queryDefaults: queryDefaults, defaultValue: false);
                ILegacyBitmapRenderer? gdi = null;
                if (string.Equals(GetStringOption("renderer", queryDefaults: queryDefaults, defaultValue: DefaultRenderer), "gdi", StringComparison.OrdinalIgnoreCase))
                {
                    gdi = GdiRenderer ?? throw new HttpError(400, "Bad Request", "renderer=gdi is not available on this server.");
                }

                // "content-disposition: inline" is not used as Chrome opens that in a tab, then
                // (sometimes?) fails to allow it to be saved due to being served via POST.
                string disposition = context.Request.HttpMethod == "POST"
                    && context.Request.UserAgent?.Contains("Chrome") == true
                    ? "attachment" : "inline";

                // A data: URI is built from the rendered bytes, so render into memory first.
                using MemoryStream? dataUriBuffer = dataURI ? new MemoryStream() : null;
                Stream outputStream = dataUriBuffer ?? Context.Response.OutputStream;
                // Download headers don't apply to data: URIs.
                string? downloadDisposition = dataURI ? null : disposition;

                if (accepter.Accepts(context, ContentTypes.Image.Svg, ignoreHeaderFallbacks: true))
                    WriteSvg(context.Response, outputStream, downloadDisposition, title, ctx, tileSize, transform,
                        gdi?.TextMeasurer ?? SkiaFonts.TextMeasurer);
                else if (accepter.Accepts(context, ContentTypes.Application.Pdf, ignoreHeaderFallbacks: true))
                    WritePdf(context.Response, outputStream, downloadDisposition, title, ctx, tileSize, transform);
                else
                    WriteBitmap(context.Response, outputStream, disposition, title, ctx, tileSize, transform, devicePixelRatio, transparent, gdi);

                if (dataUriBuffer != null)
                    WriteDataUri(context.Response, dataUriBuffer);

                context.Response.Flush();
                context.Response.Close();
            }

            /// <summary>URL parameters that adjust the stylesheet (overlays, grids, labels).</summary>
            private void ApplyStyleOptions(Stylesheet styles, IDictionary<string, object> queryDefaults)
            {
                bool Option(string name, bool defaultValue) => GetBoolOption(name, queryDefaults, defaultValue);

                // TODO: move to ParseOptions (maybe - requires options to be parsed after stylesheet creation?)
                if (Option("sscoords", false))
                    styles.hexCoordinateStyle = HexCoordinateStyle.Subsector;
                if (Option("allhexes", false))
                    styles.numberAllHexes = true;
                if (Option("nogrid", false))
                    styles.parsecGrid.visible = false;
                if (!Option("routes", true))
                {
                    styles.macroRoutes.visible = false;
                    styles.microRoutes.visible = false;
                }
                if (!Option("rifts", true))
                    styles.showRiftOverlay = false;
                if (Option("po", false))
                    styles.populationOverlay.visible = true;
                if (Option("im", false))
                    styles.importanceOverlay.visible = true;
                if (Option("cp", false))
                    styles.capitalOverlay.visible = true;
                if (Option("stellar", false))
                    styles.showStellarOverlay = true;

                styles.dimUnofficialSectors = Option("dimunofficial", false);
                styles.colorCodeSectorStatus = Option("review", false);
                styles.droyneWorlds.visible = Option("dw", false);
                styles.minorHomeWorlds.visible = Option("mh", false);
                styles.ancientsWorlds.visible = Option("an", false);

                // TODO: Return an error if pattern is invalid?
                styles.highlightWorldsPattern = HighlightWorldPattern.Parse(
                    GetStringOption("hw", queryDefaults: queryDefaults, defaultValue: String.Empty)!.Replace(' ', '+'));
                styles.highlightWorlds.visible = styles.highlightWorldsPattern != null;

                styles.routeEndAdjust = (float)GetDoubleOption("rea", defaultValue: 0.25, queryDefaults: queryDefaults);

                if (GetStringOption("milieu", SectorMap.DEFAULT_MILIEU) != SectorMap.DEFAULT_MILIEU)
                {
                    // TODO: Make this declarative in resource files.
                    if (styles.macroBorders.visible)
                    {
                        styles.macroBorders.visible = false;
                        styles.microBorders.visible = true;
                    }
                    styles.macroNames.visible = false;
                    styles.macroRoutes.visible = false;
                }
            }

            /// <summary>Device pixel ratio for bitmaps: rounded to 0.1, in (0, 2], default 1.</summary>
            private double GetDevicePixelRatio(IDictionary<string, object> queryDefaults)
            {
                double dpr = Math.Round(GetDoubleOption("dpr", defaultValue: 1, queryDefaults: queryDefaults), 1);
                if (dpr <= 0)
                    return 1;
                return Math.Min(dpr, 2);
            }

            /// <summary>Content-Length and Content-Disposition for a downloadable response.</summary>
            private static void AddDownloadHeaders(HttpResponse response, string? disposition, string title, string extension, long length)
            {
                if (disposition == null)
                    return;
                response.AddHeader("content-length", length.ToString());
                response.AddHeader("content-disposition", $"{disposition};filename=\"{Util.SanitizeFilename(title)}.{extension}\"");
            }

            private static void WriteSvg(HttpResponse response, Stream output, string? disposition, string title,
                RenderContext ctx, Size tileSize, AbstractMatrix transform, ITextMeasurer measurer)
            {
                using var svg = new SVGGraphics(tileSize.Width, tileSize.Height, measurer);
                RenderToGraphics(ctx, transform, svg);

                using var stream = new MemoryStream();
                svg.Serialize(new StreamWriter(stream));
                response.ContentType = ContentTypes.Image.Svg;
                AddDownloadHeaders(response, disposition, title, "svg", stream.Length);
                stream.WriteTo(output);
            }

            private static void WritePdf(HttpResponse response, Stream output, string? disposition, string title,
                RenderContext ctx, Size tileSize, AbstractMatrix transform)
            {
                using var stream = new MemoryStream();

                // TODO: Credits/Copyright metadata (needs an XMP /photoshop/Copyright entry).
                var info = new PdfSharpGraphics.PdfInfo
                {
                    Title = title,
                    Author = "Joshua Bell",
                    Creator = "TravellerMap.com",
                    Subject = DateTime.Now.ToString("F", CultureInfo.InvariantCulture),
                    Keywords = "The Traveller game in all forms is owned by Mongoose Publishing. Copyright 1977 - 2024 Mongoose Publishing.",
                };
                // Page size in points.
                PdfSharpGraphics.RenderPdf(stream, tileSize.Width, tileSize.Height, info,
                    graphics => RenderToGraphics(ctx, transform, graphics));
                response.ContentType = ContentTypes.Application.Pdf;
                AddDownloadHeaders(response, disposition, title, "pdf", stream.Length);
                stream.WriteTo(output);
            }

            private static void WriteBitmap(HttpResponse response, Stream output, string disposition, string title,
                RenderContext ctx, Size tileSize, AbstractMatrix transform, double devicePixelRatio, bool transparent, ILegacyBitmapRenderer? gdi)
            {
                double requestedWidth = Math.Floor(tileSize.Width * devicePixelRatio);
                double requestedHeight = Math.Floor(tileSize.Height * devicePixelRatio);
                if (!IsImageSizeAllowed(requestedWidth, requestedHeight, bitmap: true))
                {
                    throw new HttpError(400, "Bad Request",
                        $"Requested image size ({requestedWidth}x{requestedHeight}) is too large or invalid; reduce the area, scale, or dpr.");
                }
                int width = (int)requestedWidth;
                int height = (int)requestedHeight;

                // JPEG or PNG, based on style; transparency needs PNG.
                string mimeType = transparent || ctx.Styles.preferredMimeType != ContentTypes.Image.Jpeg
                    ? ContentTypes.Image.Png : ContentTypes.Image.Jpeg;
                void Render(AbstractGraphics graphics)
                {
                    graphics.ScaleTransform((float)devicePixelRatio);
                    RenderToGraphics(ctx, transform, graphics);
                }

                string? written;
                if (gdi == null)
                {
                    byte[]? encoded = SkiaGraphics.RenderBitmap(width, height, mimeType, Render);
                    if (encoded != null)
                        output.Write(encoded, 0, encoded.Length);
                    written = encoded == null ? null : mimeType;
                }
                else
                {
                    try
                    {
                        written = gdi.RenderBitmap(output, width, height, mimeType, transparent, Render);
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        // GDI+ throws "A generic error occurred in GDI+." when saving on low memory.
                        throw new HttpError(500, "Internal Server Error",
                            $"Unknown GDI error encoding bitmap ({width}x{height}). Insufficient memory?");
                    }
                }
                if (written == null)
                {
                    throw new HttpError(500, "Internal Server Error",
                        $"Failed to allocate bitmap ({width}x{height}). Insufficient memory?");
                }

                response.ContentType = written;
                string? extension = written switch
                {
                    ContentTypes.Image.Jpeg => "jpg",
                    ContentTypes.Image.Gif => "gif",
                    ContentTypes.Image.Png => "png",
                    _ => null
                };
                if (title != null && extension != null)
                    response.AddHeader("content-disposition", $"{disposition};filename=\"{Util.SanitizeFilename(title)}.{extension}\"");
            }

            /// <summary>Writes the buffered rendering as text: "data:{type};base64,...".</summary>
            private static void WriteDataUri(HttpResponse response, MemoryStream rendered)
            {
                string? contentType = response.ContentType;
                response.ContentType = ContentTypes.Text.Plain;
                rendered.Seek(0, SeekOrigin.Begin);

                response.Output.Write("data:");
                response.Output.Write(contentType);
                response.Output.Write(";base64,");
                response.Output.Flush();

                using var encoder = new System.Security.Cryptography.ToBase64Transform();
                using var cs = new System.Security.Cryptography.CryptoStream(response.OutputStream, encoder, System.Security.Cryptography.CryptoStreamMode.Write);
                rendered.WriteTo(cs);
                cs.FlushFinalBlock();
            }

            private static void RenderToGraphics(RenderContext ctx, AbstractMatrix transform, AbstractGraphics graphics)
            {
                graphics.MultiplyTransform(transform);

                if (ctx.DrawBorder && ctx.ClipPath != null)
                {
                    using (graphics.Save())
                    {
                        // Render border in world space
                        AbstractMatrix m = ctx.ImageSpaceToWorldSpace;
                        graphics.MultiplyTransform(m);
                        AbstractPen pen = new AbstractPen(ctx.Styles.imageBorderColor, ctx.Styles.imageBorderWidth);

                        // SVG/PdfSharp can't ExcludeClip so we take advantage of the fact that we know
                        // the path starts on the left edge and proceeds clockwise. We extend the
                        // path with a counterclockwise border around it, then use that to exclude
                        // the original path's region for rendering the border.
                        RectangleF bounds = PathUtil.Bounds(ctx.ClipPath);
                        bounds.Inflate(2 * (float)pen.Width, 2 * (float)pen.Width);
                        List<byte> types = new List<byte>(ctx.ClipPath.Types);
                        List<PointF> points = new List<PointF>(ctx.ClipPath.Points);

                        PointF key = points[0];
                        points.Add(new PointF(bounds.Left, key.Y)); types.Add(1);
                        points.Add(new PointF(bounds.Left, bounds.Bottom)); types.Add(1);
                        points.Add(new PointF(bounds.Right, bounds.Bottom)); types.Add(1);
                        points.Add(new PointF(bounds.Right, bounds.Top)); types.Add(1);
                        points.Add(new PointF(bounds.Left, bounds.Top)); types.Add(1);
                        points.Add(new PointF(bounds.Left, key.Y)); types.Add(1);
                        points.Add(new PointF(key.X, key.Y)); types.Add(1);

                        graphics.IntersectClip(new AbstractPath(points.ToArray(), types.ToArray()));
                        graphics.DrawPath(pen, ctx.ClipPath);
                    }
                }

                using (graphics.Save())
                {
                    ctx.Render(graphics);
                }
            }

            protected static Sector? GetPostedSector(HttpRequest request, ErrorLogger errors)
            {
                Sector? sector;

                if (request.Files["file"] is HttpPostedFile file && file.ContentLength > 0)
                {
                    sector = new Sector(file.InputStream, file.ContentType ?? ContentTypes.Text.Plain, errors);
                }
                else if (request.Form["data"] is string data && data.Length > 0)
                {
                    sector = new Sector(data.ToStream(), ContentTypes.Text.Plain, errors);
                }
                else if (new ContentType(request.ContentType ?? "").MediaType == ContentTypes.Text.Plain)
                {
                    sector = new Sector(request.InputStream, ContentTypes.Text.Plain, errors);
                }
                else
                {
                    return null;
                }

                if (request.Files["metadata"] is HttpPostedFile hpf && hpf.ContentLength > 0)
                {
                    string type = SectorMetadataFileParser.SniffType(hpf.InputStream);
                    Sector meta = SectorMetadataFileParser.ForType(type).Parse(hpf.InputStream);
                    sector.Merge(meta);
                }
                else if (request.Form["metadata"] is string metadata && metadata.Length > 0)
                {
                    string type = SectorMetadataFileParser.SniffType(metadata.ToStream());
                    var parser = SectorMetadataFileParser.ForType(type);
                    using var reader = new StringReader(metadata);
                    Sector meta = parser.Parse(reader);
                    sector.Merge(meta);
                }

                return sector;
            }
        }

        protected static void ApplyHexRotation(int hrot, Stylesheet stylesheet, ref Size bitmapSize, ref AbstractMatrix transform)
        {
            float degrees = -hrot;
            double radians = degrees * Math.PI / 180f;
            double newWidth = Math.Abs(Math.Sin(radians)) * bitmapSize.Height + Math.Abs(Math.Cos(radians)) * bitmapSize.Width;
            double newHeight = Math.Abs(Math.Sin(radians)) * bitmapSize.Width + Math.Abs(Math.Cos(radians)) * bitmapSize.Height;

            transform.TranslatePrepend((float)newWidth / 2, (float)newHeight / 2);
            transform.RotatePrepend(-degrees);
            transform.TranslatePrepend(-bitmapSize.Width / 2, -bitmapSize.Height / 2);
            bitmapSize.Width = (int)Math.Ceiling(newWidth);
            bitmapSize.Height = (int)Math.Ceiling(newHeight);

            stylesheet.hexRotation = (float)degrees;
            stylesheet.microBorders.textStyle.Rotation = degrees;
        }

    }
}
