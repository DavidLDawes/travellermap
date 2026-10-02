#nullable enable
using Maps.Utilities;
using SQLitePCL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Maps.Search
{
    /// <summary>
    /// Loads the native SQLite library (e_sqlite3, from SQLitePCLRaw.lib.e_sqlite3) and registers
    /// it with SQLitePCLRaw, which Microsoft.Data.Sqlite uses.
    ///
    /// SQLitePCLRaw's own loader looks next to its assembly, which fails under ASP.NET on IIS:
    /// assemblies are shadow-copied to "Temporary ASP.NET Files" without their native files (and
    /// its fallback path is URL-escaped, so it breaks for paths with spaces). So on .NET
    /// Framework the library is loaded from the site's bin\runtimes\{arch}\native folder; on .NET,
    /// the runtime's own native library resolution (deps.json) finds it.
    /// </summary>
    internal static class SqliteNative
    {
        private const string LibraryName = "e_sqlite3";
        private static readonly object s_lock = new object();
        private static bool s_initialized;

        public static void Init()
        {
            lock (s_lock)
            {
                if (s_initialized)
                    return;
                IntPtr handle = Load();
                SQLite3Provider_dynamic_cdecl.Setup(LibraryName, new FunctionPointers(handle));
                raw.SetProvider(new SQLite3Provider_dynamic_cdecl());
                s_initialized = true;
            }
        }

#if NETFRAMEWORK
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static IntPtr Load()
        {
            string relative = Path.Combine("runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native", LibraryName + ".dll");
            var candidates = new List<string>
            {
                Path.Combine(Util.MapPath("~/bin"), relative),             // IIS site
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative), // e.g. unit tests
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", relative),
            };
            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                    continue;
                IntPtr handle = LoadLibrary(path);
                if (handle != IntPtr.Zero)
                    return handle;
            }
            throw new DllNotFoundException($"{LibraryName}.dll not found or not loadable; looked in: {string.Join("; ", candidates)}");
        }

        private sealed class FunctionPointers : IGetFunctionPointer
        {
            private readonly IntPtr handle;
            public FunctionPointers(IntPtr handle) { this.handle = handle; }
            public IntPtr GetFunctionPointer(string name) => GetProcAddress(handle, name);
        }
#else
        private static IntPtr Load() => NativeLibrary.Load(LibraryName, typeof(SqliteNative).Assembly, null);

        private sealed class FunctionPointers : IGetFunctionPointer
        {
            private readonly IntPtr handle;
            public FunctionPointers(IntPtr handle) { this.handle = handle; }
            public IntPtr GetFunctionPointer(string name) => NativeLibrary.TryGetExport(handle, name, out IntPtr p) ? p : IntPtr.Zero;
        }
#endif
    }
}
