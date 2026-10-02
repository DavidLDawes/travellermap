using Maps;
using Maps.Admin;
using Maps.Serialization;
using Maps.Utilities;
using System.Text;
using System.Text.RegularExpressions;

// Mechanical data repairs: recomputes the values the data validator can derive from the rest of a
// world's data, editing sector data files in place:
//   dotnet run --project tools/tradecodes                     report what would change
//   dotnet run --project tools/tradecodes -- --apply          change the files
//   dotnet run --project tools/tradecodes -- --apply Koog     only sectors whose name contains "Koog"
//   dotnet run --project tools/tradecodes -- --t5ss --apply   the T5SS sources in res/t5ss/data
//
// Scope: the sectors the validator checks (tags OTU, Apocryphal, Faraway), except files generated
// from T5SS data ("Generated file - DO NOT MODIFY"). Fix those with --t5ss, then regenerate them:
//   cd res/t5ss && perl update_world_data.pl --source-data
//
// Per world:
// - Trade codes: removes codes the UWP rules out, adds codes it requires (in T5SS order, after the
//   world's other trade codes), and adds Di for Pop 0 with TL 1+ unless a Di(sophont) code is there.
//   Other remarks (sophonts, Cp/Cs/Cx, Ht, etc.) are kept, in order.
// - (Ex), T5 formats only: infrastructure 0 and efficiency -5 if Pop 0; infrastructure = importance
//   (min 0) if Pop 1-3; efficiency 0 is written +1. Resource Units are recomputed where a file has
//   an RU column.
// - {Ix}, T5 formats only: set to the calculated importance (keeping the brace style).
// - PBG: a population multiplier of 1-9 becomes 0 if Pop is 0.
// - Stars, T5 formats only: the largest star moves to the front, as the primary.
// Only those fields change; T5 columns are widened when longer values need room.
// Importance depends on trade codes, so run again after a run that changed codes.
//
// Per file, legacy SEC only: lines the parser ignores as "non-UWP data" (headers, notes) become
// "#" comments, and whitespace-only lines become empty. Lines that look like malformed worlds are
// reported, not changed.

bool apply = args.Contains("--apply");
bool t5ss = args.Contains("--t5ss");
string[] filters = args.Where(a => !a.StartsWith("--")).ToArray();

string? repo = Environment.GetEnvironmentVariable("TM_REPO_ROOT") ?? FindRepoRoot(Environment.CurrentDirectory)
    ?? FindRepoRoot(AppContext.BaseDirectory);
if (repo == null)
{
    Console.Error.WriteLine("Could not find the repository root (Maps.sln); run from inside the repo or set TM_REPO_ROOT.");
    return 2;
}
Util.ContentRoot = repo;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
var totals = new Totals();

if (t5ss)
{
    foreach (string path in Directory.GetFiles(Path.Combine(repo, "res", "t5ss", "data"), "*.tab").OrderBy(p => p, StringComparer.Ordinal))
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (filters.Length > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            continue;
        ProcessFile(path, name, isSec: false, text =>
        {
            var worlds = new WorldCollection();
            using var reader = new StringReader(text);
            new TabDelimitedParser().Parse(reader, worlds, null);
            return worlds;
        });
    }
}
else
{
    var map = SectorMap.GetInstance();
    var resources = ResourceManager.GetDedicatedInstance();
    var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var sector in map.Sectors.Where(s => s.DataFile != null && DataValidator.IsCurated(s)))
    {
        string name = sector.Names.Count > 0 ? sector.Names[0].Text : "?";
        if (filters.Length > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            continue;
        string path = Util.MapPath(sector.DataFile!.FileName);
        if (!done.Add(path) || !File.Exists(path))
            continue;
        ProcessFile(path, name, isSec: sector.DataFile.Type == "SEC", _ => sector.GetWorlds(resources, cacheResults: false));
    }
}

Console.WriteLine($"{(apply ? "Changed" : "Would change")} {totals.Worlds} worlds in {totals.Files} files: " +
    $"+{totals.Added} -{totals.Removed} trade codes, {totals.Ex} (Ex), {totals.Ix} {{Ix}}, {totals.Pbg} PBG, {totals.Stars} stars, {totals.Comments} lines commented.");
