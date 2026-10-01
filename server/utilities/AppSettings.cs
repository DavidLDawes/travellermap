#nullable enable
using System;

namespace Maps.Utilities
{
    /// <summary>
    /// Application settings (e.g. AdminKey, Renderer, PageFooter), from the host's configuration:
    /// web.config appSettings on IIS, appsettings.json/environment variables on ASP.NET Core.
    /// </summary>
    internal static class AppSettings
    {
        /// <summary>Looks up a setting by name; set by the host at startup.</summary>
        public static Func<string, string?> Provider { get; set; } = DefaultProvider;

        public static string? Get(string name) => Provider(name);

        private static string? DefaultProvider(string name)
        {
#if NETFRAMEWORK
            return System.Configuration.ConfigurationManager.AppSettings[name];
#else
            return null;
#endif
        }
    }
}
