# Data issues for sector authors

Problems the data validator still reports that need someone who knows the source material. Everything with a mechanical or evidence-based answer has been fixed (see `PLAN.md`, Phase 4b). Regenerate the underlying list with `dotnet run -c Debug --project tools/validate -- --report out.tsv`.

## Undefined allegiance codes

World allegiance codes that no allegiance table defines: not the stock T5SS codes (`res/t5ss/allegiance_codes.tab`, including their 2-letter legacy forms), and not the sector's own `<Allegiances>`. For each, I checked case variants of stock codes, definitions in neighboring sectors and in the same sector in other milieux, and the sector's border labels, and found nothing conclusive. The fix is an `<Allegiance Code="..">Name</Allegiance>` in the sector's metadata file, or a corrected code in the data.

199 worlds in 13 sectors.

### Rim Worlds (M1105)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Ne` | 25 | 0232 Ishikawa, 0235 Tenko, 0237 Hanshin, 0238 Hornsby, ... |  |
| `Ou` | 8 | 2836 Salsburg, 2937 Christchurch, 3034 Flavian, 3035 Guinevere, ... |  |
| `Mg` | 6 | 2330 Khmer, 2331 Knott, 2428 De Naburn, 2429 Hrothgar, ... |  |
| `Cc` | 5 | 1727 Weinberg, 1728 Missoula, 1729 Cilia, 1826 Barcelona, ... |  |
| `Rr` | 5 | 2530 Payntour, 2630 Sensei, 2731 Berenice, 2732 Bimler's World, ... |  |
| `Ce` | 4 | 0122 Kaufman, 0222 Estalia, 0323 Methuselah, 0423 Whitman |  |
| `Nf` | 4 | 1114 Nouveau Rwanda, 1214 Bismark, 1315 Hilbrand, 1414 Danilo |  |
| `Kc` | 4 | 1925 Kiseti, 2025 Cornelio, 2126 Kalora, 2226 Mackinac |  |
| `Ch` | 2 | 3123 Davics, 3223 Elerius |  |
| `Hu` | 1 | 3026 Losner |  |
| `He` | 1 | 3126 Heron |  |

### Gvurrdon (M1248)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Cc` | 48 | 0130, 0131, 0137, 0220, ... | Cc is defined in some Faraway sectors, with other meanings; nothing nearby defines it. |

### Constance (M1105)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Mr` | 17 | 1604 Aitken, 1605 Rognuald, 1606 Smith's World, 1607 Tibetan, ... |  |
| `Nt` | 9 | 1133 McVicar, 1134 Attaturk, 1231 Terehan, 1232 Oustyn, ... |  |
| `Ne` | 8 | 0101 Hanlo, 0301 Meginhardt, 0302 Yeats, 0303 Sabre, ... |  |
| `De` | 6 | 2232 Gallalee, 2233 Riyad, 2234 Glisten, 2331 Barstock, ... |  |

### Banners (M1105)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `In` | 13 | 0231 Service, 0607 Genesis Yes, 0707 Genesis Loyal, 0708 Genesis Wonder, ... | Neighboring Ahriman defines In as "Inyx Economic Sphere", but this sector has no metadata file to define it in. |

### Muarne (M1105)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Ka` | 9 | 2407 Kitram, 2506, 2507, 2508, ... |  |

### Ziafrplians (M1900)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Ds` | 8 | 1319 Lie Zedl, 1322 Essadap, 1419 Pliqel, 1422 Ebam Plench, ... | Only Outer Reaches (Faraway, far away) defines Ds, as "Dagon Supremacy". The sector's Ds border has no label either. |

### Windhorn (M1201)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Ts` | 5 | 2722 Votelthag, 2822 Aengkhekkuerr, 2919 Khokorrghoer, 3020 Vaerrgkhir, ... |  |

### Hanstone (M1105)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `In` | 3 | 1130 Bracing, 1618 Coral, 1815 Affinity Forward | Neighboring Ahriman defines In as "Inyx Economic Sphere", but this sector has no metadata file to define it in. |

### Lishun (M1120)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Cv` | 3 | 0205 Shuka, 0321 Shela, 0513 Gishinridu |  |

### Spica (M1201)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `H1` | 2 | 2532 Shipsboat, 3040 Import Yards |  |

### Numbis (M1105)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `J1` | 1 | 0240 Otou |  |

### Zarushagar (M1201)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Vc` | 1 | 1535 Zero Zero |  |

### Dashis (M1900)

| Code | Worlds | Examples | Notes |
| --- | --- | --- | --- |
| `Hg` | 1 | 1024 Khurisi |  |

## Undefined border allegiances

| Sector | Code | Notes |
| --- | --- | --- |
| Dhuerorrg (M1900) | `Tangle` | Two gray borders labeled "Tangle Zoukh" and "Tangle Usulak"; no worlds use the code. If these are regions rather than polities, `<Region>` may fit better than `<Border>`. |
| Dhuerorrg (M1900) | `Tangle` | Two gray borders labeled "Tangle Zoukh" and "Tangle Usulak"; no worlds use the code. If these are regions rather than polities, `<Region>` may fit better than `<Border>`. |
| Ziafrplians (M1900) | `Ds` | Only Outer Reaches (Faraway, far away) defines Ds, as "Dagon Supremacy". The sector's Ds border has no label either. |

## Unparseable world lines

| Sector | Problem | Line |
| --- | --- | --- |
| Ruupiin (M1105) | Parse Error: Invalid eHex digit: '?' | `1420 Agneex          A5327RK-F Na Po                                   - K - ?34   Kk M7 III` |
| Yiklerdanzh (Meshan Saga) (M1105) | Unexpected value for B: 'Z' | `1438 Mia'Brinrnz          C4128CF-8 Ic Ni Na                              Z    101   ZC` |

- Ruupiin 1420 Agneex: the PBG is `?34`. The population multiplier is unknown; T5SS uses a digit (0-9).
- Crucis Margin (Judges Guild), `res/Sectors/Faraway/JG-CrucisMargin.sec`: five uninhabited worlds
  have no tech level (`X200000--`), so the parser skips them as non-UWP lines and they don't
  appear on the map: 1206 Prudnik, 1701 Palompi, 2008 Taginae, 3006 Hun-kuo and 3202 Shen.
  Writing `-0` would show them, but whether the source meant TL 0 is unknown.
- Yiklerdanzh (Meshan Saga) 1438: base code `Z` isn't a T5 base code. A hint: the M1105 Yiklerzdanzh file's own legend says that in this region's legacy codes "X, Z, Y are Zhodani relay route, Naval base, Depot", so `Z` is probably a Zhodani naval base; the world's allegiance `ZC` isn't one the server treats as Zhodani, so the legacy code isn't translated.
