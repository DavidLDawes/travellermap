# .NET 10 host

`Maps.Host` runs the site on ASP.NET Core (.NET 10), on Windows, Linux, or macOS. It serves the
same routes and handlers as the IIS site (they live in the portable core, `core/Maps.Core.csproj`)
and the same static files, configured like the IIS site's `Web.config`.

## Run

From the repository root, with the .NET 10 SDK:

```
dotnet run -c Debug --project host --urls http://localhost:5080
```

Then open http://localhost:5080/. The browser suites run against it with
`npm run test:browser -- --base http://localhost:5080`.

If `dotnet` is the user-local install (`%USERPROFILE%\.dotnet\dotnet.exe`), run the built DLL
with that same `dotnet`, since `Maps.Host.exe` looks for a machine-wide .NET 10:
`dotnet host/bin/Debug/net10.0/Maps.Host.dll --urls http://localhost:5080`.

## Settings

In `appsettings.json`, or as environment variables (e.g. `AdminKey=...`):

| Setting | Meaning |
| --- | --- |
| `SiteRoot` | Directory with the site's files (`index.html`, `res/`). Default: found by searching up from the app's directory, so running from the repo needs nothing. Set it when deploying. |
| `AdminKey` | Key for `/admin/*` from other machines (over HTTPS). Local requests are always allowed. |
| `Renderer` | `skia` (the only renderer on this host; `gdi` needs the IIS host). |
| `SearchIndex` | The SQLite search index file. Default `~/App_Data/search.db`; built on first start if missing. |

## Compared with the IIS site

Responses are the same, byte for byte, for nearly all of the API, data, image, static, redirect,
error, POST, and admin requests (checked request by request against the IIS site). The
exceptions:

- **`renderer=gdi`** returns 400 (GDI+ is Windows/.NET Framework only).
- **PDF** bytes differ (a different build of PDFsharp); the pages render identically.
- **SVG** numbers can differ in the last digit (float formatting differs between runtimes).
- Not ported: the optional `PageFooter` module, and the production-only `Web.config` rewrites
  (redirect to HTTPS, strip `www.`), which belong in the reverse proxy.
