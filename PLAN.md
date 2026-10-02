# Improvement Plan

Follows from `REVIEW.md` (item IDs like B1/O2/S3 refer to it).

## Decisions (2026-09-30)
- **PDFsharp**: go straight to 6.x. **Done** (Phase 0).
- **Upstream**: decide per change. Small bug fixes and clear, simple enhancements are
  candidates for PRs to `inexorabletash/travellermap`. Work toward **moving off Microsoft
  infrastructure** (IIS, System.Web, SQL Server, System.Drawing) stays in this fork.

Tag legend: **[up]** = upstream PR candidate, **[fork]** = fork only.

### What "moving off Microsoft" changes
- Write fixes as **plain functions with no `System.Web` dependency** where practical (admin key
  check, size limits, option parsing, and so on) so the logic and its tests carry over to a
  future host unchanged.
- **Dropped:** the `System.Data.SqlClient` → `Microsoft.Data.SqlClient` upgrade. Search storage
  will probably change anyway, so time goes into putting search behind an interface instead.
- **Kept:** MSTest 3.x. It runs on modern .NET on Linux, so the tests migrate too.
- PDFsharp uses the **GDI** build for now, because rendering is built on System.Drawing
  (`XFont(Font)`, `XImage.FromGdiPlusImage`). The cross-platform build only fits once
  System.Drawing is replaced (Phase 7).

---

