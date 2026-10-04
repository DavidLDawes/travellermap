#nullable enable
using System;
using System.Diagnostics;
using System.Threading;

namespace Maps.Admin
{
    /// <summary>
    /// Request counters for /admin/status, kept per process (so per Cloud Run instance) and reset
    /// when it restarts. Lock-free: every field is updated with Interlocked, so recording a
    /// request costs a few atomic operations. The ASP.NET Core host records every request; the IIS
    /// host does not (the status page says so).
    /// </summary>
    internal sealed class RequestStats
    {
        public enum Category { Tile, Image, Api, Data, Admin, Site }

        public static readonly string[] CategoryNames = { "Tiles", "Posters and maps", "API", "Sector data", "Admin", "Site files and other" };

        /// <summary>Upper bounds (ms) of the latency histogram buckets; a final bucket holds anything slower.</summary>
        public static readonly int[] LatencyBoundsMs = { 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 };

        private static readonly int CategoryCount = Enum.GetValues(typeof(Category)).Length;
        private const int StatusClasses = 5; // 2xx, 3xx, 4xx, 5xx, other

        // Declared after the static fields above: the constructor sizes arrays from them.
        public static readonly RequestStats Current = new RequestStats();

        private readonly long[,] statusCounts = new long[CategoryCount, StatusClasses];
        private readonly long[,] latencyBuckets = new long[CategoryCount, LatencyBoundsMs.Length + 1];
        private readonly long[] totalTicks = new long[CategoryCount];
        private readonly long[] maxTicks = new long[CategoryCount];
        private long inFlight;
        private long peakInFlight;

        private int firstRequestSeen;
        private long firstRequestTicks;
        private long firstRequestDelayTicks;

        private long pdfLockWaits;
        private long pdfLockWaitTicks;
        private long pdfLockMaxWaitTicks;

        public RequestStats() { }

