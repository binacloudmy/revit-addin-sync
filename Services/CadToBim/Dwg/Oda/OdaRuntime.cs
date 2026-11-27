#if HAS_ODA
// AI-647 (AI-646 conditions 1 and 5): ODA initialises once per Revit session, lazily on the
// first DWG read, and uninitialises at shutdown. All ODA work runs on ONE dedicated thread:
// the SWIG MemoryManager keys its transactions by managed thread id, and odInitialize /
// readFile / odUninitialize are not documented as callable from arbitrary threads. Callers on
// any thread (the pane's thread pool, read_dwg_plan) block on that thread, one read at a time.
//
// Revit loads this add-in into the DEFAULT AssemblyLoadContext (BinaLoader uses LoadFrom on
// purpose for WPF), so the OdSwig assemblies land there too. If another add-in has already
// loaded OdSwig/OdKernel assemblies from somewhere else, ours would bind to theirs: that is
// refused at init with a reason, and the read falls back to ACadSharp.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services.CadToBim.Dwg
{
    internal static class OdaRuntime
    {
        private static readonly object Gate = new object();
        private static BlockingCollection<Action> _queue;
        private static Thread _thread;
        private static string _binDir;
        private static Exception _initFailure;
        private static volatile bool _initialized;
        private static bool _shutDown;

        /// <summary>A read waiting longer than this gives up (the ODA thread keeps the read; the caller falls back).</summary>
        internal static TimeSpan ReadTimeout = TimeSpan.FromSeconds(120);

        /// <summary>True once odInitialize succeeded this session (DwgReaders picks the later-read memory need).</summary>
        internal static bool IsInitialized => _initialized;

        /// <summary>Thread the ODA work runs on (the harness checks it stays the same).</summary>
        internal static int ThreadId => _thread?.ManagedThreadId ?? -1;

        /// <summary>Runs work on the ODA thread after a one-time init. Rethrows the work's exception.</summary>
        public static T Run<T>(string binDir, string keyFile, Func<T> work)
        {
            BlockingCollection<Action> queue;
            lock (Gate)
            {
                // Never a second odInitialize: after Shutdown the session is over for ODA.
                if (_shutDown) throw new InvalidOperationException("ODA was shut down for this Revit session");
                if (_queue == null)
                {
                    _queue = new BlockingCollection<Action>();
                    _thread = new Thread(Loop) { IsBackground = true, Name = "ODA" };
                    _thread.Start(_queue);
                }
                queue = _queue;
            }

            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add(() =>
            {
                try
                {
                    Init(binDir, keyFile);
                    done.SetResult(work());
                }
                catch (Exception ex) { done.SetException(ex); }
            });
            if (Task.WhenAny(done.Task, Task.Delay(ReadTimeout)).GetAwaiter().GetResult() != done.Task)   // Wait() would wrap the work's exception
                throw new TimeoutException("ODA did not finish within " + (int)ReadTimeout.TotalSeconds + " s");
            return done.Task.GetAwaiter().GetResult();
        }

        /// <summary>App.OnShutdown: odUninitialize on the ODA thread, if it ever initialised.</summary>
        public static void Shutdown()
        {
            BlockingCollection<Action> queue;
            Thread thread;
            lock (Gate)
            {
                queue = _queue;
                thread = _thread;
                _queue = null;
                _shutDown = true;
            }
            if (queue == null) return;
            queue.Add(() =>
            {
                if (_initialized) OdaSession.Stop();
                _initialized = false;
            });
            queue.CompleteAdding();
            thread?.Join(TimeSpan.FromSeconds(5));
        }

        private static void Loop(object state)
        {
            foreach (Action action in ((BlockingCollection<Action>)state).GetConsumingEnumerable())
                action();
        }

        private static void Init(string binDir, string keyFile)
        {
            if (_initFailure != null) throw new InvalidOperationException(_initFailure.Message, _initFailure);
            if (_initialized) return;   // one runtime per session; a changed odaDir takes effect on the next Revit start
            try
            {
                if (!Directory.Exists(binDir)) throw new DirectoryNotFoundException("no ODA bin folder");
                if (!File.Exists(keyFile)) throw new FileNotFoundException("no ODA key file");

                // A dynamic or from-bytes assembly has no Location (Location throws or is ""): counted as foreign.
                Assembly foreign = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => IsOda(a.GetName().Name)
                    && (a.IsDynamic || string.IsNullOrEmpty(a.Location)
                        || !string.Equals(Path.GetDirectoryName(a.Location), Path.GetFullPath(binDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)));
                if (foreign != null)
                    throw new InvalidOperationException("another add-in already loaded ODA " + foreign.GetName().Version);

                // PATH, not AddDllDirectory: ODA's own module loader (odrxDynamicLinker loading the .tx
                // chain) calls LoadLibrary without LOAD_LIBRARY_SEARCH_* flags, which AddDllDirectory
                // does not reach. Prepended, so ODA's names win over anything later on PATH.
                Environment.SetEnvironmentVariable("PATH", binDir + ";" + Environment.GetEnvironmentVariable("PATH"));
                _binDir = binDir;
                AssemblyLoadContext.Default.Resolving += Resolve;

                StartSession(keyFile);
                _initialized = true;
            }
            catch (Exception ex)
            {
                _initFailure = ex;   // never retried: ODA does not support a second odInitialize
                throw;
            }
        }

        // Separate and never inlined: OdaSession's ODA-typed members are JIT-resolved only here,
        // after the resolver above is attached.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartSession(string keyFile) => OdaSession.Start(keyFile);

        private static bool IsOda(string name) =>
            name != null && (name.StartsWith("OdSwig", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("OdKernel", StringComparison.OrdinalIgnoreCase));

        /// <summary>OdSwig*/OdKernel* names only, from the one ODA folder; anything else is not ours to answer.</summary>
        private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            try
            {
                if (!IsOda(name.Name) || _binDir == null) return null;
                // The examples library's assembly name differs from its file name.
                string file = name.Name.Equals("OdKernelExamplesMgdLib", StringComparison.OrdinalIgnoreCase)
                    ? "OdKernelExamplesLib_Net5.dll"
                    : name.Name + ".dll";
                string path = Path.Combine(_binDir, file);
                return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
            }
            catch (Exception)
            {
                return null;   // a resolver must never throw into another add-in's load
            }
        }
    }
}
#endif
