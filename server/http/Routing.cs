#nullable enable
using Maps.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Maps.HTTP
{
    /// <summary>A request handler, created per request by the route that matched.</summary>
    internal interface IRequestHandler
    {
        void ProcessRequest(HttpContext context);
    }

    /// <summary>
    /// A URL route: a regular expression over the whole (decoded) path, matched
    /// case-insensitively, whose named groups become route values on top of the defaults.
    /// The target is a handler type or a redirect.
    /// Based on https://web.archive.org/web/20080401025712/http://www.iridescence.no/Posts/Defining-Routes-using-Regular-Expressions-in-ASPNET-MVC.aspx
    /// </summary>
    internal sealed class Route
    {
        private readonly Regex regex;
        private readonly IReadOnlyDictionary<string, object> defaults;

        private Route(string pattern, IDictionary<string, object>? defaults)
        {
            Pattern = pattern;
            regex = new Regex("^" + pattern + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);
            this.defaults = new Dictionary<string, object>(defaults ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        }

        public static Route ForHandler(string pattern, Type handlerType, IDictionary<string, object>? defaults = null) =>
            new Route(pattern, defaults) { HandlerType = handlerType };

        public static Route ForRedirect(string pattern, string target, int statusCode) =>
            new Route(pattern, null) { RedirectTarget = target, RedirectStatusCode = statusCode };

        public string Pattern { get; }
        public Type? HandlerType { get; private set; }
        public string? RedirectTarget { get; private set; }
        public int RedirectStatusCode { get; private set; }

        /// <summary>The route values if the path matches, else null.</summary>
        public IDictionary<string, object>? Match(string path)
        {
            Match match = regex.Match(path);
            if (!match.Success)
                return null;
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in defaults)
                values[kv.Key] = kv.Value;
            foreach (string name in regex.GetGroupNames().Where(n => !int.TryParse(n, out _)))
                values[name] = match.Groups[name].Value;
            return values;
        }

        public IRequestHandler CreateHandler(IDictionary<string, object> values) =>
            HandlerType != null
                ? (IRequestHandler)Activator.CreateInstance(HandlerType)!
                : new RedirectHandler(RedirectHandler.ExpandTarget(RedirectTarget!, values), RedirectStatusCode);
    }

    internal sealed class RedirectHandler : IRequestHandler
    {
        private static readonly Regex replacer = new Regex(@"{(.*?)}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string url;
        private readonly int statusCode;

        public RedirectHandler(string url, int statusCode)
        {
            this.url = url;
            this.statusCode = statusCode;
        }

        /// <summary>
        /// Substitutes {name} placeholders in a redirect target with route values. Values
        /// are URL-encoded since all current targets place them in the query string, and
        /// names may contain characters like '&amp;' or '#'.
        /// </summary>
        internal static string ExpandTarget(string pattern, IDictionary<string, object> values)
        {
            return replacer.Replace(pattern, m => Uri.EscapeDataString(values[m.Groups[1].Value].ToString()!));
        }

        public void ProcessRequest(HttpContext context)
        {
            // Pass the request's query along.
            string target = url;
            string query = context.Request.Url.Query.TrimStart('?');
            if (query.Length > 0)
                target += (target.Contains("?") ? "&" : "?") + query;
            context.Response.StatusCode = statusCode;
            context.Response.AddHeader("Location", target);
        }
    }

    /// <summary>All URL routes, in precedence order (the first match wins).</summary>
    internal static class RouteTable
    {
        public static IReadOnlyList<Route> Routes { get; } = Register();

        /// <summary>The first route matching the path, and its values; null if none.</summary>
        public static (Route route, IDictionary<string, object> values)? Match(string path)
        {
            foreach (var route in Routes)
            {
                var values = route.Match(path);
                if (values != null)
                    return (route, values);
            }
            return null;
        }

        private static List<Route> Register()
        {
            var routes = new List<Route>();
            void Add(string pattern, Type handler, IDictionary<string, object>? defaults = null)
                => routes.Add(Route.ForHandler(pattern, handler, defaults));
            void Redirect(string pattern, string target, int statusCode)
                => routes.Add(Route.ForRedirect(pattern, target, statusCode));
            Dictionary<string, object> D(params (string key, string value)[] values)
                => values.ToDictionary(v => v.key, v => (object)v.value);

            var DEFAULT_JSON = D(("accept", Json.JsonConstants.MediaType));

            // Navigation  --------------------------------------------------

            Redirect(@"/go/(?<sector>[^/]+)", "/?sector={sector}", 302);
            Redirect(@"/go/(?<sector>[^/]+)/(?<hex>[0-9]{4})", "/?sector={sector}&hex={hex}", 302);
            Redirect(@"/go/(?<sector>[^/]+)/(?<subsector>[^/]+)", "/?sector={sector}&subsector={subsector}", 302);

            Redirect(@"/booklet/(?<sector>[^/]+)", "/make/booklet?sector={sector}", 302);
            Redirect(@"/sheet/(?<sector>[^/]+)/(?<hex>[0-9]{4})", "/print/world?sector={sector}&hex={hex}", 302);

            // Administration -----------------------------------------------

            Add(@"/admin/?", typeof(Admin.IndexHandler));
            Add(@"/admin/status", typeof(Admin.StatusHandler));
            Add(@"/admin/fleet", typeof(Admin.FleetHandler));
            Add(@"/admin/usage", typeof(Admin.UsageHandler));
            Add(@"/admin/admin", typeof(Admin.AdminHandler));
            Add(@"/admin/flush", typeof(Admin.AdminHandler), D(("action", "flush")));
            Add(@"/admin/reindex", typeof(Admin.AdminHandler), D(("action", "reindex")));
            Add(@"/admin/profile", typeof(Admin.AdminHandler), D(("action", "profile")));
            Add(@"/admin/uptime", typeof(Admin.AdminHandler), D(("action", "uptime")));
            Add(@"/admin/codes", typeof(Admin.CodesHandler));
            Add(@"/admin/routes", typeof(Admin.RoutesHandler));
            Add(@"/admin/dump", typeof(Admin.DumpHandler));
            Add(@"/admin/errors", typeof(Admin.ErrorsHandler));
            Add(@"/admin/overview", typeof(Admin.OverviewHandler));

            // Search -------------------------------------------------------

            Add(@"/api/search", typeof(API.SearchHandler), DEFAULT_JSON);
            Add(@"/api/route", typeof(API.RouteHandler), DEFAULT_JSON);

            // Rendering ----------------------------------------------------

            Add(@"/api/jumpmap", typeof(API.JumpMapHandler));
            Add(@"/api/poster", typeof(API.PosterHandler));
            Add(@"/api/poster/(?<sector>[^/]+)", typeof(API.PosterHandler));
            Add(@"/api/poster/(?<sector>[^/]+)/(?<quadrant>(?:alpha|beta|gamma|delta))", typeof(API.PosterHandler));
            Add(@"/api/poster/(?<sector>[^/]+)/(?<subsector>[^/]+)", typeof(API.PosterHandler));
            Add(@"/api/tile", typeof(API.TileHandler));

            // Location Queries ---------------------------------------------

            Add(@"/api/coordinates", typeof(API.CoordinatesHandler), DEFAULT_JSON);
            Add(@"/api/credits", typeof(API.CreditsHandler), DEFAULT_JSON);
            Add(@"/api/jumpworlds", typeof(API.JumpWorldsHandler), DEFAULT_JSON);

            // Data Retrieval - API-centric ---------------------------------

            Add(@"/api/universe", typeof(API.UniverseHandler), DEFAULT_JSON);
            Add(@"/api/milieux", typeof(API.MilieuxCodesHandler), DEFAULT_JSON);
            Add(@"/api/sec", typeof(API.SECHandler), D(("type", "SecondSurvey")));
            Add(@"/api/sec/(?<sector>[^/]+)", typeof(API.SECHandler), D(("type", "SecondSurvey")));
            Add(@"/api/sec/(?<sector>[^/]+)/(?<quadrant>alpha|beta|gamma|delta)", typeof(API.SECHandler), D(("type", "SecondSurvey"), ("metadata", "0")));
            Add(@"/api/sec/(?<sector>[^/]+)/(?<subsector>[^/]+)", typeof(API.SECHandler), D(("type", "SecondSurvey"), ("metadata", "0")));
            Add(@"/api/metadata", typeof(API.SectorMetaDataHandler), DEFAULT_JSON);
            Add(@"/api/metadata/(?<sector>[^/]+)", typeof(API.SectorMetaDataHandler), DEFAULT_JSON);
            Add(@"/api/msec", typeof(API.MSECHandler));
            Add(@"/api/msec/(?<sector>[^/]+)", typeof(API.MSECHandler));

            // Data Retrieval - RESTful -------------------------------------

            Add(@"/data", typeof(API.UniverseHandler), DEFAULT_JSON);

            // Sector, e.g. /data/Spinward Marches
            Add(@"/data/(?<sector>[^/]+)", typeof(API.SECHandler), D(("type", "SecondSurvey")));
            Add(@"/data/(?<sector>[^/]+)/sec", typeof(API.SECHandler));
            Add(@"/data/(?<sector>[^/]+)/tab", typeof(API.SECHandler), D(("type", "TabDelimited")));
            Add(@"/data/(?<sector>[^/]+)/coordinates", typeof(API.CoordinatesHandler), DEFAULT_JSON);
            Add(@"/data/(?<sector>[^/]+)/credits", typeof(API.CreditsHandler), DEFAULT_JSON);
            Add(@"/data/(?<sector>[^/]+)/metadata", typeof(API.SectorMetaDataHandler)); // NOTE: XML by default
            Add(@"/data/(?<sector>[^/]+)/msec", typeof(API.MSECHandler));
            Add(@"/data/(?<sector>[^/]+)/image", typeof(API.PosterHandler));

            Redirect(@"/data/(?<sector>[^/]+)/booklet", "/make/booklet?sector={sector}", 302);

            // Part of a sector (quadrant or subsector): data, /sec, /tab, /image
            void AddSectorPartRoutes(string part)
            {
                Add($@"/data/(?<sector>[^/]+)/{part}", typeof(API.SECHandler), D(("type", "SecondSurvey"), ("metadata", "0")));
                Add($@"/data/(?<sector>[^/]+)/{part}/sec", typeof(API.SECHandler), D(("metadata", "0")));
                Add($@"/data/(?<sector>[^/]+)/{part}/tab", typeof(API.SECHandler), D(("type", "TabDelimited"), ("metadata", "0")));
                Add($@"/data/(?<sector>[^/]+)/{part}/image", typeof(API.PosterHandler));
            }

            // Quadrant, e.g. /data/Spinward Marches/Alpha
            AddSectorPartRoutes("(?<quadrant>alpha|beta|gamma|delta)");

            // Subsector by Index, e.g. /data/Spinward Marches/C
            AddSectorPartRoutes("(?<subsector>[A-Pa-p])");

            // World e.g. /data/Spinward Marches/1910
            Add(@"/data/(?<sector>[^/]+)/(?<hex>[0-9]{4})", typeof(API.JumpWorldsHandler), D(("accept", Json.JsonConstants.MediaType), ("jump", "0")));
            Add(@"/data/(?<sector>[^/]+)/(?<hex>[0-9]{4})/coordinates", typeof(API.CoordinatesHandler), DEFAULT_JSON);
            Add(@"/data/(?<sector>[^/]+)/(?<hex>[0-9]{4})/credits", typeof(API.CreditsHandler), DEFAULT_JSON);
            Add(@"/data/(?<sector>[^/]+)/(?<hex>[0-9]{4})/jump/(?<jump>\d+)", typeof(API.JumpWorldsHandler), DEFAULT_JSON);
            Add(@"/data/(?<sector>[^/]+)/(?<hex>[0-9]{4})/image", typeof(API.JumpMapHandler), D(("jump", "0")));
            Add(@"/data/(?<sector>[^/]+)/(?<hex>[0-9]{4})/jump/(?<jump>\d+)/image", typeof(API.JumpMapHandler));

            Redirect(@"/data/(?<sector>[^/]+)/(?<hex>[0-9]{4})/sheet", "/print/world?sector={sector}&hex={hex}", 302);

            // Subsector by Name e.g. /data/Spinward Marches/Regina
            // NOTE: Must come after the more specific /data/{sector}/... routes above.
            AddSectorPartRoutes("(?<subsector>[^/]+)");

            // T5SS Stock Data -------------------------------------

            Add(@"/t5ss/allegiances", typeof(API.AllegianceCodesHandler), DEFAULT_JSON);
            Add(@"/t5ss/sophonts", typeof(API.SophontCodesHandler), DEFAULT_JSON);

            return routes;
        }
    }
}
