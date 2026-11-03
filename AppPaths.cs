using System;
using System.IO;

namespace AuroraLauncher
{
    internal static class AppPaths
    {
        private static string _rootDir = Path.Combine(AppContext.BaseDirectory, ".minecraft");

        public static string BaseDir { get; } = AppContext.BaseDirectory;
        public static string RootDir
        {
            get => _rootDir;
            set => _rootDir = string.IsNullOrWhiteSpace(value) ? Path.Combine(BaseDir, ".minecraft") : Path.GetFullPath(value);
        }
        public static string SettingsDir { get; } = Path.Combine(BaseDir, ".aurora");
        // While an install runs, every version/libraries write is redirected here so
        // versions/ only receives the final instance folder (PCL behaviour).
        public static string? InstallRootOverride { get; set; }
        public static string InstallRoot => string.IsNullOrWhiteSpace(InstallRootOverride) ? RootDir : Path.GetFullPath(InstallRootOverride!);
        public static string InstancesDir => Path.Combine(RootDir, "instances");

        public static void EnsureLayout()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(Path.Combine(RootDir, "versions"));
            Directory.CreateDirectory(Path.Combine(RootDir, "libraries"));
            Directory.CreateDirectory(Path.Combine(RootDir, "assets"));
            Directory.CreateDirectory(Path.Combine(RootDir, "runtime"));
            Directory.CreateDirectory(InstancesDir);
            Directory.CreateDirectory(SettingsDir);
        }

        public static void UseRoot(string path)
        {
            RootDir = path;
            EnsureLayout();
        }
    }
}
