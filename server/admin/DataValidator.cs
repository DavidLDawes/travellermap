#nullable enable
using Maps.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;

namespace Maps.Admin
{
    /// <summary>
    /// Checks sector data and metadata for problems that break or degrade the map.
    /// Has no ASP.NET dependency, so it can run from unit tests or tools (set
    /// Util.ContentRoot first when not hosted).
    /// </summary>
    internal class DataValidator
    {
        internal enum Severity { Warning, Error }

        internal class Finding
        {
            public Finding(Severity severity, string category, string where, string message)
            {
                Severity = severity;
                Category = category;
                Where = where;
                Message = message;
            }
            public Severity Severity { get; }
            /// <summary>Short, stable kind of problem, for grouping and baselines.</summary>
            public string Category { get; }
            /// <summary>Sector and milieu, or file.</summary>
            public string Where { get; }
            public string Message { get; }
            public override string ToString() => $"{Severity}: [{Category}] {Where}: {Message}";
        }

        /// <summary>
        /// Sector tags whose data is curated for this site (the same set /admin/errors checks).
        /// Other data (fan projects such as the Zhodani Core Route) is still checked for errors,
        /// but its data-quality warnings are not reported.
        /// </summary>
        internal static readonly IReadOnlyList<string> CuratedTags = new[] { "OTU", "Apocryphal", "Faraway" };

        internal static bool IsCurated(Sector sector) => CuratedTags.Any(tag => sector.Tags.Contains(tag));

        private readonly List<Finding> findings = new List<Finding>();
        public IReadOnlyList<Finding> Findings => findings;

        private void Add(Severity severity, string category, string where, string message)
            => findings.Add(new Finding(severity, category, where, message));

        /// <summary>
        /// Validates every sector with a data file. World-level data warnings from the
        /// parser (UWP, stellar, etc.) are only available in DEBUG builds.
        /// </summary>
        public void ValidateSectors(SectorMap map, ResourceManager resourceManager, Func<Sector, bool>? filter = null)
        {
            foreach (var sector in map.Sectors.Where(s => s.DataFile != null && (filter == null || filter(s))))
            {
                string where = $"{sector.Names[0].Text} ({sector.CanonicalMilieu})";
                bool reportWarnings = IsCurated(sector);

                WorldCollection? worlds = null;
                try
                {
                    worlds = sector.GetWorlds(resourceManager, cacheResults: false);
                }
                catch (Exception ex)
                {
                    Add(Severity.Error, "data-file", where, $"{sector.DataFile!.FileName}: {ex.Message}");
                }

                if (worlds != null)
                {
                    if (worlds.ErrorList != null)
                    {
                        foreach (var record in worlds.ErrorList.Records)
                        {
                            if (record.severity >= ErrorLogger.Severity.Error)
                                Add(Severity.Error, "world-parse", where, record.message);
                            else if (record.severity == ErrorLogger.Severity.Warning && reportWarnings)
                                Add(Severity.Warning, "world-data", where, record.message);
                        }
                    }

                    foreach (var world in worlds)
                    {
                        if (world.Allegiance != "" && sector.GetAllegianceFromCode(world.Allegiance) == null)
                            Add(Severity.Error, "world-allegiance", where, $"Undefined allegiance code {world.Allegiance} on {world.Name} {world.Hex}");
                    }
                }

                foreach (IAllegiance item in sector.Borders.AsEnumerable<IAllegiance>()
                    .Concat(sector.Routes)
                    .Concat(sector.Labels))
                {
                    if (!string.IsNullOrWhiteSpace(item.Allegiance) && sector.GetAllegianceFromCode(item.Allegiance!) == null)
                        Add(Severity.Error, "metadata-allegiance", where, $"Undefined allegiance code {item.Allegiance} on {item.GetType().Name}");
                }

                foreach (var route in sector.Routes)
                {
                    var startSector = sector.Location;
                    var endSector = sector.Location;
                    startSector.Offset(route.StartOffset);
                    endSector.Offset(route.EndOffset);
                    int distance = Astrometrics.HexDistance(
                        Astrometrics.LocationToCoordinates(new Location(startSector, route.Start)),
                        Astrometrics.LocationToCoordinates(new Location(endSector, route.End)));
                    if (distance == 0)
                        Add(Severity.Error, "route-length", where, $"Zero-length route: {route}");
                    else if (distance > 4 && reportWarnings)
                        Add(Severity.Warning, "route-length", where, $"Route length {distance}: {route}");
                }
            }
        }

        /// <summary>Validates all sectors and all sector index/metadata XML.</summary>
        public void ValidateAll(SectorMap map, ResourceManager resourceManager)
        {
            ValidateSectors(map, resourceManager);
            var xmlFiles = SectorMap.MetafilePaths()
                .Concat(map.Sectors.Where(s => s.MetadataFile != null).Select(s => s.MetadataFile!))
                .ToList();
            ValidateXml(xmlFiles);
            ValidateFileNameCase(xmlFiles.Concat(map.Sectors.Where(s => s.DataFile != null).Select(s => s.DataFile!.FileName)));
        }

