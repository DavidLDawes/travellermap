#nullable enable
using Maps.Web;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Maps.Admin
{
    /// <summary>The newest Cloud Billing budget notification the cost-alert responder received.</summary>
    internal sealed class BudgetNotification
    {
        public DateTime ReceivedUtc { get; set; }
        public string BudgetName { get; set; } = "";
        public double CostAmount { get; set; }
        public double BudgetAmount { get; set; }
        public string Currency { get; set; } = "USD";
        public DateTime? CostIntervalStartUtc { get; set; }
    }

    /// <summary>A budget threshold the responder announced, and when.</summary>
    internal sealed class Announcement
    {
        /// <summary>For example "2026-10-budget-actual-50".</summary>
        public string Key { get; set; } = "";
        public DateTime TimeUtc { get; set; }
    }

    internal sealed class PolicyInfo
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public string Severity { get; set; } = "";
    }

    internal sealed class IncidentInfo
    {
        public string Policy { get; set; } = "";
        public DateTime OpenedUtc { get; set; }
        public string Severity { get; set; } = "";
    }

    internal sealed class LastAlertInfo
    {
        public DateTime TimeUtc { get; set; }
        public string Title { get; set; } = "";
        public int Priority { get; set; }
        public bool Drill { get; set; }
        public bool CutOff { get; set; }
    }

    /// <summary>Everything /admin/budget reads from the platform. A source that failed is named in Problems and left empty.</summary>
    internal sealed class BudgetData
    {
        public DateTime AsOfUtc { get; set; }
        public BudgetNotification? Latest { get; set; }
        public List<Announcement> Announcements { get; set; } = new List<Announcement>();
        /// <summary>Whether anyone can reach the site (the cost cutoff removes this); null if it could not be read.</summary>
        public bool? SitePublic { get; set; }
        public List<PolicyInfo> Policies { get; set; } = new List<PolicyInfo>();
        public List<IncidentInfo> OpenIncidents { get; set; } = new List<IncidentInfo>();
        public LastAlertInfo? LastAlert { get; set; }
        public List<string> Problems { get; set; } = new List<string>();
    }

    internal sealed class BudgetReport
    {
        public DateTime AsOfUtc { get; set; }
        public string Currency { get; set; } = "USD";
        public double? BudgetAmount { get; set; }
        /// <summary>Where the budget figure came from: Google's notification, or the deployed configuration.</summary>
        public string BudgetSource { get; set; } = "";
        public double? SpendSoFar { get; set; }
        public DateTime? SpendAsOfUtc { get; set; }
        public double? SpendPercent { get; set; }
        /// <summary>The newest notification is from an earlier month, so it says nothing about this one.</summary>
        public bool NotificationIsStale { get; set; }
        public double? EstimatedFromMetrics { get; set; }
        public List<ThresholdRow> Thresholds { get; set; } = new List<ThresholdRow>();
        public bool? SitePublic { get; set; }
        public List<PolicyInfo> Policies { get; set; } = new List<PolicyInfo>();
        public List<IncidentInfo> OpenIncidents { get; set; } = new List<IncidentInfo>();
        public LastAlertInfo? LastAlert { get; set; }
        public List<string> Problems { get; set; } = new List<string>();

        internal sealed class ThresholdRow
        {
            public string Label { get; set; } = "";
            public string Response { get; set; } = "";
            public DateTime? CrossedUtc { get; set; }
        }
    }

    internal static class BudgetReports
    {
        // The thresholds `deploy/setup.sh budget` creates, and what the responder does at each (see deploy/alert-function).
        private static readonly (string Key, string Label, string Response)[] Thresholds =
        {
            ("actual-25", "25% of the budget spent", "Pushover, normal priority"),
            ("actual-50", "50% spent", "Pushover, high priority"),
            ("actual-90", "90% spent", "Pushover, high priority"),
            ("actual-100", "100% spent", "Pushover, emergency (repeats until acknowledged), and the site is cut off"),
            ("forecast-100", "Forecast to reach 100%", "Pushover, high priority"),
        };

        public static string PriorityName(int priority) => priority switch
        {
            2 => "emergency",
            1 => "high",
            0 => "normal",
            _ => "low",
        };

        public static BudgetReport Build(BudgetData data, UsageReport? usage, double configuredBudget)
        {
            string month = CloudReports.BillingMonthStartUtc(data.AsOfUtc).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var report = new BudgetReport
            {
                AsOfUtc = data.AsOfUtc,
                SitePublic = data.SitePublic,
                Policies = data.Policies.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
                OpenIncidents = data.OpenIncidents.OrderByDescending(i => i.OpenedUtc).ToList(),
                LastAlert = data.LastAlert,
                Problems = data.Problems,
                EstimatedFromMetrics = usage?.EstimatedDollarsSoFar,
            };

            var latest = data.Latest;
            if (latest != null)
            {
                report.Currency = latest.Currency;
                report.BudgetAmount = latest.BudgetAmount;
                report.BudgetSource = "Google's latest budget notification";
                report.SpendAsOfUtc = latest.ReceivedUtc;
                bool current = latest.CostIntervalStartUtc?.ToString("yyyy-MM", CultureInfo.InvariantCulture) == month;
                report.NotificationIsStale = !current;
                if (current)
                {
                    report.SpendSoFar = latest.CostAmount;
                    report.SpendPercent = latest.BudgetAmount > 0 ? latest.CostAmount * 100 / latest.BudgetAmount : (double?)null;
                }
            }
            else if (configuredBudget > 0)
            {
                report.BudgetAmount = configuredBudget;
                report.BudgetSource = "the deployed configuration (BUDGET_USD)";
            }

            foreach (var t in Thresholds)
            {
                string key = $"{month}-budget-{t.Key}";
                var seen = data.Announcements.FirstOrDefault(a => a.Key == key);
                report.Thresholds.Add(new BudgetReport.ThresholdRow
                {
                    Label = t.Label,
                    Response = t.Response,
                    CrossedUtc = seen?.TimeUtc,
                });
            }
            return report;
        }
    }

    /// <summary>/admin/budget: the money side: spend against the budget, what the alarms are doing, and the kill switch.</summary>
    internal class BudgetHandler : AdminHandlerBase
    {
        protected override void Process(HttpContext context, ResourceManager resourceManager)
            => CloudPages.Respond(context, "Budget", (provider, key) =>
            {
                var data = provider.GetBudgetData();
                UsageReport? usage = null;
                try { usage = CloudReports.BuildUsage(provider.GetUsageData(), ServiceLimits.FromEnvironment()); }
                catch (Exception ex) { data.Problems.Add("Usage estimate: " + ex.GetBaseException().Message); }
                double.TryParse(Environment.GetEnvironmentVariable("BUDGET_USD"), NumberStyles.Float, CultureInfo.InvariantCulture, out double configured);
                return BudgetPages.Render(BudgetReports.Build(data, usage, configured), key, provider.Describe());
            });
    }

    internal static class BudgetPages
    {
        private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        private static string Money(double amount, string currency) =>
            (currency == "USD" ? "$" : "") + amount.ToString("0.00", CultureInfo.InvariantCulture) + (currency == "USD" ? "" : " " + currency);

        public static string Render(BudgetReport report, string key, string source)
        {
            var page = new StringBuilder();
            StatusPage.Header(page, "Budget", key);
            page.Append("<p class=\"note\">The money side: what Google says has been spent, what the alarms have done, and whether the cost cutoff has fired.</p>\n");

            StatusPage.Section(page, "This month");
            page.Append("<table>\n");
            if (report.BudgetAmount != null)
                StatusPage.Row(page, "Budget", Money(report.BudgetAmount.Value, report.Currency) + " per month", "(from " + report.BudgetSource + ")");
            else
                StatusPage.Row(page, "Budget", "unknown", "(no notification yet and no BUDGET_USD configured)");

            if (report.SpendSoFar != null)
            {
                StatusPage.Row(page, "Spent so far", Money(report.SpendSoFar.Value, report.Currency)
                    + (report.SpendPercent != null ? " (" + CloudPages.Percent(report.SpendPercent.Value) + " of the budget)" : ""),
                    "as of " + (report.SpendAsOfUtc is DateTime at ? Time(at) : "?") + "; Google's figures lag by hours");
            }
            else if (report.NotificationIsStale && report.SpendAsOfUtc is DateTime old)
            {
                StatusPage.Row(page, "Spent so far", "no notification for this month yet", "(the last one arrived " + Time(old) + ", for an earlier month)");
            }
            else
            {
                StatusPage.Row(page, "Spent so far", "no budget notification received yet",
                    "(Google sends them as spend changes, so there may be none while usage is inside the free tier)");
            }
            if (report.EstimatedFromMetrics != null)
                StatusPage.Row(page, "Estimated from usage", Money(report.EstimatedFromMetrics.Value, "USD"), "(rough, from list prices; see Usage)");
            page.Append("</table>\n");
            if (report.SpendPercent != null)
                page.Append(CloudPages.Bar(report.SpendPercent.Value)).Append('\n');

            StatusPage.Section(page, "Budget alerts this month", "Each threshold is announced once a month.");
            page.Append("<table>\n<tr><th>When</th><th>What happens</th><th>Status</th></tr>\n");
            foreach (var t in report.Thresholds)
            {
                page.Append("<tr><td>").Append(StatusPage.E(t.Label)).Append("</td><td>").Append(StatusPage.E(t.Response)).Append("</td><td>")
                    .Append(t.CrossedUtc is DateTime crossed ? "reached " + StatusPage.E(Time(crossed)) : "<span class=\"note\">not reached</span>")
                    .Append("</td></tr>\n");
            }
            page.Append("</table>\n");

            StatusPage.Section(page, "Cost cutoff");
            page.Append("<table>\n");
            if (report.SitePublic == null)
                StatusPage.Row(page, "Site access", "unknown", "(see the problems below)");
            else if (report.SitePublic == true)
                StatusPage.Row(page, "Site access", "public", "(the cutoff has not fired, or access was restored)");
            else
                StatusPage.Row(page, "Site access", "CUT OFF: visitors get 403",
                    "restore with ./deploy/setup.sh restore (see deploy/README.md)");
            page.Append("</table>\n");

            StatusPage.Section(page, "Alerts watching the site");
            page.Append("<table>\n");
            if (report.OpenIncidents.Count == 0)
                StatusPage.Row(page, "Open incidents", "none");
            foreach (var incident in report.OpenIncidents)
                StatusPage.Row(page, "OPEN: " + incident.Policy, "since " + Time(incident.OpenedUtc), incident.Severity.Length > 0 ? "(" + incident.Severity + ")" : null);
            page.Append("</table>\n");
            if (report.Policies.Count > 0)
            {
                page.Append("<table>\n<tr><th>Policy</th><th>Severity</th><th>State</th></tr>\n");
                foreach (var p in report.Policies)
                    page.Append("<tr><td>").Append(StatusPage.E(p.Name)).Append("</td><td>").Append(StatusPage.E(p.Severity.Length > 0 ? p.Severity : "–"))
                        .Append("</td><td>").Append(p.Enabled ? "enabled" : "<b>disabled</b>").Append("</td></tr>\n");
                page.Append("</table>\n");
            }

            StatusPage.Section(page, "Last alert sent to your phone");
            page.Append("<table>\n");
            if (report.LastAlert is LastAlertInfo last)
            {
                StatusPage.Row(page, "When", Time(last.TimeUtc));
                StatusPage.Row(page, "Message", last.Title, "(" + BudgetReports.PriorityName(last.Priority) + " priority"
                    + (last.Drill ? ", a drill" : "") + (last.CutOff ? ", the site was cut off" : "") + ")");
            }
            else
                StatusPage.Row(page, "Last alert", "none recorded yet");
            page.Append("</table>\n");

            if (report.Problems.Count > 0)
            {
                StatusPage.Section(page, "Could not read");
                page.Append("<ul class=\"note\">\n");
                foreach (string problem in report.Problems)
                    page.Append("<li>").Append(StatusPage.E(problem)).Append("</li>\n");
                page.Append("</ul>\n");
                if (report.Problems.Any(p => p.Contains("403") || p.IndexOf("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase) >= 0))
                    page.Append("<p class=\"note\">Permission denied: run <code>./deploy/setup.sh status-access</code>.</p>\n");
            }
            page.Append("<p class=\"note\">").Append(StatusPage.E(source)).Append(". As of ").Append(Time(report.AsOfUtc)).Append(".</p>\n");
            return page.ToString();
        }
    }
}
