using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Factions
{
    /// <summary>
    /// <c>factions bench dump [path]</c>: a blocking full collection on the game thread, then a full memory dump of
    /// this process for offline heap analysis (ClrMD's GC-root walk covers handles and stacks, which
    /// <see cref="BenchRoots"/> cannot see). The dump is written from a PSS snapshot, a copy-on-write clone taken
    /// in milliseconds, on a background thread, so the game keeps running while several GB are written. The
    /// default path is <c>dumps\factions-&lt;utc&gt;.dmp</c> under the instance directory. One at a time; test
    /// server only.
    /// </summary>
    internal static class BenchDump
    {
        private const uint PssCaptureFlags =
            0x00000001    // PSS_CAPTURE_VA_CLONE
            | 0x00000004  // PSS_CAPTURE_HANDLES
            | 0x00000008  // PSS_CAPTURE_HANDLE_NAME_INFORMATION
            | 0x00000010  // PSS_CAPTURE_HANDLE_BASIC_INFORMATION
            | 0x00000020  // PSS_CAPTURE_HANDLE_TYPE_SPECIFIC_INFORMATION
            | 0x00000040  // PSS_CAPTURE_HANDLE_TRACE
            | 0x00000080  // PSS_CAPTURE_THREADS
            | 0x00000100  // PSS_CAPTURE_THREAD_CONTEXT
            | 0x00000200  // PSS_CAPTURE_THREAD_CONTEXT_EXTENDED
            | 0x04000000  // PSS_CREATE_BREAKAWAY_OPTIONAL
            | 0x08000000  // PSS_CREATE_BREAKAWAY
            | 0x20000000  // PSS_CREATE_USE_VM_ALLOCATIONS
            | 0x80000000; // PSS_CREATE_RELEASE_SECTION

        private const uint ContextAllAmd64 = 0x0010001F;

        private const uint DumpType =
            0x00000002    // MiniDumpWithFullMemory
            | 0x00000004  // MiniDumpWithHandleData
            | 0x00000020  // MiniDumpWithUnloadedModules
            | 0x00000800  // MiniDumpWithFullMemoryInfo
            | 0x00001000; // MiniDumpWithThreadInfo

        private const int IsProcessSnapshotCallback = 16;
        private const int SFalse = 1;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct MinidumpCallbackInformation
        {
            public IntPtr CallbackRoutine;
            public IntPtr CallbackParam;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool MinidumpCallback(IntPtr param, IntPtr input, IntPtr output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern int PssCaptureSnapshot(IntPtr process, uint captureFlags, uint threadContextFlags, out IntPtr snapshot);

        [DllImport("kernel32.dll")]
        private static extern int PssFreeSnapshot(IntPtr process, IntPtr snapshot);

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file, uint dumpType,
            IntPtr exceptionParam, IntPtr userStreamParam, ref MinidumpCallbackInformation callbackParam);

        private static readonly MinidumpCallback Callback = OnCallback;
        private static int _running;

        /// <summary>MINIDUMP_CALLBACK_INPUT is packed to 4: ProcessId at 0, ProcessHandle at 4, CallbackType at 12.
        /// A snapshot handle must be answered S_FALSE in the output's Status, at offset 0.</summary>
        private static bool OnCallback(IntPtr param, IntPtr input, IntPtr output)
        {
            if (input == IntPtr.Zero || output == IntPtr.Zero) return true;
            if (Marshal.ReadInt32(input, 12) == IsProcessSnapshotCallback) Marshal.WriteInt32(output, 0, SFalse);
            return true;
        }

        internal static string Run(string[] t)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return "bench dump: a dump is still being written.";
            string path;
            try
            {
                path = t != null && t.Length > 3 ? t[3] : DefaultPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
            }
            catch (Exception ex)
            {
                _running = 0;
                return "bench dump: bad path: " + ex.Message;
            }

            long before = GC.GetTotalMemory(false);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            long after = GC.GetTotalMemory(false);
            string census = GridRetention.Census();

            IntPtr self = GetCurrentProcess();
            IntPtr snapshot;
            long t0 = Stopwatch.GetTimestamp();
            int hr = PssCaptureSnapshot(self, PssCaptureFlags, ContextAllAmd64, out snapshot);
            double captureMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (hr != 0)
            {
                _running = 0;
                return "bench dump: PssCaptureSnapshot failed, error " + hr;
            }

            uint pid = (uint)Process.GetCurrentProcess().Id;
            var thread = new Thread(() => Write(self, snapshot, pid, path)) { IsBackground = true, Name = "Factions bench dump" };
            thread.Start();
            return "bench dump: heapMB " + Mb(before) + " -> " + Mb(after) + " snapshotMs="
                + captureMs.ToString("0", CultureInfo.InvariantCulture) + " writing " + path + "; " + census;
        }

        private static void Write(IntPtr self, IntPtr snapshot, uint pid, string path)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                bool ok;
                int err = 0;
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    var info = new MinidumpCallbackInformation
                    {
                        CallbackRoutine = Marshal.GetFunctionPointerForDelegate(Callback),
                        CallbackParam = IntPtr.Zero,
                    };
                    ok = MiniDumpWriteDump(snapshot, pid, fs.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, ref info);
                    if (!ok) err = Marshal.GetLastWin32Error();
                    GC.KeepAlive(Callback);
                }
                double s = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                long bytes = new FileInfo(path).Length;
                if (ok) FactionsPlugin.Log.Info("BENCH_DUMP written path=" + path + " mb=" + Mb(bytes) + " s=" + s.ToString("0", CultureInfo.InvariantCulture));
                else FactionsPlugin.Log.Warn("BENCH_DUMP failed path=" + path + " error=" + err);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn("BENCH_DUMP failed path=" + path + " " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                PssFreeSnapshot(self, snapshot);
                _running = 0;
            }
        }

        private static string DefaultPath()
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            string root = plugin != null && plugin.InstanceDir != null ? plugin.InstanceDir : Path.GetTempPath();
            return Path.Combine(Path.Combine(root, "dumps"),
                "factions-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".dmp");
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