## Phase 0 — Baseline — DONE
- PDFsharp-GDI 1.5 (hand-built DLL) → **6.2.4 via NuGet** in `Maps.csproj` and `UnitTests.csproj`.
- Added `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 (build-only), so the .NET 4.8
  Developer Pack no longer has to be installed. This also makes a CI build possible.
- `Web.config.sample`: binding redirects for PDFsharp's dependencies; `targetFramework` 4.6.1 → 4.8.
- Verified under IIS Express: PDF, PNG and SVG posters; a PDF jump map in the candy style
  (embedded images, alpha, hex clipping); 8 PDF requests at once. The PDF output was checked
  visually against the PNG.
- Unit tests: 17/19 pass. Both failures predate this work (see Phase 4, item 1).
- `SETUP.md`, `README.md` and `CLAUDE.md` updated. **[up]** candidate once it has run a while.

## Phase 1 — Security and safety fixes [up] — DONE (`bce47ea1`, branch `phase1-fixes`)
Done: B1, B3, B6, B7, B8, B9, B10, B11 (narrowed: 500 only for exception types that always
mean a server fault, because the code also uses `ApplicationException`/`Exception` for input
errors), B16. Unit tests: `HandlerTest.cs`. Checked end to end under IIS Express, except
B1, which can't be exercised from localhost (local requests are always allowed); the unit
test covers it. Bitmap cap: 2^27 pixels; vector cap: 2^20 per side.
Upstream note: cherry-picking onto upstream `main` will conflict in `UnitTests.csproj`
(the PDFsharp reference lines differ). The fix is to keep upstream's reference and add
`HandlerTest.cs` + `System.Web`.

Original scope:
- B1 admin key bypass. Put the check in a pure function: reject a missing or empty key and the
  placeholder, compare in constant time, and unit-test it.
- B3 duplicate 403 body.
- B6/B7: one shared image-size check (64-bit math) used by tiles and posters.
- B8 missing User-Agent, B9 negative rotation, B10 unencoded redirect values.
- **B16 (new)**: `PosterHandler.cs:192` — `'A' + index` gives a number, so titles and filenames
  read "Subsector 67" instead of "Subsector C". Fix: `(char)('A' + index)`.
- B11: return 400 only for input errors, 500 plus logging otherwise.
- Check: new unit tests; `curl` against the admin page, oversized posters, and subsector poster titles.

## Phase 2 — Browser fixes and JavaScript tests [up] — DONE (branch `phase2-client`)
Done: B4, B5, B15, O2, plus B17 (Zhodani `Bases`, latent), B18 (`redir.html` accepted any URL
scheme), B19 (blocked popup). `npm test` runs 13 tests in `test/unit/`. The script cache-busters
are bumped on every page, because `world_util.js` now depends on new `Util` methods.
Checked in headless Chrome against the running site: corrupt storage, rapid world selection,
closing the card mid-load, and `redir.html`. All 4 bugs reproduce on the old code and are
fixed on the new code. The check script is not committed; it could become a CI browser test
in Phase 4.

Original scope:
- Add `"test": "node --test"` and `test/unit/`; export `LRUCache`, `Util.makeURL`, and the
  `world_util` decoders.
- B4 stale world card (request token), B5 guarded preferences, B15 service-worker fallback.
- O2: rebuild `LRUCache` on `Map`, with tests.
- Bump the `index.js?update=` cache-buster.

## Phase 3 — Server cache correctness — B2 DONE (branch `phase3-flush`) [up]
New `ThreadLocalCache<T>` plus a global `CacheGeneration` counter. `/admin/flush` now reloads
`SectorMap`, `ResourceManager`, the T5SS allegiance/sophont tables, and the default stylesheet
on every thread. The code tables previously needed an app restart. `SectorMap.Flush()` keeps
its thread-local meaning for the admin report pages.
Checked under IIS Express with 64 concurrent clients after editing a sector's metadata. On the
old code, 1 of 300 requests saw the edit after a flush; on the new code, 300 of 300. The
allegiance table edit reached 300 of 300 after a flush. `/admin/errors` and `/admin/codes`
still work. Unit tests: `CacheTest.cs` (4 tests).
**O1 DONE** (branch `shared-sectormap`, after Phase 7):
- `ThreadLocalCache` → `SharedCache`: one copy of the sector map, resource cache, code tables,
  stylesheet, and decoded images for all threads, instead of one per worker thread.
- The per-thread copies cost ~30 MB each up front, plus every parsed sector's worlds, which are
  cached on `Sector` objects for the life of the map.
- Made safe for concurrent use: `MilieuMap` (lookups can add Dotmap sectors) and the
  `SectorStylesheet` memo are concurrent; `ResourceManager`'s LRU is locked;
  `World.CalculatedImportance` is an atomic `int`.
- Measured on the .NET 10 host, 5,640 mixed requests from 16 concurrent clients:
  **1,193 MB and still climbing → 273 MB, flat** (−77%), and slightly faster (55 s → 51 s).
- Rendering is unchanged (all 45 ImageTest references byte-identical); browser suites pass on
  both hosts.

Original scope:
- B2: a generation counter makes `/admin/flush` reach every thread's `SectorMap` and
  `ResourceManager`. **[up]**
- O1: one shared, immutable `SectorMap`. **[fork]**, and only if memory matters. It's
  more naturally done during the Phase 7 host migration.

## Phase 4 — Tests and CI — DONE (branch `phase4-tests`)
- Fixed both long-failing tests. They needed `Util.MapPath` and `Util.ContentRoot`, so data
  files load outside IIS; `MSECWriterTest`'s expectation had been stale since a 2017 color
  change.
- `DataValidator` and `DataValidationTest`, with a baseline ratchet of 725 known errors.
  `sectors.xsd` now allows metadata attributes on `<Sector>`, which removed about 1,400
  false schema errors.
- New unit tests: SecondSurvey (found and fixed B20), Astrometrics (fixture shared with JS),
  PathFinder, and the route table. 45 C# tests and 14 JS tests.
- `npm run test:browser` runs the three browser suites headlessly. Added JSONP tests and
  refreshed stale references (legend legacy codes after B20; Regina's world count; the
  overview image).
- GitHub Actions CI (Linux JS job, Windows build/test/data job). It has not run on GitHub yet.
- Search query parsing tests were not done: `ParseQuery` is private and tied to SQL. That
  fits better with Phase 7's search interface.

Original scope:
1. Fix the two old test failures: update the `ColumnParserTest` input; make
   `Sector`'s default stylesheet path injectable so `MSECWriterTest` runs outside IIS. **[up]**
2. Data validation tool: load every milieu, sector and metadata file, check the XML against
   `sectors.xsd`, and report unknown codes. Build it as a console entry point with no
   `System.Web`. **[up]**
3. Unit tests: SectorParser round-trips, SecondSurvey codes, Astrometrics (plus agreement with
   `map.js`), the route table, PathFinder, search query parsing, JSONP.
4. GitHub Actions: a Linux job (eslint, `npm test`, XML schema check) and a Windows job
   (msbuild restore/build, vstest). The Windows job now works because the reference
   assemblies come from NuGet. **[fork]** first; offer upstream if wanted.

## Phase 4b — Data quality backlog (revisit errors and warnings)
The Phase 4 ratchet stops *new* errors; this item works down the existing ones. Analysis from
2026-10-01 (full report: run `DataValidationTest` with `TM_VALIDATION_REPORT=<path>`).

**Where the 187,263 warnings and 725 errors come from**
- 112k warnings (60%) are in the **Zhodani Core Route** fan project (tag `ZCR`, 2005, legacy
  `.sec`). `/admin/errors` deliberately skips non-curated tags; the Phase 4 validator didn't.
- The **T5SS-generated official sectors** (files headed "Generated file - DO NOT MODIFY") have
  only **1,110** warnings. Their source is `res/t5ss/data`, not the generated files.
- 64k warnings are in **hand-maintained official sectors** (e.g. Koog, Rfigh, Hadji, Harbinger;
  HIWG-era data). These use **pre-T5 trade-code conventions**: population-0 outposts coded
  `Ba Lo Ni`, and Ni on population 1–3. Classic Traveller defined Lo as ≤3 and Ni as ≤6; T5
  (which the checker implements) defines them as 1–3 and 4–6. `Extraneous code: Ni/Lo` alone
  is 73k warnings.
- 74% of all warnings (137,776) are trade codes, which are fully determined by the UWP.

**Proposals, with measured effect** (cumulative; warnings / errors)

| # | Change | Owner | After |
|---|---|---|---|
| — | Today | | 187,263 / 725 |
| T1 | Validator warnings use the same scope as `/admin/errors` (OTU/Apocryphal/Faraway); errors still checked everywhere | us [up] | 74,931 / 725 |
| T2 | Demote generation-rule checks (TL = mods+1D, Gov/Law = Flux) to Hint: they test whether a world *could be randomly generated*, and canon worlds deviate on purpose. Still visible on `/admin/errors` as hints | us [up] | 60,057 / 725 |
| T3 | `World.Validate` crashes on placeholder `{Ix}`/`(Ex)` (`----`), reported as 40 "Parse Error"s in Nadir. Data is fine (production shows the worlds); treat dashes as absent | us [up] | 60,057 / 685 |
| T4 | `sectors.xsd`: `Label/@Color` is optional (the server defaults it to amber) | us [up] | 60,057 / 664 |
| D1 | Tool that recomputes T5 trade codes from the UWP for a sector file, producing a reviewable diff; apply per sector with maintainer agreement (changes published data conventions) | tool: us; data: maintainers | 18,906 / 664 |
| D2 | Same tool: population-0 Ex efficiency −5 / infrastructure rules (mechanical) | as D1 | 9,875 / 664 |
| D3 | Rim Worlds (Faraway): 262 worlds use lowercase `na`; almost certainly `Na` (Non-aligned). Codes are case-sensitive, and real codes differ by case (`Cs`/`CS`), so fix the data, not the lookup | sector author (active upstream contributor) | 9,875 / 402 |

**Smaller data fixes worth doing** (each confirmed by reading the file)
- *Visible on the map:* `M1201/Spinward Marches.xml:219` uses `label=` instead of `Label=`, so the
  "Federation of Arden" border label never renders. `M1105/Kidunal.xml:44,48` uses
  `Wraplabel=` instead of `WrapLabel=`. `M1105/Astron.xml:42-44` puts `WrapLabel` on `<Label>`
  (should be `Wrap`; value is false, so there's no visible effect).
- Vanguard Reaches: zero-length route `2340 → 2340`; delete it.
- Undefined border allegiances (8): `Ec` (Kruse), `Tangle` (Dhuerorrg ×2), `Ds` (Ziafrplians),
  `Dw` and `MF` (The Beyond), `Au` and `OC` (Alte Grenzen). Add `<Allegiance>` definitions
  (names needed from the source material).
- Remaining undefined world allegiance codes (~330, after `na`): `Cc` in Gvurrdon M1248 (48;
  defined for Faraway sectors but not here), `Ne`, `Dw`, `Hf`, `Ms`, `Mr`, … Review per sector.
- 38 schema errors are stray text inside `<Routes>`/`<Borders>`/`<Sector>` (22 in `Rzakki.xml`),
  probably notes that should be XML comments.
- `Tabs`, `Era`, `Source-Milieu` attributes aren't read by the server; remove them, or declare
  them as ignored in the schema.

**Suggested order:** T1–T4 first (small code changes, no data judgement, upstream-friendly), then
the visible-on-map fixes, then D1/D2 as an opt-in tool, then D3 and the allegiance definitions
with their authors. Regenerate the baseline after each step to lock in the gains.

**Status: all actionable items DONE** (branch `data-fixes`). Errors 664 → **204**; warnings
~60,300 → **10,325**.
- Visible-on-map fixes, the zero-length route, and all 57 schema errors (stray text from
  non-breaking spaces and typos, `Tabs=` for `Tags=` in 11 M1900 sectors, Alte Grenzen's
  `<Allegiance>` wrapper, loose CDATA credits now in `<Credits>`).
- Allegiance definitions from evidence in the repo: the sector's own border label, a companion
  sector, neighbors, or another milieu. Case typos of stock codes fixed in the data (Rim Worlds
  `na` and others; 272 worlds).
- D1/D2: `tools/tradecodes` recomputes UWP-determined trade codes and mechanical (Ex) values,
  using the validator's own rules (`World.TradeCodeRules`). Applied: 31,315 worlds in 215
  files (generated T5SS files excluded).
- Group 1 mechanical fixes (branch `datafix-group1`), also in `tools/tradecodes`: warnings
  9,972 → **6,839**, errors unchanged.
  - (Ex) efficiency `+0` → `+1` (1,022 worlds).
  - PBG population multiplier → 0 when Pop is 0 (780; the 41 written `X` are left alone).
  - `{Ix}` set to the calculated importance (573, keeping each file's brace style).
  - Legacy SEC header/note lines the parser ignored → `#` comments (372 lines). Five malformed
    world lines in JG-CrucisMargin.sec are left as they are, and the tool reports them.
  - T5SS-generated M1105 sectors: `--t5ss` edits the sources in `res/t5ss/data` (392 Pop-0
    (Ex) values, 1 missing `Pr`), then `perl update_world_data.pl --source-data` regenerates them.
