using Maps.API;
using Maps.Graphics;
using Maps.HTTP;
using Maps.Search;
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

            // Services only this host has: SQL Server search and the GDI+ renderer.
            SearchEngine.Index = new SqlSearchIndex();
            ImageHandlerBase.GdiRenderer = new GdiBitmapRenderer();

            System.Web.Routing.RouteTable.Routes.Add(new PortableRoutes());
        }
    }
}
