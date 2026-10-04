#nullable enable
using Maps.Utilities;
using Maps.Web;
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Maps.Admin
{
    /// <summary>/admin/fleet: every instance of the service, from the platform's metrics.</summary>
    internal class FleetHandler : AdminHandlerBase
    {
        protected override void Process(HttpContext context, ResourceManager resourceManager)
            => CloudPages.Respond(context, "Fleet",
                (provider, key) => CloudPages.RenderFleet(CloudReports.BuildFleet(provider.GetFleetData(), ServiceLimits.FromEnvironment()), key, provider.Describe()));
    }

    /// <summary>/admin/usage: this month's usage against the free tier.</summary>
    internal class UsageHandler : AdminHandlerBase
    {
        protected override void Process(HttpContext context, ResourceManager resourceManager)
            => CloudPages.Respond(context, "Usage",
                (provider, key) => CloudPages.RenderUsage(CloudReports.BuildUsage(provider.GetUsageData(), ServiceLimits.FromEnvironment()), ServiceLimits.FromEnvironment(), key, provider.Describe()));
    }

    internal static class CloudPages
    {
        private static string N0(double value) => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);
        private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        /// <summary>Runs a page that needs the cloud provider, turning "none" and failures into readable pages.</summary>
        public static void Respond(HttpContext context, string title, Func<ICloudStatusProvider, string, string> render)
        {
            context.Response.ContentType = ContentTypes.Text.Html;
            string key = AdminLinks.KeySuffix(context);
            var provider = CloudStatus.Provider;
            if (provider == null)
            {
                context.Response.Write(Unavailable(title, key,
                    "These numbers come from the cloud platform's monitoring, which this server isn't connected to. " +
                    "They are available when the site runs on Cloud Run (or locally with GCP_ACCESS_TOKEN and GCP_PROJECT_ID set).", null));
                return;
            }
            try
            {
                context.Response.Write(render(provider, key));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 502;
                context.Response.Write(Unavailable(title, key, "The monitoring service could not be queried.", ex.GetBaseException().Message));
            }
        }

        public static string Unavailable(string title, string key, string message, string? detail)
        {
            var page = new StringBuilder();
            StatusPage.Header(page, title, key);
            page.Append("<p>").Append(StatusPage.E(message)).Append("</p>\n");
            if (detail != null)
            {
                page.Append("<pre class=\"note\">").Append(StatusPage.E(detail)).Append("</pre>\n");
                if (detail.Contains("403") || detail.IndexOf("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase) >= 0)
                    page.Append("<p class=\"note\">Permission denied: the service's account needs read access to monitoring. Run <code>./deploy/setup.sh status-access</code>.</p>\n");
            }
            return page.ToString();
        }

        /// <summary>A horizontal bar, amber from 80% and red from 100%.</summary>
        public static string Bar(double percent)
        {
            double width = Math.Max(0, Math.Min(100, percent));
            string css = percent >= 100 ? "bar over" : percent >= 80 ? "bar warn" : "bar";
            return $"<div class=\"{css}\"><i style=\"width:{width.ToString("0.#", CultureInfo.InvariantCulture)}%\"></i></div>";
        }

        public static string Percent(double percent) =>
            percent <= 0 ? "0%" : percent < 1 ? "<1%" : percent < 10 ? percent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : N0(percent) + "%";

        public static string RenderFleet(FleetReport report, string key, string source)
        {
            var page = new StringBuilder();
            StatusPage.Header(page, "Fleet", key);
            page.Append("<p class=\"note\">Every instance of the service, from Cloud Monitoring (a few minutes behind). ")
                .Append("Unlike Status, these numbers survive restarts and scale-to-zero.</p>\n");

            StatusPage.Section(page, "Right now");
            page.Append("<table>\n");
            if (report.LatestSampleUtc == null)
            {
                StatusPage.Row(page, "Instances", "no samples in the last 24 hours");
            }
            else
            {
                StatusPage.Row(page, "Active instances", $"{report.ActiveNow} of at most {report.MaxInstances}", $"sampled {Time(report.LatestSampleUtc.Value)}");
                StatusPage.Row(page, "Idle instances", report.IdleNow, "(started and waiting; they cost nothing while idle with request-based billing)");
            }
            page.Append("</table>\n");

            StatusPage.Section(page, "Peaks");
            page.Append("<table>\n");
            StatusPage.Row(page, "Most instances at once, last 24 hours", report.PeakActive24h,
                report.PeakActive24h > report.MaxInstances ? "(can top the limit while a deploy has two revisions running)" : null);
            StatusPage.Row(page, "Most instances at once, last 30 days", report.PeakActive30d);
            StatusPage.Row(page, "Minutes at the limit, last 24 hours", report.MinutesAtLimit24h,
                report.MinutesAtLimit24h > 0 ? $"(one revision had all {report.MaxInstances} instances busy: requests may have queued)" : null);
            page.Append("</table>\n");

            StatusPage.Section(page, "Requests");
            page.Append("<table>\n");
            StatusPage.Row(page, "Last full hour", N0(report.RequestsLastHour), $"({report.RequestsPerSecondLastHour:0.00} per second)");
            StatusPage.Row(page, "Last 24 hours", N0(report.Requests24h),
                report.RequestsByClass24h.Count == 0 ? null : string.Join(", ", report.RequestsByClass24h.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}: {N0(c.Value)}")));
            page.Append("</table>\n");

            if (report.Hours.Count > 0)
            {
                StatusPage.Section(page, "By hour (UTC)");
                double max = Math.Max(1, report.Hours.Max(h => h.Requests));
                page.Append("<table>\n<tr><th>Hour</th><th class=\"num\">Requests</th><th style=\"width:50%\"></th><th class=\"num\">Peak instances</th></tr>\n");
                foreach (var hour in report.Hours.OrderByDescending(h => h.HourStartUtc))
                {
                    page.Append("<tr><td>").Append(hour.HourStartUtc.ToString("MM-dd HH:00", CultureInfo.InvariantCulture)).Append("</td>")
                        .Append("<td class=\"num\">").Append(N0(hour.Requests)).Append(hour.InProgress ? " <span class=\"note\">so far</span>" : "").Append("</td>")
                        .Append("<td>").Append(Bar(hour.Requests * 100 / max)).Append("</td>")
                        .Append("<td class=\"num\">").Append(hour.PeakActive).Append("</td></tr>\n");
                }
                page.Append("</table>\n");
            }
            page.Append("<p class=\"note\">").Append(StatusPage.E(source)).Append(". As of ").Append(Time(report.AsOfUtc)).Append(".</p>\n");
            return page.ToString();
        }

        public static string RenderUsage(UsageReport report, ServiceLimits limits, string key, string source)
        {
            var page = new StringBuilder();
            StatusPage.Header(page, "Usage", key);
            page.Append("<p class=\"note\">This month against Cloud Run's monthly free tier. The month runs on Pacific time: it began ")
                .Append(Time(report.MonthStartUtc)).Append(" and is ").Append(StatusPage.E(Percent(report.MonthFraction * 100)))
                .Append(" over. Figures lag by a few minutes.</p>\n");

            StatusPage.Section(page, "Free tier");
            page.Append("<table>\n<tr><th>Resource</th><th class=\"num\">Used</th><th class=\"num\">Free per month</th><th style=\"width:24%\"></th><th class=\"num\">Used</th><th class=\"num\">At this pace, month end</th></tr>\n");
            foreach (var m in report.Measures)
            {
                string fmt(double v) => m.Unit == "GiB" ? v.ToString("0.00", CultureInfo.InvariantCulture) : N0(v);
                page.Append("<tr><td>").Append(StatusPage.E(m.Name)).Append(" <span class=\"note\">(").Append(StatusPage.E(m.Unit)).Append(")</span></td>")
                    .Append("<td class=\"num\">").Append(fmt(m.Used)).Append("</td>")
                    .Append("<td class=\"num\">").Append(fmt(m.Free)).Append("</td>")
                    .Append("<td>").Append(Bar(m.PercentUsed)).Append("</td>")
                    .Append("<td class=\"num\">").Append(StatusPage.E(Percent(m.PercentUsed))).Append("</td>")
                    .Append("<td class=\"num\">").Append(StatusPage.E(report.ProjectionIsReliable ? Percent(m.PercentProjected) : "–")).Append("</td></tr>\n");
            }
            page.Append("</table>\n<table>\n");
            StatusPage.Row(page, "Busy instance time",
                $"{StatusPage.FormatDuration(TimeSpan.FromHours(report.BusyHoursUsed))} of {report.BusyHoursFree:0.#} h free",
                $"(at {limits.Vcpu:0.##} vCPU and {limits.MemoryGiB:0.##} GiB per instance)");
            if (report.UsedUp.Count > 0)
                StatusPage.Row(page, "Already used up", string.Join(", ", report.UsedUp), "(usage beyond the free tier is billed)");
            else
                StatusPage.Row(page, "Free tier runs out",
                    report.FreeTierRunsOutUtc is DateTime runsOut ? Time(runsOut) : "not at this pace",
                    report.ProjectionIsReliable ? null : "(too early in the month to tell)");
            page.Append("</table>\n");

            StatusPage.Section(page, "Rough cost beyond the free tier");
            page.Append("<table>\n");
            StatusPage.Row(page, "So far this month", "$" + report.EstimatedDollarsSoFar.ToString("0.00", CultureInfo.InvariantCulture));
            StatusPage.Row(page, "At this pace, month end", "$" + report.ProjectedDollarsMonthEnd.ToString("0.00", CultureInfo.InvariantCulture));
            page.Append("</table>\n<p class=\"note\">An estimate from list prices written into the code ($")
                .Append(FreeTier.DollarsPerVcpuSecond.ToString("0.000000", CultureInfo.InvariantCulture)).Append(" per vCPU-second, $")
                .Append(FreeTier.DollarsPerGiBSecond.ToString("0.0000000", CultureInfo.InvariantCulture)).Append(" per GiB-second, $")
                .Append(FreeTier.DollarsPerMillionRequests.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" per million requests), which may be out of date. It leaves out egress charges and the extra CPU used during startup boost. ")
                .Append("The real figure is in Google's billing report.</p>\n");

            if (report.BillableSecondsPerDay.Count > 0)
            {
                StatusPage.Section(page, "Busy instance time by day (last 30 days)", "Days with no activity are left out.");
                double max = Math.Max(1, report.BillableSecondsPerDay.Max(p => p.Value));
                page.Append("<table>\n<tr><th>Day (UTC)</th><th class=\"num\">Busy time</th><th style=\"width:60%\"></th></tr>\n");
                foreach (var day in report.BillableSecondsPerDay.OrderByDescending(p => p.Time).Take(31))
                {
                    page.Append("<tr><td>").Append(day.Time.AddSeconds(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</td>")
                        .Append("<td class=\"num\">").Append(StatusPage.E(StatusPage.FormatDuration(TimeSpan.FromSeconds(day.Value)))).Append("</td>")
                        .Append("<td>").Append(Bar(day.Value * 100 / max)).Append("</td></tr>\n");
                }
                page.Append("</table>\n");
            }
            page.Append("<p class=\"note\">").Append(StatusPage.E(source)).Append(". As of ").Append(Time(report.AsOfUtc)).Append(".</p>\n");
            return page.ToString();
        }
    }
}
