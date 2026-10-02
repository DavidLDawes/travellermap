#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SearchResultsType = Maps.Search.SearchEngine.SearchResultsType;

namespace Maps.Search
{
    /// <summary>
    /// A parsed search query: SQL WHERE clauses (each using the parameter "@term") with their
    /// values, and the result types to search. Shared by the SQL Server and SQLite indexes.
    ///
    /// Syntax: words match the start of any word in a name; "quoted" or % and _ wildcards match
    /// the whole name; operators uwp: pbg: zone: alleg: stellar: remark: ix: ex: cx: in: (worlds),
    /// exact: and like: (sounds like); the words "sector", "subsector" and "world" restrict the
    /// result types; "sector 1910" finds a world by sector name and hex.
    /// </summary>
    internal sealed class SearchQuery
    {
        /// <summary>The SQL used for string concatenation, which differs between databases.</summary>
        internal enum Dialect
        {
            SqlServer, // a + b
            Sqlite,    // a || b
        }

        public SearchResultsType Types { get; }
        public IReadOnlyList<string> Clauses { get; }
        public IReadOnlyList<string> Terms { get; }

        private SearchQuery(SearchResultsType types, List<string> clauses, List<string> terms)
        {
            Types = types;
            Clauses = clauses;
            Terms = terms;
        }

