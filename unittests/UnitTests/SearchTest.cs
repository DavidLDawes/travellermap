using Maps;
using Maps.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using SearchResultsType = Maps.Search.SearchEngine.SearchResultsType;

namespace UnitTests
{
    [TestClass]
    public class SearchTest
    {
        private static SearchQuery Parse(string query, SearchResultsType types = SearchResultsType.Default)
            => SearchQuery.Parse(query, types, SearchQuery.Dialect.Sqlite);

        [TestMethod]
        public void QueryWords()
        {
            var q = Parse("Regina");
            Assert.AreEqual(SearchResultsType.Default, q.Types);
            CollectionAssert.AreEqual(new[] { "regina" }, q.Terms.ToArray(), "lower-cased");
            Assert.AreEqual("name LIKE @term || '%' OR name LIKE '% ' || @term || '%'", q.Clauses[0], "start of any word");

            q = Parse("  ");
            Assert.AreEqual(0, q.Clauses.Count);

            // Several words: one clause each, ANDed, after the milieu.
            q = Parse("new home");
            Assert.AreEqual(2, q.Clauses.Count);
            Assert.AreEqual("(milieu = @term0) AND (name LIKE @term1 || '%' OR name LIKE '% ' || @term1 || '%') AND (name LIKE @term2 || '%' OR name LIKE '% ' || @term2 || '%')", q.Where);
            CollectionAssert.AreEqual(new[] { "M1105", "new", "home" }, q.TermsWithMilieu(null).ToArray());
            CollectionAssert.AreEqual(new[] { "M990", "new", "home" }, q.TermsWithMilieu("M990").ToArray());
        }

        [TestMethod]
        public void QueryQuotesAndWildcards()
        {
            var q = Parse("\"Rhylanor\"");
            Assert.AreEqual("name LIKE @term", q.Clauses[0], "quoted: the whole name");
            Assert.AreEqual("rhylanor", q.Terms[0]);

            // A missing closing quote is inferred, for the word it starts (an unclosed quote
            // doesn't span words).
            q = Parse("\"New Home");
            CollectionAssert.AreEqual(new[] { "new", "home" }, q.Terms.ToArray());
            Assert.AreEqual("name LIKE @term", q.Clauses[0]);

            q = Parse("reg%");
            Assert.AreEqual("name LIKE @term", q.Clauses[0], "% wildcard");
            q = Parse("reg_na");
            Assert.AreEqual("name LIKE @term", q.Clauses[0], "_ wildcard");

            Assert.AreEqual(0, Parse("\"\"").Clauses.Count, "empty quotes are ignored");
        }

        [TestMethod]
        public void QueryOperators()
        {
            void Check(string query, string clause, string term)
            {
                var q = Parse(query);
                Assert.AreEqual(clause, q.Clauses[0], query);
                Assert.AreEqual(term, q.Terms[0], query);
                Assert.AreEqual(SearchResultsType.Worlds, q.Types, query);
            }
            Check("uwp:A788899-C", "uwp LIKE @term", "a788899-c");
            Check("pbg:1__", "pbg LIKE @term", "1__");
            Check("zone:R", "zone LIKE @term", "r");
            Check("alleg:ImDd", "alleg LIKE @term", "imdd");
            Check("ix:3", "ix = @term", "3");
            Check("ex:A", "ex LIKE @term", "a");
            Check("cx:9", "cx LIKE @term", "9");
            Check("stellar:G2", "' ' || stellar || ' ' LIKE '% ' || @term || ' %'", "g2");
            Check("remark:Hi", "' ' || remarks || ' ' LIKE '% ' || @term || ' %'", "hi");
            Check("in:\"Spinward Marches\"", "sector_name LIKE '%' || @term || '%'", "spinward marches");

            var exact = Parse("exact:Regina");
            Assert.AreEqual("name LIKE @term", exact.Clauses[0]);
            Assert.AreEqual(SearchResultsType.Default, exact.Types, "exact: searches all types");
            Assert.AreEqual("SOUNDEX(name) = SOUNDEX(@term)", Parse("like:Rejina").Clauses[0]);

            // Operators combine: worlds with a UWP in a sector.
            var both = Parse("uwp:A* in:Spin");
            Assert.AreEqual(2, both.Clauses.Count);
        }

        [TestMethod]
        public void QueryTypeWords()
        {
            var q = Parse("spinward sector");
            Assert.AreEqual(SearchResultsType.Sectors, q.Types);
            Assert.AreEqual(1, q.Clauses.Count, "the type word is not a search term");
            Assert.AreEqual(SearchResultsType.Subsectors, Parse("regina subsector").Types);
            Assert.AreEqual(SearchResultsType.Worlds, Parse("regina world").Types);
        }

        [TestMethod]
        public void QuerySectorHex()
        {
            var q = Parse("Spinward Marches 1910");
            Assert.AreEqual(SearchResultsType.Worlds, q.Types);
            CollectionAssert.AreEqual(new[] { "spinward marches", "19", "10" }, q.Terms.ToArray());
            CollectionAssert.AreEqual(new[] { "sector_name LIKE @term || '%'", "hex_x = @term", "hex_y = @term" }, q.Clauses.ToArray());
        }

        [TestMethod]
        public void QuerySqlServerDialect()
        {
            // The SQL Server index uses + for concatenation.
            var q = SearchQuery.Parse("Regina", SearchResultsType.Default, SearchQuery.Dialect.SqlServer);
            Assert.AreEqual("name LIKE @term + '%' OR name LIKE '% ' + @term + '%'", q.Clauses[0]);
            q = SearchQuery.Parse("stellar:G2", SearchResultsType.Default, SearchQuery.Dialect.SqlServer);
            Assert.AreEqual("' ' + stellar + ' ' LIKE '% ' + @term + ' %'", q.Clauses[0]);
        }

