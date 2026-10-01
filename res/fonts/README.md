# Bundled fonts

The SkiaSharp renderer (`server/graphics/SkiaGraphics.cs`) draws text only with these files, never
with fonts installed on the machine. Rendered images are then the same on Windows, Linux, and in
a container. `SkiaFonts.cs` maps the family names that stylesheets ask for to these fonts:

| Requested family | Bundled font | Notes |
| --- | --- | --- |
| Arial | Liberation Sans 2.1.5 | Metrically compatible with Arial |
| Calibri | Carlito | Metrically compatible with Calibri |
| Courier New | Liberation Mono 2.1.5 | Metrically compatible with Courier New |
| Georgia | Gelasio (variable; default instance) | Metrically compatible with Georgia |
| Comic Sans MS | Comic Neue | Similar style, not metrically compatible |
| Arial Unicode MS, Segoe UI Symbol, Wingdings | DejaVu Sans 2.37 | Symbols (stars, diamonds); also the first fallback for characters other fonts lack |
| (fallback) | Noto Sans Symbols 2 | Second fallback, for symbols DejaVu Sans lacks (e.g. ⌖, the anomaly marker) |

Licenses: Liberation fonts and Carlito, Gelasio, Comic Neue, Noto Sans Symbols 2 are under the SIL Open Font License
1.1 (`*-LICENSE.txt`, `*-OFL.txt`); DejaVu fonts are under the Bitstream Vera / public domain
license in `DejaVuFonts-LICENSE.txt`. Sources: [Liberation](https://github.com/liberationfonts/liberation-fonts),
[DejaVu](https://dejavu-fonts.github.io/), and [Google Fonts](https://github.com/google/fonts)
(`ofl/carlito`, `ofl/gelasio`, `ofl/comicneue`, `ofl/notosanssymbols2`).
