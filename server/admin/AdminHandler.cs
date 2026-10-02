#nullable enable
using Maps.Search;
using Maps.Utilities;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Maps.Web;

namespace Maps.Admin
{
    internal abstract class AdminHandlerBase : Maps.HandlerBase, Maps.HTTP.IRequestHandler
    {
        // Value shipped in Web.config.sample; never accepted as a real key.
        internal const string PlaceholderAdminKey = "YOUR_KEY_HERE";

        public static bool AdminAuthorized(HttpContext context)
        {
            if (context.Request.IsLocal)
                return true;

            if (!context.Request.IsSecureConnection)
                return false;

            return IsValidAdminKey(context.Request["key"],
                AppSettings.Get("AdminKey"));
        }

        /// <summary>
        /// True if the provided key matches the configured key. A missing, empty, or
        /// placeholder configured key never matches, so remote admin access is disabled
        /// unless a real key has been set.
        /// </summary>
        internal static bool IsValidAdminKey(string? provided, string? configured)
        {
            if (string.IsNullOrEmpty(configured) || configured == PlaceholderAdminKey)
                return false;
            if (provided == null)
                return false;

            // Constant-time comparison, to avoid leaking the key via response timing.
            byte[] a = Encoding.UTF8.GetBytes(provided);
            byte[] b = Encoding.UTF8.GetBytes(configured);
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Min(a.Length, b.Length); ++i)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public void ProcessRequest(HttpContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            context.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            if (!AdminAuthorized(context))
            {
                context.Response.TrySkipIisCustomErrors = true;
                context.Response.StatusCode = 403;
                context.Response.StatusDescription = "Forbidden";
                context.Response.ContentType = ContentTypes.Text.Plain;
                context.Response.Output.WriteLine("Incorrect secret or connection not secure.");
                context.Response.Flush();
                return;
            }
            Process(context, ResourceManager.GetInstance());
        }

        protected abstract void Process(HttpContext context, ResourceManager resourceManager);

        protected static string? GetStringOption(HttpContext context, string name)
            => GetStringOption(context.Request, Defaults(context), name);

        protected static bool GetBoolOption(HttpContext context, string name, bool defaultValue = false)
            => GetBoolOption(context.Request, Defaults(context), name, defaultValue);
    }

    internal class AdminHandler : AdminHandlerBase
    {
        protected override void Process(HttpContext context, ResourceManager resourceManager)
        {
            context.ExtendTimeout(TimeSpan.FromHours(1)); // An hour should be plenty
            context.Response.ContentType = ContentTypes.Text.Html;
            context.Response.BufferOutput = false;

            void WriteLine(string s) { context.Response.Write(s); context.Response.Write("\n"); }

            WriteLine("<!DOCTYPE html>");
            WriteLine("<title>Admin Page</title>");
            WriteLine("<style>");
            using (var reader = new StreamReader(Util.MapPath("~/site.css"), Encoding.UTF8))
            {
                while (!reader.EndOfStream)
                {
                    WriteLine(reader.ReadLine() ?? "");
                }
            }
            WriteLine("</style>");
#if DEBUG
            WriteLine("<h1>Traveller Map - Administration [DEBUG]</h1>");
#else
            WriteLine("<h1>Traveller Map - Administration</h1>");
#endif
            context.Response.Flush();

            string? action = GetStringOption(context, "action");
            switch (action)
            {
                case "reindex": Reindex(context); return;
                case "flush": Flush(context); return;
                case "profile": Profile(context); return;
                case "uptime": Uptime(context); return;
            }
            Write(context.Response, "Unknown action: <pre>" + action + "</pre>");
            Write(context.Response, "<b>&Omega;</b>");
        }

        private static void Write(HttpResponse response, string line)
        {
            response.Write("<div>");
            response.Write(line);
            response.Write("</div>\n");
            response.Flush();
        }

        private static void Flush(HttpContext context)
        {
            // Every thread's sector map, resource cache, and code tables reload on next use.
            CacheGeneration.InvalidateAll();

            context.ClearHostCache();

            Write(context.Response, $"Caches flushed on all threads (generation {CacheGeneration.Current}).");
            Write(context.Response, "<b>&Omega;</b>");
        }

        private static void WriteStat<T>(HttpResponse response, string name, T value) where T : notnull
        {
            Write(response, name + ": " + value.ToString());
        }
        private static void Uptime(HttpContext context)
        {
            TimeSpan uptime = DateTime.Now - StartupTime;

            Write(context.Response, $"Uptime: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s<br>");
            Write(context.Response, "<b>&Omega;</b>");
        }
        private static void Profile(HttpContext context)
        {
            foreach (var stat in context.HostCacheStats())
                WriteStat(context.Response, stat.Key, stat.Value);
            var process = System.Diagnostics.Process.GetCurrentProcess();
            WriteStat(context.Response, "Process.Id", process.Id);
            WriteStat(context.Response, "Process.MinWorkingSet", process.MinWorkingSet);
            WriteStat(context.Response, "Process.MaxWorkingSet", process.MaxWorkingSet);
            WriteStat(context.Response, "Process.PeakWorkingSet64", process.PeakWorkingSet64);
            WriteStat(context.Response, "Process.PagedMemorySize64", process.PagedMemorySize64);
            WriteStat(context.Response, "Process.PeakPagedMemorySize64", process.PeakPagedMemorySize64);
            WriteStat(context.Response, "Process.PrivateMemorySize64", process.PrivateMemorySize64);
            WriteStat(context.Response, "Process.StartTime", process.StartTime);
            WriteStat(context.Response, "Process.VirtualMemorySize64", process.VirtualMemorySize64);
            WriteStat(context.Response, "Process.WorkingSet64", process.WorkingSet64);
            WriteStat(context.Response, "Process.Threads.Count", process.Threads.Count);
            Write(context.Response, "<b>&Omega;</b>");
        }

        private static void Reindex(HttpContext context)
        {
            Write(context.Response, "Initializing resource manager...");
            ResourceManager resourceManager = ResourceManager.GetDedicatedInstance();

            SearchEngine.PopulateDatabase(resourceManager, s => Write(context.Response, s));

            Write(context.Response, "<b>&Omega;</b>");
        }
    }

}
