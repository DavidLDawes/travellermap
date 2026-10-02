#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Web;
using System.Web.Routing;

// Runs the host-neutral handlers (Maps.Web, Maps.HTTP.RouteTable) under System.Web on IIS.

namespace Maps.HTTP
{
    /// <summary>A System.Web route that dispatches through the portable route table.</summary>
    internal sealed class PortableRoutes : RouteBase
    {
        public override RouteData? GetRouteData(HttpContextBase httpContext)
        {
            var match = RouteTable.Match(httpContext.Request.Path);
            if (match == null)
                return null;
            var (route, values) = match.Value;
            var data = new RouteData(this, new Handler(route, values));
            foreach (var kv in values)
                data.Values[kv.Key] = kv.Value;
            return data;
        }

        public override VirtualPathData? GetVirtualPath(RequestContext requestContext, RouteValueDictionary values) => null;

        private sealed class Handler : IRouteHandler, IHttpHandler
        {
            private readonly Route route;
            private readonly IDictionary<string, object> values;

            public Handler(Route route, IDictionary<string, object> values)
            {
                this.route = route;
                this.values = values;
            }

            IHttpHandler IRouteHandler.GetHttpHandler(RequestContext requestContext) => this;

            bool IHttpHandler.IsReusable => false;

            void IHttpHandler.ProcessRequest(System.Web.HttpContext context)
            {
                var adapted = new SystemWebContext(context) { RouteValues = values };
                route.CreateHandler(values).ProcessRequest(adapted);
            }
        }
    }

    internal sealed class SystemWebContext : Maps.Web.HttpContext
    {
        private readonly System.Web.HttpContext context;

        public SystemWebContext(System.Web.HttpContext context)
        {
            this.context = context;
            Request = new SystemWebRequest(context.Request);
            Response = new SystemWebResponse(context.Response);
        }

        public override Maps.Web.HttpRequest Request { get; }
        public override Maps.Web.HttpResponse Response { get; }

        public override void ExtendTimeout(TimeSpan timeout) => context.Server.ScriptTimeout = (int)timeout.TotalSeconds;

        public override void ClearHostCache()
        {
            var enumerator = context.Cache.GetEnumerator();
            var keys = new List<string>();
            while (enumerator.MoveNext())
                keys.Add(enumerator.Key.ToString());
            foreach (var key in keys)
                context.Cache.Remove(key);
        }

        public override IEnumerable<KeyValuePair<string, object>> HostCacheStats()
        {
            yield return new KeyValuePair<string, object>("Cache.Count", context.Cache.Count);
            yield return new KeyValuePair<string, object>("Cache.EffectivePercentagePhysicalMemoryLimit", context.Cache.EffectivePercentagePhysicalMemoryLimit);
            yield return new KeyValuePair<string, object>("Cache.EffectivePrivateBytesLimit", context.Cache.EffectivePrivateBytesLimit);
        }
    }

    internal sealed class SystemWebRequest : Maps.Web.HttpRequest
    {
        private readonly System.Web.HttpRequest request;
        private Maps.Web.HttpFileCollection? files;

        public SystemWebRequest(System.Web.HttpRequest request)
        {
            this.request = request;
        }

        public override NameValueCollection QueryString => request.QueryString;
        public override NameValueCollection Form => request.Form;
        public override NameValueCollection Headers => request.Headers;
        public override string HttpMethod => request.HttpMethod;
        public override string Path => request.Path;
        public override string RawUrl => request.RawUrl;
        public override Uri Url => request.Url;
        public override bool IsLocal => request.IsLocal;
        public override bool IsSecureConnection => request.IsSecureConnection;
        public override Stream InputStream => request.InputStream;

        public override Maps.Web.HttpFileCollection Files
        {
            get
            {
                if (files == null)
                {
                    files = new Maps.Web.HttpFileCollection();
                    foreach (string name in request.Files.AllKeys)
                    {
                        var file = request.Files[name];
                        files.Add(name, new Maps.Web.HttpPostedFile(file.ContentType, file.InputStream));
                    }
                }
                return files;
            }
        }
    }

    internal sealed class SystemWebResponse : Maps.Web.HttpResponse
    {
        private readonly System.Web.HttpResponse response;

        public SystemWebResponse(System.Web.HttpResponse response)
        {
            this.response = response;
            Cache = new CachePolicy(response.Cache);
        }

        public override int StatusCode { get => response.StatusCode; set => response.StatusCode = value; }
        public override string? StatusDescription { get => response.StatusDescription; set => response.StatusDescription = value; }
        public override string? ContentType { get => response.ContentType; set => response.ContentType = value; }
        public override Encoding ContentEncoding { get => response.ContentEncoding; set => response.ContentEncoding = value; }
        public override Maps.Web.HttpCachePolicy Cache { get; }
        public override bool BufferOutput { get => response.BufferOutput; set => response.BufferOutput = value; }
        public override bool TrySkipIisCustomErrors { get => response.TrySkipIisCustomErrors; set => response.TrySkipIisCustomErrors = value; }
        public override TextWriter Output => response.Output;
        public override Stream OutputStream => response.OutputStream;
        public override void AddHeader(string name, string value) => response.AddHeader(name, value);
        public override void TransmitFile(string path) => response.TransmitFile(path);
        public override void Flush() => response.Flush();
        public override void Close() => response.Close();

        private sealed class CachePolicy : Maps.Web.HttpCachePolicy
        {
            private readonly System.Web.HttpCachePolicy cache;
            public CachePolicy(System.Web.HttpCachePolicy cache) { this.cache = cache; }

            public override void SetCacheability(Maps.Web.HttpCacheability cacheability)
            {
                base.SetCacheability(cacheability);
                cache.SetCacheability(cacheability == Maps.Web.HttpCacheability.Public
                    ? System.Web.HttpCacheability.Public : System.Web.HttpCacheability.NoCache);
            }
            public override void SetMaxAge(TimeSpan maxAge) { base.SetMaxAge(maxAge); cache.SetMaxAge(maxAge); }
            public override void SetValidUntilExpires(bool validUntilExpires) => cache.SetValidUntilExpires(validUntilExpires);
            public override void SetOmitVaryStar(bool omit) => cache.SetOmitVaryStar(omit);
            public override void VaryByAllParams() => cache.VaryByParams["*"] = true;
            public override void VaryByHeader(string header) { base.VaryByHeader(header); cache.VaryByHeaders[header] = true; }
        }
    }
}