- The rest needs sector authors: **`DATA-ISSUES.md`** lists the 199 worlds with undefined codes
  (13 sectors), 3 border codes, and 2 unparseable lines, with what was checked.

Earlier: **T1–T4 DONE** (branch `data-quality-tools`). Measured result: **664 errors** (as predicted)
and **~60,300 warnings** (from 187,263). The baseline dropped 61 entries. `/admin/errors` shows
the TL/Gov/Law checks as `Hint:` lines, and Nadir no longer reports parse errors. Remaining:
the visible-on-map fixes, D1–D3, and the other data items above (owner review needed).

## Phase 5 — Remaining version updates — DONE (branch `phase5-updates`) [up]
- MSTest v1 → **MSTest 4.4.1** NuGet (supports net462+ and modern .NET). Its analyzers found 7 swapped
  expected/actual assertions and 1 always-true assertion; fixed.
- SRI hash on all 7 Handlebars script tags (verified a wrong hash blocks the script).
- Removed the TLS line (no outgoing calls). If outgoing calls are added, set
  `<httpRuntime targetFramework="4.8">` so they use OS TLS defaults.
- C# 8.0 → **12.0** pinned in both projects (no new warnings).
- `@types/handlebars`: **kept**. The review was wrong: it provides the real types for the
  CDN-loaded global.
