using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace AuroraLauncher
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static System.Threading.Mutex? _singleInstance;
        public static bool BlockedByExistingInstance { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // One launcher per directory: WebView2 locks its user-data folder,
            // so a second copy in the same folder would otherwise fail later.
            try
            {
                if (AnotherInstanceInDirectory())
                {
                    BlockedByExistingInstance = true;
                    MessageBox.Show("同一目录只能同时运行一个 Aurora Launcher。\n请先关闭正在运行的启动器，再打开新版本。",
                        "Aurora Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
                var path = System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/').ToLowerInvariant();
                var hash = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(path)))[..16];
                _singleInstance = new System.Threading.Mutex(true, "Local\\AuroraLauncher-" + hash, out var created);
                if (!created)
                {
                    BlockedByExistingInstance = true;
                    MessageBox.Show("同一目录只能同时运行一个 Aurora Launcher。\n请先关闭正在运行的启动器，再打开新版本。",
                        "Aurora Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }
            catch { }
            base.OnStartup(e);
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            _ = Task.Run(ModNamesService.Warm);
            try
            {
                var settings = BridgeService.GetSettings();
                if (settings["autoImportVersions"]?.GetValue<bool>() != false)
                {
                    InstanceArchiveService.ImportExisting(true);
                }
            }
            catch { }
        }

        private static bool AnotherInstanceInDirectory()
        {
            try
            {
                var dir = System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/');
                foreach (var process in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId) continue;
                        if (!process.ProcessName.StartsWith("AuroraLauncher", StringComparison.OrdinalIgnoreCase)) continue;
                        var file = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(file)) continue;
                        var other = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))?.TrimEnd('\\', '/');
                        if (string.Equals(dir, other, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
            return false;
        }
    }
}