        /// <summary>
        /// Checks that referenced files exist with exactly the given case. Windows file systems
        /// ignore case but Linux doesn't, so a mismatch works locally and fails on Linux.
        /// </summary>
        public void ValidateFileNameCase(IEnumerable<string> virtualPaths)
        {
            string root = Util.MapPath("~/");
            foreach (var path in virtualPaths.Distinct(StringComparer.Ordinal))
            {
                string dir = root;
                foreach (var segment in path.TrimStart('~').Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment == ".")
                        continue;
                    if (segment == "..")
                    {
                        dir = System.IO.Path.GetDirectoryName(dir.TrimEnd('/', '\\')) ?? dir;
                        continue;
                    }
                    var names = System.IO.Directory.Exists(dir)
                        ? System.IO.Directory.EnumerateFileSystemEntries(dir).Select(System.IO.Path.GetFileName).ToList()
                        : new List<string?>();
                    if (!names.Contains(segment, StringComparer.Ordinal))
                    {
                        string? actual = names.FirstOrDefault(n => string.Equals(n, segment, StringComparison.OrdinalIgnoreCase));
                        if (actual != null)
                            Add(Severity.Error, "file-case", path, $"'{segment}' is '{actual}' on disk (Linux file names are case-sensitive)");
                        else
                            Add(Severity.Error, "file-missing", path, $"'{segment}' not found");
                        break;
                    }
                    dir = System.IO.Path.Combine(dir, segment);
                }
            }
        }

        #region Baseline
        // test/data-validation-baseline.txt lists known errors; checks fail only on new ones.

        private static readonly Regex LINE_NUMBER = new Regex(@"\bline \d+[:,]?\s*");

        /// <summary>
        /// Baseline key for an error. Line numbers are omitted because they shift whenever a
        /// file is edited.
        /// </summary>
        public static string BaselineKey(Finding f) => $"{f.Category} | {f.Where} | {LINE_NUMBER.Replace(f.Message, "")}";

        public IEnumerable<Finding> Errors => findings.Where(f => f.Severity == Severity.Error);

        /// <summary>Contents for a new baseline file.</summary>
        public IEnumerable<string> FormatBaseline() =>
            new[] {
                "# Known data validation errors; see server/admin/DataValidator.cs.",
                "# Format: category | sector (milieu) or file | message. Regenerate with TM_UPDATE_BASELINE=1.",
            }.Concat(Errors.Select(BaselineKey).OrderBy(k => k, StringComparer.Ordinal));

        /// <summary>
        /// Compares current errors with a baseline. Returns errors not in the baseline (counting
        /// duplicates), and how many baseline entries no longer occur.
        /// </summary>
        public (IReadOnlyList<string> added, int fixedCount) CompareToBaseline(IEnumerable<string> baselineLines)
        {
            static Dictionary<string, int> Count(IEnumerable<string> keys) =>
                keys.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            var current = Count(Errors.Select(BaselineKey));
            var baseline = Count(baselineLines.Where(l => l.Length > 0 && !l.StartsWith("#")));
            var added = current
                .Where(kv => kv.Value > (baseline.TryGetValue(kv.Key, out int n) ? n : 0))
                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            int fixedCount = baseline.Sum(kv => Math.Max(0, kv.Value - (current.TryGetValue(kv.Key, out int n) ? n : 0)));
            return (added, fixedCount);
        }

        /// <summary>Counts per severity and category, e.g. "Error world-allegiance: 590".</summary>
        public IEnumerable<string> Summary() =>
            findings.GroupBy(f => (f.Severity, f.Category)).OrderBy(g => g.Key.ToString())
                .Select(g => $"{g.Key.Severity} {g.Key.Category}: {g.Count()}");
        #endregion

        /// <summary>
        /// Validates XML files (sector metadata and milieu index files) against
        /// res/sectors.xsd.
        /// </summary>
        public void ValidateXml(IEnumerable<string> virtualPaths)
        {
            var schemas = new XmlSchemaSet();
            schemas.Add(null, Util.MapPath("~/res/sectors.xsd"));
            schemas.Compile();

            foreach (var path in virtualPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Fresh settings per file (not XmlReaderSettings.Clone): on .NET 10 a handler
                // added to a clone isn't used, and validation errors throw instead.
                var fileSettings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
                fileSettings.ValidationEventHandler += (sender, e) =>
                    Add(e.Severity == XmlSeverityType.Error ? Severity.Error : Severity.Warning, "xml-schema", path,
                        $"line {e.Exception.LineNumber}: {e.Message}");
                try
                {
                    using var reader = XmlReader.Create(Util.MapPath(path), fileSettings);
                    while (reader.Read()) { }
                }
                catch (XmlException ex)
                {
                    Add(Severity.Error, "xml-syntax", path, ex.Message);
                }
            }
        }
    }
}
