#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Maps.Admin
{
    /// <summary>One sample of a metric: its end time (UTC) and value.</summary>
    internal sealed class MetricPoint
    {
        public MetricPoint(DateTime time, double value) { Time = time; Value = value; }
        public DateTime Time { get; }
        public double Value { get; }
    }

    /// <summary>
    /// Where /admin/fleet and /admin/usage get their numbers: the cloud platform's own metrics (on Cloud
    /// Run, Cloud Monitoring), which see every instance and survive restarts, unlike the per-process
    /// counters on /admin/status. The ASP.NET Core host registers an implementation when it runs on
    /// Cloud Run; elsewhere there is none and the pages say so. Calls may block and may throw.
    /// </summary>
    internal interface ICloudStatusProvider
    {
        /// <summary>Which project and service the numbers are for, for the page footer.</summary>
        string Describe();
        FleetData GetFleetData();
        UsageData GetUsageData();
    }

    internal static class CloudStatus
    {
        public static ICloudStatusProvider? Provider { get; set; }
    }

    /// <summary>What the platform measured recently, as it measured it.</summary>
    internal sealed class FleetData
    {
        public DateTime AsOfUtc { get; set; }
        /// <summary>Active instances per minute over the last 24 hours (ascending).</summary>
        public List<MetricPoint> ActiveInstancesPerMinute { get; set; } = new List<MetricPoint>();
        /// <summary>Idle (started, waiting) instances per minute over the last 24 hours.</summary>
        public List<MetricPoint> IdleInstancesPerMinute { get; set; } = new List<MetricPoint>();
        /// <summary>
        /// Per minute over the last 24 hours, the active instances of the busiest single revision. The
        /// instance limit applies per revision, while the totals above add up all revisions (two
        /// overlap while a deploy rolls out).
        /// </summary>
        public List<MetricPoint> BusiestRevisionActivePerMinute { get; set; } = new List<MetricPoint>();
        /// <summary>The most active instances (all revisions) in each hour over the last 30 days.</summary>
        public List<MetricPoint> PeakActiveInstancesPerHour { get; set; } = new List<MetricPoint>();
        /// <summary>Requests per hour over the last 24 hours, by HTTP status class ("2xx", "4xx", ...).</summary>
        public Dictionary<string, List<MetricPoint>> RequestsPerHourByClass { get; set; } = new Dictionary<string, List<MetricPoint>>();
    }

    /// <summary>Totals since the start of the billing month.</summary>
    internal sealed class UsageData
    {
        public DateTime MonthStartUtc { get; set; }
        public DateTime AsOfUtc { get; set; }
        public double BillableInstanceSeconds { get; set; }
        public double Requests { get; set; }
        public double EgressBytes { get; set; }
        /// <summary>Busy instance seconds per day of the last 30 days, for the daily table.</summary>
        public List<MetricPoint> BillableSecondsPerDay { get; set; } = new List<MetricPoint>();
    }

    /// <summary>The service's configuration, which the metrics are measured against.</summary>
    internal sealed class ServiceLimits
    {
        public double Vcpu { get; set; } = 1;
        public double MemoryGiB { get; set; } = 1;
        public int MaxInstances { get; set; } = 2;

        public static ServiceLimits FromEnvironment()
        {
            static double Number(string name, double fallback) =>
                double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 ? v : fallback;
            return new ServiceLimits
            {
                Vcpu = Number("CLOUD_RUN_VCPU", 1),
                MemoryGiB = Number("CLOUD_RUN_MEMORY_GIB", 1),
                MaxInstances = (int)Number("CLOUD_RUN_MAX_INSTANCES", 2),
            };
        }
    }

    /// <summary>
    /// Cloud Run's monthly free amounts (request-based billing, us-central1) and the list prices
    /// beyond them. The free amounts were confirmed against Google's pricing in October 2026; the
    /// prices are from memory and only feed a rough estimate.
    /// </summary>
    internal static class FreeTier
    {
        public const double VcpuSeconds = 180000;
        public const double GiBSeconds = 360000;
        public const double Requests = 2000000;
        public const double EgressGiB = 1;

        public const double DollarsPerVcpuSecond = 0.000024;
        public const double DollarsPerGiBSecond = 0.0000025;
        public const double DollarsPerMillionRequests = 0.40;
    }

    internal sealed class FleetReport
    {
        public DateTime AsOfUtc { get; set; }
        public int MaxInstances { get; set; }
        public DateTime? LatestSampleUtc { get; set; }
        public int ActiveNow { get; set; }
        public int IdleNow { get; set; }
        public int PeakActive24h { get; set; }
        public int PeakActive30d { get; set; }
        /// <summary>Minutes in which one revision had as many active instances as the limit allows.</summary>
        public int MinutesAtLimit24h { get; set; }
        public double Requests24h { get; set; }
        public double RequestsLastHour { get; set; }
        public Dictionary<string, double> RequestsByClass24h { get; set; } = new Dictionary<string, double>();
        public List<HourRow> Hours { get; set; } = new List<HourRow>();

        public double RequestsPerSecondLastHour => RequestsLastHour / 3600;

        internal sealed class HourRow
        {
            public DateTime HourStartUtc { get; set; }
            public double Requests { get; set; }
            public int PeakActive { get; set; }
            /// <summary>The hour that contains "as of": still filling.</summary>
            public bool InProgress { get; set; }
        }
    }

    internal sealed class UsageReport
    {
        public DateTime MonthStartUtc { get; set; }
        public DateTime AsOfUtc { get; set; }
        public double MonthFraction { get; set; }
        /// <summary>False in the first day of the month, when "at this pace" would mean little.</summary>
        public bool ProjectionIsReliable { get; set; }
        public List<Measure> Measures { get; set; } = new List<Measure>();
        public double BusyHoursUsed { get; set; }
        public double BusyHoursFree { get; set; }
        public double EstimatedDollarsSoFar { get; set; }
        public double ProjectedDollarsMonthEnd { get; set; }
        /// <summary>When the first allowance would run out at the pace so far, if before month end and none is used up already.</summary>
        public DateTime? FreeTierRunsOutUtc { get; set; }
        /// <summary>Allowances already used up this month.</summary>
        public List<string> UsedUp { get; set; } = new List<string>();
        public List<MetricPoint> BillableSecondsPerDay { get; set; } = new List<MetricPoint>();

        internal sealed class Measure
        {
            public string Name { get; set; } = "";
            public string Unit { get; set; } = "";
            public double Used { get; set; }
            public double Free { get; set; }
            public double Projected { get; set; }
            public double PercentUsed => Free <= 0 ? 0 : Used * 100 / Free;
            public double PercentProjected => Free <= 0 ? 0 : Projected * 100 / Free;
        }
    }

    internal static class CloudReports
    {
        private static readonly TimeSpan Pst = TimeSpan.FromHours(-8);
        private static readonly TimeSpan Pdt = TimeSpan.FromHours(-7);

        private static DateTime FirstSunday(int year, int month)
        {
            var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            return first.AddDays(((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7);
        }

        /// <summary>
        /// Pacific time's offset from UTC at an instant, by the US rules in force since 2007: daylight
        /// time runs from 2:00 on the second Sunday of March to 2:00 on the first Sunday of November.
        /// Computed rather than read from the system time zone database, because the Linux container
        /// has none and both hosts must give the same answer.
        /// </summary>
        public static TimeSpan PacificOffset(DateTime utc)
        {
            DateTime starts = FirstSunday(utc.Year, 3).AddDays(7).AddHours(2).Subtract(Pst);  // 2:00 PST
            DateTime ends = FirstSunday(utc.Year, 11).AddHours(2).Subtract(Pdt);              // 2:00 PDT
            return utc >= starts && utc < ends ? Pdt : Pst;
        }

        /// <summary>
        /// Midnight at the start of the current month in Pacific time, in UTC: Google bills (and
        /// resets the free tier) by Pacific time.
        /// </summary>
        public static DateTime BillingMonthStartUtc(DateTime nowUtc)
        {
            DateTime local = nowUtc + PacificOffset(nowUtc);
            var wallMidnight = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            // Midnight on the 1st is never inside a clock change (those are at 2:00 on a Sunday).
            DateTime ifDaylight = wallMidnight - Pdt;
            return PacificOffset(ifDaylight) == Pdt ? ifDaylight : wallMidnight - Pst;
        }

        public static FleetReport BuildFleet(FleetData data, ServiceLimits limits)
        {
            var active = data.ActiveInstancesPerMinute.OrderBy(p => p.Time).ToList();
            var idle = data.IdleInstancesPerMinute.OrderBy(p => p.Time).ToList();
            var report = new FleetReport { AsOfUtc = data.AsOfUtc, MaxInstances = limits.MaxInstances };

            if (active.Count > 0)
            {
                report.LatestSampleUtc = active[active.Count - 1].Time;
                report.ActiveNow = (int)Math.Round(active[active.Count - 1].Value);
                report.PeakActive24h = (int)Math.Round(active.Max(p => p.Value));
            }
            // The limit is per revision; fall back to the totals if the per-revision series is missing.
            var perRevision = data.BusiestRevisionActivePerMinute.Count > 0 ? data.BusiestRevisionActivePerMinute : active;
            report.MinutesAtLimit24h = perRevision.Count(p => Math.Round(p.Value) >= limits.MaxInstances);
            if (idle.Count > 0)
                report.IdleNow = (int)Math.Round(idle[idle.Count - 1].Value);
            if (data.PeakActiveInstancesPerHour.Count > 0)
                report.PeakActive30d = (int)Math.Round(data.PeakActiveInstancesPerHour.Max(p => p.Value));
            report.PeakActive30d = Math.Max(report.PeakActive30d, report.PeakActive24h);

            // Requests: one row per hour of the last 24 (oldest first), summed across status classes.
            var perHour = new SortedDictionary<DateTime, double>();
            foreach (var byClass in data.RequestsPerHourByClass)
            {
                double total = 0;
                foreach (var p in byClass.Value)
                {
                    total += p.Value;
                    perHour[p.Time] = (perHour.TryGetValue(p.Time, out double existing) ? existing : 0) + p.Value;
                }
                report.RequestsByClass24h[byClass.Key] = total;
            }
            report.Requests24h = perHour.Values.Sum();
            // Buckets end on the hour, so the last full hour is the one that ended at the top of the current hour.
            DateTime currentHourStart = new DateTime(data.AsOfUtc.Year, data.AsOfUtc.Month, data.AsOfUtc.Day, data.AsOfUtc.Hour, 0, 0, DateTimeKind.Utc);
            report.RequestsLastHour = perHour.TryGetValue(currentHourStart, out double lastFull) ? lastFull : 0;

            var peakByHour = new Dictionary<DateTime, int>();
            foreach (var p in active)
            {
                DateTime hour = new DateTime(p.Time.Year, p.Time.Month, p.Time.Day, p.Time.Hour, 0, 0, DateTimeKind.Utc);
                int v = (int)Math.Round(p.Value);
                peakByHour[hour] = peakByHour.TryGetValue(hour, out int seen) ? Math.Max(seen, v) : v;
            }
            foreach (var bucket in perHour)
            {
                DateTime start = bucket.Key.AddHours(-1);
                DateTime hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
                report.Hours.Add(new FleetReport.HourRow
                {
                    HourStartUtc = hour,
                    InProgress = hour == currentHourStart,
                    Requests = bucket.Value,
                    PeakActive = peakByHour.TryGetValue(hour, out int peak) ? peak : 0,
                });
            }
            return report;
        }

        public static UsageReport BuildUsage(UsageData data, ServiceLimits limits)
        {
            double elapsedSeconds = Math.Max(1, (data.AsOfUtc - data.MonthStartUtc).TotalSeconds);
            DateTime nextMonthStart = BillingMonthStartUtc(data.MonthStartUtc.AddDays(45));
            double monthSeconds = (nextMonthStart - data.MonthStartUtc).TotalSeconds;
            double fraction = Math.Min(1, elapsedSeconds / monthSeconds);
            // Too little of the month has passed for a pace to mean anything.
            bool canProject = elapsedSeconds >= 86400;
            double Project(double used) => canProject ? used / fraction : used;

            double vcpuSeconds = data.BillableInstanceSeconds * limits.Vcpu;
            double gibSeconds = data.BillableInstanceSeconds * limits.MemoryGiB;
            double egressGiB = data.EgressBytes / (1024.0 * 1024 * 1024);

            var report = new UsageReport
            {
                MonthStartUtc = data.MonthStartUtc,
                AsOfUtc = data.AsOfUtc,
                MonthFraction = fraction,
                ProjectionIsReliable = canProject,
                BillableSecondsPerDay = data.BillableSecondsPerDay,
            };
            report.Measures.Add(new UsageReport.Measure { Name = "vCPU time", Unit = "vCPU-seconds", Used = vcpuSeconds, Free = FreeTier.VcpuSeconds, Projected = Project(vcpuSeconds) });
            report.Measures.Add(new UsageReport.Measure { Name = "Memory time", Unit = "GiB-seconds", Used = gibSeconds, Free = FreeTier.GiBSeconds, Projected = Project(gibSeconds) });
            report.Measures.Add(new UsageReport.Measure { Name = "Requests", Unit = "requests", Used = data.Requests, Free = FreeTier.Requests, Projected = Project(data.Requests) });
            report.Measures.Add(new UsageReport.Measure { Name = "Egress", Unit = "GiB", Used = egressGiB, Free = FreeTier.EgressGiB, Projected = Project(egressGiB) });

            // "Busy hours" is the tightest of the two time allowances, in hours of one running instance.
            double busyFreeSeconds = Math.Min(FreeTier.VcpuSeconds / limits.Vcpu, FreeTier.GiBSeconds / limits.MemoryGiB);
            report.BusyHoursFree = busyFreeSeconds / 3600;
            report.BusyHoursUsed = data.BillableInstanceSeconds / 3600;

            report.EstimatedDollarsSoFar = Cost(vcpuSeconds, gibSeconds, data.Requests);
            report.ProjectedDollarsMonthEnd = Cost(Project(vcpuSeconds), Project(gibSeconds), Project(data.Requests));

            report.UsedUp = report.Measures.Where(m => m.Used >= m.Free).Select(m => m.Name).ToList();

            // When the first allowance would run out at the pace so far (if it is going to before month end).
            if (canProject && report.UsedUp.Count == 0)
            {
                double? soonest = null;
                foreach (var m in report.Measures)
                {
                    if (m.Used <= 0) continue;
                    double secondsToExhaust = (m.Free - m.Used) / (m.Used / elapsedSeconds);
                    if (soonest == null || secondsToExhaust < soonest) soonest = secondsToExhaust;
                }
                if (soonest != null && data.AsOfUtc.AddSeconds(soonest.Value) < nextMonthStart)
                    report.FreeTierRunsOutUtc = data.AsOfUtc.AddSeconds(soonest.Value);
            }
            return report;
        }

        private static double Cost(double vcpuSeconds, double gibSeconds, double requests) =>
            Math.Max(0, vcpuSeconds - FreeTier.VcpuSeconds) * FreeTier.DollarsPerVcpuSecond +
            Math.Max(0, gibSeconds - FreeTier.GiBSeconds) * FreeTier.DollarsPerGiBSecond +
            Math.Max(0, requests - FreeTier.Requests) / 1000000.0 * FreeTier.DollarsPerMillionRequests;
    }
}