return 0;

void ProcessFile(string path, string name, bool isSec, Func<string, WorldCollection?> load)
{
    byte[] raw = File.ReadAllBytes(path);
    bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
    string text;
    try
    {
        text = utf8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
    }
    catch (DecoderFallbackException)
    {
        Console.WriteLine($"SKIP {Rel(path)}: not UTF-8");
        return;
    }
    if (text.Contains("Generated file - DO NOT MODIFY"))
        return;

    WorldCollection? worlds = load(text);
    if (worlds == null)
        return;

    string nl = text.Contains("\r\n") ? "\r\n" : "\n";
    var lines = text.Split(nl).ToList();
    var editor = LineEditor.For(lines);
    if (editor == null)
    {
        Console.WriteLine($"SKIP {Rel(path)}: format not recognized");
        return;
    }

    int changedWorlds = 0, added = 0, removed = 0, exFixes = 0, ixFixes = 0, pbgFixes = 0, stellarFixes = 0;
    var samples = new List<string>();
    for (int i = 0; i < lines.Count; ++i)
    {
        var fields = editor.Read(lines, i);
        if (fields == null)
            continue;
        if (!Regex.IsMatch(fields.Hex, "^[0-9]{4}$"))
            continue;
        var hex = new Hex(fields.Hex);
        if (!hex.IsValid)
            continue;
        World? world = worlds[hex.X, hex.Y];
        if (world == null)
            continue;

        // With an unknown UWP, only the star order can be fixed.
        int a = 0, r = 0;
        var fix = world.UWP.Contains('?') || world.UWP == "XXXXXXX-X"
            ? fields with { Stellar = fields.Stellar == null ? null : FixStellar(fields.Stellar) }
            : new Fields(fields.Hex,
                FixRemarks(fields.Remarks, world, out a, out r),
                fields.Economic == null ? null : FixEconomic(fields.Economic, world),
                fields.Importance == null ? null : FixImportance(fields.Importance, world),
                fields.Pbg == null ? null : FixPbg(fields.Pbg, world),
                fields.Stellar == null ? null : FixStellar(fields.Stellar));
        if (fix == fields)
            continue;

        var changes = new List<string>();
        if (fix.Remarks != fields.Remarks) changes.Add($"'{fields.Remarks}' -> '{fix.Remarks}'");
        if (fix.Economic != fields.Economic) { changes.Add($"{fields.Economic} -> {fix.Economic}"); ++exFixes; }
        if (fix.Importance != fields.Importance) { changes.Add($"{fields.Importance} -> {fix.Importance}"); ++ixFixes; }
        if (fix.Pbg != fields.Pbg) { changes.Add($"PBG {fields.Pbg} -> {fix.Pbg}"); ++pbgFixes; }
        if (fix.Stellar != fields.Stellar) { changes.Add($"'{fields.Stellar}' -> '{fix.Stellar}'"); ++stellarFixes; }
        if (samples.Count < 3)
            samples.Add($"    {fields.Hex} {world.UWP}: {string.Join("  ", changes)}");
        editor.Write(lines, i, fields, fix);
        ++changedWorlds; added += a; removed += r;
    }
    if (changedWorlds > 0)
        editor.Finish(lines);

    // Legacy SEC: comment out the lines the parser ignores as non-UWP data.
    int comments = 0;
    if (isSec && editor is SecEditor)
    {
        for (int i = 0; i < lines.Count; ++i)
        {
            string line = lines[i];
            if (line.Length == 0 || "#$@".Contains(line[0]) || SecEditor.UwpRegex.IsMatch(line))
                continue;
            if (line.Trim().Length == 0)
            {
                lines[i] = "";
            }
            else if (Regex.IsMatch(line, @"\b[0-9]{4}\b") && Regex.IsMatch(line, @"\b[A-EX?][0-9A-Z?]{4,}"))
            {
                Console.WriteLine($"  {Rel(path)}, line {i + 1}: looks like a malformed world, left alone: {line.Trim()}");
                continue;
            }
            else
            {
                lines[i] = "# " + line;
            }
            ++comments;
        }
    }

    if (changedWorlds == 0 && comments == 0)
        return;
    ++totals.Files; totals.Worlds += changedWorlds; totals.Added += added; totals.Removed += removed;
    totals.Ex += exFixes; totals.Ix += ixFixes; totals.Pbg += pbgFixes; totals.Stars += stellarFixes; totals.Comments += comments;
    Console.WriteLine($"{Rel(path)} ({name}): {changedWorlds} worlds, +{added} -{removed} codes, {exFixes} (Ex), {ixFixes} {{Ix}}, {pbgFixes} PBG, {stellarFixes} stars, {comments} lines commented");
    foreach (var s in samples)
        Console.WriteLine(s);
    if (apply)
    {
        byte[] output = utf8.GetBytes(string.Join(nl, lines));
        File.WriteAllBytes(path, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(output).ToArray() : output);
    }
}