        [TestMethod]
        public void SoundexCodes()
        {
            Assert.AreEqual("R163", SearchQuery.Soundex("Robert"));
            Assert.AreEqual("R163", SearchQuery.Soundex("Rupert"));
            Assert.AreEqual("R150", SearchQuery.Soundex("Rubin"));
            Assert.AreEqual("A261", SearchQuery.Soundex("Ashcraft"), "H doesn't separate same-coded letters");
            Assert.AreEqual("T522", SearchQuery.Soundex("Tymczak"));
            Assert.AreEqual("P236", SearchQuery.Soundex("Pfister"));
            Assert.AreEqual(SearchQuery.Soundex("Regina"), SearchQuery.Soundex("Rejina"));
            Assert.AreEqual("", SearchQuery.Soundex(""));
            Assert.AreEqual("", SearchQuery.Soundex("123"));
        }

        #region SQLite index (built from res/Sectors)

        private static SqliteSearchIndex index;
        private static string indexPath;

        [ClassInitialize]
        public static void BuildIndex(TestContext context)
        {
            indexPath = Path.Combine(Path.GetTempPath(), $"tm-search-test-{Guid.NewGuid():N}.db");
            index = new SqliteSearchIndex(indexPath);
            index.PopulateDatabase(ResourceManager.GetDedicatedInstance(), s => { });
        }

        [ClassCleanup]
        public static void DeleteIndex()
        {
            if (indexPath != null && File.Exists(indexPath))
                File.Delete(indexPath);
        }

        private static readonly Point SpinwardMarches = new Point(-4, -1);

        [TestMethod]
        public void SearchWorldByName()
        {
            var results = index.PerformSearch(null, "Regina", SearchResultsType.Default, 160, random: false).ToList();
            var worlds = results.OfType<WorldResult>().ToList();
            Assert.IsTrue(worlds.Any(w => w.Sector == SpinwardMarches && w.Hex.X == 19 && w.Hex.Y == 10), "Regina (Spinward Marches 1910)");
            Assert.IsTrue(results.OfType<SubsectorResult>().Any(s => s.SectorLocation == SpinwardMarches && s.Index == 'C'), "Regina subsector");
        }

        [TestMethod]
        public void SearchIsCaseInsensitiveAndPerMilieu()
        {
            Assert.IsTrue(index.PerformSearch(null, "REGINA", SearchResultsType.Worlds, 160, false).Any());
            Assert.IsTrue(index.PerformSearch("m1105", "regina", SearchResultsType.Worlds, 160, false).Any(), "milieu matches case-insensitively");
            Assert.IsFalse(index.PerformSearch("M9999", "regina", SearchResultsType.Default, 160, false).Any(), "no such milieu");
        }

        [TestMethod]
        public void SearchOperators()
        {
            var uwp = index.PerformSearch(null, "uwp:a788899-c", SearchResultsType.Default, 160, false).OfType<WorldResult>().ToList();
            Assert.IsTrue(uwp.Any(w => w.Sector == SpinwardMarches && w.Hex.X == 19 && w.Hex.Y == 10), "Regina's UWP");

            var hex = index.PerformSearch(null, "Spinward Marches 1910", SearchResultsType.Default, 160, false).OfType<WorldResult>().ToList();
            Assert.AreEqual(1, hex.Count, "sector + hex");
            Assert.AreEqual(19, hex[0].Hex.X);

            var like = index.PerformSearch(null, "like:Rejina", SearchResultsType.Worlds, 160, false).OfType<WorldResult>();
            Assert.IsTrue(like.Any(w => w.Sector == SpinwardMarches && w.Hex.X == 19), "sounds like");

            var sectors = index.PerformSearch(null, "spinward sector", SearchResultsType.Default, 160, false).ToList();
            Assert.IsTrue(sectors.All(r => r is SectorResult));
            Assert.IsTrue(sectors.OfType<SectorResult>().Any(s => s.SectorCoords == SpinwardMarches));

            var labels = index.PerformSearch(null, "imperium", SearchResultsType.Labels, 160, false).OfType<LabelResult>().ToList();
            Assert.IsTrue(labels.Count > 0, "labels and border names");
        }

        [TestMethod]
        public void SearchLimitsAndRandom()
        {
            Assert.AreEqual(5, index.PerformSearch(null, "a%", SearchResultsType.Worlds, 5, false).Count());
            var random = index.PerformSearch(null, null, SearchResultsType.Worlds, 1, random: true).ToList();
            Assert.AreEqual(1, random.Count, "random world");
            Assert.AreEqual(0, index.PerformSearch(null, null, SearchResultsType.Worlds, 1, random: false).Count(), "no query, no results");
        }

        [TestMethod]
        public void NearestWorldMatch()
        {
            var w = index.FindNearestWorldMatch("Regina", "M1105", -110, -70);
            Assert.IsNotNull(w);
            Assert.AreEqual(SpinwardMarches, w.Sector);
            Assert.IsNull(index.FindNearestWorldMatch("No Such World", "M1105", 0, 0));
        }

        [TestMethod]
        public void MissingIndexIsUnavailable()
        {
            var missing = new SqliteSearchIndex(Path.Combine(Path.GetTempPath(), "tm-no-such-index.db"));
            Assert.ThrowsExactly<SearchUnavailableException>(() => missing.PerformSearch(null, "regina", SearchResultsType.Default, 10, false).ToList());
        }

        #endregion
    }
}
