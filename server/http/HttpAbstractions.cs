#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;

// A host-neutral subset of System.Web's HttpContext/HttpRequest/HttpResponse: exactly what the
// handlers use. Each host supplies an adapter (System.Web on IIS: server/http/SystemWebHost.cs;
// ASP.NET Core: host/AspNetCoreContext.cs), so the same handler code runs on both. Names and
// semantics follow System.Web, so handler code reads as before.

namespace Maps.Web
{
    internal abstract class HttpContext
    {
        public abstract HttpRequest Request { get; }
        public abstract HttpResponse Response { get; }

        /// <summary>Values from the matched route: its defaults, then its named captures.</summary>
        public IDictionary<string, object> RouteValues { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Allow a long-running request (e.g. admin reindex) to run up to this long.</summary>
        public virtual void ExtendTimeout(TimeSpan timeout) { }

        /// <summary>Clears host-level caches (System.Web's HttpRuntime cache on IIS).</summary>
        public virtual void ClearHostCache() { }

        /// <summary>Host-level cache statistics, for /admin/profile.</summary>
        public virtual IEnumerable<KeyValuePair<string, object>> HostCacheStats() => Enumerable.Empty<KeyValuePair<string, object>>();
    }

    internal abstract class HttpRequest
    {
        /// <summary>A query string value, else a form value (like System.Web's indexer).</summary>
        public string? this[string name] => QueryString[name] ?? Form[name];

        /// <summary>
        /// Query parameters, decoded. As in System.Web, parameters without "=value" are collected
        /// under the null key, comma-separated (e.g. "?hide-uwp&amp;hide-tl").
        /// </summary>
        public abstract NameValueCollection QueryString { get; }
        public abstract NameValueCollection Form { get; }
        public abstract HttpFileCollection Files { get; }
        public abstract NameValueCollection Headers { get; }

        public abstract string HttpMethod { get; }
        /// <summary>The URL path, decoded (e.g. "/data/Spinward Marches").</summary>
        public abstract string Path { get; }
        /// <summary>Path and query as sent.</summary>
        public abstract string RawUrl { get; }
        public abstract Uri Url { get; }
        public abstract bool IsLocal { get; }
        public abstract bool IsSecureConnection { get; }
        public abstract Stream InputStream { get; }

        public string? ContentType => Headers["Content-Type"];
        public string? UserAgent => Headers["User-Agent"];

        public Encoding ContentEncoding
        {
            get
            {
                try
                {
                    string? charset = ContentType == null ? null : new System.Net.Mime.ContentType(ContentType).CharSet;
                    return string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
                }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
                {
                    return Encoding.UTF8;
                }
            }
        }

        /// <summary>Media types from the Accept header, without parameters (like System.Web).</summary>
        public string[]? AcceptTypes
        {
            get
            {
                string? accept = Headers["Accept"];
                if (string.IsNullOrEmpty(accept))
                    return null;
                return accept!.Split(',').Select(t => t.Split(';')[0].Trim()).Where(t => t.Length > 0).ToArray();
            }
        }

        /// <summary>
        /// Parses a query string ("a=1&amp;b=x+y&amp;flag") the way System.Web does: '+' is a space,
        /// %XX is decoded, and parameters without '=' go under the null key.
        /// </summary>
        public static NameValueCollection ParseQueryString(string? query)
        {
            var result = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return result;
            if (query![0] == '?')
                query = query.Substring(1);
            foreach (string part in query.Split('&'))
            {
                if (part.Length == 0)
                    continue;
                int eq = part.IndexOf('=');
                if (eq < 0)
                    result.Add(null, Decode(part));
                else
                    result.Add(Decode(part.Substring(0, eq)), Decode(part.Substring(eq + 1)));
            }
            return result;
        }

        private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    internal sealed class HttpPostedFile
    {
        public HttpPostedFile(string? contentType, Stream inputStream)
        {
            ContentType = contentType;
            InputStream = inputStream;
        }
        public string? ContentType { get; }
        public Stream InputStream { get; }
        public long ContentLength => InputStream.Length;
    }

    internal sealed class HttpFileCollection
    {
        private readonly Dictionary<string, HttpPostedFile> files = new Dictionary<string, HttpPostedFile>(StringComparer.OrdinalIgnoreCase);
        public HttpPostedFile? this[string name] => files.TryGetValue(name, out var file) ? file : null;
        public void Add(string name, HttpPostedFile file) => files[name] = file;
    }

    internal enum HttpCacheability
    {
        NoCache,
        Public,
    }

    /// <summary>Response caching, as System.Web's HttpCachePolicy (the subset in use).</summary>
    internal class HttpCachePolicy
    {
        public HttpCacheability? Cacheability { get; private set; }
        public TimeSpan? MaxAge { get; private set; }
        public ISet<string> VaryByHeaderNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public virtual void SetCacheability(HttpCacheability cacheability) => Cacheability = cacheability;
        public virtual void SetMaxAge(TimeSpan maxAge) => MaxAge = maxAge;
        public virtual void SetValidUntilExpires(bool validUntilExpires) { }
        public virtual void SetOmitVaryStar(bool omit) { }
        public virtual void VaryByAllParams() { }
        public virtual void VaryByHeader(string header) => VaryByHeaderNames.Add(header);
    }

    internal abstract class HttpResponse
    {
        public abstract int StatusCode { get; set; }
        public abstract string? StatusDescription { get; set; }
        /// <summary>The media type, without charset (added for text types from ContentEncoding).</summary>
        public abstract string? ContentType { get; set; }
        public abstract Encoding ContentEncoding { get; set; }
        public abstract HttpCachePolicy Cache { get; }
        public virtual bool BufferOutput { get; set; } = true;
        public virtual bool TrySkipIisCustomErrors { get; set; }

        /// <summary>Text output; interleaves correctly with writes to OutputStream.</summary>
        public abstract TextWriter Output { get; }
        /// <summary>Binary output. Closing it doesn't end the response.</summary>
        public abstract Stream OutputStream { get; }

        public void Write(string s) => Output.Write(s);
        public abstract void AddHeader(string name, string value);
        public abstract void TransmitFile(string path);
        public abstract void Flush();
        public virtual void Close() => Flush();
    }
}
