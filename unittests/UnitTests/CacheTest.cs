using Maps.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTests
{
    [TestClass]
    public class CacheTest
    {
        private class Box { public int Id; }

        private static T OnNewThread<T>(System.Func<T> f)
        {
            T result = default(T);
            var t = new Thread(() => result = f());
            t.Start();
            t.Join();
            return result;
        }

        [TestMethod]
        public void SharedCacheSharesOneValue()
        {
            int built = 0;
            var cache = new SharedCache<Box>(() => new Box { Id = Interlocked.Increment(ref built) });

            Box first = cache.Value, second = cache.Value;
            Assert.AreSame(first, second, "repeated access returns the cached value");

            // Other threads get the same value.
            Assert.AreSame(first, OnNewThread(() => cache.Value));
            Assert.AreEqual(1, built);
        }

        [TestMethod]
        public void SharedCacheBuildsOnceUnderConcurrentAccess()
        {
            int built = 0;
            var cache = new SharedCache<Box>(() =>
            {
                Interlocked.Increment(ref built);
                Thread.Sleep(50); // A slow build, so many threads arrive while it runs.
                return new Box();
            });
            var start = new ManualResetEventSlim();
            var values = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => { start.Wait(); return cache.Value; }))
                .ToArray();
            start.Set();
            Task.WaitAll(values);

            Assert.AreEqual(1, built, "built once");
            Assert.IsTrue(values.All(v => v.Result == values[0].Result), "every thread got the same value");
        }

        [TestMethod]
        public void InvalidateAllRebuilds()
        {
            var cache = new SharedCache<Box>(() => new Box());
            Box before = cache.Value;

            CacheGeneration.InvalidateAll();

            Box after = OnNewThread(() => cache.Value);
            Assert.AreNotSame(before, after, "rebuilt after invalidation");
            Assert.AreSame(after, cache.Value, "and shared again");
        }

        [TestMethod]
        public void ResetRebuildsForEveryone()
        {
            var cache = new SharedCache<Box>(() => new Box());
            Box before = cache.Value;

            cache.Reset();

            Box after = OnNewThread(() => cache.Value);
            Assert.AreNotSame(before, after);
            Assert.AreSame(after, cache.Value);
        }
    }
}
