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
| `RedirectToHttps` | `true`: redirect plain HTTP to HTTPS (except `localhost`), as `Web.config` does. Off by default. |
| `RemoveWww` | `true`: redirect `www.example.com` to `https://example.com`. Off by default. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` behind a reverse proxy (environment variable): take the client's address and scheme from `X-Forwarded-For`/`-Proto`. |

## Deploy (Docker)

The `Dockerfile` at the repository root builds a Linux image. It contains the host, the site's
files, and a search index built from the sector data in the image.

```
docker build -t travellermap .
docker run -d -p 8080:8080 -e AdminKey=YOUR_SECRET travellermap
```

- **Base image:** `aspnet:10.0-noble-chiseled` (Ubuntu with no shell or package manager), about 330 MB.
- **User:** runs as a non-root user.
- **Port:** listens on port 8080.
- **Architectures:** builds for x64 and arm64 (`docker buildx build --platform linux/arm64 ...`).
- **Updating data:** rebuild the image when the sector data changes. For a running container,
  `/admin/reindex` refreshes the search index in place; the container user owns `App_Data`.

**Behind a reverse proxy** (nginx, Caddy, a cloud load balancer), which terminates TLS:

- Set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so HTTPS is recognized and logs show
  client addresses.
- Make sure the container is reachable only through the proxy. Otherwise clients could send their
  own `X-Forwarded-*` headers.
- Do the `www.`/HTTPS redirects in the proxy, or with `RemoveWww`/`RedirectToHttps`.

**Admin pages:**

- Requests from other machines need `?key=` matching `AdminKey`, over HTTPS (directly, or
  via the proxy's `X-Forwarded-Proto`).
- Requests through Docker's port mapping or a proxy never count as local. A spoofed
  `X-Forwarded-For: 127.0.0.1` is refused.

## Compared with the IIS site

Responses are the same, byte for byte, for nearly all of the API, data, image, static, redirect,
error, POST, and admin requests (checked request by request against the IIS site). The
exceptions:

- **`renderer=gdi`** returns 400 (GDI+ is Windows/.NET Framework only).
- **PDF** bytes differ (a different build of PDFsharp); the pages render identically.
- **SVG** numbers can differ in the last digit (float formatting differs between runtimes).
- Not ported: the optional `PageFooter` module. The production redirects are opt-in settings
  (above).
