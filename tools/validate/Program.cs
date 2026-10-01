using Maps;
using Maps.Admin;
using Maps.Utilities;
using System.Text;

// Validates all sector data in res/Sectors against test/data-validation-baseline.txt, with the
// same checks as DataValidationTest, on any OS:
//   dotnet run -c Debug --project tools/validate                      check against the baseline
//   dotnet run -c Debug --project tools/validate -- --update-baseline  after fixing data
//   dotnet run -c Debug --project tools/validate -- --report out.tsv   all findings, for triage
// Use the Debug configuration: world-level parse errors are only recorded in Debug builds.
// Exit code: 0 = no new errors, 1 = new errors, 2 = setup problem.

string? repo = Environment.GetEnvironmentVariable("TM_REPO_ROOT") ?? FindRepoRoot(Environment.CurrentDirectory)
    ?? FindRepoRoot(AppContext.BaseDirectory);
if (repo == null)
{
    Console.Error.WriteLine("Could not find the repository root (Maps.sln); run from inside the repo or set TM_REPO_ROOT.");
    return 2;
}
Util.ContentRoot = repo;

bool update = args.Contains("--update-baseline");
int reportIndex = Array.IndexOf(args, "--report");
string? reportPath = reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : null;

var watch = System.Diagnostics.Stopwatch.StartNew();
var validator = new DataValidator();
validator.ValidateAll(SectorMap.GetInstance(), ResourceManager.GetDedicatedInstance());
Console.WriteLine($"Validated res/Sectors in {watch.Elapsed.TotalSeconds:F1}s");
foreach (var line in validator.Summary())
    Console.WriteLine("  " + line);

if (reportPath != null)
{
    File.WriteAllLines(reportPath,
        new[] { "severity\tcategory\twhere\tmessage" }.Concat(validator.Findings.Select(f =>
            $"{f.Severity}\t{f.Category}\t{f.Where}\t{f.Message.Replace('\t', ' ')}")),
        new UTF8Encoding(false));
    Console.WriteLine($"Wrote {validator.Findings.Count} findings to {reportPath}");
}

string baselinePath = Path.Combine(repo, "test", "data-validation-baseline.txt");
if (update)
{
    File.WriteAllLines(baselinePath, validator.FormatBaseline(), new UTF8Encoding(false));
    Console.WriteLine($"Wrote {validator.Errors.Count()} entries to {baselinePath}");
    return 0;
}

var (added, fixedCount) = validator.CompareToBaseline(File.ReadAllLines(baselinePath));
if (fixedCount > 0)
    Console.WriteLine($"{fixedCount} baseline error(s) no longer occur; run with --update-baseline to lock in the fix.");
if (added.Count > 0)
{
    Console.WriteLine($"{added.Count} new data error(s) not in the baseline:");
    foreach (var key in added)
        Console.WriteLine("  " + key);
    return 1;
}
Console.WriteLine("No new errors.");
return 0;

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Maps.sln")))
            return dir.FullName;
    return null;
}