string Rel(string p) => Path.GetRelativePath(repo!, p);

static string FixRemarks(string remarks, World world, out int added, out int removed)
{
    var rules = World.TradeCodeRules;
    var standard = rules.Select(r => r.Code).ToList();
    var want = new HashSet<string>(rules.Where(r => r.Applies(world)).Select(r => r.Code));
    var tokens = remarks.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    var kept = tokens.Where(t => !standard.Contains(t) || want.Contains(t)).ToList();
    removed = tokens.Count - kept.Count;
    var missing = standard.Where(c => want.Contains(c) && !kept.Contains(c)).ToList();
    // Di (or Di(sophont)) for a dieback world: Pop 0, TL 1+.
    if (world.PopulationExponent == 0 && world.TechLevel > 0 && !kept.Any(t => t.StartsWith("Di", StringComparison.Ordinal)))
        missing.Add("Di");
    added = missing.Count;
    if (added == 0 && removed == 0)
        return remarks;

    // Missing codes go after the world's other trade codes, in rule order.
    int at = kept.FindLastIndex(t => standard.Contains(t) || t == "Di") + 1;
    kept.InsertRange(at, missing);
    return string.Join(" ", kept);
}

// (Ex) is "(RLI+E)": resources, labor, infrastructure (eHex), efficiency (signed).
static string FixEconomic(string ex, World world)
{
    var m = Regex.Match(ex.Trim(), @"^\(([0-9A-Za-z])([0-9A-Za-z])([0-9A-Za-z])([+-]\d)\)$");
    if (!m.Success)
        return ex;
    int pop = world.PopulationExponent;
    int infrastructure = Maps.SecondSurvey.FromHex(m.Groups[3].Value[0]);
    int efficiency = int.Parse(m.Groups[4].Value);
    if (pop == 0)
    {
        infrastructure = 0;
        efficiency = -5;
    }
    else if (pop <= 3)
    {
        infrastructure = Math.Max(0, world.CalculatedImportance);
    }
    if (efficiency == 0)
        efficiency = 1;
    string result = $"({m.Groups[1].Value}{m.Groups[2].Value}{Maps.SecondSurvey.ToHex(infrastructure)}{(efficiency >= 0 ? "+" : "")}{efficiency})";
    return ex.Trim() == result ? ex : result;
}

// {Ix} is "{N}", "{ N }" or "{+N }"; keep whichever style the value uses.
static string FixImportance(string ix, World world)
{
    var m = Regex.Match(ix.Trim(), @"^\{( *)([+-]?)(\d+)( *)\}$");
    if (!m.Success)
        return ix;
    int imp = world.CalculatedImportance;
    if (int.Parse(m.Groups[2].Value + m.Groups[3].Value) == imp)
        return ix;
    string sign = imp < 0 ? "-" : m.Groups[2].Value == "+" ? "+" : "";
    return $"{{{m.Groups[1].Value}{sign}{Math.Abs(imp)}{m.Groups[4].Value}}}";
}

// PBG: no population multiplier without population. Only digits; X/? mean unknown.
static string FixPbg(string pbg, World world)
{
    string p = pbg.Trim();
    if (world.PopulationExponent != 0 || p.Length != 3 || p[0] < '1' || p[0] > '9')
        return pbg;
    return "0" + p.Substring(1);
}

