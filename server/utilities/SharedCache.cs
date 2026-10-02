#nullable enable
using System;
using System.Threading;

namespace Maps.Utilities
{
    /// <summary>
    /// Global generation counter for the shared caches. Incrementing it causes every
    /// SharedCache to rebuild its value on next access. Used by /admin/flush so that edited
    /// data files are picked up without an app restart.
    /// </summary>
    internal static class CacheGeneration
    {
        private static int current;

        public static int Current => Volatile.Read(ref current);

        public static void InvalidateAll() => Interlocked.Increment(ref current);
    }

    /// <summary>
    /// One lazily built value shared by all threads (so the value must be safe for concurrent
    /// reads), rebuilt after CacheGeneration.InvalidateAll() or Reset(). A rebuild happens once,
    /// under a lock; meanwhile other threads keep using the previous value.
    /// </summary>
    internal sealed class SharedCache<T> where T : class
    {
        private readonly Func<T> factory;
        private readonly object buildLock = new object();
        private Entry? entry;

        private sealed class Entry
        {
            public Entry(int generation, T value) { Generation = generation; Value = value; }
            public int Generation { get; }
            public T Value { get; }
        }

        public SharedCache(Func<T> factory)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public T Value
        {
            get
            {
                int generation = CacheGeneration.Current;
                Entry? current = Volatile.Read(ref entry);
                if (current != null && current.Generation == generation)
                    return current.Value;

                if (current != null && !Monitor.TryEnter(buildLock))
                {
                    // Another thread is rebuilding; use the previous value meanwhile.
                    return current.Value;
                }
                if (current == null)
                    Monitor.Enter(buildLock);
                try
                {
                    // Read the generation before building, so an invalidation that happens
                    // during a (slow) build causes another rebuild on the next access.
                    generation = CacheGeneration.Current;
                    current = Volatile.Read(ref entry);
                    if (current == null || current.Generation != generation)
                    {
                        current = new Entry(generation, factory());
                        Volatile.Write(ref entry, current);
                    }
                    return current.Value;
                }
                finally
                {
                    Monitor.Exit(buildLock);
                }
            }
        }

        /// <summary>Discards the value; the next access rebuilds it.</summary>
        public void Reset() => Volatile.Write(ref entry, null);
    }
}