- ~~Microsoft.Data.SqlClient~~: dropped (see Decisions).
- **CI image tests** (follow-up from Phase 4): the CI artifact showed two causes.
  1. About half the "failures" were images that didn't load in time (byte-identical on
     re-fetch). Fixed by limiting concurrency and reporting load errors.
  2. The rest are **ClearType** sub-pixel fringes on text (14–802 px, ≤0.035% of an image).
     `ImageHandlerBase` renders text with `TextRenderingHint.ClearTypeGridFit`, whose fringe
     colors vary by machine. A pixel tolerance would also hide real regressions such as a
     missing label (similar size).

  **Decided: (c), done on branch `grayscale-text`.** PNG text now uses `AntiAliasGridFit`, and all
  references were regenerated. Grayscale text still jitters slightly between environments, and
  even between server runs on one machine: a few edge pixels up to ~35 levels, plus 2–3 isolated
  full-intensity flips in small text. The root cause wasn't found: it isn't DPI (all 96), request
  order, or worker threads. So ImageTest now tolerates sparse differences (hard ≥64 up to 0.01%
  of pixels, soft up to 0.1%). That was validated both ways: jittered references pass, and a
  covered label, a single covered letter, and a +12 color shift all fail.
  **Follow-up done:** ImageTest passed on the Windows CI runner (PR #6's CI run, 45/46 with only
  the intentional bad example failing), so it fails CI again (`--informational` removed).