// Stars: the primary (first) star must be the largest (T5StellarData): by size class, then spectral
// type, then digit. If it isn't, move the first largest star to the front and keep the rest in order.
// Only changes values made entirely of stars the validator recognizes.
static string FixStellar(string stellar)
{
    var sizes = new[] { "Ia", "Ib", "II", "III", "IV", "V", "VI", "D", "NS", "PSR", "BH", "BD" };
    const string spectrals = "OBAFGKM";
    var stars = Regex.Matches(stellar, @"\b(D|NS|PSR|BH|BD|[OBAFGKM][0-9]\x20(?:Ia|Ib|II|III|IV|V|VI))\b")
        .Select(m => m.Value).ToList();
    if (stars.Count < 2 || string.Join(" ", stars) != Regex.Replace(stellar.Trim(), @"\s+", " "))
        return stellar;
    // Lower is larger. "M2 V" has 4+ characters; D, NS, PSR, BH and BD fewer.
    (int, int, int) Rank(string s) => s.Length < 4
        ? (Array.IndexOf(sizes, s), -1, -1)
        : (Array.IndexOf(sizes, s.Substring(3)), spectrals.IndexOf(s[0]), s[1] - '0');
    int best = 0;
    for (int i = 1; i < stars.Count; ++i)
        if (Rank(stars[i]).CompareTo(Rank(stars[best])) < 0)
            best = i;
    if (best == 0)
        return stellar;
    string primary = stars[best];
    stars.RemoveAt(best);
    stars.Insert(0, primary);
    return string.Join(" ", stars);
}

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Maps.sln")))
            return dir.FullName;
    return null;
}

sealed class Totals
{
    public int Files, Worlds, Added, Removed, Ex, Ix, Pbg, Stars, Comments;
}

/// <summary>A world line's editable fields; null for a field the format doesn't have.</summary>
sealed record Fields(string Hex, string Remarks, string? Economic, string? Importance, string? Pbg, string? Stellar = null);

/// <summary>Reads and rewrites world lines in one sector file format.</summary>
abstract class LineEditor
{
    public abstract Fields? Read(List<string> lines, int i);
    public abstract void Write(List<string> lines, int i, Fields old, Fields fix);
    public virtual void Finish(List<string> lines) { }

    public static LineEditor? For(List<string> lines)
    {
        int tabHeader = lines.FindIndex(l => l.Split('\t') is var cells && cells.Contains("Hex") && cells.Any(TabEditor.RemarksColumns.Contains));
        if (tabHeader >= 0)
            return new TabEditor(lines[tabHeader].Split('\t').ToList(), tabHeader);
        int header = lines.FindIndex(l => l.StartsWith("Hex", StringComparison.Ordinal));
        if (header >= 0 && header + 1 < lines.Count && lines[header + 1].StartsWith("----", StringComparison.Ordinal))
            return new ColumnEditor(lines, header);
        if (lines.Any(l => SecEditor.WorldRegex.IsMatch(l)))
            return new SecEditor();
        return null;
    }

    /// <summary>Resource units, as the validator computes them: R*L*I*E, each treated as 1 if 0.</summary>
    protected static int ResourceUnits(string ex)
    {
        var m = Regex.Match(ex.Trim(), @"^\(([0-9A-Za-z])([0-9A-Za-z])([0-9A-Za-z])([+-]\d)\)$");
        int OneIfZero(int n) => n == 0 ? 1 : n;
        return OneIfZero(Maps.SecondSurvey.FromHex(m.Groups[1].Value[0])) * OneIfZero(Maps.SecondSurvey.FromHex(m.Groups[2].Value[0])) *
            OneIfZero(Maps.SecondSurvey.FromHex(m.Groups[3].Value[0])) * OneIfZero(int.Parse(m.Groups[4].Value));
    }
}

