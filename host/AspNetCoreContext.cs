using Maps.Web;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Specialized;
using System.Net;
using System.Text;

namespace Maps.Host
{
    /// <summary>
    /// Runs a host-neutral handler (Maps.Web) on ASP.NET Core. The handlers are synchronous and
    /// mix text and binary writes, as under System.Web, so the request body is read up front and
    /// the response is buffered, then sent asynchronously.
    /// </summary>
    internal sealed class AspNetCoreContext : Web.HttpContext
    {
        private readonly AspNetCoreResponse response;

        private AspNetCoreContext(Microsoft.AspNetCore.Http.HttpContext context, AspNetCoreRequest request)
        {
            Request = request;
            response = new AspNetCoreResponse(context);
        }

        public override Web.HttpRequest Request { get; }
        public override Web.HttpResponse Response => response;

        public static async Task<AspNetCoreContext> CreateAsync(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> routeValues)
        {
            var request = await AspNetCoreRequest.CreateAsync(context);
            return new AspNetCoreContext(context, request) { RouteValues = routeValues };
        }

        public Task SendAsync() => response.SendAsync();
    }

    internal sealed class AspNetCoreRequest : Web.HttpRequest
    {
        private readonly Microsoft.AspNetCore.Http.HttpContext context;

        private AspNetCoreRequest(Microsoft.AspNetCore.Http.HttpContext context)
        {
            this.context = context;
            var request = context.Request;
            QueryString = ParseQueryString(request.QueryString.Value);
            Headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
                Headers.Add(header.Key, header.Value.ToString());
        }

        public static async Task<AspNetCoreRequest> CreateAsync(Microsoft.AspNetCore.Http.HttpContext context)
        {
            var result = new AspNetCoreRequest(context);
            var request = context.Request;
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync();
                foreach (var field in form)
                    result.Form.Add(field.Key, field.Value.ToString());
                foreach (var file in form.Files)
                {
                    var data = new MemoryStream();
                    await file.CopyToAsync(data);
                    data.Position = 0;
                    result.Files.Add(file.Name, new HttpPostedFile(file.ContentType, data));
                }
            }
            else if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
            {
                var body = new MemoryStream();
                await request.Body.CopyToAsync(body);
                body.Position = 0;
                result.inputStream = body;
            }
            return result;
        }

        public override NameValueCollection QueryString { get; }
        public override NameValueCollection Form { get; } = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        public override HttpFileCollection Files { get; } = new HttpFileCollection();
        public override NameValueCollection Headers { get; }
        private Stream inputStream = new MemoryStream();
        public override Stream InputStream => inputStream;

        public override string HttpMethod => context.Request.Method;
        public override string Path => context.Request.Path.Value ?? "/";
        public override string RawUrl => context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? (Path + context.Request.QueryString.Value);
        public override Uri Url => new Uri(context.Request.GetEncodedUrl());
        public override bool IsSecureConnection => context.Request.IsHttps;

        // As System.Web: the request came from this machine.
        public override bool IsLocal
        {
            get
            {
                var connection = context.Connection;
                if (connection.RemoteIpAddress == null)
                    return true; // In-process (e.g. test server)
                return IPAddress.IsLoopback(connection.RemoteIpAddress) || connection.RemoteIpAddress.Equals(connection.LocalIpAddress);
            }
        }

    }

    internal sealed class AspNetCoreResponse : Web.HttpResponse
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly Microsoft.AspNetCore.Http.HttpContext context;
        private readonly MemoryStream body = new MemoryStream();
        private readonly List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();
        private Encoding contentEncoding = Utf8;
        private StreamWriter? writer;

        public AspNetCoreResponse(Microsoft.AspNetCore.Http.HttpContext context)
        {
            this.context = context;
            OutputStream = new NonClosingStream(body);
        }

        public override int StatusCode { get; set; } = 200;
        public override string? StatusDescription { get; set; }
        public override string? ContentType { get; set; } = "text/html";
        public override HttpCachePolicy Cache { get; } = new HttpCachePolicy();
        public override Stream OutputStream { get; }

        public override Encoding ContentEncoding
        {
            get => contentEncoding;
            set
            {
                if (writer != null)
                {
                    writer.Flush();
                    writer = new StreamWriter(OutputStream, WithoutBom(value)) { AutoFlush = true };
                }
                contentEncoding = value;
            }
        }

        public override TextWriter Output => writer ??= new StreamWriter(OutputStream, WithoutBom(contentEncoding)) { AutoFlush = true };

        // System.Web never writes a byte-order mark into a response.
        private static Encoding WithoutBom(Encoding encoding) => encoding is UTF8Encoding ? Utf8 : encoding;

        public override void AddHeader(string name, string value) => headers.Add(new KeyValuePair<string, string>(name, value));

        public override void TransmitFile(string path)
        {
            using var file = File.OpenRead(path);
            file.CopyTo(OutputStream);
        }

        public override void Flush() => writer?.Flush();

        public async Task SendAsync()
        {
            Flush();
            var response = context.Response;
            response.StatusCode = StatusCode;
            if (StatusDescription != null)
                context.Features.Get<IHttpResponseFeature>()!.ReasonPhrase = StatusDescription;

            // As System.Web: no type for an empty body (e.g. a redirect), and text written through
            // Output (not raw bytes, e.g. serialized XML) is labelled with its charset.
            if (ContentType != null && body.Length > 0)
            {
                response.ContentType = writer != null && ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && !ContentType.Contains("charset")
                    ? $"{ContentType}; charset={CharsetName(contentEncoding)}"
                    : ContentType;
            }

            // Caching headers, as System.Web's HttpCachePolicy writes them.
            switch (Cache.Cacheability)
            {
                case HttpCacheability.NoCache:
                    response.Headers.CacheControl = "no-cache";
                    response.Headers.Pragma = "no-cache";
                    response.Headers.Expires = "-1";
                    break;
                case HttpCacheability.Public:
                    response.Headers.CacheControl = Cache.MaxAge is TimeSpan maxAge ? $"public, max-age={(int)maxAge.TotalSeconds}" : "public";
                    break;
                default:
                    response.Headers.CacheControl = "private";
                    break;
            }
            foreach (var header in Cache.VaryByHeaderNames)
                response.Headers.Append("Vary", header);

            foreach (var header in headers)
                response.Headers.Append(header.Key, header.Value);

            response.ContentLength = body.Length;
            body.Position = 0;
            await body.CopyToAsync(response.Body);
        }

        // Charset names as .NET Framework spells them (clients and tests compare them literally).
        private static string CharsetName(Encoding encoding) => encoding.CodePage == 1252 ? "Windows-1252" : encoding.WebName;

        /// <summary>Handlers sometimes dispose writers over OutputStream; that mustn't end the response.</summary>
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream inner;
            public NonClosingStream(Stream inner) { this.inner = inner; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
            protected override void Dispose(bool disposing) { }
        }
    }
}