        private static readonly string[] OPS = {
            "uwp:",
            "pbg:",
            "zone:",
            "alleg:",
            "stellar:",
            "remark:",
            "exact:",
            "like:",
            "in:",
            "ix:", "ex:", "cx:"
        };
        private static readonly Regex RE_TERMS = new Regex("(" + string.Join("|", OPS) + ")?(\"[^\"]+\"|\\S+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SECTOR_HEX_REGEX = new Regex(@"^(?<sector>[A-Za-z0-9!' ]{3,}) (?<hex>\d{4})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static IEnumerable<string> ParseTerms(string q) =>
            RE_TERMS.Matches(q).Cast<Match>().Select(m => m.Value).Where(s => !string.IsNullOrWhiteSpace(s));

        public static SearchQuery Parse(string? query, SearchResultsType types, Dialect dialect)
        {
            string Concat(params string[] parts) => string.Join(dialect == Dialect.Sqlite ? " || " : " + ", parts);

            var clauses = new List<string>();
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(query))
                return new SearchQuery(types, clauses, terms);
            query = query!.Trim().ToLowerInvariant();

            Match m = SECTOR_HEX_REGEX.Match(query);
            if (m.Success)
            {
                int hex = int.Parse(m.Groups["hex"].Value);
                clauses.Add($"sector_name LIKE {Concat("@term", "'%'")}");
                terms.Add(m.Groups["sector"].Value);
                clauses.Add("hex_x = @term");
                terms.Add((hex / 100).ToString());
                clauses.Add("hex_y = @term");
                terms.Add((hex % 100).ToString());
                return new SearchQuery(SearchResultsType.Worlds, clauses, terms);
            }

            foreach (string t in ParseTerms(query))
            {
                string term = t;
                string? op = null;
                bool quoted = false;

                foreach (var o in OPS)
                {
                    if (term.StartsWith(o, StringComparison.Ordinal))
                    {
                        op = o;
                        term = term.Substring(o.Length);
                        break;
                    }
                }

                // Infer a trailing "
                if (term.StartsWith("\"", StringComparison.Ordinal) && (!term.EndsWith("\"", StringComparison.Ordinal) || term.Length == 1))
                    term += '"';
                if (term.Length >= 2 && term.StartsWith("\"", StringComparison.Ordinal) && term.EndsWith("\"", StringComparison.Ordinal))
                {
                    quoted = true;
                    term = term.Substring(1, term.Length - 2);
                }
                if (term.Length == 0)
                    continue;

                string clause;
                switch (op)
                {
                    case "uwp:": clause = "uwp LIKE @term"; types = SearchResultsType.Worlds; break;
                    case "pbg:": clause = "pbg LIKE @term"; types = SearchResultsType.Worlds; break;
                    case "ix:": clause = "ix = @term"; types = SearchResultsType.Worlds; break;
                    case "ex:": clause = "ex LIKE @term"; types = SearchResultsType.Worlds; break;
                    case "cx:": clause = "cx LIKE @term"; types = SearchResultsType.Worlds; break;
                    case "zone:": clause = "zone LIKE @term"; types = SearchResultsType.Worlds; break;
                    case "alleg:": clause = "alleg LIKE @term"; types = SearchResultsType.Worlds; break;
                    case "stellar:":
                        clause = $"{Concat("' '", "stellar", "' '")} LIKE {Concat("'% '", "@term", "' %'")}";
                        types = SearchResultsType.Worlds;
                        break;
                    case "remark:":
                        clause = $"{Concat("' '", "remarks", "' '")} LIKE {Concat("'% '", "@term", "' %'")}";
                        types = SearchResultsType.Worlds;
                        break;
                    case "in:":
                        clause = $"sector_name LIKE {Concat("'%'", "@term", "'%'")}";
                        types = SearchResultsType.Worlds;
                        break;
                    case "exact:": clause = "name LIKE @term"; break;
                    case "like:": clause = "SOUNDEX(name) = SOUNDEX(@term)"; break;
                    default:
                        if (quoted || term.Contains("%") || term.Contains("_"))
                        {
                            clause = "name LIKE @term";
                        }
                        else if (term == "sector")
                        {
                            types = SearchResultsType.Sectors;
                            continue;
                        }
                        else if (term == "subsector")
                        {
                            types = SearchResultsType.Subsectors;
                            continue;
                        }
                        else if (term == "world")
                        {
                            types = SearchResultsType.Worlds;
                            continue;
                        }
                        else
                        {
                            // The start of the name, or of a later word in it.
                            clause = $"name LIKE {Concat("@term", "'%'")} OR name LIKE {Concat("'% '", "@term", "'%'")}";
                        }
                        break;
                }

                clauses.Add(clause);
                terms.Add(term);
            }
            return new SearchQuery(types, clauses, terms);
        }

        /// <summary>
        /// The WHERE condition: the milieu, then every clause, ANDed, with "@term" renamed per
        /// clause to "@term0", "@term1", ...; the values are TermsWithMilieu.
        /// </summary>
        public string Where => string.Join(" AND ",
            new[] { "milieu = @term" }.Concat(Clauses).Select((clause, index) => "(" + clause.Replace("@term", $"@term{index}") + ")"));

        public IReadOnlyList<string> TermsWithMilieu(string? milieu) =>
            new[] { milieu ?? SectorMap.DEFAULT_MILIEU }.Concat(Terms).ToList();

        /// <summary>American Soundex code (e.g. "Robert" -> "R163"), for like: queries.</summary>
        public static string Soundex(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            string letters = new string(s!.ToUpperInvariant().Where(c => c >= 'A' && c <= 'Z').ToArray());
            if (letters.Length == 0)
                return "";
            static char Code(char c) => c switch
            {
                'B' or 'F' or 'P' or 'V' => '1',
                'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
                'D' or 'T' => '3',
                'L' => '4',
                'M' or 'N' => '5',
                'R' => '6',
                _ => '0', // vowels, H, W, Y
            };
            var result = new System.Text.StringBuilder().Append(letters[0]);
            char last = Code(letters[0]);
            for (int i = 1; i < letters.Length && result.Length < 4; ++i)
            {
                char c = letters[i];
                char code = Code(c);
                if (code != '0' && code != last)
                    result.Append(code);
                // H and W don't separate letters with the same code; vowels do.
                if (c != 'H' && c != 'W')
                    last = code;
            }
            return result.ToString().PadRight(4, '0');
        }
    }
}