/// <summary>Tab-delimited T5 files (.tab).</summary>
sealed class TabEditor : LineEditor
{
    // The names the server's parser accepts for the remarks column (TabDelimitedParser).
    internal static readonly string[] RemarksColumns = { "Remarks", "Trade Codes", "Comments" };
    // And for the stars column.
    internal static readonly string[] StarsColumns = { "Stellar", "Stars", "Stellar Data" };
    private readonly int header, hex, remarks, ex, ix, pbg, stars, ru;
    public TabEditor(List<string> columns, int header)
    {
        this.header = header;
        hex = columns.IndexOf("Hex");
        remarks = columns.FindIndex(RemarksColumns.Contains);
        ex = columns.IndexOf("{Ex}") is int a && a >= 0 ? a : columns.IndexOf("(Ex)");
        ix = columns.IndexOf("{Ix}");
        pbg = columns.IndexOf("PBG");
        stars = columns.FindIndex(StarsColumns.Contains);
        ru = columns.IndexOf("RU");
    }
    private static string? Cell(string[] cells, int i) => i >= 0 && i < cells.Length ? cells[i] : null;
    public override Fields? Read(List<string> lines, int i)
    {
        if (i <= header || hex < 0 || remarks < 0)
            return null;
        var cells = lines[i].Split('\t');
        if (cells.Length <= Math.Max(hex, remarks))
            return null;
        return new Fields(cells[hex], cells[remarks], Cell(cells, ex), Cell(cells, ix), Cell(cells, pbg), Cell(cells, stars));
    }
    public override void Write(List<string> lines, int i, Fields old, Fields fix)
    {
        var cells = lines[i].Split('\t');
        cells[remarks] = fix.Remarks;
        if (fix.Importance != null)
            cells[ix] = fix.Importance;
        if (fix.Pbg != null)
            cells[pbg] = fix.Pbg;
        if (fix.Stellar != null)
            cells[stars] = fix.Stellar;
        if (fix.Economic != null && fix.Economic != old.Economic)
        {
            cells[ex] = fix.Economic;
            if (ru >= 0 && ru < cells.Length && cells[ru].Trim().Length > 0)
                cells[ru] = ResourceUnits(fix.Economic).ToString();
        }
        lines[i] = string.Join("\t", cells);
    }
}

/// <summary>T5 Second Survey column files: a "Hex ..." header, then a dashed line giving the columns.</summary>
sealed class ColumnEditor : LineEditor
{
    private readonly int header;
    private readonly List<(int start, int end)> spans;
    private readonly int hex, remarks, ex, ix, pbg, stars;
    // New values by column, then line; placed in Finish, once each column's width is known.
    private readonly Dictionary<int, Dictionary<int, string>> pending = new Dictionary<int, Dictionary<int, string>>();

    public ColumnEditor(List<string> lines, int header)
    {
        this.header = header;
        spans = Regex.Matches(lines[header + 1], "-+").Select(m => (m.Index, m.Index + m.Length)).ToList();
        var names = spans.Select(s => Slice(lines[header], s.start, s.end).Trim()).ToList();
        hex = names.IndexOf("Hex");
        remarks = names.IndexOf("Remarks");
        ex = names.IndexOf("(Ex)");
        ix = names.IndexOf("{Ix}");
        pbg = names.IndexOf("PBG");
        stars = names.FindIndex(TabEditor.StarsColumns.Contains);
    }

    private static string Slice(string line, int start, int end) =>
        start >= line.Length ? "" : line.Substring(start, Math.Min(end, line.Length) - start);

    private string? Column(string line, int column) =>
        column >= 0 ? Slice(line, spans[column].start, spans[column].end).Trim() : null;

    public override Fields? Read(List<string> lines, int i)
    {
        if (i <= header + 1 || hex < 0 || remarks < 0 || lines[i].Length < spans[remarks].end || lines[i].StartsWith("#", StringComparison.Ordinal))
            return null;
        return new Fields(Column(lines[i], hex)!, Column(lines[i], remarks)!, Column(lines[i], ex), Column(lines[i], ix), Column(lines[i], pbg), Column(lines[i], stars));
    }