        /// <summary>Which line of the status page a path counts toward.</summary>
        public static Category Categorize(string path)
        {
            if (path.StartsWith("/api/tile", StringComparison.OrdinalIgnoreCase))
                return Category.Tile;
            if (path.StartsWith("/api/poster", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/jumpmap", StringComparison.OrdinalIgnoreCase))
                return Category.Image;
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                return Category.Api;
            if (path.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
                return Category.Data;
            if (path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
                return Category.Admin;
            return Category.Site;
        }

        public void Begin()
        {
            long now = Interlocked.Increment(ref inFlight);
            long peak;
            while (now > (peak = Volatile.Read(ref peakInFlight)) &&
                Interlocked.CompareExchange(ref peakInFlight, now, peak) != peak) { }
        }

        /// <param name="sinceStart">Time from process start to the moment the request began (used once, for the first request).</param>
        public void End(string path, int statusCode, TimeSpan elapsed, TimeSpan sinceStart)
        {
            Interlocked.Decrement(ref inFlight);
            int category = (int)Categorize(path);
            int statusClass = statusCode >= 200 && statusCode < 600 ? Math.Min(statusCode / 100 - 2, StatusClasses - 1) : StatusClasses - 1;
            Interlocked.Increment(ref statusCounts[category, statusClass]);

            long ticks = elapsed.Ticks;
            Interlocked.Increment(ref latencyBuckets[category, BucketFor(elapsed.TotalMilliseconds)]);
            Interlocked.Add(ref totalTicks[category], ticks);
            InterlockedMax(ref maxTicks[category], ticks);

            if (Interlocked.Exchange(ref firstRequestSeen, 1) == 0)
            {
                Interlocked.Exchange(ref firstRequestTicks, ticks);
                Interlocked.Exchange(ref firstRequestDelayTicks, sinceStart.Ticks);
            }
        }

        /// <summary>Time a PDF render waited for the PDFsharp lock (PDF generation is serialized).</summary>
        public void RecordPdfLockWait(TimeSpan waited)
        {
            Interlocked.Increment(ref pdfLockWaits);
            Interlocked.Add(ref pdfLockWaitTicks, waited.Ticks);
            InterlockedMax(ref pdfLockMaxWaitTicks, waited.Ticks);
        }

        private static int BucketFor(double milliseconds)
        {
            for (int i = 0; i < LatencyBoundsMs.Length; ++i)
                if (milliseconds <= LatencyBoundsMs[i])
                    return i;
            return LatencyBoundsMs.Length;
        }

        private static void InterlockedMax(ref long target, long value)
        {
            long seen;
            while (value > (seen = Volatile.Read(ref target)) &&
                Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }

        /// <summary>A consistent-enough copy of the counters (each is read atomically; they are not read together).</summary>
        public Snapshot Capture()
        {
            var categories = new CategorySnapshot[CategoryCount];
            for (int c = 0; c < CategoryCount; ++c)
            {
                var statuses = new long[StatusClasses];
                long requests = 0;
                for (int s = 0; s < StatusClasses; ++s)
                {
                    statuses[s] = Interlocked.Read(ref statusCounts[c, s]);
                    requests += statuses[s];
                }
                var buckets = new long[LatencyBoundsMs.Length + 1];
                for (int b = 0; b < buckets.Length; ++b)
                    buckets[b] = Interlocked.Read(ref latencyBuckets[c, b]);
                categories[c] = new CategorySnapshot(
                    CategoryNames[c], requests, statuses, buckets,
                    TimeSpan.FromTicks(Interlocked.Read(ref totalTicks[c])),
                    TimeSpan.FromTicks(Interlocked.Read(ref maxTicks[c])));
            }
            bool first = Volatile.Read(ref firstRequestSeen) != 0;
            long waits = Interlocked.Read(ref pdfLockWaits);
            return new Snapshot(
                categories,
                Interlocked.Read(ref inFlight),
                Interlocked.Read(ref peakInFlight),
                first ? TimeSpan.FromTicks(Interlocked.Read(ref firstRequestDelayTicks)) : (TimeSpan?)null,
                first ? TimeSpan.FromTicks(Interlocked.Read(ref firstRequestTicks)) : (TimeSpan?)null,
                waits,
                TimeSpan.FromTicks(Interlocked.Read(ref pdfLockWaitTicks)),
                TimeSpan.FromTicks(Interlocked.Read(ref pdfLockMaxWaitTicks)));
        }

        internal sealed class Snapshot
        {
            public Snapshot(CategorySnapshot[] categories, long inFlight, long peakInFlight,
                TimeSpan? startToFirstRequest, TimeSpan? firstRequestDuration,
                long pdfLockWaits, TimeSpan pdfLockTotalWait, TimeSpan pdfLockMaxWait)
            {
                Categories = categories;
                InFlight = inFlight;
                PeakInFlight = peakInFlight;
                StartToFirstRequest = startToFirstRequest;
                FirstRequestDuration = firstRequestDuration;
                PdfLockWaits = pdfLockWaits;
                PdfLockTotalWait = pdfLockTotalWait;
                PdfLockMaxWait = pdfLockMaxWait;
            }

            public CategorySnapshot[] Categories { get; }
            public long InFlight { get; }
            public long PeakInFlight { get; }
            /// <summary>Process start until the first request arrived; null if none has.</summary>
            public TimeSpan? StartToFirstRequest { get; }
            /// <summary>How long that first request took (for a scaled-to-zero instance, the cold start).</summary>
            public TimeSpan? FirstRequestDuration { get; }
            public long PdfLockWaits { get; }
            public TimeSpan PdfLockTotalWait { get; }
            public TimeSpan PdfLockMaxWait { get; }

            public long TotalRequests
            {
                get { long total = 0; foreach (var c in Categories) total += c.Requests; return total; }
            }
        }

        internal sealed class CategorySnapshot
        {
            public CategorySnapshot(string name, long requests, long[] statusClasses, long[] latencyBuckets, TimeSpan totalTime, TimeSpan maxTime)
            {
                Name = name;
                Requests = requests;
                StatusClasses = statusClasses;
                LatencyBuckets = latencyBuckets;
                TotalTime = totalTime;
                MaxTime = maxTime;
            }

            public string Name { get; }
            public long Requests { get; }
            /// <summary>Counts of 2xx, 3xx, 4xx, 5xx and anything else.</summary>
            public long[] StatusClasses { get; }
            public long[] LatencyBuckets { get; }
            public TimeSpan TotalTime { get; }
            public TimeSpan MaxTime { get; }

            public TimeSpan Mean => Requests == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(TotalTime.Ticks / Requests);

            /// <summary>
            /// The bucket upper bound (ms) that the p-th fraction of requests fell within (so
            /// "p95 ≤ 250 ms"); PositiveInfinity if it fell in the slowest bucket; null if there are no requests.
            /// </summary>
            public double? PercentileUpperBoundMs(double fraction)
            {
                if (Requests == 0)
                    return null;
                long rank = (long)Math.Ceiling(fraction * Requests);
                long seen = 0;
                for (int b = 0; b < LatencyBuckets.Length; ++b)
                {
                    seen += LatencyBuckets[b];
                    if (seen >= rank)
                        return b < LatencyBoundsMs.Length ? LatencyBoundsMs[b] : double.PositiveInfinity;
                }
                return double.PositiveInfinity;
            }
        }

        /// <summary>Elapsed time since a Stopwatch timestamp.</summary>
        public static TimeSpan Since(long timestamp) => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency);
    }
}
