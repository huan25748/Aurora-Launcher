using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace AuroraLauncher
{
    internal static class PerformanceTuner
    {
        private const string GpuPreferenceKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

        public static void Apply(Process process, string javaPath)
        {
            try { process.PriorityClass = ProcessPriorityClass.High; }
            catch
            {
                try { process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            }
            try { PreferHighPerformanceGpu(javaPath); } catch { }
        }

        // 让 Windows 为这个 java.exe 使用「高性能」显卡，和 设置 → 显示 → 图形 里的选项等价。
        private static void PreferHighPerformanceGpu(string javaPath)
        {
            if (string.IsNullOrWhiteSpace(javaPath)) return;
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferenceKey, true);
            if (key == null) return;
            key.SetValue(javaPath, "GpuPreference=2;", RegistryValueKind.String);
        }
    }
}
