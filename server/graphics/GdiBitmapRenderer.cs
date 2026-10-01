#nullable enable
using Maps.Utilities;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Maps.Graphics
{
    /// <summary>
    /// The legacy GDI+ (System.Drawing) bitmap renderer, for renderer=gdi on the IIS host.
    /// Registered as ImageHandlerBase.GdiRenderer by Global.asax.
    /// </summary>
    internal sealed class GdiBitmapRenderer : ILegacyBitmapRenderer
    {
        public ITextMeasurer TextMeasurer => GdiSupport.TextMeasurer;

        public string? RenderBitmap(Stream output, int width, int height, string mimeType, bool transparent, Action<AbstractGraphics> render)
        {
            using var bitmap = TryConstructBitmap(width, height, PixelFormat.Format32bppArgb);
            if (bitmap == null)
                return null;

            if (transparent)
                bitmap.MakeTransparent();

            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                // Grayscale anti-aliasing. ClearType's sub-pixel color fringes depend on the
                // rendering machine's ClearType settings (so output varied between machines)
                // and assume an LCD's sub-pixel layout, which a saved/scaled image doesn't have.
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                using var graphics = new BitmapGraphics(g);
                render(graphics);
            }

            return Encode(bitmap, mimeType, output);
        }

        private static Bitmap? TryConstructBitmap(int width, int height, PixelFormat pixelFormat)
        {
            try
            {
                return new Bitmap(width, height, pixelFormat);
            }
            catch (ArgumentException)
            {
                // See http://stackoverflow.com/questions/1949045/net-bitmap-class-constructor-int-int-and-int-int-pixelformat-throws-argu
                return null;
            }
        }

        /// <summary>Encodes as the requested type if possible, else GIF; returns the type written.</summary>
        private static string Encode(Bitmap bitmap, string mimeType, Stream outputStream)
        {
            ImageCodecInfo encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.MimeType == mimeType);
            if (encoder == null)
            {
                // Default to GIF if we can't find anything
                bitmap.Save(outputStream, ImageFormat.Gif);
                return ContentTypes.Image.Gif;
            }

            using var encoderParams = new EncoderParameters(mimeType == ContentTypes.Image.Jpeg || mimeType == ContentTypes.Image.Png ? 1 : 0);
            if (mimeType == ContentTypes.Image.Jpeg)
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)95);
            else if (mimeType == ContentTypes.Image.Png)
                encoderParams.Param[0] = new EncoderParameter(Encoder.ColorDepth, 8);

            if (mimeType == ContentTypes.Image.Png)
            {
                // PNG encoder is picky about streams - need to do an indirection
                // http://www.west-wind.com/WebLog/posts/8230.aspx
                using var ms = new MemoryStream();
                bitmap.Save(ms, encoder, encoderParams);
                ms.WriteTo(outputStream);
            }
            else
            {
                bitmap.Save(outputStream, encoder, encoderParams);
            }
            return mimeType;
        }
    }
}