## Phase 6 — Simplifications — DONE (branch `phase6-simplify`, built on `phase5-updates`) [up, case by case]
Each refactor was checked for unchanged behavior with more than the unit tests:
- **S1** One option parser in `HandlerBase`, shared by the APIs and admin pages
  (`OptionParsingTest`). Edge-case changes: admin `=2` is now true, and API booleans accept bare
  flags (`?nogrid`).
- **S2** Route helper for the quadrant/subsector groups. A full route-table dump (67 routes:
  pattern, handler, defaults, order) is identical before and after.
- **S3** `ProduceResponse` split into style options, DPR, SVG/PDF/bitmap writers, and the data
  URI. 14 output variants are byte-identical before and after (PDFs equal apart from the
  per-request XMP timestamps/UUIDs and font-subset tags, which differ between any two requests).
- **S4** Poster domains as a table; domain posters are byte-identical (`PosterDomainsTest`).
- **B14** dead route parameter; **O3** `TOP 1`.

Not done, with reasons:
- **S5 (legacy style bits): keep.** `doc/api.html` promises old URLs using the deprecated
  `options` style flags keep working.
- **S6 (compatibility code): skip.** The `webkit` fullscreen fallbacks still serve iPads before
  iPadOS 16.4. The legacy `.csproj` IDE properties are ignored by MSBuild and would go away in a
  Phase 7 project conversion.
- **B13 (LIKE wildcards): not a bug.** Wildcards are a documented search feature (`*` → `%` in
  `SearchHandler`), so escaping them would break search.

Notes:
- Since Phase 1's bitmap cap, the large undocumented `domain` posters (e.g. `chartedspace`, 16×8
  sectors) need an explicit smaller `scale`. Before Phase 1 they tried to allocate ~580M-pixel
  bitmaps.
- **ImageTest also drifts locally:** during Phase 6, 11 text-heavy references started failing
  on this dev machine *with code that had passed earlier the same day*. The cause was confirmed
  by building the earlier commit, which renders the same new output. It's the same ClearType
  machine-dependence as CI (Phase 5 note). That makes the ClearType decision more pressing:
  refreshing the references would only fix them for one machine, temporarily.

## Phase 7 — Moving off Microsoft infrastructure [fork]
**Decisions (2026-10-01):** target **.NET 10 LTS** (supported to Nov 2028; .NET 8 ends Nov 2026).
The SDK is installed user-locally in `%USERPROFILE%\.dotnet`. Search moves to **SQLite**.

**Scope (survey of 60 server files, ~17.6k lines):**
- System.Web: all 30 handler/host files. The data core needed only small fixes.
- Windows-only System.Drawing (fonts, bitmaps, graphics): 14 rendering files. `RenderContext`,
  `RenderUtil` and `Stylesheet` use `Font`/`Graphics` directly (about 50 uses), on top of the
  three graphics backends.
- SQL Server: `SearchEngine`, plus the admin reindex. PDFsharp: the PDF backend.

**Approach:** one step at a time, keeping the IIS site working throughout. Shared code builds
for both net48 and net10.0. The browser suites and the image/content references serve as
parity tests between the old and new hosts.

