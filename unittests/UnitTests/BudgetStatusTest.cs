#nullable enable
using Maps.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests
{
    [TestClass]
    public class BudgetStatusTest
    {
        private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new DateTime(y, m, d, h, min, 0, DateTimeKind.Utc);

        private static readonly DateTime Now = Utc(2026, 10, 4, 10);

        private static BudgetNotification Notification(double cost, DateTime intervalStart, double budget = 2)
            => new BudgetNotification
            {
                ReceivedUtc = Now.AddHours(-3),
                BudgetName = "travellermap",
                CostAmount = cost,
                BudgetAmount = budget,
                Currency = "USD",
                CostIntervalStartUtc = intervalStart,
            };

        private static BudgetData Data(Action<BudgetData>? change = null)
        {
            var data = new BudgetData { AsOfUtc = Now, SitePublic = true };
            change?.Invoke(data);
            return data;
        }

        [TestMethod]
        public void WithoutANotificationTheConfiguredBudgetIsShown()
        {
            var report = BudgetReports.Build(Data(), null, configuredBudget: 2);
            Assert.AreEqual(2.0, report.BudgetAmount);
            Assert.IsTrue(report.BudgetSource.Contains("BUDGET_USD"));
            Assert.IsNull(report.SpendSoFar);
            Assert.IsFalse(report.NotificationIsStale);
        }

        [TestMethod]
        public void NoNotificationAndNoConfigurationMeansUnknown()
        {
            Assert.IsNull(BudgetReports.Build(Data(), null, configuredBudget: 0).BudgetAmount);
        }

        [TestMethod]
        public void ThisMonthsNotificationGivesSpendAndPercent()
        {
            var report = BudgetReports.Build(Data(d => d.Latest = Notification(1.1, Utc(2026, 10, 1, 7))), null, 5);
            Assert.AreEqual(1.1, report.SpendSoFar);
            Assert.AreEqual(55.0, report.SpendPercent!.Value, 0.001);
            Assert.AreEqual(2.0, report.BudgetAmount, "Google's figure wins over the configured one");
            Assert.AreEqual(Now.AddHours(-3), report.SpendAsOfUtc);
            Assert.IsFalse(report.NotificationIsStale);
        }

        [TestMethod]
        public void LastMonthsNotificationIsNotThisMonthsSpend()
        {
            var report = BudgetReports.Build(Data(d => d.Latest = Notification(1.9, Utc(2026, 9, 1, 7))), null, 0);
            Assert.IsTrue(report.NotificationIsStale);
            Assert.IsNull(report.SpendSoFar);
            Assert.IsNull(report.SpendPercent);
            Assert.AreEqual(2.0, report.BudgetAmount);
        }

        [TestMethod]
        public void ThresholdsAreMatchedByMonth()
        {
            var report = BudgetReports.Build(Data(d => d.Announcements = new List<Announcement>
            {
                new Announcement { Key = "2026-10-budget-actual-50", TimeUtc = Utc(2026, 10, 3, 12, 30) },
                new Announcement { Key = "2026-09-budget-actual-100", TimeUtc = Utc(2026, 9, 20) },
                new Announcement { Key = "2026-10-incident-abc-open", TimeUtc = Utc(2026, 10, 2) },
            }), null, 2);

            Assert.AreEqual(5, report.Thresholds.Count);
            Assert.AreEqual(Utc(2026, 10, 3, 12, 30), report.Thresholds.Single(t => t.Label.StartsWith("50%")).CrossedUtc);
            Assert.IsNull(report.Thresholds.Single(t => t.Label.StartsWith("100%")).CrossedUtc, "last month's 100% does not count");
            Assert.AreEqual(1, report.Thresholds.Count(t => t.CrossedUtc != null));
        }

        [TestMethod]
        public void ThresholdsDescribeWhatTheResponderDoes()
        {
            var report = BudgetReports.Build(Data(), null, 2);
            Assert.IsTrue(report.Thresholds.Single(t => t.Label.StartsWith("100%")).Response.Contains("cut off"));
            Assert.IsFalse(report.Thresholds.Single(t => t.Label.StartsWith("90%")).Response.Contains("cut off"));
            Assert.IsTrue(report.Thresholds.Any(t => t.Label.StartsWith("Forecast")));
        }

        [TestMethod]
        public void PoliciesAreSortedAndIncidentsNewestFirst()
        {
            var report = BudgetReports.Build(Data(d =>
            {
                d.Policies = new List<PolicyInfo> { new PolicyInfo { Name = "b" }, new PolicyInfo { Name = "a" } };
                d.OpenIncidents = new List<IncidentInfo>
                {
                    new IncidentInfo { Policy = "old", OpenedUtc = Utc(2026, 10, 1) },
                    new IncidentInfo { Policy = "new", OpenedUtc = Utc(2026, 10, 3) },
                };
            }), null, 2);
            CollectionAssert.AreEqual(new[] { "a", "b" }, report.Policies.Select(p => p.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "new", "old" }, report.OpenIncidents.Select(i => i.Policy).ToArray());
        }

        [TestMethod]
        public void PrioritiesHaveNames()
        {
            Assert.AreEqual("emergency", BudgetReports.PriorityName(2));
            Assert.AreEqual("high", BudgetReports.PriorityName(1));
            Assert.AreEqual("normal", BudgetReports.PriorityName(0));
            Assert.AreEqual("low", BudgetReports.PriorityName(-1));
        }

        // The page ---------------------------------------------------------------

        private static string Page(BudgetData data, double configured = 2) => BudgetPages.Render(BudgetReports.Build(data, null, configured), "?key=k", "Google Cloud, project p");

        [TestMethod]
        public void PageShowsEverythingForAHealthySite()
        {
            string page = Page(Data(d =>
            {
                d.Latest = Notification(0.37, Utc(2026, 10, 1, 7));
                d.Policies = new List<PolicyInfo> { new PolicyInfo { Name = "request rate", Severity = "critical", Enabled = true } };
                d.LastAlert = new LastAlertInfo { TimeUtc = Utc(2026, 10, 4, 5, 10), Title = "[DRILL] site: 100% of budget spent", Priority = 2, Drill = true };
            }));

            foreach (string expected in new[] { "$2.00 per month", "$0.37", "18%", "Budget alerts this month", "not reached", "public", "none", "request rate", "enabled", "emergency priority", "a drill", "Google Cloud, project p", "href=\"/admin/usage?key=k\"" })
                Assert.IsTrue(page.Contains(expected), expected);
            Assert.IsFalse(page.Contains("CUT OFF"));
        }

        [TestMethod]
        public void PageShoutsWhenTheSiteIsCutOff()
        {
            string page = Page(Data(d => d.SitePublic = false));
            Assert.IsTrue(page.Contains("CUT OFF"));
            Assert.IsTrue(page.Contains("setup.sh restore"));
        }

        [TestMethod]
        public void PageSaysWhenItCannotTell()
        {
            string page = Page(Data(d =>
            {
                d.SitePublic = null;
                d.Problems.Add("Site access: run.googleapis.com answered 403: PERMISSION_DENIED");
            }));
            Assert.IsTrue(page.Contains("Site access</td><td>unknown"));
            Assert.IsTrue(page.Contains("Could not read"));
            Assert.IsTrue(page.Contains("setup.sh status-access"));
        }

        [TestMethod]
        public void PageExplainsAMissingNotification()
        {
            string page = Page(Data());
            Assert.IsTrue(page.Contains("no budget notification received yet"));
            Assert.IsTrue(page.Contains("the deployed configuration"));
        }

        [TestMethod]
        public void PageFlagsAnEarlierMonthsNotification()
        {
            string page = Page(Data(d => d.Latest = Notification(1.9, Utc(2026, 9, 1, 7))));
            Assert.IsTrue(page.Contains("no notification for this month yet"));
            Assert.IsTrue(page.Contains("earlier month"));
        }

        [TestMethod]
        public void PageListsOpenIncidentsAndDisabledPolicies()
        {
            string page = Page(Data(d =>
            {
                d.OpenIncidents = new List<IncidentInfo> { new IncidentInfo { Policy = "request rate", OpenedUtc = Utc(2026, 10, 4, 9), Severity = "critical" } };
                d.Policies = new List<PolicyInfo> { new PolicyInfo { Name = "request rate", Enabled = false } };
            }));
            Assert.IsTrue(page.Contains("OPEN: request rate"));
            Assert.IsTrue(page.Contains("<b>disabled</b>"));
        }

        [TestMethod]
        public void PageEncodesWhatCameFromOutside()
        {
            string page = Page(Data(d =>
            {
                d.Policies = new List<PolicyInfo> { new PolicyInfo { Name = "<script>x</script>", Enabled = true } };
                d.LastAlert = new LastAlertInfo { TimeUtc = Now, Title = "<img src=x>", Priority = 0 };
                d.Problems.Add("<b>oops</b>");
            }));
            Assert.IsFalse(page.Contains("<script>x"));
            Assert.IsFalse(page.Contains("<img src=x>"));
            Assert.IsFalse(page.Contains("<b>oops</b>"));
            Assert.IsTrue(page.Contains("&lt;script&gt;x&lt;/script&gt;"));
        }

        [TestMethod]
        public void SpendBarShowsOnlyWhenThereIsSpend()
        {
            Assert.IsFalse(Page(Data()).Contains("class=\"bar"));
            Assert.IsTrue(Page(Data(d => d.Latest = Notification(1.9, Utc(2026, 10, 1, 7)))).Contains("class=\"bar warn\""), "95% of budget is amber");
        }
    }
}
