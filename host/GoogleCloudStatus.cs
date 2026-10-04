using Maps.Admin;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Maps.Host;

/// <summary>
/// Cloud Run's own metrics, read from the Cloud Monitoring API for /admin/fleet and /admin/usage.
/// On Cloud Run it authenticates as the service's account (which needs roles/monitoring.viewer;
/// see deploy/setup.sh status-access). For a local run, set GCP_ACCESS_TOKEN (for example from
/// `gcloud auth print-access-token`), GCP_PROJECT_ID and GCP_SERVICE. Answers are cached for a minute.
/// </summary>
internal sealed class GoogleCloudStatusProvider : ICloudStatusProvider
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private const string MetadataToken = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";
    private const string MetadataProject = "http://metadata.google.internal/computeMetadata/v1/project/project-id";

    private readonly HttpClient http;
    private readonly string service;
    private readonly string? developerToken;
    private string? project;
    private (string Token, DateTime Expires)? metadataToken;

    private readonly object cacheLock = new();
    private (DateTime At, FleetData Value)? fleet;
    private (DateTime At, UsageData Value)? usage;

    public GoogleCloudStatusProvider(HttpClient http, string service, string? project, string? developerToken)
    {
        this.http = http;
        this.service = service;
        this.project = project;
        this.developerToken = developerToken;
    }

    /// <summary>A provider if this is Cloud Run (K_SERVICE is set) or a developer token is supplied; else null.</summary>
    public static GoogleCloudStatusProvider? FromEnvironment(HttpClient http)
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        string? token = Env("GCP_ACCESS_TOKEN");
        string? service = Env("K_SERVICE") ?? Env("GCP_SERVICE");
        if (service == null || (Env("K_SERVICE") == null && token == null))
            return null;
        return new GoogleCloudStatusProvider(http, service, Env("GCP_PROJECT_ID"), token);
    }

    public string Describe() => $"Cloud Monitoring, project {project ?? "(from the metadata server)"}, service {service}; answers are cached for {CacheFor.TotalSeconds:0} s";

    public FleetData GetFleetData() => Cached(ref fleet, LoadFleet);

    public UsageData GetUsageData() => Cached(ref usage, LoadUsage);

    private T Cached<T>(ref (DateTime At, T Value)? slot, Func<T> load) where T : class
    {
        lock (cacheLock)
        {
            if (slot is { } cached && DateTime.UtcNow - cached.At < CacheFor)
                return cached.Value;
            T value = load();
            slot = (DateTime.UtcNow, value);
            return value;
        }
    }

    // What to ask for ------------------------------------------------------------

    private static DateTime TopOfHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    private FleetData LoadFleet()
    {
        DateTime now = DateTime.UtcNow;
        // Hour and day buckets end at the next boundary (the API accepts a future end), so they line up with the clock.
        DateTime hourEnd = TopOfHour(now).AddHours(1);

        var instances = QueryAsync("run.googleapis.com/container/instance_count", now.AddHours(-24), now,
            TimeSpan.FromMinutes(1), "ALIGN_MAX", "REDUCE_SUM", groupBy: "metric.labels.state");
        var peak = QueryAsync("run.googleapis.com/container/instance_count", hourEnd.AddDays(-30), hourEnd,
            TimeSpan.FromHours(1), "ALIGN_MAX", "REDUCE_SUM", extraFilter: "metric.labels.state = \"active\"");
        var requests = QueryAsync("run.googleapis.com/request_count", hourEnd.AddHours(-24), hourEnd,
            TimeSpan.FromHours(1), "ALIGN_SUM", "REDUCE_SUM", groupBy: "metric.labels.response_code_class");
        var busiest = QueryAsync("run.googleapis.com/container/instance_count", now.AddHours(-24), now,
            TimeSpan.FromMinutes(1), "ALIGN_MAX", "REDUCE_MAX", extraFilter: "metric.labels.state = \"active\"");
        Task.WaitAll(instances, peak, requests, busiest);

        var data = new FleetData { AsOfUtc = now };
        data.BusiestRevisionActivePerMinute = busiest.Result.SelectMany(s => s.Points).ToList();
        foreach (var series in instances.Result)
        {
            series.Labels.TryGetValue("state", out string? state);
            if (state == "active") data.ActiveInstancesPerMinute = series.Points;
            else if (state == "idle") data.IdleInstancesPerMinute = series.Points;
        }
        data.PeakActiveInstancesPerHour = peak.Result.SelectMany(s => s.Points).ToList();
        foreach (var series in requests.Result)
            data.RequestsPerHourByClass[series.Labels.GetValueOrDefault("response_code_class") ?? "other"] = series.Points;
        return data;
    }

    private UsageData LoadUsage()
    {
        DateTime now = DateTime.UtcNow;
        DateTime monthStart = CloudReports.BillingMonthStartUtc(now);
        // One bucket for the whole month so far gives the exact total.
        TimeSpan month = TimeSpan.FromSeconds(Math.Max(60, Math.Ceiling((now - monthStart).TotalSeconds)));
        DateTime tomorrow = now.Date.AddDays(1);

        var billable = QueryAsync("run.googleapis.com/container/billable_instance_time", monthStart, now, month, "ALIGN_SUM", "REDUCE_SUM");
        var requests = QueryAsync("run.googleapis.com/request_count", monthStart, now, month, "ALIGN_SUM", "REDUCE_SUM");
        var egress = QueryAsync("run.googleapis.com/container/network/sent_bytes_count", monthStart, now, month, "ALIGN_SUM", "REDUCE_SUM");
        var daily = QueryAsync("run.googleapis.com/container/billable_instance_time", tomorrow.AddDays(-30), tomorrow,
            TimeSpan.FromDays(1), "ALIGN_SUM", "REDUCE_SUM");
        Task.WaitAll(billable, requests, egress, daily);

        static double Total(List<Series> all) => all.SelectMany(s => s.Points).Sum(p => p.Value);
        return new UsageData
        {
            MonthStartUtc = monthStart,
            AsOfUtc = now,
            BillableInstanceSeconds = Total(billable.Result),
            Requests = Total(requests.Result),
            EgressBytes = Total(egress.Result),
            BillableSecondsPerDay = daily.Result.SelectMany(s => s.Points).ToList(),
        };
    }

    // The Monitoring API ---------------------------------------------------------

    internal sealed record Series(Dictionary<string, string> Labels, List<MetricPoint> Points);

    private async Task<string> ProjectAsync()
    {
        if (project != null)
            return project;
        using var request = new HttpRequestMessage(HttpMethod.Get, MetadataProject);
        request.Headers.Add("Metadata-Flavor", "Google");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return project = (await response.Content.ReadAsStringAsync()).Trim();
    }

    private async Task<string> TokenAsync()
    {
        if (developerToken != null)
            return developerToken;
        if (metadataToken is { } cached && cached.Expires > DateTime.UtcNow)
            return cached.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, MetadataToken);
        request.Headers.Add("Metadata-Flavor", "Google");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string token = json.RootElement.GetProperty("access_token").GetString()!;
        int seconds = json.RootElement.GetProperty("expires_in").GetInt32();
        metadataToken = (token, DateTime.UtcNow.AddSeconds(seconds - 60));
        return token;
    }

    private async Task<List<Series>> QueryAsync(string metric, DateTime start, DateTime end, TimeSpan alignment,
        string aligner, string reducer, string? groupBy = null, string? extraFilter = null)
    {
        string projectId = await ProjectAsync();
        string token = await TokenAsync();
        string filter = $"metric.type = \"{metric}\" AND resource.labels.service_name = \"{service}\"" +
            (extraFilter == null ? "" : " AND " + extraFilter);

        string Url(string? pageToken)
        {
            var q = new StringBuilder();
            void Add(string name, string value) => q.Append(q.Length == 0 ? "?" : "&").Append(name).Append('=').Append(Uri.EscapeDataString(value));
            Add("filter", filter);
            Add("interval.startTime", start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            Add("interval.endTime", end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            Add("aggregation.alignmentPeriod", $"{(long)alignment.TotalSeconds}s");
            Add("aggregation.perSeriesAligner", aligner);
            Add("aggregation.crossSeriesReducer", reducer);
            if (groupBy != null) Add("aggregation.groupByFields", groupBy);
            if (pageToken != null) Add("pageToken", pageToken);
            return $"https://monitoring.googleapis.com/v3/projects/{projectId}/timeSeries{q}";
        }

        var all = new List<Series>();
        string? next = null;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(next));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // A developer's own token needs a quota project; the service's account must not send one
            // (that would need an extra role).
            if (developerToken != null)
                request.Headers.Add("x-goog-user-project", projectId);
            using var response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Monitoring API answered {(int)response.StatusCode}: {(body.Length > 400 ? body[..400] : body)}");
            all.AddRange(ParseSeries(body, out next));
        } while (next != null);
        return all;
    }

    /// <summary>The series in a timeSeries.list response, with points oldest first; sets the next page token.</summary>
    internal static List<Series> ParseSeries(string json, out string? nextPageToken)
    {
        using var doc = JsonDocument.Parse(json);
        nextPageToken = doc.RootElement.TryGetProperty("nextPageToken", out var token) && token.GetString() is { Length: > 0 } t ? t : null;
        var result = new List<Series>();
        if (!doc.RootElement.TryGetProperty("timeSeries", out var seriesList))
            return result;
        foreach (var s in seriesList.EnumerateArray())
        {
            var labels = new Dictionary<string, string>();
            if (s.TryGetProperty("metric", out var metric) && metric.TryGetProperty("labels", out var l))
                foreach (var label in l.EnumerateObject())
                    labels[label.Name] = label.Value.GetString() ?? "";
            var points = new List<MetricPoint>();
            foreach (var p in s.GetProperty("points").EnumerateArray())
            {
                var value = p.GetProperty("value");
                double number = value.TryGetProperty("doubleValue", out var d) ? d.GetDouble()
                    : value.TryGetProperty("int64Value", out var i) ? double.Parse(i.GetString()!, CultureInfo.InvariantCulture)
                    : 0;
                DateTime end = DateTime.Parse(p.GetProperty("interval").GetProperty("endTime").GetString()!,
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                points.Add(new MetricPoint(end, number));
            }
            points.Sort((a, b) => a.Time.CompareTo(b.Time));
            result.Add(new Series(labels, points));
        }
        return result;
    }
}
