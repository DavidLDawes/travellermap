#nullable enable
using Maps.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests
{
    [TestClass]
    public class CloudStatusTest
    {
        private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new DateTime(y, m, d, h, min, 0, DateTimeKind.Utc);

        private static List<MetricPoint> Minutes(DateTime start, params double[] values)
            => values.Select((v, i) => new MetricPoint(start.AddMinutes(i), v)).ToList();

        private static readonly ServiceLimits OneCpu = new ServiceLimits { Vcpu = 1, MemoryGiB = 1, MaxInstances = 2 };

        // Billing month ---------------------------------------------------------

        [TestMethod]
        public void BillingMonthStartsAtMidnightPacific()
        {
            Assert.AreEqual(Utc(2026, 10, 1, 7), CloudReports.BillingMonthStartUtc(Utc(2026, 10, 4, 10)), "PDT is UTC-7");
            Assert.AreEqual(Utc(2026, 11, 1, 7), CloudReports.BillingMonthStartUtc(Utc(2026, 11, 15)), "DST ends at 2am on 1 Nov, after midnight");
            Assert.AreEqual(Utc(2026, 12, 1, 8), CloudReports.BillingMonthStartUtc(Utc(2026, 12, 3)), "PST is UTC-8");
            Assert.AreEqual(Utc(2026, 12, 1, 8), CloudReports.BillingMonthStartUtc(Utc(2027, 1, 1, 0, 30)), "00:30 UTC on 1 Jan is still 31 Dec in Pacific time");
            Assert.AreEqual(Utc(2026, 3, 1, 8), CloudReports.BillingMonthStartUtc(Utc(2026, 3, 20)), "DST starts on 8 Mar, after the 1st");
            Assert.AreEqual(Utc(2026, 4, 1, 7), CloudReports.BillingMonthStartUtc(Utc(2026, 4, 2)));
            Assert.AreEqual(Utc(2027, 11, 1, 7), CloudReports.BillingMonthStartUtc(Utc(2027, 11, 20)), "in 2027 DST lasts until 7 Nov");
            Assert.AreEqual(Utc(2027, 12, 1, 8), CloudReports.BillingMonthStartUtc(Utc(2027, 12, 20)));
        }

        [TestMethod]
        public void PacificOffsetChangesAtTwoInTheMorningOnTheRightSundays()
        {
            // 2026: DST starts Sunday 8 March at 2:00 PST (10:00 UTC) and ends Sunday 1 November at 2:00 PDT (09:00 UTC).
            Assert.AreEqual(TimeSpan.FromHours(-8), CloudReports.PacificOffset(Utc(2026, 3, 8, 9, 59)));
            Assert.AreEqual(TimeSpan.FromHours(-7), CloudReports.PacificOffset(Utc(2026, 3, 8, 10, 0)));
            Assert.AreEqual(TimeSpan.FromHours(-7), CloudReports.PacificOffset(Utc(2026, 11, 1, 8, 59)));
            Assert.AreEqual(TimeSpan.FromHours(-8), CloudReports.PacificOffset(Utc(2026, 11, 1, 9, 0)));
            Assert.AreEqual(TimeSpan.FromHours(-8), CloudReports.PacificOffset(Utc(2026, 1, 15)));
            Assert.AreEqual(TimeSpan.FromHours(-7), CloudReports.PacificOffset(Utc(2026, 7, 4)));
        }

        [TestMethod]
        public void PacificOffsetAgreesWithTheSystemTimeZoneWhereThereIsOne()
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch (TimeZoneNotFoundException) { try { zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch (TimeZoneNotFoundException) { Assert.Inconclusive("No Pacific time zone on this machine."); return; } }

            // Every 6 hours for 2026 and 2027.
            for (DateTime t = Utc(2026, 1, 1); t < Utc(2028, 1, 1); t = t.AddHours(6))
                Assert.AreEqual(zone.GetUtcOffset(t), CloudReports.PacificOffset(t), t.ToString("u"));
        }

        // Fleet ------------------------------------------------------------------

        [TestMethod]
        public void MinutesAtLimitAreCountedPerRevision()
        {
            DateTime t = Utc(2026, 10, 4, 9);
            var data = new FleetData
            {
                AsOfUtc = t.AddMinutes(10),
                // Two revisions overlap during a deploy: 2 instances in total, but never 2 in one revision.
                ActiveInstancesPerMinute = Minutes(t, 0, 1, 2, 2, 1),
                BusiestRevisionActivePerMinute = Minutes(t, 0, 1, 1, 1, 1),
            };
            var report = CloudReports.BuildFleet(data, OneCpu);
            Assert.AreEqual(0, report.MinutesAtLimit24h);
            Assert.AreEqual(2, report.PeakActive24h, "the total across revisions is still reported");

            data.BusiestRevisionActivePerMinute = Minutes(t, 0, 1, 2, 2, 1);
            Assert.AreEqual(2, CloudReports.BuildFleet(data, OneCpu).MinutesAtLimit24h);

            data.BusiestRevisionActivePerMinute = new List<MetricPoint>();
            Assert.AreEqual(2, CloudReports.BuildFleet(data, OneCpu).MinutesAtLimit24h, "falls back to the totals");
        }

        [TestMethod]
        public void FleetShowsTheLatestInstanceCounts()
        {
            DateTime t = Utc(2026, 10, 4, 9);
            var report = CloudReports.BuildFleet(new FleetData
            {
                AsOfUtc = t.AddMinutes(10),
                ActiveInstancesPerMinute = Minutes(t, 0, 2, 1),
                IdleInstancesPerMinute = Minutes(t, 1, 0, 1),
                PeakActiveInstancesPerHour = new List<MetricPoint> { new MetricPoint(t, 1), new MetricPoint(t.AddHours(1), 4) },
            }, OneCpu);

            Assert.AreEqual(1, report.ActiveNow);
            Assert.AreEqual(1, report.IdleNow);
            Assert.AreEqual(t.AddMinutes(2), report.LatestSampleUtc);
            Assert.AreEqual(2, report.PeakActive24h);
            Assert.AreEqual(4, report.PeakActive30d);
        }

        [TestMethod]
        public void PeakOverThirtyDaysIsNeverBelowThePeakOverADay()
        {
            DateTime t = Utc(2026, 10, 4, 9);
            var report = CloudReports.BuildFleet(new FleetData { AsOfUtc = t, ActiveInstancesPerMinute = Minutes(t, 3) }, OneCpu);
            Assert.AreEqual(3, report.PeakActive30d);
        }

        [TestMethod]
        public void LastFullHourIsTheOneBeforeTheCurrentHour()
        {
            // Hour buckets are labelled by their end. At 10:30 the bucket ending 11:00 is still filling.
            var requests = new Dictionary<string, List<MetricPoint>>
            {
                ["2xx"] = new List<MetricPoint> { new MetricPoint(Utc(2026, 10, 4, 9), 40), new MetricPoint(Utc(2026, 10, 4, 10), 90), new MetricPoint(Utc(2026, 10, 4, 11), 5) },
                ["4xx"] = new List<MetricPoint> { new MetricPoint(Utc(2026, 10, 4, 10), 10) },
            };
            var report = CloudReports.BuildFleet(new FleetData { AsOfUtc = Utc(2026, 10, 4, 10, 30), RequestsPerHourByClass = requests }, OneCpu);

            Assert.AreEqual(100, report.RequestsLastHour, "the bucket that ended at 10:00 covers 09:00-10:00, the last full hour at 10:30");
        }

        [TestMethod]
        public void RequestsAreTotaledByHourAndClass()
        {
            var requests = new Dictionary<string, List<MetricPoint>>
            {
                ["2xx"] = new List<MetricPoint> { new MetricPoint(Utc(2026, 10, 4, 10), 90), new MetricPoint(Utc(2026, 10, 4, 11), 5) },
                ["4xx"] = new List<MetricPoint> { new MetricPoint(Utc(2026, 10, 4, 10), 10) },
            };
            var report = CloudReports.BuildFleet(new FleetData { AsOfUtc = Utc(2026, 10, 4, 10, 30), RequestsPerHourByClass = requests }, OneCpu);

            Assert.AreEqual(105, report.Requests24h);
            Assert.AreEqual(95, report.RequestsByClass24h["2xx"]);
            Assert.AreEqual(10, report.RequestsByClass24h["4xx"]);
            Assert.AreEqual(2, report.Hours.Count);
            Assert.AreEqual(Utc(2026, 10, 4, 9), report.Hours[0].HourStartUtc);
            Assert.AreEqual(100, report.Hours[0].Requests);
            Assert.IsFalse(report.Hours[0].InProgress);
            Assert.IsTrue(report.Hours[1].InProgress, "the hour containing 'as of' is still filling");
        }

        [TestMethod]
        public void NoDataMeansZerosNotErrors()
        {
            var report = CloudReports.BuildFleet(new FleetData { AsOfUtc = Utc(2026, 10, 4) }, OneCpu);
            Assert.IsNull(report.LatestSampleUtc);
            Assert.AreEqual(0, report.ActiveNow);
            Assert.AreEqual(0, report.Requests24h);
            Assert.AreEqual(0, report.Hours.Count);
        }

        // Usage ------------------------------------------------------------------

        private static UsageData Month(double busySeconds, double requests = 0, double egressBytes = 0, int daysIn = 10)
            => new UsageData
            {
                MonthStartUtc = Utc(2026, 10, 1, 7),
                AsOfUtc = Utc(2026, 10, 1, 7).AddDays(daysIn),
                BillableInstanceSeconds = busySeconds,
                Requests = requests,
                EgressBytes = egressBytes,
            };

        private static UsageReport.Measure Find(UsageReport report, string name) => report.Measures.Single(m => m.Name == name);

        [TestMethod]
        public void UsageIsMeasuredAgainstTheFreeTier()
        {
            var report = CloudReports.BuildUsage(Month(36000, requests: 500000, egressBytes: 512L * 1024 * 1024), OneCpu);

            Assert.AreEqual(36000, Find(report, "vCPU time").Used);
            Assert.AreEqual(20.0, Find(report, "vCPU time").PercentUsed, 0.001);
            Assert.AreEqual(10.0, Find(report, "Memory time").PercentUsed, 0.001);
            Assert.AreEqual(25.0, Find(report, "Requests").PercentUsed, 0.001);
            Assert.AreEqual(50.0, Find(report, "Egress").PercentUsed, 0.001);
            Assert.AreEqual(10, report.BusyHoursUsed, 0.001);
            Assert.AreEqual(50, report.BusyHoursFree, 0.001, "180,000 vCPU-seconds is 50 hours of one 1-vCPU instance");
        }

        [TestMethod]
        public void InstanceSizeScalesCpuAndMemoryTime()
        {
            var half = new ServiceLimits { Vcpu = 0.5, MemoryGiB = 1, MaxInstances = 2 };
            var report = CloudReports.BuildUsage(Month(36000), half);
            Assert.AreEqual(18000, Find(report, "vCPU time").Used);
            Assert.AreEqual(36000, Find(report, "Memory time").Used);
            Assert.AreEqual(100, report.BusyHoursFree, 0.001, "half the CPU doubles the CPU allowance in instance-time; memory allows the same 100 h");
        }

        [TestMethod]
        public void ProjectionExtrapolatesTheMonthSoFar()
        {
            // 10 of 31 days gone.
            var report = CloudReports.BuildUsage(Month(36000), OneCpu);
            Assert.IsTrue(report.ProjectionIsReliable);
            Assert.AreEqual(10.0 / 31, report.MonthFraction, 0.001);
            Assert.AreEqual(36000 * 3.1, Find(report, "vCPU time").Projected, 1);
            Assert.AreEqual(62.0, Find(report, "vCPU time").PercentProjected, 0.1);
        }

        [TestMethod]
        public void TooEarlyInTheMonthIsNotProjected()
        {
            var data = Month(500);
            data.AsOfUtc = data.MonthStartUtc.AddHours(6);
            var report = CloudReports.BuildUsage(data, OneCpu);
            Assert.IsFalse(report.ProjectionIsReliable);
            Assert.AreEqual(500, Find(report, "vCPU time").Projected, "no extrapolation from a few hours");
            Assert.IsNull(report.FreeTierRunsOutUtc);
        }

        [TestMethod]
        public void FreeTierExhaustionIsPredictedFromThePace()
        {
            // 100,000 of 180,000 vCPU-seconds in 10 days: 80,000 left at 10,000 a day is 8 more days.
            var report = CloudReports.BuildUsage(Month(100000), OneCpu);
            Assert.IsTrue(Math.Abs((Utc(2026, 10, 19, 7) - report.FreeTierRunsOutUtc!.Value).TotalSeconds) < 1, report.FreeTierRunsOutUtc.ToString());
            Assert.AreEqual(0, report.UsedUp.Count);

            Assert.IsNull(CloudReports.BuildUsage(Month(100), OneCpu).FreeTierRunsOutUtc, "slow use lasts past month end");
        }

        [TestMethod]
        public void AnAllowanceAlreadyUsedUpIsReportedAsThatNotAsRunningOutLater()
        {
            // 200,000 vCPU-seconds is past the 180,000 allowance; memory-seconds (200,000 of 360,000) are not.
            var report = CloudReports.BuildUsage(Month(200000), OneCpu);
            Assert.IsTrue(Find(report, "vCPU time").PercentUsed > 100);
            CollectionAssert.AreEqual(new[] { "vCPU time" }, report.UsedUp);
            Assert.IsNull(report.FreeTierRunsOutUtc, "the memory allowance's date would mislead");
            Assert.IsTrue(CloudPages.RenderUsage(report, OneCpu, "", "src").Contains("Already used up"));
        }

        [TestMethod]
        public void CostCountsOnlyWhatIsBeyondTheFreeTier()
        {
            Assert.AreEqual(0.0, CloudReports.BuildUsage(Month(100000, requests: 1000000), OneCpu).EstimatedDollarsSoFar);

            // 200,000 vCPU-seconds: 20,000 over. 200,000 GiB-seconds is under its allowance. 2.5M requests: 0.5M over.
            var report = CloudReports.BuildUsage(Month(200000, requests: 2500000), OneCpu);
            Assert.AreEqual(20000 * FreeTier.DollarsPerVcpuSecond + 0.5 * FreeTier.DollarsPerMillionRequests, report.EstimatedDollarsSoFar, 1e-9);
        }

        // Pages ------------------------------------------------------------------

        [TestMethod]
        public void PercentagesAreReadable()
        {
            Assert.AreEqual("0%", CloudPages.Percent(0));
            Assert.AreEqual("<1%", CloudPages.Percent(0.2));
            Assert.AreEqual("1.6%", CloudPages.Percent(1.6));
            Assert.AreEqual("16%", CloudPages.Percent(16.2));
            Assert.AreEqual("250%", CloudPages.Percent(250));
        }

        [TestMethod]
        public void BarsTurnAmberAndRed()
        {
            Assert.IsTrue(CloudPages.Bar(10).Contains("class=\"bar\""));
            Assert.IsTrue(CloudPages.Bar(85).Contains("bar warn"));
            Assert.IsTrue(CloudPages.Bar(100).Contains("bar over"));
            Assert.IsTrue(CloudPages.Bar(500).Contains("width:100%"), "never wider than its track");
            Assert.IsTrue(CloudPages.Bar(-5).Contains("width:0%"));
        }

        [TestMethod]
        public void UsagePageShowsEverythingAndEncodesPercentages()
        {
            var report = CloudReports.BuildUsage(Month(125, requests: 787, egressBytes: 17000000), OneCpu);
            string page = CloudPages.RenderUsage(report, OneCpu, "?key=k", "Cloud Monitoring, project p");

            foreach (string expected in new[] { "vCPU time", "Memory time", "Requests", "Egress", "Free tier runs out", "Rough cost", "Cloud Monitoring, project p", "href=\"/admin/fleet?key=k\"" })
                Assert.IsTrue(page.Contains(expected), expected);
            Assert.IsTrue(page.Contains("&lt;1%"), "a percentage below one is HTML-encoded");
            Assert.IsFalse(page.Contains("<1%"));
        }

        [TestMethod]
        public void FleetPageShowsEverything()
        {
            DateTime t = Utc(2026, 10, 4, 9);
            var data = new FleetData
            {
                AsOfUtc = t.AddMinutes(30),
                ActiveInstancesPerMinute = Minutes(t, 1, 2),
                IdleInstancesPerMinute = Minutes(t, 0, 0),
                RequestsPerHourByClass = new Dictionary<string, List<MetricPoint>> { ["2xx"] = new List<MetricPoint> { new MetricPoint(t, 120), new MetricPoint(t.AddHours(1), 7) } },
            };
            string page = CloudPages.RenderFleet(CloudReports.BuildFleet(data, OneCpu), "", "Cloud Monitoring, project p");

            foreach (string expected in new[] { "Active instances", "of at most 2", "Peaks", "By hour (UTC)", "10-04 09:00", "so far", "2xx: 127", "Cloud Monitoring, project p" })
                Assert.IsTrue(page.Contains(expected), expected);
        }

        [TestMethod]
        public void FleetPageWithoutSamplesSaysSo()
        {
            string page = CloudPages.RenderFleet(CloudReports.BuildFleet(new FleetData { AsOfUtc = Utc(2026, 10, 4) }, OneCpu), "", "src");
            Assert.IsTrue(page.Contains("no samples in the last 24 hours"));
        }

        [TestMethod]
        public void PermissionErrorsPointAtTheFix()
        {
            string page = CloudPages.Unavailable("Fleet", "", "could not query", "Monitoring API answered 403: PERMISSION_DENIED <b>x</b>");
            Assert.IsTrue(page.Contains("setup.sh status-access"));
            Assert.IsTrue(page.Contains("&lt;b&gt;x&lt;/b&gt;"), "the detail is encoded");

            Assert.IsFalse(CloudPages.Unavailable("Fleet", "", "m", "timeout").Contains("status-access"));
            Assert.IsFalse(CloudPages.Unavailable("Fleet", "", "m", null).Contains("<pre"));
        }

        [TestMethod]
        public void ServiceLimitsComeFromTheEnvironment()
        {
            string? cpu = Environment.GetEnvironmentVariable("CLOUD_RUN_VCPU");
            string? max = Environment.GetEnvironmentVariable("CLOUD_RUN_MAX_INSTANCES");
            try
            {
                Environment.SetEnvironmentVariable("CLOUD_RUN_VCPU", "0.5");
                Environment.SetEnvironmentVariable("CLOUD_RUN_MAX_INSTANCES", "not a number");
                var limits = ServiceLimits.FromEnvironment();
                Assert.AreEqual(0.5, limits.Vcpu);
                Assert.AreEqual(2, limits.MaxInstances, "an unreadable value falls back to the default");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CLOUD_RUN_VCPU", cpu);
                Environment.SetEnvironmentVariable("CLOUD_RUN_MAX_INSTANCES", max);
            }
        }
    }
}
