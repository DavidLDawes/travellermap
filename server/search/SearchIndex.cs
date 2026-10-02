#nullable enable
using System;
using System.Collections.Generic;

namespace Maps.Search
{
    /// <summary>A search index implementation (SQL Server on the IIS host).</summary>
    internal interface ISearchIndex
    {
        IEnumerable<SearchResult> PerformSearch(string? milieu, string? query, SearchEngine.SearchResultsType types, int maxResultsPerType, bool random);
        WorldResult? FindNearestWorldMatch(string name, string milieu, int x, int y);
        /// <summary>Rebuilds the index from the sector data, reporting progress.</summary>
        void PopulateDatabase(ResourceManager resourceManager, Action<string> statusCallback);
    }

    /// <summary>Thrown when no search index is configured for this host.</summary>
    internal sealed class SearchUnavailableException : Exception
    {
        public SearchUnavailableException() : base("Search is not available on this server.") { }
        public SearchUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Search, through the host's index (SearchEngine.Index). Hosts without one throw
    /// SearchUnavailableException, reported as 503 Service Unavailable.
    /// </summary>
    internal static class SearchEngine
    {
        [Flags]
        public enum SearchResultsType : int
        {
            Sectors = 1 << 0,
            Subsectors = 1 << 1,
            Worlds = 1 << 2,
            Labels = 1 << 3,
            Default = Sectors | Subsectors | Worlds | Labels
        }

        public static ISearchIndex? Index { get; set; }

        private static ISearchIndex RequireIndex() => Index ?? throw new SearchUnavailableException();

        public static IEnumerable<SearchResult> PerformSearch(string? milieu, string? query, SearchResultsType types, int maxResultsPerType, bool random = false)
            => RequireIndex().PerformSearch(milieu, query, types, maxResultsPerType, random);

        public static WorldResult? FindNearestWorldMatch(string name, string milieu, int x, int y)
            => RequireIndex().FindNearestWorldMatch(name, milieu, x, y);

        public static void PopulateDatabase(ResourceManager resourceManager, Action<string> statusCallback)
            => RequireIndex().PopulateDatabase(resourceManager, statusCallback);
    }
}