### 7.1 Portable core library — DONE (branch `phase7-core`)
- `core/Maps.Core.csproj` links (doesn't move) 25 `server/` files: the data model, parsing and
  serialization, astrometrics, SecondSurvey, SectorMap, ResourceManager, validation, and
  utilities. `Maps.csproj` references it instead of compiling them.
  - `msbuild.exe`/Visual Studio builds net48 only, so the IIS site and Windows CI need nothing new.
  - `dotnet build` (SDK 10) builds net48 and net10.0.
- Portable geometry (`BorderPath`, `PathUtil`, `ClipPath`, `AbstractPath`, hex edges) moved from
  `RenderUtil`/`AbstractGraphics` to `server/Geometry.cs`. Path point types are now byte
  constants with GDI+'s values. `TravellerColors` moved to `ColorUtil.cs`.
- `tools/validate`: data validation on .NET 10. It finds the same 664 errors as the net48 test,
  in 6.6 s. A new **Linux CI job** runs it.
- **Found by running on .NET 10 / targeting Linux:**
  - `XmlReaderSettings.Clone()` loses the validation handler on .NET 10 (schema errors threw).
    Fixed with fresh settings per file.
  - **31 sector-index references had the wrong file-name case** (e.g. `listanaya.sec` vs
    `Listanaya.sec`). Windows ignores case, Linux doesn't, so those sectors would have been
    missing on a Linux host. Fixed (case-only edits to `M1105.xml`/`M1248.xml`; upstream-friendly).
- 46 nullable warnings that surfaced only on net10.0 (the .NET 10 base library is annotated)
  were fixed and the exemption removed: warnings are errors on both targets.

### 7.2 Rendering on SkiaSharp (largest step)
- **7.2a DONE — portable drawing abstraction.** `AbstractGraphics` and its types no longer use
  GDI+ or PDFsharp types:
  - `AbstractMatrix` has its own math, ported from PDFsharp's `XMatrix` with its type shortcuts.
  - `AbstractFont` is families/size/style.
  - Font metrics come from `GetFontMetrics`.
  - `AbstractImage` holds path/URL.
  - Own `FontStyle`/`SmoothingMode` enums; `TextGridFit` replaces the raw `Graphics` property.
  - GDI+ code is in `graphics/GdiSupport.cs`; backends cache native objects per font/image.
- **7.2b DONE — renderer in the core.** `RenderContext`, `RenderUtil`, `Stylesheet`,
  `VectorObject`, `AbstractGraphics` and `SVGGraphics` build in `Maps.Core` for net48 and
  net10.0. SVG takes an `ITextMeasurer` (GDI+ on the IIS host). `IsRaster` replaces an
  `is BitmapGraphics` check. The bitmap and PDF backends stay in `Maps.csproj` (GDI+).
- 7.2a/b verification: 28 PNG/SVG/PDF renders (all styles, rotations, images, overlays, data
  URIs) are byte-identical to the previous build. PDFs differ only in per-request XMP metadata.
  - Gotcha: GDI+ renders an image slightly differently the first time after server start
    (glyph caching). Compare a second, warm render.
- **7.2c DONE — SkiaSharp bitmap backend.**
  - `SkiaGraphics` (SkiaSharp 4.153, in the core) is the default PNG/JPEG renderer on the IIS host.
  - The `Renderer` app setting (`skia`/`gdi`) chooses the renderer; the hidden
    `renderer=gdi|skia` query option overrides it per request, for side-by-side comparison.
  - SVG text measurement follows the same choice.
  - It mirrors GDI+ where layout depends on it:
    - pens narrower than a pixel draw as hairlines;
    - dash patterns scale with the pen width;
    - nonzero fill rule;
    - GDI+'s cardinal-spline formula;
    - string alignment;
    - `MeasureString` padding: measured on GDI+ as advance × 1.03 + ⅓ em by line spacing + ⅛ em.
  - **Fonts:** bundled in `res/fonts` (8.4 MB, open licenses, see its README) and never taken from
    the system:
    - Liberation Sans/Mono for Arial/Courier New, Carlito for Calibri, Gelasio for Georgia, and
      Comic Neue for Comic Sans MS.
    - DejaVu Sans, then Noto Sans Symbols 2, for symbols and any character a font lacks.
    - Wingdings isn't used; glyphs fall back to Unicode symbols, so ◆ and ★ are a bit different.
  - **Output is deterministic:** byte-identical across runs and server restarts, unlike GDI+.
    ImageTest references were regenerated once, deliberately, after side-by-side review of every
    style:
    - default, print, atlas, FASA, Mongoose, terminal, draft, candy;
    - jump maps, rotations, macro and galaxy scales, the data overview.
    - Small text at macro scales is cleaner than GDI+'s, which mis-spaced letters at tiny sizes.
  - `npm run test:update-refs` regenerates references from a running server, for future
    deliberate rendering changes.
  - Unit tests:
    - `AbstractMatrix` matches `XMatrix` exactly over random operation sequences.
    - The bundled fonts cover every glyph and overlay symbol.
    - Family resolution.
    - A render/encode smoke test.
    - GDI-compatible measurement.
- **7.2d DONE — PDF without GDI+** (branch `phase7-pdf`).
  - `PdfSharpGraphics` now uses PDFsharp 6's **core** build (cross-platform) and lives in the core.
    PDFsharp-GDI is gone from every project.
  - A font resolver serves the bundled fonts. Text uses the same fallback runs and layout as
    the Skia renderer (`FontSet.Layout`, shared). Faded images are made with SkiaSharp.
  - Why not Skia's PDF backend (`SKDocument`)? SkiaSharp's native build has no font
    subsetter, so it embeds whole fonts: a subsector poster was 800 KB against 80 KB before.
    PDFsharp subsets per document, so the new PDFs are **smaller** than before (50 KB; the
    bundled fonts subset better than the Windows ones).
  - Verified against the previous PDFsharp-GDI output, rasterized with PDFium:
    - same page sizes and same extractable text;
    - about 1% of pixels differ (font substitutes), as with PNG;
    - data-URI PDFs and concurrent requests checked too.
  - `TestSetup` resolves dependencies (e.g. `Microsoft.Extensions.Logging.Abstractions`) from the
    test directory, because the test host doesn't apply binding redirects.
- **Linux:** a Linux host needs `SkiaSharp.NativeAssets.Linux.NoDependencies` (in the 7.3 host
  project). Rendering on Linux gets verified by running the browser suites against the 7.3 host.

### 7.3 ASP.NET Core host (net10.0) — DONE (branch `phase7-host`)
- **Shared handlers.** Instead of rewriting the handlers for ASP.NET Core, they moved to the
  core unchanged in shape, on a host-neutral HTTP layer (`Maps.Web`, the subset of System.Web
  they use). Both hosts adapt it:
  - IIS: `server/http/SystemWebHost.cs`.
  - ASP.NET Core: `host/AspNetCoreContext.cs`. It reads request bodies up front and buffers
    responses, since the handlers are synchronous.
- **Routes** moved from `Global.asax` to a portable `RouteTable` (`server/http/Routing.cs`);
  `RoutingTest` tests it.
- **Search** sits behind `ISearchIndex` (`SearchEngine.Index`). IIS registers SQL Server; the new
  host has none yet (503).
- **GDI+ renderer** sits behind `ILegacyBitmapRenderer`; IIS only.
- **`host/`**: static files, hidden paths, extensionless pages, CORS, MIME types, caching,
  compression (success responses only, as IIS) and the 404 page, all as in `Web.config`.
- **Parity, checked response by response** (90 requests: API, data, images, static,
  redirects, errors, POST uploads, admin):
  - The refactored IIS site matches `main` except two intended changes (below).
  - The new host matches the IIS site except: search (503), `renderer=gdi` (400), PDF bytes
    (a different PDFsharp build; pages render identically), and one SVG float's last digit.
  - Browser suites pass on both hosts. A new CI job runs them against the host on Linux.
- **Made deterministic across runtimes** (found by the comparison):
  - MSEC output used `List.Sort`, which is unstable and orders differently on .NET Framework
    and .NET. It's now stable (file order); `msec_legend.txt` was regenerated.
  - Code lists sorted culture-aware (NLS vs ICU disagree on e.g. "K'kr"). They now use
    `Util.StableStringComparer` (ordinal, case-insensitive).
  - The XML declaration and namespace order are pinned to .NET Framework's.
  - Windows-1252 needs `CodePagesEncodingProvider` on .NET.
- **Same images on Windows and Linux.** The first Linux CI run showed text rendered differently:
  SkiaSharp rasterizes glyphs with DirectWrite on Windows and FreeType on Linux, which hint,
  measure and place glyphs differently (~2% of pixels). Text is now drawn as glyph outlines,
  filled by Skia, with advances and vertical metrics from the font's own tables
  (`server/graphics/FontTables.cs`).
  - Most test images are now byte-identical across the two OSes; the rest differ by a few
    levels on isolated pixels.
  - Text is unhinted, so slightly softer. The ImageTest references were regenerated (34 with
    text).
  - Checked locally by running the host in a Linux container (Docker).
- **Not ported:** the `PageFooter` module, and the production redirects (HTTPS, `www.`), which
  belong in the reverse proxy (7.5).
- **Gotchas:**
  - ASP.NET Core reserves the `contentRoot` setting, so the host's is `SiteRoot`.
  - `Maps.Host.exe` looks for a machine-wide .NET; with the user-local SDK run
    `dotnet Maps.Host.dll`.
  - Stopping a background `dotnet run` leaves its child process holding the build output.

### 7.4 Search on SQLite — DONE (branch `phase7-search`)
- **`SqliteSearchIndex`** is the search index on both hosts. It uses the same tables and
  queries as SQL Server; text columns are `COLLATE NOCASE` to match SQL Server's
  case-insensitive default, and a C# Soundex backs `like:` queries.
  - The file is `App_Data/search.db` (git-ignored, ~25 MB).
  - It's built in the background on first start if missing; searches return 503 meanwhile.
  - `/admin/reindex` and `tools/reindex` rebuild it.
  - All sectors take ~10–15 s (120,564 worlds), so Debug builds no longer index only "selected"
    sectors.
  - A rebuild writes a new file and swaps it in; searches keep working (checked with 120
    concurrent searches during a reindex).
- **`SearchQuery`** parses queries for both backends; SQL concatenation differs per dialect.
  The SQL Server index (`SearchBackend=sqlserver`, IIS only) uses it too.
- **Tests:**
  - 13 new unit tests: the query-parsing tests deferred from Phase 4 (words, quotes, wildcards,
    every operator, type words, sector+hex, both dialects), Soundex, and end-to-end searches
    against an index built from `res/Sectors`.
  - The browser suites now run with search on both hosts and in CI: APITest 81/81, with no
    expected failures.
- **Native library loading** (`SqliteNative`): SQLitePCLRaw's own loader fails under IIS.
  - ASP.NET shadow-copies assemblies without their native files, and its fallback path is
    URL-escaped, which breaks paths with spaces.
  - So the core loads `e_sqlite3` itself: `bin\runtimes\<arch>\native` on .NET Framework,
    the runtime's resolution on .NET.
- **Build workaround:** `Directory.Build.targets` drops a `win-arm` native file that
  SQLitePCLRaw 2.1.12's .NET Framework targets reference but the package lacks.

### 7.5 Config, container, deployment — DONE (branch `phase7-deploy`)
- **`Dockerfile`** (multi-stage):
  - The build stage publishes the host for the target architecture (x64/arm64) and builds the
    search index from the image's sector data (~5 s).
  - The runtime stage is `aspnet:10.0-noble-chiseled`: no shell, non-root, port 8080, ~330 MB
    (the Debian base was 523 MB).
  - `.dockerignore` keeps local build outputs (over 1 GB) out of the build context.
- **Settings:** `appsettings.json`/environment variables (from 7.3), plus Web.config's
  production redirects as opt-in `RedirectToHttps`/`RemoveWww`.
- **Reverse proxy:** `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Forwarded requests never count as
  local, so a spoofed `X-Forwarded-For: 127.0.0.1` can't reach the admin pages without the key.
  Checked: redirects, proxied HTTPS, and admin key/HTTPS rules (8 cases).
- **CI:** a new job builds the image and runs the browser suites against the container.
- **IIS host:** kept, since upstream deploys on IIS; both hosts run the same code and pass the
  same suites.

Originally planned:
- `appsettings.json` + environment variables (admin key, paths). A Dockerfile (Linux, .NET 10
  runtime + fonts + data). CI builds the image and runs the browser suites against it.
- Retire the IIS host once the new host passes everything (or keep both while upstream uses IIS).
