using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class SystemService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        public static JsonObject Info()
        {
            var total = 0UL;
            var available = 0UL;
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                total = status.ullTotalPhys / 1024 / 1024;
                available = status.ullAvailPhys / 1024 / 1024;
            }
            var cpu = "Unknown CPU";
            try
            {
                cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? cpu;
            }
            catch { }
            return new JsonObject
            {
                ["platform"] = "win32",
                ["arch"] = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                ["os"] = Environment.OSVersion.VersionString,
                ["cpu"] = cpu.Trim(),
                ["cores"] = Environment.ProcessorCount,
                ["memoryMB"] = (long)total,
                ["totalMemoryMB"] = (long)total,
                ["availableMemoryMB"] = (long)available,
                ["suggestedMemory"] = AutoMemoryMB("", 0, false)
            };
        }

        // PCL auto-memory: allocate in four stages instead of a fixed 1/4 of
        // physical memory. Targets grow with the number of installed mods.
        public static int AutoMemoryMB(string gameDir, int? modCount = null, bool optifine = false)
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            double availableGb = 8;
            if (GlobalMemoryStatusEx(ref status) && status.ullAvailPhys > 0)
                availableGb = status.ullAvailPhys / 1024d / 1024d / 1024d;
            availableGb = Math.Round(availableGb * 10) / 10;
            var mods = modCount ?? 0;
            if (modCount == null && !string.IsNullOrWhiteSpace(gameDir))
            {
                try
                {
                    var dir = Path.Combine(gameDir, "mods");
                    if (Directory.Exists(dir)) mods = Directory.GetFiles(dir).Length;
                }
                catch { }
            }
            double min, t1, t2, t3;
            if (optifine)
            {
                min = 0.5d; t1 = 1.5d; t2 = 3d; t3 = 5d;
            }
            else if (mods > 0)
            {
                min = 0.5d + mods / 150d;
                t1 = 1.5d + mods / 90d;
                t2 = 2.7d + mods / 50d;
                t3 = 4.5d + mods / 25d;
            }
            else
            {
                min = 0.5d; t1 = 1.5d; t2 = 2.5d; t3 = 4d;
            }
            var available = availableGb;
            var give = 0d;
            var delta = t1;
            give += Math.Min(available, delta);
            available -= delta;
            if (available >= 0.1d)
            {
                delta = t2 - t1;
                give += Math.Min(available * 0.7d, delta);
                available -= delta / 0.7d;
                if (available >= 0.1d)
                {
                    delta = t3 - t2;
                    give += Math.Min(available * 0.4d, delta);
                    available -= delta / 0.4d;
                    if (available >= 0.1d)
                    {
                        give += Math.Min(available * 0.15d, t3);
                    }
                }
            }
            give = Math.Round(Math.Max(give, min), 1);
            return Math.Max(1024, (int)(give * 1024));
        }

        public static JsonObject StorageStats()
        {
            long Size(string path)
            {
                if (!Directory.Exists(path)) return 0;
                try
                {
                    long total = 0;
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        try { total += new FileInfo(file).Length; } catch { }
                    return total;
                }
                catch { return 0; }
            }
            var root = AppPaths.RootDir;
            var stats = new JsonObject
            {
                ["root"] = root,
                ["total"] = 0L,
                ["assets"] = Size(Path.Combine(root, "assets")),
                ["libraries"] = Size(Path.Combine(root, "libraries")),
                ["versions"] = Size(Path.Combine(root, "versions")),
                ["instances"] = Size(Path.Combine(root, "instances")),
                ["cache"] = Size(Path.Combine(AppPaths.SettingsDir, "cache")),
                ["packs"] = 0L,
                ["natives"] = Size(Path.Combine(root, "natives")),
                ["runtime"] = Size(Path.Combine(root, "runtime")),
                ["mods"] = 0L,
                ["installerArtifacts"] = 0L
            };
            long total = 0;
            foreach (var key in new[] { "assets", "libraries", "versions", "instances", "cache", "natives", "runtime" })
                total += stats[key]?.GetValue<long>() ?? 0;
            stats["total"] = total;
            return stats;
        }

        public static long Clean()
        {
            var cache = Path.Combine(AppPaths.SettingsDir, "cache");
            if (!Directory.Exists(cache)) return 0;
            long freed = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
                    try { freed += new FileInfo(file).Length; } catch { }
                Directory.Delete(cache, true);
            }
            catch { }
            return freed;
        }

        public static long Prune()
        {
            return 0;
        }

        public static JsonObject PickDirectory(string title, string defaultPath)
        {
            var paths = new System.Collections.Generic.List<string>();
            void Show()
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = string.IsNullOrWhiteSpace(title) ? "选择文件夹" : title,
                    SelectedPath = Directory.Exists(defaultPath) ? defaultPath : "",
                    ShowNewFolderButton = true
                };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) paths.Add(dialog.SelectedPath);
            }
            if (System.Windows.Application.Current.Dispatcher.CheckAccess()) Show();
            else System.Windows.Application.Current.Dispatcher.Invoke(Show);
            return new JsonObject { ["canceled"] = paths.Count == 0, ["paths"] = new JsonArray(paths.Select(item => (JsonNode)item!).ToArray()) };
        }

        public static JsonObject PickFile(JsonElement filters, bool multi)
        {
            var paths = new System.Collections.Generic.List<string>();
            void Show()
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择文件", Multiselect = multi };
                if (filters.ValueKind == JsonValueKind.Array && filters.GetArrayLength() > 0)
                {
                    var extensions = new System.Collections.Generic.List<string>();
                    foreach (var filter in filters.EnumerateArray())
                    {
                        if (filter.ValueKind != JsonValueKind.Object || !filter.TryGetProperty("extensions", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                        foreach (var ext in list.EnumerateArray())
                        {
                            var text = ext.GetString() ?? "";
                            if (text.Length > 0 && text != "*") extensions.Add("*." + text.TrimStart('.'));
                        }
                    }
                    if (extensions.Count > 0) dialog.Filter = "可选文件|" + string.Join(";", extensions);
                }
                if (dialog.ShowDialog() == true) paths.AddRange(dialog.FileNames);
            }
            if (System.Windows.Application.Current.Dispatcher.CheckAccess()) Show();
            else System.Windows.Application.Current.Dispatcher.Invoke(Show);
            return new JsonObject { ["canceled"] = paths.Count == 0, ["paths"] = new JsonArray(paths.Select(item => (JsonNode)item!).ToArray()) };
        }

        public static string PickFolder()
        {
            var result = "";
            void Show()
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "选择游戏文件夹",
                    ShowNewFolderButton = true
                };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = dialog.SelectedPath;
            }
            if (System.Windows.Application.Current.Dispatcher.CheckAccess()) Show();
            else System.Windows.Application.Current.Dispatcher.Invoke(Show);
            return result;
        }
    }
}
