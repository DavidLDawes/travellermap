using Maps;
using Maps.Admin;
using Maps.Host;
using Maps.HTTP;
using Maps.Utilities;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

// The map site on ASP.NET Core: the portable route table and handlers, plus static files,
// configured like the IIS site's Web.config (CORS, hidden paths, extensionless pages, MIME
// types, static caching, 404 page). Settings (appsettings.json or environment variables):
//   SiteRoot     the site's files (the repo root); found by searching up from the app if unset
//   AdminKey     key for /admin pages from other machines (over HTTPS)
//   Renderer     "skia" (only option on this host)
//   SearchIndex  the search index file (default ~/App_Data/search.db; built if missing)
//   RedirectToHttps, RemoveWww   "true" for Web.config's production redirects (off by default)
// Behind a reverse proxy, set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true so the client's address
// and scheme come from X-Forwarded-For/-Proto (see host/README.md).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
// Legacy SEC output uses Windows-1252, which .NET (not .NET Framework) only has via this provider.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
string contentRoot = builder.Configuration["SiteRoot"] is string configured && configured.Length > 0
    ? Path.GetFullPath(configured)
    : FindSiteRoot(AppContext.BaseDirectory) ?? FindSiteRoot(Environment.CurrentDirectory)
        ?? throw new InvalidOperationException("Site files not found; set SiteRoot to the directory containing index.html and res/.");

Util.ContentRoot = contentRoot;
AppSettings.Provider = name => builder.Configuration[name];
_ = HandlerBase.StartupTime; // For /admin/uptime

// Compress text, as IIS's dynamic/static compression does by default (not JSON or images).
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = new[] { "text/plain", "text/html", "text/xml", "text/css", "application/javascript", "application/x-javascript" };
});
builder.Services.AddSingleton<Microsoft.AspNetCore.ResponseCompression.IResponseCompressionProvider, SuccessOnlyCompressionProvider>();

var app = builder.Build();
app.Logger.LogInformation("Serving {ContentRoot}", contentRoot);

// Search index (SQLite), built in the background if missing.
var searchIndex = Maps.Search.SqliteSearchIndex.FromSettings();
Maps.Search.SearchEngine.Index = searchIndex;
searchIndex.EnsureBuilt(message => app.Logger.LogInformation("{Message}", message));

// Platform metrics for /admin/fleet and /admin/usage: on Cloud Run, or locally with GCP_ACCESS_TOKEN (see host/README.md).
CloudStatus.Provider = GoogleCloudStatusProvider.FromEnvironment(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

var files = new PhysicalFileProvider(contentRoot);

// The site's 404 page (Web.config httpErrors).
async Task SendNotFound(HttpContext context)
{
    context.Response.StatusCode = 404;
    var page = files.GetFileInfo("404.html");
    if (page.Exists && context.Request.Method != "HEAD")
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(page);
    }
}

// Request counters for /admin/status (outermost, so the timing covers everything below).
app.Use(async (context, next) =>
{
    var stats = RequestStats.Current;
    TimeSpan sinceStart = DateTime.Now - HandlerBase.StartupTime;
    long started = Stopwatch.GetTimestamp();
    stats.Begin();
    int status = 500; // if next() throws
    try
    {
        await next(context);
        status = context.Response.StatusCode;
    }
    finally
    {
        stats.End(context.Request.Path.Value ?? "/", status, RequestStats.Since(started), sinceStart);
    }
});

app.UseResponseCompression();

// Production redirects (Web.config rewrite rules): strip "www." and/or require HTTPS (but not
// for localhost). Opt-in, since they usually belong in the reverse proxy.
bool removeWww = string.Equals(builder.Configuration["RemoveWww"], "true", StringComparison.OrdinalIgnoreCase);
bool redirectToHttps = string.Equals(builder.Configuration["RedirectToHttps"], "true", StringComparison.OrdinalIgnoreCase);
if (removeWww || redirectToHttps)
{
    app.Use((context, next) =>
    {
        string host = context.Request.Host.Host;
        bool www = removeWww && host.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        bool insecure = redirectToHttps && !context.Request.IsHttps && host != "localhost";
        if (!www && !insecure)
            return next(context);
        string target = "https://" + (www ? host.Substring(4) : host) +
            context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        context.Response.Redirect(target, permanent: true);
        return Task.CompletedTask;
    });
}

// CORS (as Web.config's customHeaders).
app.Use((context, next) =>
{
    context.Response.Headers.AccessControlAllowOrigin = "*";
    return next(context);
});

// Paths IIS hides (Web.config hiddenSegments, plus IIS's defaults: bin, web.config, ...).
var hiddenSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "package.json", "package-lock.json", "yarn.lock", "jsconfig.json", "jsconfig.sw.json", "eslint.config.js",
    ".env", ".gitignore", "Maps.csproj.user", ".git", ".vscode", ".vs", "node_modules", ".clang-format", "server",
    "bin", "obj", "App_Data", "web.config", "Web.config.sample", "host", "core", "unittests",
};
app.Use((context, next) =>
{
    string path = context.Request.Path.Value ?? "";
    if (path.Split('/').Any(hiddenSegments.Contains))
        return SendNotFound(context);
    return next(context);
});

// Extensionless pages, e.g. /doc/api -> /doc/api.html (Web.config rewrite rule).
var extensionless = new Regex(@"^/((doc|make|print|tools)/\w+)$", RegexOptions.Compiled);
app.Use((context, next) =>
{
    var match = extensionless.Match(context.Request.Path.Value ?? "");
    if (match.Success)
        context.Request.Path = "/" + match.Groups[1].Value + ".html";
    return next(context);
});

// API, data, and admin routes.
app.Use(async (context, next) =>
{
    var match = RouteTable.Match(context.Request.Path.Value ?? "/");
    if (match == null)
    {
        await next(context);
        return;
    }
    var (route, values) = match.Value;
    var adapted = await AspNetCoreContext.CreateAsync(context, values);
    route.CreateHandler(values).ProcessRequest(adapted);
    await adapted.SendAsync();
});

// Static files, cached for 24 hours, with the site's extra MIME types.
var types = new FileExtensionContentTypeProvider();
foreach (var ext in new[] { ".sec", ".msec", ".t5col", ".t5tab", ".tab" })
    types.Mappings[ext] = "text/plain";
types.Mappings[".json"] = "application/json";
types.Mappings[".svg"] = "image/svg+xml";
types.Mappings[".js"] = "application/javascript";
foreach (var ext in new[] { ".dll", ".exe", ".pdb", ".config", ".cs", ".csproj", ".sln" })
    types.Mappings.Remove(ext);

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    ContentTypeProvider = types,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=86400",
});

// Anything else: the site's 404 page.
app.Run(SendNotFound);

app.Run();

// The directory containing the site (index.html and res/Sectors), searching up from start.
static string? FindSiteRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "index.html")) && Directory.Exists(Path.Combine(dir.FullName, "res", "Sectors")))
            return dir.FullName;
    }
    return null;
}

// Like IIS, compress only successful responses (not error pages).
internal sealed class SuccessOnlyCompressionProvider : Microsoft.AspNetCore.ResponseCompression.ResponseCompressionProvider
{
    public SuccessOnlyCompressionProvider(IServiceProvider services, Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.ResponseCompression.ResponseCompressionOptions> options)
        : base(services, options) { }

    public override bool ShouldCompressResponse(HttpContext context)
        => context.Response.StatusCode is >= 200 and < 300 && base.ShouldCompressResponse(context);
}
