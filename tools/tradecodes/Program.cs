using Maps;
using Maps.Admin;
using Maps.Utilities;
using System.Text;
using System.Text.RegularExpressions;

// Recomputes the trade codes that the UWP fully determines (World.TradeCodeRules, the same rules
// the data validator checks) and the mechanical (Ex) values, editing sector data files in place:
//   dotnet run --project tools/tradecodes                     report what would change
//   dotnet run --project tools/tradecodes -- --apply          change the files
//   dotnet run --project tools/tradecodes -- --apply Koog     only sectors whose name contains "Koog"
//
// Scope: the sectors the validator checks (tags OTU, Apocryphal, Faraway), except files generated
// from T5SS data ("Generated file - DO NOT MODIFY"; fix those in res/t5ss/data instead).
//
// Per world:
// - Trade codes: removes codes the UWP rules out, adds codes it requires (in T5SS order, after the
//   world's other trade codes), and adds Di for Pop 0 with TL 1+ unless a Di(sophont) code is there.
//   Other remarks (sophonts, Cp/Cs/Cx, Ht, etc.) are kept, in order.
// - (Ex), T5 formats only: infrastructure 0 and efficiency -5 if Pop 0; infrastructure = importance
//   (min 0) if Pop 1-3. Resource Units are recomputed where a file has an RU column.
// Only those fields change; T5 columns are widened when longer remarks need room.

bool apply = args.Contains("--apply");
string[] filters = args.Where(a => !a.StartsWith("--")).ToArray();

string? repo = Environment.GetEnvironmentVariable("TM_REPO_ROOT") ?? FindRepoRoot(Environment.CurrentDirectory)
    ?? FindRepoRoot(AppContext.BaseDirectory);
if (repo == null)
{
    Console.Error.WriteLine("Could not find the repository root (Maps.sln); run from inside the repo or set TM_REPO_ROOT.");
    return 2;
}
Util.ContentRoot = repo;

var map = SectorMap.GetInstance();
var resources = ResourceManager.GetDedicatedInstance();
var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
int totalWorlds = 0, totalFiles = 0, totalAdded = 0, totalRemoved = 0, totalEx = 0;

foreach (var sector in map.Sectors.Where(s => s.DataFile != null && DataValidator.IsCurated(s)))
{
    string name = sector.Names.Count > 0 ? sector.Names[0].Text : "?";
    if (filters.Length > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
        continue;
    string path = Util.MapPath(sector.DataFile!.FileName);
    if (!done.Add(path) || !File.Exists(path))
        continue;

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
        continue;
    }
    if (text.Contains("Generated file - DO NOT MODIFY"))
        continue;

    WorldCollection? worlds = sector.GetWorlds(resources, cacheResults: false);
    if (worlds == null)
        continue;

    string nl = text.Contains("\r\n") ? "\r\n" : "\n";
    var lines = text.Split(nl).ToList();
    var editor = LineEditor.For(lines);
    if (editor == null)
    {
        Console.WriteLine($"SKIP {Rel(path)}: format not recognized");
        continue;
    }

    int changedWorlds = 0, added = 0, removed = 0, exFixes = 0;
    var samples = new List<string>();
    for (int i = 0; i < lines.Count; ++i)
    {
        var fields = editor.Read(lines, i);
        if (fields == null)
            continue;
        if (!System.Text.RegularExpressions.Regex.IsMatch(fields.Hex, "^[0-9]{4}$"))
            continue;
        var hex = new Hex(fields.Hex);
        if (!hex.IsValid)
            continue;
        World? world = worlds[hex.X, hex.Y];
        if (world == null || world.UWP.Contains('?') || world.UWP == "XXXXXXX-X")
            continue;

        string newRemarks = FixRemarks(fields.Remarks, world, out int a, out int r);
        string? newEx = fields.Economic == null ? null : FixEconomic(fields.Economic, world);
        bool exChanged = newEx != null && newEx != fields.Economic;
        if (newRemarks == fields.Remarks && !exChanged)
            continue;

        if (samples.Count < 3)
            samples.Add($"    {fields.Hex} {world.UWP}: '{fields.Remarks}' -> '{newRemarks}'" + (exChanged ? $"  {fields.Economic} -> {newEx}" : ""));
        editor.Write(lines, i, newRemarks, exChanged ? newEx : null, exChanged ? world : null);
        ++changedWorlds; added += a; removed += r; if (exChanged) ++exFixes;
    }
    if (changedWorlds == 0)
        continue;

    editor.Finish(lines);
    ++totalFiles; totalWorlds += changedWorlds; totalAdded += added; totalRemoved += removed; totalEx += exFixes;
    Console.WriteLine($"{Rel(path)} ({name}): {changedWorlds} worlds, +{added} -{removed} codes, {exFixes} (Ex)");
    foreach (var s in samples)
        Console.WriteLine(s);
    if (apply)
    {
        byte[] output = utf8.GetBytes(string.Join(nl, lines));
        File.WriteAllBytes(path, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(output).ToArray() : output);
    }
}

Console.WriteLine($"{(apply ? "Changed" : "Would change")} {totalWorlds} worlds in {totalFiles} files: +{totalAdded} -{totalRemoved} trade codes, {totalEx} (Ex) fixes.");
return 0;

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
static string? FixEconomic(string ex, World world)
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
    else
    {
        return ex;
    }
    string result = $"({m.Groups[1].Value}{m.Groups[2].Value}{Maps.SecondSurvey.ToHex(infrastructure)}{(efficiency >= 0 ? "+" : "")}{efficiency})";
    return ex.Trim() == result ? ex : result;
}

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Maps.sln")))
            return dir.FullName;
    return null;
}

