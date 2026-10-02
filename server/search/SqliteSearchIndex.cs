#nullable enable
using Maps.Utilities;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using SearchResultsType = Maps.Search.SearchEngine.SearchResultsType;

namespace Maps.Search
{
    /// <summary>
    /// The search index as a SQLite file (by default App_Data/search.db), used by both hosts.
    /// The same tables and queries as the SQL Server index; text columns compare
    /// case-insensitively, as SQL Server's default collation does. Rebuilt by /admin/reindex or
    /// tools/reindex; built in the background on first use if the file is missing.
    /// </summary>
    internal sealed class SqliteSearchIndex : ISearchIndex
    {
        private readonly string path;
        private readonly object buildLock = new object();
        private int building; // 1 while a background build runs

        public SqliteSearchIndex(string path)
        {
            this.path = path;
        }

        /// <summary>The index file configured by the SearchIndex setting (default ~/App_Data/search.db).</summary>
        public static SqliteSearchIndex FromSettings() =>
            new SqliteSearchIndex(Util.MapPath(AppSettings.Get("SearchIndex") is string p && p.Length > 0 ? p : "~/App_Data/search.db"));

        public string Path => path;
        public bool Exists => File.Exists(path);

        /// <summary>If the index file is missing, builds it on a background thread.</summary>
        public void EnsureBuilt(Action<string>? log = null)
        {
            if (Exists || Interlocked.Exchange(ref building, 1) == 1)
                return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    log?.Invoke($"Building search index {path}...");
                    PopulateDatabase(ResourceManager.GetDedicatedInstance(), s => { });
                    log?.Invoke("Search index built.");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Building the search index failed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref building, 0);
                }
            });
        }

        private SqliteConnection Open(bool readOnly = true, string? file = null)
        {
            if (readOnly && !Exists)
            {
                throw Volatile.Read(ref building) == 1
                    ? new SearchUnavailableException("The search index is being built; try again in a minute.")
                    : new SearchUnavailableException();
            }
            SqliteNative.Init();
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file ?? path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                // Not pooled, so a rebuilt index can replace the file.
                Pooling = false,
            }.ToString());
            connection.Open();
            connection.CreateFunction("soundex", (string? s) => SearchQuery.Soundex(s), isDeterministic: true);
            return connection;
        }

        #region Building

        private static readonly string[] SCHEMA = {
            "CREATE TABLE sectors (milieu TEXT COLLATE NOCASE, x INTEGER NOT NULL, y INTEGER NOT NULL, name TEXT COLLATE NOCASE)",
            "CREATE INDEX sector_name ON sectors (name)",
            "CREATE INDEX sector_milieu ON sectors (milieu)",
            "CREATE TABLE subsectors (milieu TEXT COLLATE NOCASE, sector_x INTEGER NOT NULL, sector_y INTEGER NOT NULL, subsector_index TEXT NOT NULL, name TEXT COLLATE NOCASE)",
            "CREATE INDEX subsector_name ON subsectors (name)",
            "CREATE INDEX subsector_milieu ON subsectors (milieu)",
            "CREATE TABLE worlds (milieu TEXT COLLATE NOCASE, x INTEGER NOT NULL, y INTEGER NOT NULL, sector_x INTEGER NOT NULL, sector_y INTEGER NOT NULL, " +
                "hex_x INTEGER NOT NULL, hex_y INTEGER NOT NULL, name TEXT COLLATE NOCASE, uwp TEXT COLLATE NOCASE, remarks TEXT COLLATE NOCASE, " +
                "pbg TEXT COLLATE NOCASE, zone TEXT COLLATE NOCASE, alleg TEXT COLLATE NOCASE, stellar TEXT COLLATE NOCASE, ix INTEGER NOT NULL, " +
                "ex TEXT COLLATE NOCASE, cx TEXT COLLATE NOCASE, sector_name TEXT COLLATE NOCASE)",
            "CREATE INDEX world_name ON worlds (name)",
            "CREATE INDEX world_uwp ON worlds (uwp)",
            "CREATE INDEX world_pbg ON worlds (pbg)",
            "CREATE INDEX world_alleg ON worlds (alleg)",
            "CREATE INDEX world_stellar ON worlds (stellar)",
            "CREATE INDEX world_sector_name ON worlds (sector_name)",
            "CREATE INDEX world_milieu ON worlds (milieu)",
            "CREATE TABLE labels (milieu TEXT COLLATE NOCASE, x INTEGER NOT NULL, y INTEGER NOT NULL, radius INTEGER NOT NULL, name TEXT COLLATE NOCASE)",
            "CREATE INDEX label_name ON labels (name)",
            "CREATE INDEX label_milieu ON labels (milieu)",
        };

        private static string SanifyLabel(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

        private static string? StripBrackets(string? input) =>
            input == null ? null : string.Join("", input.Split('(', ')', '[', ']', '{', '}'));

        /// <summary>
        /// Rebuilds the index from the sector data (OTU and Faraway sectors; all of them, about
        /// 120,000 worlds, in ~15 s). Writes a new file, then replaces the old one, so searches
        /// keep working meanwhile.
        /// </summary>
        public void PopulateDatabase(ResourceManager resourceManager, Action<string> statusCallback)
        {
            lock (buildLock)
            {
                // NOTE: This (re)initializes a static data structure used for resolving names
                // into sector locations, so needs to be run before any other objects (e.g. Worlds)
                // are loaded.
                SectorMap map = SectorMap.GetInstance();

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                string temp = path + ".new";
                if (File.Exists(temp))
                    File.Delete(temp);

                var counts = new Dictionary<string, int>();
                var worldsByMilieu = new SortedDictionary<string, int>(StringComparer.Ordinal);

                using (var connection = Open(readOnly: false, file: temp))
                {
                    statusCallback("Creating schema...");
                    foreach (string sql in SCHEMA)
                        Execute(connection, sql);

                    using var transaction = connection.BeginTransaction();
                    using var insertSector = Insert(connection, transaction, "sectors", "milieu", "x", "y", "name");
                    using var insertSubsector = Insert(connection, transaction, "subsectors", "milieu", "sector_x", "sector_y", "subsector_index", "name");
                    using var insertWorld = Insert(connection, transaction, "worlds", "milieu", "x", "y", "sector_x", "sector_y", "hex_x", "hex_y",
                        "name", "uwp", "remarks", "pbg", "zone", "alleg", "stellar", "ix", "ex", "cx", "sector_name");
                    using var insertLabel = Insert(connection, transaction, "labels", "milieu", "x", "y", "radius", "name");

                    void Row(SqliteCommand command, string table, params object?[] values)
                    {
                        for (int i = 0; i < values.Length; ++i)
                            command.Parameters[i].Value = values[i] ?? DBNull.Value;
                        command.ExecuteNonQuery();
                        counts[table] = counts.TryGetValue(table, out int n) ? n + 1 : 1;
                    }

                    // (milieu, label) => points
                    var labels = new Dictionary<(string, string), List<Point>>();
                    void AddLabel(string milieu, string? text, Point coords)
                    {
                        if (text == null)
                            return;
                        var key = (milieu, SanifyLabel(text));
                        if (!labels.TryGetValue(key, out var points))
                            labels.Add(key, points = new List<Point>());
                        points.Add(coords);
                    }

                    statusCallback("Parsing data...");
                    foreach (Sector sector in map.Sectors)
                    {
                        // TODO: Allow searching non-OTU additions (Orion OB-1, etc)
                        if (!sector.Tags.Contains("OTU") && !sector.Tags.Contains("Faraway"))
                            continue;

                        // Apply suffix to borders/labels so that alternate versions are not merged.
                        string suffix = sector.Tags.Contains("Apocryphal") ? " (Apocryphal)" :
                            sector.Tags.Contains("Alternate") ? " (Alternate)" : "";
                        string milieu = sector.CanonicalMilieu;

                        foreach (Name name in sector.Names)
                            Row(insertSector, "sectors", milieu, sector.X, sector.Y, name.Text);
                        if (!string.IsNullOrEmpty(sector.Abbreviation))
                            Row(insertSector, "sectors", milieu, sector.X, sector.Y, sector.Abbreviation);

                        foreach (Subsector subsector in sector.Subsectors)
                            Row(insertSubsector, "subsectors", milieu, sector.X, sector.Y, subsector.Index.ToString(), subsector.Name);

                        foreach (Border border in sector.BordersAndRegions.Where(b => b.ShowLabel))
                        {
                            AddLabel(milieu, border.GetLabel(sector) + suffix,
                                Astrometrics.LocationToCoordinates(new Location(sector.Location, border.LabelPosition)));
                        }
                        foreach (Label label in sector.Labels)
                        {
                            AddLabel(milieu, label.Text + suffix,
                                Astrometrics.LocationToCoordinates(new Location(sector.Location, label.Hex)));
                        }

                        WorldCollection? worlds = sector.GetWorlds(resourceManager, cacheResults: false);
                        if (worlds == null)
                            continue;

                        foreach (World world in worlds.Where(w => !w.IsPlaceholder))
                        {
                            Row(insertWorld, "worlds",
                                milieu,
                                world.Coordinates.X,
                                world.Coordinates.Y,
                                sector.X,
                                sector.Y,
                                world.X,
                                world.Y,
                                string.IsNullOrEmpty(world.Name) ? null : world.Name,
                                world.UWP,
                                world.Remarks,
                                world.PBG,
                                string.IsNullOrEmpty(world.Zone) ? "G" : world.Zone,
                                world.Allegiance,
                                world.Stellar,
                                world.CalculatedImportance,
                                StripBrackets(world.Economic),
                                StripBrackets(world.Cultural),
                                sector.Names.Count > 0 ? sector.Names[0].Text : null);
                            worldsByMilieu[milieu] = worldsByMilieu.TryGetValue(milieu, out int n) ? n + 1 : 1;
                        }
                    }

                    foreach (var entry in labels)
                    {
                        var (milieu, name) = entry.Key;
                        List<Point> points = entry.Value;
                        int x = (int)Math.Round(points.Select(p => p.X).Average());
                        int y = (int)Math.Round(points.Select(p => p.Y).Average());
                        int width = points.Select(p => p.X).Max() - points.Select(p => p.X).Min();
                        int height = points.Select(p => p.Y).Max() - points.Select(p => p.Y).Min();
                        Row(insertLabel, "labels", milieu, x, y, Math.Max(width, height), name);
                    }

                    statusCallback("Writing...");
                    transaction.Commit();
                }

                // Replace the old index. A search may have it open for a moment (Windows won't
                // replace an open file), so retry briefly.
                for (int attempt = 1; ; ++attempt)
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                        File.Move(temp, path);
                        break;
                    }
                    catch (IOException) when (attempt < 50)
                    {
                        Thread.Sleep(100);
                    }
                }

                statusCallback("Complete!");
                statusCallback("&nbsp;");
                statusCallback("Summary:");
                foreach (string table in new[] { "sectors", "subsectors", "worlds", "labels" })
                    statusCallback($"{table}: {(counts.TryGetValue(table, out int n) ? n : 0)}");
                statusCallback("&nbsp;");
                statusCallback("Worlds by Milieu:");
                foreach (var kv in worldsByMilieu)
                    statusCallback($"{kv.Key} &mdash; {kv.Value}");
            }
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static SqliteCommand Insert(SqliteConnection connection, SqliteTransaction transaction, string table, params string[] columns)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((c, i) => "@p" + i))})";
            for (int i = 0; i < columns.Length; ++i)
                command.Parameters.Add(new SqliteParameter("@p" + i, DBNull.Value));
            command.Prepare();
            return command;
        }

        #endregion

        #region Queries

        public IEnumerable<SearchResult> PerformSearch(string? milieu, string? query, SearchResultsType types, int maxResultsPerType, bool random)
        {
            var results = new List<SearchResult>();
            var parsed = SearchQuery.Parse(query, types, SearchQuery.Dialect.Sqlite);
            types = parsed.Types;
            if (parsed.Clauses.Count == 0 && !random)
                return results;

            string where = parsed.Where;
            var terms = parsed.TermsWithMilieu(milieu);
            string orderBy = random ? "ORDER BY RANDOM()" : "";

            // DISTINCT filters out e.g. "Ley" and "Ley Sector" (different names, same result).
            // {0}: the distinct (result) columns; {1}: the subquery's columns; {2}: the table.
            string Sql(string columns, string innerColumns, string table) =>
                $"SELECT DISTINCT {columns} FROM (SELECT {innerColumns} FROM {table} WHERE {where} {orderBy} LIMIT {maxResultsPerType}) AS TT LIMIT {maxResultsPerType}";

            using var connection = Open();

            IEnumerable<SqliteDataReader> Query(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                for (int i = 0; i < terms.Count; ++i)
                    command.Parameters.AddWithValue($"@term{i}", terms[i]);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    yield return reader;
            }

            if (types.HasFlag(SearchResultsType.Sectors))
            {
                foreach (var row in Query(Sql("TT.x, TT.y", "x, y", "sectors")))
                    results.Add(new SectorResult(row.GetInt32(0), row.GetInt32(1)));
            }
            if (types.HasFlag(SearchResultsType.Subsectors))
            {
                foreach (var row in Query(Sql("TT.sector_x, TT.sector_y, TT.subsector_index", "sector_x, sector_y, subsector_index", "subsectors")))
                    results.Add(new SubsectorResult(row.GetInt32(0), row.GetInt32(1), row.GetString(2)[0]));
            }
            if (types.HasFlag(SearchResultsType.Worlds))
            {
                foreach (var row in Query(Sql("TT.sector_x, TT.sector_y, TT.hex_x, TT.hex_y", "sector_x, sector_y, hex_x, hex_y", "worlds")))
                    results.Add(new WorldResult(row.GetInt32(0), row.GetInt32(1), (byte)row.GetInt32(2), (byte)row.GetInt32(3)));
            }
            if (types.HasFlag(SearchResultsType.Labels))
            {
                foreach (var row in Query(Sql("TT.x, TT.y, TT.radius, TT.name", "x, y, radius, name", "labels")))
                    results.Add(new LabelResult(row.GetString(3), new Point(row.GetInt32(0), row.GetInt32(1)), row.GetInt32(2)));
            }
            return results;
        }

        public WorldResult? FindNearestWorldMatch(string name, string milieu, int x, int y)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sector_x, sector_y, hex_x, hex_y, " +
                "((@x - x) * (@x - x) + (@y - y) * (@y - y)) AS distance " +
                "FROM worlds WHERE name = @name AND milieu = @milieu ORDER BY distance ASC LIMIT 1";
            command.Parameters.AddWithValue("@x", x);
            command.Parameters.AddWithValue("@y", y);
            command.Parameters.AddWithValue("@milieu", milieu ?? SectorMap.DEFAULT_MILIEU);
            command.Parameters.AddWithValue("@name", name);
            using var row = command.ExecuteReader();
            if (!row.Read())
                return null;
            return new WorldResult(row.GetInt32(0), row.GetInt32(1), (byte)row.GetInt32(2), (byte)row.GetInt32(3));
        }

        #endregion
    }
}
