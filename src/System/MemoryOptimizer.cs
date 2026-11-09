using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AuroraLauncher
{
    // PCL 风格的内存整理：GC + 释放自身工作集 + 回收当前用户可访问进程的闲置工作集。
    internal static class MemoryOptimizer
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        public static long Optimize()
        {
            var before = AvailablePhysical();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            try { EmptyWorkingSet(GetCurrentProcess()); } catch { }

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id <= 4 || process.Id == Environment.ProcessId) continue;
                    EmptyWorkingSet(process.Handle);
                }
                catch { }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
            var after = AvailablePhysical();
            return Math.Max(0, after - before);
        }

        private static long AvailablePhysical()
        {
            try
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                return GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.AvailPhys, long.MaxValue) : 0;
            }
            catch { return 0; }
        }
    }
}
