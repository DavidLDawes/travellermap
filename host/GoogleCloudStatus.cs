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
    private const string MetadataRegion = "http://metadata.google.internal/computeMetadata/v1/instance/region";

    private readonly HttpClient http;
    private readonly string service;
    private readonly string? developerToken;
    private readonly string? stateBucket;
    private string? project;
    private string? region;
    private (string Token, DateTime Expires)? metadataToken;

    private readonly object cacheLock = new();
    private (DateTime At, FleetData Value)? fleet;
    private (DateTime At, UsageData Value)? usage;
    private (DateTime At, BudgetData Value)? budget;

    /// <param name="stateBucket">The cost-alert responder's bucket; defaults to "{project}-alert-state".</param>
    public GoogleCloudStatusProvider(HttpClient http, string service, string? project, string? developerToken,
        string? region = null, string? stateBucket = null)
    {
        this.http = http;
        this.service = service;
        this.project = project;
        this.developerToken = developerToken;
        this.region = region;
        this.stateBucket = stateBucket;
    }

    /// <summary>A provider if this is Cloud Run (K_SERVICE is set) or a developer token is supplied; else null.</summary>
    public static GoogleCloudStatusProvider? FromEnvironment(HttpClient http)
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        string? token = Env("GCP_ACCESS_TOKEN");
        string? service = Env("K_SERVICE") ?? Env("GCP_SERVICE");
        if (service == null || (Env("K_SERVICE") == null && token == null))
            return null;
        return new GoogleCloudStatusProvider(http, service, Env("GCP_PROJECT_ID"), token, Env("GCP_REGION"), Env("ALERT_STATE_BUCKET"));
    }

    public string Describe() => $"Google Cloud, project {project ?? "(from the metadata server)"}, service {service}; answers are cached for {CacheFor.TotalSeconds:0} s";

    public FleetData GetFleetData() => Cached(ref fleet, LoadFleet);

    public UsageData GetUsageData() => Cached(ref usage, LoadUsage);

    public BudgetData GetBudgetData() => Cached(ref budget, LoadBudget);

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
            all.AddRange(ParseSeries((await GetAsync(Url(next)))!, out next));
        } while (next != null);
        return all;
    }

    /// <summary>GET with the account's token; throws with the API's own error text. Null for a 404 if asked.</summary>
    private async Task<string?> GetAsync(string url, bool notFoundIsNull = false)
    {
        string projectId = await ProjectAsync();
        string token = await TokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // A developer's own token needs a quota project; the service's account must not send one
        // (that would need an extra role).
        if (developerToken != null)
            request.Headers.Add("x-goog-user-project", projectId);
        using var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (notFoundIsNull && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{new Uri(url).Host} answered {(int)response.StatusCode}: {(body.Length > 400 ? body[..400] : body)}");
        return body;
    }

    // Budget, alerts and the kill switch -----------------------------------------

    private BudgetData LoadBudget()
    {
        var data = new BudgetData { AsOfUtc = DateTime.UtcNow };

        // Each source is read on its own, so one that fails (a missing permission, say) is reported
        // on the page without hiding the rest.
        async Task Guard(string what, Func<Task> read)
        {
            try { await read(); }
            catch (Exception ex) { lock (data.Problems) data.Problems.Add($"{what}: {ex.GetBaseException().Message}"); }
        }

        async Task<string> Bucket() => stateBucket ?? $"{await ProjectAsync()}-alert-state";
        async Task<string?> StateFile(string name) =>
            await GetAsync($"https://storage.googleapis.com/storage/v1/b/{await Bucket()}/o/{Uri.EscapeDataString(name)}?alt=media", notFoundIsNull: true);

        var tasks = new[]
        {
            Guard("Budget notification", async () => data.Latest = ParseNotification(await StateFile("latest-budget.json"))),
            Guard("Last alert", async () => data.LastAlert = ParseLastAlert(await StateFile("latest-alert.json"))),
            Guard("Threshold history", async () => data.Announcements = ParseAnnouncements((await GetAsync(
                $"https://storage.googleapis.com/storage/v1/b/{await Bucket()}/o?prefix=announced%2F&fields=items(name%2CtimeCreated)"))!)),
            Guard("Site access", async () => data.SitePublic = ParseSiteIsPublic((await GetAsync(
                $"https://run.googleapis.com/v2/projects/{await ProjectAsync()}/locations/{await RegionAsync()}/services/{service}:getIamPolicy"))!)),
            Guard("Alert policies", async () => data.Policies = ParsePolicies((await GetAsync(
                $"https://monitoring.googleapis.com/v3/projects/{await ProjectAsync()}/alertPolicies"))!)),
            Guard("Open incidents", async () => data.OpenIncidents = ParseIncidents((await GetAsync(
                $"https://monitoring.googleapis.com/v3/projects/{await ProjectAsync()}/alerts?filter={Uri.EscapeDataString("state = \"OPEN\"")}"))!)),
        };
        Task.WaitAll(tasks);
        return data;
    }

    private async Task<string> RegionAsync()
    {
        if (region != null)
            return region;
        using var request = new HttpRequestMessage(HttpMethod.Get, MetadataRegion);
        request.Headers.Add("Metadata-Flavor", "Google");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        // "projects/123456/regions/us-central1"
        return region = (await response.Content.ReadAsStringAsync()).Trim().Split('/').Last();
    }

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    internal static BudgetNotification? ParseNotification(string? json)
    {
        if (json == null) return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new BudgetNotification
        {
            ReceivedUtc = ParseTime(r.GetProperty("receivedAt").GetString()!),
            BudgetName = r.TryGetProperty("budgetDisplayName", out var name) ? name.GetString() ?? "" : "",
            CostAmount = r.GetProperty("costAmount").GetDouble(),
            BudgetAmount = r.GetProperty("budgetAmount").GetDouble(),
            Currency = r.TryGetProperty("currencyCode", out var c) && c.GetString() is { Length: > 0 } code ? code : "USD",
            CostIntervalStartUtc = r.TryGetProperty("costIntervalStart", out var s) && s.ValueKind == JsonValueKind.String ? ParseTime(s.GetString()!) : null,
        };
    }

    internal static LastAlertInfo? ParseLastAlert(string? json)
    {
        if (json == null) return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new LastAlertInfo
        {
            TimeUtc = ParseTime(r.GetProperty("time").GetString()!),
            Title = r.GetProperty("title").GetString() ?? "",
            Priority = r.GetProperty("priority").GetInt32(),
            Drill = r.TryGetProperty("drill", out var d) && d.ValueKind == JsonValueKind.True,
            CutOff = r.TryGetProperty("cutOff", out var o) && o.ValueKind == JsonValueKind.True,
        };
    }

    /// <summary>Objects under announced/ in a storage list response ({} when there are none).</summary>
    internal static List<Announcement> ParseAnnouncements(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<Announcement>();
        if (!doc.RootElement.TryGetProperty("items", out var items))
            return result;
        foreach (var item in items.EnumerateArray())
            result.Add(new Announcement
            {
                Key = item.GetProperty("name").GetString()!.Substring("announced/".Length),
                TimeUtc = ParseTime(item.GetProperty("timeCreated").GetString()!),
            });
        return result;
    }

    /// <summary>True if allUsers can invoke the service (the cost cutoff removes this).</summary>
    internal static bool ParseSiteIsPublic(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("bindings", out var bindings))
            return false;
        return bindings.EnumerateArray().Any(b =>
            b.GetProperty("role").GetString() == "roles/run.invoker" &&
            b.TryGetProperty("members", out var members) &&
            members.EnumerateArray().Any(m => m.GetString() == "allUsers"));
    }

    internal static List<PolicyInfo> ParsePolicies(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<PolicyInfo>();
        if (!doc.RootElement.TryGetProperty("alertPolicies", out var policies))
            return result;
        foreach (var p in policies.EnumerateArray())
            result.Add(new PolicyInfo
            {
                Name = p.GetProperty("displayName").GetString() ?? "",
                // "enabled" is omitted when true on some responses; only an explicit false disables.
                Enabled = !(p.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False),
                Severity = p.TryGetProperty("userLabels", out var labels) && labels.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "" : "",
            });
        return result;
    }

    internal static List<IncidentInfo> ParseIncidents(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<IncidentInfo>();
        if (!doc.RootElement.TryGetProperty("alerts", out var alerts))
            return result;
        foreach (var a in alerts.EnumerateArray())
        {
            var policy = a.TryGetProperty("policy", out var p) ? p : default;
            result.Add(new IncidentInfo
            {
                Policy = policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "(unnamed policy)",
                OpenedUtc = ParseTime(a.GetProperty("openTime").GetString()!),
                Severity = policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("userLabels", out var l) && l.TryGetProperty("severity", out var s) ? s.GetString() ?? "" : "",
            });
        }
        return result;
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