    public override void Write(List<string> lines, int i, Fields old, Fields fix)
    {
        void Set(int column, string? before, string? after)
        {
            if (column < 0 || after == null || after == before)
                return;
            if (!pending.TryGetValue(column, out var values))
                pending[column] = values = new Dictionary<int, string>();
            values[i] = after;
        }
        Set(remarks, old.Remarks, fix.Remarks);
        Set(ex, old.Economic, fix.Economic);
        Set(ix, old.Importance, fix.Importance);
        Set(pbg, old.Pbg, fix.Pbg);
        Set(stars, old.Stellar, fix.Stellar);
    }

    public override void Finish(List<string> lines)
    {
        // Right to left, so widening a column doesn't move the ones still to be placed.
        foreach (int column in pending.Keys.OrderByDescending(c => spans[c].start))
        {
            var values = pending[column];
            var (start, end) = spans[column];
            int width = end - start;
            int needed = values.Values.Max(r => r.Length);
            if (needed > width)
            {
                // Widen the column in every line that reaches it.
                int extra = needed - width;
                for (int i = header; i < lines.Count; ++i)
                {
                    if (lines[i].Length < end || lines[i].StartsWith("#", StringComparison.Ordinal))
                        continue;
                    char pad = i == header + 1 ? '-' : ' ';
                    lines[i] = lines[i].Substring(0, end) + new string(pad, extra) + lines[i].Substring(end);
                }
                width = needed;
            }
            foreach (var (i, text) in values)
                lines[i] = lines[i].Substring(0, start) + text.PadRight(width) + lines[i].Substring(Math.Min(start + width, lines[i].Length));
        }
    }
}

/// <summary>Legacy SEC files: located with the server's SEC regex (SecParser), which allows any spacing.</summary>
sealed class SecEditor : LineEditor
{
    internal static readonly Regex WorldRegex = new Regex(@"^" +
        @"( [ \t]*       (?<name>        .*                              ) )  " +
        @"( [ \t]*       (?<hex>         [0-9]{4}                        ) )  " +
        @"( [ \t]{1,2}   (?<uwp>         [ABCDEX?][0-9A-Z?]{6}-[0-9A-Z?] ) )  " +
        @"( [ \t]{1,2}   (?<base>        [A-Zr1-9* \-]                   ) )  " +
        @"( [ \t]{1,2}   (?<codes>       .{10,}?                         ) )  " +
        @"( [ \t]+       (?<zone>        [GARBFU \-]                     ) )? " +
        @"( [ \t]{1,2}   (?<pbg>         [0-9X?][0-9A-FX?][0-9A-FX?]     ) )  " +
        @"( [ \t]{1,2}   (?<allegiance>  ([A-Za-z0-9][A-Za-z0-9?\-]|--)  ) )  " +
        @"( [ \t]*       (?<rest>        .*?                             ) )  " +
        @"[ \t]*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.IgnorePatternWhitespace);

    // What SecParser requires of a line before it tries to parse a world.
    internal static readonly Regex UwpRegex = new Regex(@"[ABCDEX?][0-9A-Z?]{6}-[0-9A-Z?]", RegexOptions.CultureInvariant);

    public override Fields? Read(List<string> lines, int i)
    {
        var m = WorldRegex.Match(lines[i]);
        return m.Success && !lines[i].StartsWith("#", StringComparison.Ordinal)
            ? new Fields(m.Groups["hex"].Value, m.Groups["codes"].Value.Trim(), null, null, m.Groups["pbg"].Value) : null;
    }

    public override void Write(List<string> lines, int i, Fields old, Fields fix)
    {
        var groups = WorldRegex.Match(lines[i]).Groups;
        string line = lines[i];
        // PBG first: it comes after the codes, so its index is still good.
        if (fix.Pbg != old.Pbg)
        {
            var p = groups["pbg"];
            line = line.Substring(0, p.Index) + fix.Pbg + line.Substring(p.Index + p.Length);
        }
        if (fix.Remarks != old.Remarks)
        {
            var g = groups["codes"];
            // Keep the field's width (it includes trailing padding); grow it if the codes need room.
            string field = fix.Remarks.Length < g.Length ? fix.Remarks.PadRight(g.Length) : fix.Remarks + " ";
            line = line.Substring(0, g.Index) + field + line.Substring(g.Index + g.Length);
        }
        lines[i] = line;
    }
}
