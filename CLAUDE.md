# CLAUDE.md

Guidance for Claude Code (and other contributors) working in this repository.

## What this is

Source for https://travellermap.com — a zoomable 2D map of the *Traveller* RPG universe.
Hierarchy: **world** (one hex) → **subsector** (8 wide × 10 tall hexes, lettered A–P) →
**sector** (4×4 subsectors = 32×40 hexes) → **milieu** (a whole map at one point in the
timeline, e.g. `M1105`, the default). Sectors carry metadata: allegiances/polities, borders,
routes, labels, credits and per-world remarks.

This repo is a fork of `inexorabletash/travellermap` (`origin` = `DavidLDawes/travellermap`).
Most upstream commits are **data** changes under `res/Sectors/`, not code.

## Architecture

**Portable core — `core/Maps.Core.csproj` (net48 + net10.0).** Links (doesn't move) the data
model, parsing/serialization, astrometrics, SectorMap, ResourceManager, geometry
(`server/Geometry.cs`), validation, utilities, the renderer (`RenderContext`, `RenderUtil`,
`Stylesheet`, the `AbstractGraphics` drawing abstraction, and the SkiaSharp/PDFsharp/SVG
backends), and **all request handlers and routes** from `server/`. Handlers use a host-neutral
HTTP layer, `Maps.Web.HttpContext`/`HttpRequest`/`HttpResponse` (`server/http/HttpAbstractions.cs`),
which follows System.Web's API. Only IIS-specific pieces stay in `Maps.csproj`:
`Global.asax.cs`, the System.Web adapter (`server/http/SystemWebHost.cs`), SQL Server search
(`server/search/SearchEngine.cs`), and the GDI+ renderer (`renderer=gdi`). Rules for code in the core: no
System.Web and no Windows-only System.Drawing (`Point`/`PointF`/`Color`/`RectangleF` are fine).
Use `Util.MapPath`, and `#if NETFRAMEWORK` for anything IIS-only. Visual Studio/msbuild builds only
net48; `dotnet build` (SDK 10, in `%USERPROFILE%\.dotnet\dotnet.exe`) builds both. `PLAN.md`
Phase 7 describes the migration off IIS/System.Drawing/SQL Server.

**Two hosts run the same handlers:**
- **IIS** — ASP.NET (System.Web), .NET Framework 4.8, C# 12 (pinned), Windows only (`Maps.csproj`,
  `Global.asax.cs`). Registers SQL Server search and the GDI+ renderer.
- **ASP.NET Core** — .NET 10, any OS (`host/`, see `host/README.md`). Static files, hidden paths,
  extensionless pages, CORS and the 404 page are configured in `host/Program.cs` to match
  `Web.config`; change both together. No search until Phase 7.4 (returns 503).

**Server code.**
- `server/http/Routing.cs` — `RouteTable`: every URL route (regex-based), used by both hosts.
  Route order matters: more specific patterns (e.g. `/data/{sector}/sec`) must be registered
  before catch-alls (e.g. `/data/{sector}/{subsector}`). `AddSectorPartRoutes` registers the
  data/`sec`/`tab`/`image` routes for quadrants and subsectors.
- Output must be the same on both runtimes: sort strings with `Util.StableStringComparer`
  (culture-aware ordering differs between Windows and Linux) and prefer stable sorts (`OrderBy`)
  to `List.Sort`. Settings come from `AppSettings.Get(name)` (web.config or appsettings.json).
- Query options: use `HandlerBase.GetStringOption`/`GetBoolOption`/`HasOption` (the request
  first, then route defaults). Booleans: non-zero integer or a bare flag (`?nogrid`) is true.
- `server/api/*Handler.cs` — one handler per API. Data handlers derive from `DataHandlerBase`
  (content negotiation: `accept=` query param → `Accept` header → route default → handler
  default; JSON/XML/text; JSONP via `jsonp=`). Image handlers derive from `ImageHandlerBase`
  (PNG/JPEG via SkiaSharp, SVG, or PDF via PDFsharp 6's core build, serialized behind a lock;
  text uses only the fonts in `res/fonts`).