/// <summary>A world line's editable fields.</summary>
sealed record Fields(string Hex, string Remarks, string? Economic);

/// <summary>Reads and rewrites world lines in one sector file format.</summary>
abstract class LineEditor
{
    public abstract Fields? Read(List<string> lines, int i);
    public abstract void Write(List<string> lines, int i, string remarks, string? economic, World? world);
    public virtual void Finish(List<string> lines) { }

    public static LineEditor? For(List<string> lines)
    {
        int tabHeader = lines.FindIndex(l => l.Split('\t') is var cells && cells.Contains("Hex") && cells.Contains("Remarks"));
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
    private readonly int header, hex, remarks, ex, ru;
    public TabEditor(List<string> columns, int header)
    {
        this.header = header;
        hex = columns.IndexOf("Hex");
        remarks = columns.IndexOf("Remarks");
        ex = columns.IndexOf("{Ex}") is int a && a >= 0 ? a : columns.IndexOf("(Ex)");
        ru = columns.IndexOf("RU");
    }
    public override Fields? Read(List<string> lines, int i)
    {
        if (i <= header || hex < 0 || remarks < 0)
            return null;
        var cells = lines[i].Split('\t');
        if (cells.Length <= Math.Max(hex, remarks))
            return null;
        return new Fields(cells[hex], cells[remarks], ex >= 0 && ex < cells.Length ? cells[ex] : null);
    }
    public override void Write(List<string> lines, int i, string newRemarks, string? economic, World? world)
    {
        var cells = lines[i].Split('\t');
        cells[remarks] = newRemarks;
        if (economic != null && ex >= 0)
        {
            cells[ex] = economic;
            if (ru >= 0 && ru < cells.Length && cells[ru].Trim().Length > 0)
                cells[ru] = ResourceUnits(economic).ToString();
        }
        lines[i] = string.Join("\t", cells);
    }
}

/// <summary>T5 Second Survey column files: a "Hex ..." header, then a dashed line giving the columns.</summary>
sealed class ColumnEditor : LineEditor
{
    private readonly int header;
    private readonly List<(int start, int end)> spans;
    private readonly int hex, remarks, ex;
    private readonly Dictionary<int, string> newRemarks = new Dictionary<int, string>();

    public ColumnEditor(List<string> lines, int header)
    {
        this.header = header;
        spans = Regex.Matches(lines[header + 1], "-+").Select(m => (m.Index, m.Index + m.Length)).ToList();
        var names = spans.Select(s => Slice(lines[header], s.start, s.end).Trim()).ToList();
        hex = names.IndexOf("Hex");
        remarks = names.IndexOf("Remarks");
        ex = names.IndexOf("(Ex)");
    }

    private static string Slice(string line, int start, int end) =>
        start >= line.Length ? "" : line.Substring(start, Math.Min(end, line.Length) - start);

    public override Fields? Read(List<string> lines, int i)
    {
        if (i <= header + 1 || hex < 0 || remarks < 0 || lines[i].Length < spans[remarks].end || lines[i].StartsWith("#", StringComparison.Ordinal))
            return null;
        var (rs, re) = spans[remarks];
        return new Fields(Slice(lines[i], spans[hex].start, spans[hex].end).Trim(),
            Slice(lines[i], rs, re).Trim(),
            ex >= 0 ? Slice(lines[i], spans[ex].start, spans[ex].end).Trim() : null);
    }

    public override void Write(List<string> lines, int i, string remarksText, string? economic, World? world)
    {
        newRemarks[i] = remarksText; // placed in Finish, once the column width is known
        if (economic != null && ex >= 0)
        {
            var (es, ee) = spans[ex];
            string line = lines[i];
            lines[i] = line.Substring(0, es) + economic.PadRight(ee - es) + line.Substring(Math.Min(ee, line.Length));
        }
    }

    public override void Finish(List<string> lines)
    {
        var (rs, re) = spans[remarks];
        int width = re - rs;
        int needed = newRemarks.Values.Max(r => r.Length);
        if (needed > width)
        {
            // Widen the Remarks column in every line that reaches it.
            int extra = needed - width;
            for (int i = header; i < lines.Count; ++i)
            {
                if (lines[i].Length < re || lines[i].StartsWith("#", StringComparison.Ordinal))
                    continue;
                char pad = i == header + 1 ? '-' : ' ';
                lines[i] = lines[i].Substring(0, re) + new string(pad, extra) + lines[i].Substring(re);
            }
            width = needed;
        }
        foreach (var (i, text) in newRemarks)
            lines[i] = lines[i].Substring(0, rs) + text.PadRight(width) + lines[i].Substring(rs + width);
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

    public override Fields? Read(List<string> lines, int i)
    {
        var m = WorldRegex.Match(lines[i]);
        return m.Success && !lines[i].StartsWith("#", StringComparison.Ordinal)
            ? new Fields(m.Groups["hex"].Value, m.Groups["codes"].Value.Trim(), null) : null;
    }

    public override void Write(List<string> lines, int i, string remarks, string? economic, World? world)
    {
        var g = WorldRegex.Match(lines[i]).Groups["codes"];
        // Keep the field's width (it includes trailing padding); grow it if the codes need room.
        string field = remarks.Length < g.Length ? remarks.PadRight(g.Length) : remarks + " ";
        lines[i] = lines[i].Substring(0, g.Index) + field + lines[i].Substring(g.Index + g.Length);
    }
}
