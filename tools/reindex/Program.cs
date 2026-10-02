using Maps;
using Maps.Search;
using Maps.Utilities;

// Builds the SQLite search index from res/Sectors, on any OS:
//   dotnet run --project tools/reindex                  -> App_Data/search.db
//   dotnet run --project tools/reindex -- path/to.db    -> that file
// Takes about 15 seconds for all sectors.
// The servers also build App_Data/search.db themselves on first start if it's missing.

string? repo = Environment.GetEnvironmentVariable("TM_REPO_ROOT") ?? FindRepoRoot(Environment.CurrentDirectory)
    ?? FindRepoRoot(AppContext.BaseDirectory);
if (repo == null)
{
    Console.Error.WriteLine("Could not find the repository root (Maps.sln); run from inside the repo or set TM_REPO_ROOT.");
    return 2;
}
Util.ContentRoot = repo;

string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repo, "App_Data", "search.db");
var watch = System.Diagnostics.Stopwatch.StartNew();
new SqliteSearchIndex(output).PopulateDatabase(ResourceManager.GetDedicatedInstance(),
    message => Console.WriteLine(message.Replace("&nbsp;", "").Replace("&mdash;", "-")));
Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length / 1024} KB) in {watch.Elapsed.TotalSeconds:F1}s");
return 0;

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Maps.sln")))
            return dir.FullName;
    return null;
}