- `server/admin/*` — admin pages (`/admin/flush`, `/admin/reindex`, `/admin/errors`, …).
  Allowed from localhost, or over HTTPS with `?key=` matching `AdminKey` in `web.config`.
- `server/SectorMap.cs` — loads `res/Sectors/milieu.tab` → per-milieu XML sector lists →
  per-sector metadata XML; resolves sectors by name/abbreviation/location.
- `server/serialization/` — parsers/writers for sector data: T5 Second Survey column format
  (`.tab`/`.sec`), legacy SEC, MSEC metadata, XML metadata.
- `server/RenderContext.cs`, `Stylesheet.cs`, `RenderUtil.cs`, `server/graphics/` — map rendering.
  `AbstractGraphics` has Skia (bitmaps), SVG and PdfSharp backends, plus the legacy GDI+
  `BitmapGraphics`; keep them working. Text layout for Skia and PDF is shared
  (`SkiaFonts.FontSet.Layout`), so PNG and PDF place text identically.
- `server/search/SearchEngine.cs` — SQL Server search index (built by `/admin/reindex`).
- Caches are **thread-affine**: one copy per worker thread, so they need no locking. Don't
  convert them to plain statics without adding locking. Anything loaded from a data file
  (`SectorMap`, `ResourceManager`, the T5SS allegiance/sophont tables, the default sector
  stylesheet) uses `ThreadLocalCache<T>` (`server/utilities/ThreadLocalCache.cs`), so
  `/admin/flush` → `CacheGeneration.InvalidateAll()` reloads it on every thread. Use it for
  any new file-backed cache; plain `ThreadLocal<T>` is fine for constant tables.
  `SectorMap.Flush()` resets only the current thread (admin reports use it to release memory).

**Client — plain ES modules, no build step.**
- `index.html` + `index.js` — main page UI (search, routes, world/sector info cards, settings).
- `map.js` — the `TravellerMap` tiled map widget, `MapService` API client, `Util`, LRU cache.
- `world_util.js` — decodes UWP/extensions/remarks into human-readable world details.
- Templates are Handlebars (loaded from cdnjs with an SRI `integrity` hash, so update the hash
  if you change the version), inlined in `<script type="text/x-handlebars-template">`.
- `sw.js` — service worker providing an offline fallback page.
- `make/` (posters, booklets, atlases, border/route makers), `print/` (world sheets),
  `borders/` (border generation), `doc/` (API/file-format docs), `tools/` (ad-hoc tools).

**Data — `res/`.**
- `res/Sectors/milieu.tab` lists the per-milieu index XML files (e.g. `M1105/M1105.xml`).
- Each sector = data file (`*.tab`, T5 column format) + metadata XML (borders, routes,
  allegiances, subsector names, credits). Schema: `res/sectors.xsd`.
- `res/t5ss/` — T5SS allegiance/sophont code tables (served at `/t5ss/*`).

## Building & running

Visual Studio 2022 (or its MSBuild) on Windows is required (see `SETUP.md` for full steps).
1. Copy `Web.config.sample` → `web.config` (git-ignored). Set `AdminKey`; connection strings
   are only needed for search. The `<runtime>` binding redirects are required for PDF output.
2. Build: `msbuild Maps.sln -t:Restore` then `msbuild Maps.sln -p:Configuration=Debug`
   (MSBuild lives at `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`).
   NuGet supplies SkiaSharp, PDFsharp 6 (core build) and the net48 reference assemblies.
3. Run: `"C:\Program Files\IIS Express\iisexpress.exe" /path:<repo> /port:50103`, or Ctrl+F5 in VS.
   Smoke test: `/api/poster?sector=Spinward%20Marches&subsector=C&accept=application/pdf`.
4. Optional: SQL Server + `/admin/reindex` to populate search. Debug builds index only
   "selected" sectors.

If PDFsharp's or SkiaSharp's dependency versions change, the build prints MSB3247 with the
binding redirects to copy into `Web.config.sample`. (Unit tests don't use them:
`TestSetup` resolves such dependencies from the test directory.)

`TreatWarningsAsErrors` is on for both configurations — new warnings break the build.

## Tests & linting

