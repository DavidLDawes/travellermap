#nullable enable
using Maps.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace UnitTests
{
    [TestClass]
    public class StatusTest
    {
        private static void Record(RequestStats stats, string path, int status, double milliseconds)
        {
            stats.Begin();
            stats.End(path, status, TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromSeconds(1));
        }

        private static RequestStats.CategorySnapshot Category(RequestStats stats, RequestStats.Category category)
            => stats.Capture().Categories[(int)category];

        [TestMethod]
        public void PathsAreCategorized()
        {
            Assert.AreEqual(RequestStats.Category.Tile, RequestStats.Categorize("/api/tile"));
            Assert.AreEqual(RequestStats.Category.Image, RequestStats.Categorize("/api/poster/Spinward%20Marches"));
            Assert.AreEqual(RequestStats.Category.Image, RequestStats.Categorize("/api/jumpmap"));
            Assert.AreEqual(RequestStats.Category.Api, RequestStats.Categorize("/api/search"));
            Assert.AreEqual(RequestStats.Category.Data, RequestStats.Categorize("/data/Spinward Marches/tab"));
            Assert.AreEqual(RequestStats.Category.Admin, RequestStats.Categorize("/admin/status"));
            Assert.AreEqual(RequestStats.Category.Admin, RequestStats.Categorize("/ADMIN"));
            Assert.AreEqual(RequestStats.Category.Site, RequestStats.Categorize("/index.js"));
            Assert.AreEqual(RequestStats.Category.Site, RequestStats.Categorize("/"));
        }

        [TestMethod]
        public void RequestsAreCountedByKindAndStatus()
        {
            var stats = new RequestStats();
            Record(stats, "/api/tile", 200, 5);
            Record(stats, "/api/tile", 200, 5);
            Record(stats, "/api/tile", 404, 5);
            Record(stats, "/api/search", 503, 5);
            Record(stats, "/index.html", 304, 5);
            Record(stats, "/weird", 0, 5);

            var tiles = Category(stats, RequestStats.Category.Tile);
            Assert.AreEqual(3, tiles.Requests);
            CollectionAssert.AreEqual(new long[] { 2, 0, 1, 0, 0 }, tiles.StatusClasses);
            CollectionAssert.AreEqual(new long[] { 0, 0, 0, 1, 0 }, Category(stats, RequestStats.Category.Api).StatusClasses);
            CollectionAssert.AreEqual(new long[] { 0, 1, 0, 0, 1 }, Category(stats, RequestStats.Category.Site).StatusClasses);
            Assert.AreEqual(6, stats.Capture().TotalRequests);
        }

        [TestMethod]
        public void InFlightRisesAndFalls()
        {
            var stats = new RequestStats();
            stats.Begin();
            stats.Begin();
            var snapshot = stats.Capture();
            Assert.AreEqual(2, snapshot.InFlight);
            Assert.AreEqual(2, snapshot.PeakInFlight);

            stats.End("/", 200, TimeSpan.Zero, TimeSpan.Zero);
            snapshot = stats.Capture();
            Assert.AreEqual(1, snapshot.InFlight);
            Assert.AreEqual(2, snapshot.PeakInFlight, "peak is kept");
        }

        [TestMethod]
        public void PercentilesAreBucketUpperBounds()
        {
            var stats = new RequestStats();
            for (int i = 0; i < 90; ++i) Record(stats, "/api/tile", 200, 8);     // <= 10 ms
            for (int i = 0; i < 9; ++i) Record(stats, "/api/tile", 200, 200);    // <= 250 ms
            Record(stats, "/api/tile", 200, 3000);                               // <= 5000 ms

            var tiles = Category(stats, RequestStats.Category.Tile);
            Assert.AreEqual(10.0, tiles.PercentileUpperBoundMs(0.5));
            Assert.AreEqual(10.0, tiles.PercentileUpperBoundMs(0.9));
            Assert.AreEqual(250.0, tiles.PercentileUpperBoundMs(0.95));
            Assert.AreEqual(5000.0, tiles.PercentileUpperBoundMs(1.0));
            Assert.AreEqual(TimeSpan.FromMilliseconds(3000), tiles.MaxTime);
        }

        [TestMethod]
        public void NoRequestsMeansNoPercentile()
        {
            Assert.IsNull(Category(new RequestStats(), RequestStats.Category.Tile).PercentileUpperBoundMs(0.5));
        }

        [TestMethod]
        public void VerySlowRequestsFallInTheLastBucket()
        {
            var stats = new RequestStats();
            Record(stats, "/api/poster", 200, 60000);
            Assert.IsTrue(double.IsPositiveInfinity(Category(stats, RequestStats.Category.Image).PercentileUpperBoundMs(0.5)!.Value));
        }

        [TestMethod]
        public void OnlyTheFirstRequestIsRememberedAsFirst()
        {
            var stats = new RequestStats();
            Assert.IsNull(stats.Capture().StartToFirstRequest);

            stats.Begin();
            stats.End("/", 200, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
            Record(stats, "/", 200, 5);

            var snapshot = stats.Capture();
            Assert.AreEqual(TimeSpan.FromSeconds(2), snapshot.StartToFirstRequest);
            Assert.AreEqual(TimeSpan.FromSeconds(3), snapshot.FirstRequestDuration);
        }

        [TestMethod]
        public void PdfLockWaitsAreTotaledAndMaxed()
        {
            var stats = new RequestStats();
            stats.RecordPdfLockWait(TimeSpan.FromMilliseconds(100));
            stats.RecordPdfLockWait(TimeSpan.FromMilliseconds(300));

            var snapshot = stats.Capture();
            Assert.AreEqual(2, snapshot.PdfLockWaits);
            Assert.AreEqual(TimeSpan.FromMilliseconds(400), snapshot.PdfLockTotalWait);
            Assert.AreEqual(TimeSpan.FromMilliseconds(300), snapshot.PdfLockMaxWait);
        }

        [TestMethod]
        public void ConcurrentRecordingLosesNothing()
        {
            var stats = new RequestStats();
            Parallel.For(0, 8, _ =>
            {
                for (int i = 0; i < 2000; ++i)
                    Record(stats, i % 2 == 0 ? "/api/tile" : "/api/search", 200, i % 50);
            });

            var snapshot = stats.Capture();
            Assert.AreEqual(16000, snapshot.TotalRequests);
            Assert.AreEqual(0, snapshot.InFlight);
            Assert.AreEqual(8000, Category(stats, RequestStats.Category.Tile).Requests);
            Assert.AreEqual(16000, snapshot.Categories.Sum(c => c.LatencyBuckets.Sum()));
        }

        [TestMethod]
        public void DurationsAreReadable()
        {
            Assert.AreEqual("0 ms", StatusPage.FormatDuration(TimeSpan.Zero));
            Assert.AreEqual("250 ms", StatusPage.FormatDuration(TimeSpan.FromMilliseconds(250)));
            Assert.AreEqual("4.6 s", StatusPage.FormatDuration(TimeSpan.FromSeconds(4.6)));
            Assert.AreEqual("3m 5s", StatusPage.FormatDuration(TimeSpan.FromSeconds(185)));
            Assert.AreEqual("2h 5m", StatusPage.FormatDuration(TimeSpan.FromMinutes(125)));
            Assert.AreEqual("3d 1h 0m", StatusPage.FormatDuration(TimeSpan.FromHours(73)));
            Assert.AreEqual("0 ms", StatusPage.FormatDuration(TimeSpan.FromSeconds(-5)), "clock skew never shows negative");
        }

        [TestMethod]
        public void BytesAreReadable()
        {
            Assert.AreEqual("0 B", StatusPage.FormatBytes(0));
            Assert.AreEqual("1023 B", StatusPage.FormatBytes(1023));
            Assert.AreEqual("1.0 KiB", StatusPage.FormatBytes(1024));
            Assert.AreEqual("24.0 MiB", StatusPage.FormatBytes(24L * 1024 * 1024));
            Assert.AreEqual("1.5 GiB", StatusPage.FormatBytes(1610612736));
        }

        [TestMethod]
        public void LatencyBoundsAreReadable()
        {
            Assert.AreEqual("–", StatusPage.FormatBound(null));
            Assert.AreEqual("≤ 250 ms", StatusPage.FormatBound(250));
            Assert.AreEqual("≤ 5.0 s", StatusPage.FormatBound(5000));
            Assert.AreEqual("> 10.0 s", StatusPage.FormatBound(double.PositiveInfinity));
        }

        [TestMethod]
        public void LandingPageLinksCarryTheKey()
        {
            string page = StatusPage.RenderIndex("?key=a%26b");
            Assert.IsTrue(page.Contains("href=\"/admin/status?key=a%26b\""), "status link keeps the key");
            Assert.IsTrue(page.Contains("href=\"/admin/errors?key=a%26b\""));
            Assert.IsTrue(page.Contains("href=\"/admin/flush?key=a%26b\""));

            string local = StatusPage.RenderIndex("");
            Assert.IsTrue(local.Contains("href=\"/admin/status\""), "no key, no suffix");
            Assert.IsFalse(local.Contains("key="));
        }

        [TestMethod]
        public void StateChangingLinksAskForConfirmation()
        {
            string page = StatusPage.RenderIndex("");
            foreach (string path in new[] { "/admin/flush", "/admin/reindex" })
            {
                int link = page.IndexOf($"href=\"{path}\"", StringComparison.Ordinal);
                Assert.IsTrue(link >= 0, path);
                Assert.IsTrue(page.Substring(link, 120).Contains("confirm("), path);
            }
        }

        [TestMethod]
        public void StatusPageShowsTheSections()
        {
            var stats = new RequestStats();
            Record(stats, "/api/tile", 200, 12);
            string page = StatusPage.Render(stats.Capture(), "", new Dictionary<string, object> { ["Cache.Items"] = 42 });

            foreach (string expected in new[] { "Deployment", "Process", "Requests since start", "Tiles", "PDF generation", "Data and caches", "Sectors loaded", "Search index", "Cache.Items" })
                Assert.IsTrue(page.Contains(expected), expected);
        }

        [TestMethod]
        public void StatusPageEncodesWhatItShows()
        {
            var stats = new RequestStats();
            string page = StatusPage.Render(stats.Capture(), "", new Dictionary<string, object> { ["<b>x</b>"] = "<script>alert(1)</script>" });
            Assert.IsFalse(page.Contains("<script>alert"));
            Assert.IsTrue(page.Contains("&lt;script&gt;alert(1)&lt;/script&gt;"));
            Assert.IsTrue(page.Contains("None counted"), "no requests recorded");
        }
    }
}
