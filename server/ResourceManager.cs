#nullable enable
using Maps.Utilities;
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;

namespace Maps
{
    internal interface IDeserializable
    {
        void Deserialize(Stream stream, string mediaType, ErrorLogger? errors = null);
    }

    internal class ResourceManager
    {
        // Shared by all threads; replaced (dropping its cache) after CacheGeneration.InvalidateAll().
        private static readonly SharedCache<ResourceManager> s_instance = new SharedCache<ResourceManager>(() => new ResourceManager());
        
        /// <summary>
        /// The shared instance, whose cache all threads use (it's thread-safe).
        /// </summary>
        /// <returns></returns>
        public static ResourceManager GetInstance()
        {
            return s_instance.Value;
        }
        /// <summary>
        /// Use for tasks where caching should expire at the end of the lifetime.
        /// </summary>
        /// <returns></returns>
        public static ResourceManager GetDedicatedInstance()
        {
            return new ResourceManager();
        }

        private readonly LRUCache cache = new LRUCache(50);

        // Files are parsed outside the lock, so a slow parse doesn't block other threads; if two
        // threads load the same file at once, the first one cached wins.
        private object? CacheGet(string name) { lock (cache) return cache[name]; }
        private object CachePut(string name, object o)
        {
            lock (cache)
            {
                object? existing = cache[name];
                if (existing != null)
                    return existing;
                cache[name] = o;
                return o;
            }
        }

        private ResourceManager()
        {
        }

        public static T GetXmlFileObject<T>(string name)
        {
            using var stream = new FileStream(Util.MapPath(name), FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                object? o = new XmlSerializer(typeof(T)).Deserialize(stream);
                if (o == null || o.GetType() != typeof(T))
                    throw new ApplicationException($"Invalid file: {name}");
                return (T)o;
            }
            catch (InvalidOperationException ex) when (ex.InnerException is System.Xml.XmlException)
            {
                throw ex.InnerException;
            }
        }

        public T GetCachedXmlFileObject<T>(string name)
        {
            object? o = CacheGet(name);

            if (o == null)
                o = CachePut(name, GetXmlFileObject<T>(name)!);
            if (o == null)
                throw new ApplicationException("Unexpected null");

            return (T)o;
        }

        private static T GetDeserializableFileObject<T>(string name, string mediaType)
        {
            using (var stream = new FileStream(Util.MapPath(name), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ConstructorInfo constructorInfoObj = (typeof(T)).GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public, null,
                    CallingConventions.HasThis, new Type[0], null) ??
                    throw new TargetException();

                object obj = constructorInfoObj.Invoke(null);

                IDeserializable ides = obj as IDeserializable ??
                    throw new TargetException();

                ides.Deserialize(stream, mediaType);

                if (obj.GetType() != typeof(T))
                    throw new ApplicationException($"Invalid file: {name}");

                return (T)obj;
            }
        }
        public T GetCachedDeserializableFileObject<T>(string name, string mediaType)
        {
            object? obj = CacheGet(name);

            if (obj == null)
                obj = CachePut(name, GetDeserializableFileObject<T>(name, mediaType)!);
            if (obj == null)
                throw new ApplicationException("Unexpected null");

            return (T)obj;
        }
        public void Flush()
        {
            lock (cache)
                cache.Clear();
        }
    }
}