- **Unit tests** (C#, MSTest 4 from NuGet; C# 12 like the main project, nullable context
  opt-in per file). MSTest's analyzers run as errors: put `expected` before `actual` in
  assertions. Tests are in `unittests/UnitTests`. Run from VS Test Explorer or
  `vstest.console.exe unittests\UnitTests\bin\Debug\UnitTests.dll`
  (under `Common7\IDE\Extensions\TestPlatform\`). Tests run outside IIS, so `TestSetup` sets
  `Util.ContentRoot` to the repo root. Server code must resolve data files with
  `Util.MapPath("~/...")`, not `HostingEnvironment.MapPath`.
  - `RoutingTest`: URL → handler and route values, in registration order. Update it when
    adding or reordering routes.
  - `AstrometricsTest` and the JS tests share `test/fixtures/astrometrics.json`, so server
    and client coordinate math can't drift.
- **Data validation** (`DataValidationTest`, ~10 s, Debug build): every sector's data and
  metadata must parse, use defined allegiance codes, and match `res/sectors.xsd`. Existing
  problems are listed in `test/data-validation-baseline.txt`; the test fails only on errors
  not in the baseline. After fixing data, regenerate the baseline by running the test with
  `TM_UPDATE_BASELINE=1` and commit it. The same check runs on any OS with .NET 10:
  `dotnet run -c Debug --project tools/validate` (add `-- --update-baseline` or
  `-- --report out.tsv`); CI runs it on Linux. Sector index `<DataFile>`/`<MetadataFile>` names
  must match the file names' **case** exactly (Linux). Interactive equivalents: `/admin/errors`,
  `/admin/codes`, `tools/lintsec.html`.
- **JS unit tests**: `npm test` (Node's built-in `node --test`, no extra dependencies).
  Tests live in `test/unit/*.test.js`. Import `./setup.js` first; it stubs `window`,
  `location`, `localStorage`, and the `fetch` calls that `world_util.js` makes at import time,
  so `map.js` and `world_util.js` load unchanged in Node.
- **Browser tests**: with the site running on port 50103, `npm run test:browser -- --no-search`
  runs `test/APITest.html`, `ContentTest.html`, and `ImageTest.html` in headless Chrome
  (`--no-search` when there's no SQL Server search index). Or open the pages directly.
  References live in `test/refs/`. When data changes legitimately alter output, update the
  matching reference after confirming the difference is the data change:
  `npm run test:update-refs -- ref3 ref28` (or no names for all) fetches them from the running
  server. Bitmaps render with SkiaSharp and the fonts in `res/fonts`, so output is deterministic.
  Append `&renderer=gdi` to a PNG/JPEG URL to compare with the old GDI+ renderer.
- **JS lint**: `npm install` then `npm run lint` (whole repo) or `npx eslint <file>`. The flat
  config is in `eslint.config.js`. Type checking via `jsconfig.json` (`checkJs`) in editors that
  support it.

**CI** (`.github/workflows/ci.yml`): lint and JS tests on Linux; MSBuild, C# unit tests, and
data validation on Windows; browser tests too if the runner has IIS Express.

## Conventions

- C#: `#nullable enable` at the top of files, Allman braces, 4-space indent, `Maps.*` namespaces.
- JS: 2-space indent, `const`/`let`, ES modules, clang-format style (`.clang-format`).
- Cache-busting: pages load scripts as `x.js?update=<timestamp>`, and pages that import
  `map.js`/`world_util.js` pin them in an `<script type="importmap">`. When changing a shared
  module's API, bump its timestamp on **every** page that maps it, or a cached old `map.js`
  can be paired with a new `world_util.js`.
- Browser storage: use `Util.storageGet`/`storageSet`/`storageGetJSON` (they tolerate
  disabled storage and corrupt values), not `localStorage` directly.
- Coordinates: hex `XXYY` is 1-based within a sector (`0101`–`3240`). World-space ("x,y")
  coordinates are relative to Reference (Core 0140); see `server/Astrometrics.cs` and
  `Astrometrics` in `map.js` — keep the two in sync.
- Data edits: keep sector `.tab` columns aligned, use T5SS allegiance codes, and check
  `/admin/errors` afterwards. Border/route changes go in the sector metadata XML.
- Don't commit `web.config`, `bin/`, `obj/`, or `node_modules/`.
