#nullable enable
using Maps.Search;
using Maps.Utilities;
using Maps.Web;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Maps.Admin
{
    /// <summary>
    /// /admin/status: how this server process is doing. Everything here is about the one process
    /// that answers (on Cloud Run there can be several, and counters restart with the process).
    /// </summary>
    internal class StatusHandler : AdminHandlerBase
    {
        protected override void Process(HttpContext context, ResourceManager resourceManager)
        {
            context.Response.ContentType = ContentTypes.Text.Html;
            context.Response.Write(StatusPage.Render(
                RequestStats.Current.Capture(), AdminLinks.KeySuffix(context), context.HostCacheStats()));
        }
    }

    /// <summary>The /admin landing page: links to every admin page.</summary>
    internal class IndexHandler : AdminHandlerBase
    {
        protected override void Process(HttpContext context, ResourceManager resourceManager)
        {
            context.Response.ContentType = ContentTypes.Text.Html;
            context.Response.Write(StatusPage.RenderIndex(AdminLinks.KeySuffix(context)));
        }
    }

    internal static class AdminLinks
    {
        /// <summary>"?key=..." to carry on links when this request supplied a key, else "".</summary>
        public static string KeySuffix(HttpContext context)
        {
            string? key = context.Request["key"];
            return string.IsNullOrEmpty(key) ? "" : "?key=" + Uri.EscapeDataString(key);
        }
    }

    internal static class StatusPage
    {
        internal static string E(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

        // Formatting ----------------------------------------------------------

        public static string FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalSeconds < 1) return $"{span.TotalMilliseconds:0} ms";
            if (span.TotalMinutes < 1) return $"{span.TotalSeconds:0.0} s";
            if (span.TotalHours < 1) return $"{span.Minutes}m {span.Seconds}s";
            if (span.TotalDays < 1) return $"{span.Hours}h {span.Minutes}m";
            return $"{span.Days}d {span.Hours}h {span.Minutes}m";
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; ++unit; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
        }

        /// <summary>"≤ 250 ms" for a latency bucket bound; "&gt; 10 s" past the last one; "–" when unknown.</summary>
        public static string FormatBound(double? milliseconds)
        {
            if (milliseconds == null) return "–";
            if (double.IsPositiveInfinity(milliseconds.Value))
                return "> " + FormatDuration(TimeSpan.FromMilliseconds(RequestStats.LatencyBoundsMs[RequestStats.LatencyBoundsMs.Length - 1]));
            return "≤ " + FormatDuration(TimeSpan.FromMilliseconds(milliseconds.Value));
        }

        // Pages ----------------------------------------------------------------

        internal const string Style = @"
:root { color-scheme: light dark; --line: #8884; --muted: #888; }
body { font: 15px/1.45 system-ui, sans-serif; max-width: 62rem; margin: 1.5rem auto; padding: 0 1rem; }
h1 { font-size: 1.4rem; margin-bottom: .2rem; }
h2 { font-size: 1.05rem; margin: 1.6rem 0 .4rem; border-bottom: 1px solid var(--line); }
table { border-collapse: collapse; width: 100%; }
th, td { text-align: left; padding: .25rem .6rem .25rem 0; vertical-align: top; }
td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
td.label { color: var(--muted); width: 14rem; }
.note { color: var(--muted); font-size: .9em; }
nav a { margin-right: 1rem; }
.bar { background: var(--line); border-radius: 3px; height: .75rem; min-width: 6rem; }
.bar > i { display: block; height: 100%; background: #3a8; border-radius: 3px; }
.bar.warn > i { background: #d90; }
.bar.over > i { background: #d33; }
";

        internal static void Header(StringBuilder page, string title, string key)
        {
            page.Append("<!DOCTYPE html>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            page.Append("<title>Traveller Map - ").Append(E(title)).Append("</title>\n<style>").Append(Style).Append("</style>\n");
            page.Append("<nav><a href=\"/admin").Append(key).Append("\">Admin</a>");
            foreach (string name in new[] { "status", "fleet", "usage" })
                page.Append("<a href=\"/admin/").Append(name).Append(key).Append("\">").Append(char.ToUpperInvariant(name[0])).Append(name, 1, name.Length - 1).Append("</a>");
            page.Append("</nav>\n");
            page.Append("<h1>Traveller Map - ").Append(E(title)).Append("</h1>\n");
        }

        internal static void Section(StringBuilder page, string title, string? note = null)
        {
            page.Append("<h2>").Append(E(title)).Append("</h2>\n");
            if (note != null)
                page.Append("<p class=\"note\">").Append(E(note)).Append("</p>\n");
        }

        internal static void Row(StringBuilder page, string label, object? value, string? note = null)
        {
            page.Append("<tr><td class=\"label\">").Append(E(label)).Append("</td><td>").Append(E(value));
            if (note != null)
                page.Append(" <span class=\"note\">").Append(E(note)).Append("</span>");
            page.Append("</td></tr>\n");
        }

        private static string Env(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? "–" : value!;
        }

        public static string Render(RequestStats.Snapshot requests, string key, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object>> hostCacheStats)
        {
            var page = new StringBuilder();
            Header(page, "Status", key);
            page.Append("<p class=\"note\">This is the one server process that answered. On Cloud Run there can be zero to two, and the counters restart whenever one stops.</p>\n");

            DateTime now = DateTime.Now;
            TimeSpan uptime = now - HandlerBase.StartupTime;
            string sha = Env("GIT_SHA");

            Section(page, "Deployment");
            page.Append("<table>\n");
            Row(page, "Service", Env("K_SERVICE"));
            Row(page, "Revision", Env("K_REVISION"));
            Row(page, "Commit", sha.Length > 12 ? sha.Substring(0, 12) : sha);
            Row(page, "Deployed", Env("DEPLOYED_AT"));
            Row(page, "Runtime", RuntimeInformation.FrameworkDescription);
            Row(page, "System", RuntimeInformation.OSDescription);
            Row(page, "Host name", Environment.MachineName);
            page.Append("</table>\n");

            var process = Process.GetCurrentProcess();
            Section(page, "Process");
            page.Append("<table>\n");
            Row(page, "Started", HandlerBase.StartupTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            Row(page, "Uptime", FormatDuration(uptime));
            Row(page, "First request", requests.StartToFirstRequest is TimeSpan first
                ? $"{FormatDuration(first)} after start, took {FormatDuration(requests.FirstRequestDuration ?? TimeSpan.Zero)}"
                : "none yet", "(for an instance Cloud Run started on demand, this is the cold start)");
            Row(page, "CPU time used", FormatDuration(process.TotalProcessorTime), $"on {Environment.ProcessorCount} CPU(s) available");
            long workingSet = process.WorkingSet64;
#if NETFRAMEWORK
            Row(page, "Memory in use", FormatBytes(workingSet));
#else
            long limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            Row(page, "Memory in use", FormatBytes(workingSet), limit > 0 ? $"of {FormatBytes(limit)} available ({workingSet * 100.0 / limit:0}%)" : null);
#endif
            Row(page, ".NET heap", FormatBytes(GC.GetTotalMemory(false)));
            Row(page, "Garbage collections", $"{GC.CollectionCount(0)} gen0, {GC.CollectionCount(1)} gen1, {GC.CollectionCount(2)} gen2");
            Row(page, "Threads", process.Threads.Count);
            page.Append("</table>\n");

            Section(page, "Requests since start");
            if (requests.TotalRequests == 0 && requests.InFlight == 0)
            {
                page.Append("<p class=\"note\">None counted. (The IIS host does not collect these.)</p>\n");
            }
            else
            {
                page.Append("<table>\n<tr><th>Kind</th><th class=\"num\">Requests</th><th class=\"num\">2xx</th><th class=\"num\">3xx</th><th class=\"num\">4xx</th><th class=\"num\">5xx</th><th class=\"num\">Median</th><th class=\"num\">95th pct</th><th class=\"num\">Mean</th><th class=\"num\">Slowest</th></tr>\n");
                foreach (var c in requests.Categories)
                {
                    if (c.Requests == 0) continue;
                    page.Append("<tr><td>").Append(E(c.Name)).Append("</td>");
                    page.Append("<td class=\"num\">").Append(c.Requests.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>");
                    for (int s = 0; s < 4; ++s)
                        page.Append("<td class=\"num\">").Append(c.StatusClasses[s].ToString("N0", CultureInfo.InvariantCulture)).Append("</td>");
                    page.Append("<td class=\"num\">").Append(E(FormatBound(c.PercentileUpperBoundMs(0.5)))).Append("</td>");
                    page.Append("<td class=\"num\">").Append(E(FormatBound(c.PercentileUpperBoundMs(0.95)))).Append("</td>");
                    page.Append("<td class=\"num\">").Append(E(FormatDuration(c.Mean))).Append("</td>");
                    page.Append("<td class=\"num\">").Append(E(FormatDuration(c.MaxTime))).Append("</td></tr>\n");
                }
                page.Append("</table>\n<table>\n");
                Row(page, "Total", requests.TotalRequests.ToString("N0", CultureInfo.InvariantCulture));
                Row(page, "In flight now", requests.InFlight, $"peak {requests.PeakInFlight}");
                page.Append("</table>\n");
                page.Append("<p class=\"note\">Percentiles are the upper bound of the latency bucket they fall in.</p>\n");
            }

            Section(page, "PDF generation", "PDFsharp is not safe to run in parallel, so PDF renders take turns.");
            page.Append("<table>\n");
            if (requests.PdfLockWaits == 0)
                Row(page, "PDFs rendered", 0);
            else
            {
                Row(page, "PDFs rendered", requests.PdfLockWaits.ToString("N0", CultureInfo.InvariantCulture));
                Row(page, "Waited for the lock", $"{FormatDuration(TimeSpan.FromTicks(requests.PdfLockTotalWait.Ticks / requests.PdfLockWaits))} on average, {FormatDuration(requests.PdfLockMaxWait)} at most");
            }
            page.Append("</table>\n");

            Section(page, "Data and caches");
            page.Append("<table>\n");
            try
            {
                var sectors = SectorMap.GetInstance().Sectors.ToList();
                Row(page, "Sectors loaded", sectors.Count.ToString("N0", CultureInfo.InvariantCulture),
                    $"in {sectors.Select(s => s.CanonicalMilieu).Distinct().Count()} milieus");
            }
            catch (Exception ex)
            {
                Row(page, "Sectors loaded", "error: " + ex.Message);
            }
            Row(page, "Cache generation", CacheGeneration.Current, "(raised by /admin/flush)");
            foreach (var stat in hostCacheStats)
                Row(page, stat.Key, stat.Value);
            page.Append("</table>\n");

            Section(page, "Search index");
            page.Append("<table>\n");
            if (SearchEngine.Index is SqliteSearchIndex sqlite)
            {
                var file = new FileInfo(sqlite.Path);
                Row(page, "File", sqlite.Path);
                if (file.Exists)
                {
                    Row(page, "Size", FormatBytes(file.Length));
                    Row(page, "Built", file.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
                }
                else
                    Row(page, "State", "missing (searches answer 503 until it is built)");
            }
            else
                Row(page, "Index", SearchEngine.Index == null ? "none configured" : SearchEngine.Index.GetType().Name);
            page.Append("</table>\n");
            return page.ToString();
        }

        public static string RenderIndex(string key)
        {
            var page = new StringBuilder();
            Header(page, "Administration", key);
            page.Append("<h2>Reports</h2>\n<table>\n");
            void Link(string path, string description) =>
                page.Append("<tr><td class=\"label\"><a href=\"").Append(path).Append(key).Append("\">").Append(E(path)).Append("</a></td><td>").Append(E(description)).Append("</td></tr>\n");
            Link("/admin/status", "This server process: deployment, memory, requests, caches, search index.");
            Link("/admin/fleet", "All instances, from Cloud Monitoring: how many are running, peaks, requests per hour.");
            Link("/admin/usage", "This month's usage against the Cloud Run free tier, projected to month end.");
            Link("/admin/overview", "Overview map of the sector data.");
            Link("/admin/errors", "Data errors found in the sector files.");
            Link("/admin/uptime", "How long this process has been running.");
            Link("/admin/profile", "Process memory and host cache details.");
            Link("/admin/codes", "Allegiance and other codes in use.");
            Link("/admin/routes", "The route table.");
            Link("/admin/dump", "Dump of the sector data.");
            page.Append("</table>\n<h2>Actions</h2>\n<p class=\"note\">These change this server process only. On Cloud Run, each instance has its own memory and search index; data changes go out by deploying.</p>\n<table>\n");
            void ActionRow(string path, string description, string confirm) =>
                page.Append("<tr><td class=\"label\"><a href=\"").Append(path).Append(key).Append("\" onclick=\"return confirm('").Append(confirm).Append("')\">").Append(E(path)).Append("</a></td><td>").Append(E(description)).Append("</td></tr>\n");
            ActionRow("/admin/flush", "Reload the cached sector data.", "Reload all cached data on this server process?");
            ActionRow("/admin/reindex", "Rebuild the search index (takes about 15 seconds; searches wait).", "Rebuild the search index on this server process?");
            page.Append("</table>\n");
            return page.ToString();
        }
    }
}
