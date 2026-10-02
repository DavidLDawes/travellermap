using Maps.API;
using Maps.Graphics;
using Maps.HTTP;
using Maps.Search;
using Maps.Utilities;
using System;
using System.Globalization;

namespace Maps
{
    /// <summary>
    /// The IIS (System.Web) host. Routes and handlers are host-neutral: see
    /// server/http/Routing.cs (RouteTable) and server/http/SystemWebHost.cs.
    /// </summary>
    public class GlobalAsax : System.Web.HttpApplication
    {
        protected void Application_Start(object sender, EventArgs e)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            // Search: SQLite (App_Data/search.db, built on first start if missing), or the SQL
            // Server index with the setting SearchBackend=sqlserver.
            if (AppSettings.Get("SearchBackend") == "sqlserver")
            {
                SearchEngine.Index = new SqlSearchIndex();
            }
            else
            {
                var index = SqliteSearchIndex.FromSettings();
                SearchEngine.Index = index;
                index.EnsureBuilt(message => System.Diagnostics.Trace.TraceInformation(message));
            }

            // Only this host has the GDI+ renderer.
            ImageHandlerBase.GdiRenderer = new GdiBitmapRenderer();

            System.Web.Routing.RouteTable.Routes.Add(new PortableRoutes());
        }
    }
}
