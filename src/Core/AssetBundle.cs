using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AuroraLauncher
{
    internal sealed class AssetBundle
    {
        public string RendererDir { get; private init; } = "";
        public string ShimFile { get; private init; } = "";
        public string LoaderFile { get; private init; } = "";
        public string VersionHash { get; private init; } = "";

        private static AssetBundle? _current;

        public static AssetBundle Ensure()
        {
            if (_current != null) return _current;
            var baseDir = AppContext.BaseDirectory;
            var appDir = Path.Combine(AppPaths.SettingsDir, "app");
            var renderer = Path.Combine(appDir, "renderer");
            var shim = Path.Combine(appDir, "web", "aurora-shim.js");
            var loader = Path.Combine(appDir, "WebView2Loader.dll");
            var marker = Path.Combine(appDir, "version.txt");
            var versionHash = HashAssets();

            var needsExtract = !Directory.Exists(renderer) || !File.Exists(shim) || !File.Exists(marker)
                || File.ReadAllText(marker).Trim() != versionHash;
            if (needsExtract)
            {
                try
                {
                    var temp = appDir + ".new";
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    Directory.CreateDirectory(temp);
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AuroraLauncher.assets.zip");
                    if (stream == null) throw new Exception("缺少内置界面资源");
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var destination = Path.GetFullPath(Path.Combine(temp, entry.FullName));
                        if (!destination.StartsWith(Path.GetFullPath(temp), StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        entry.ExtractToFile(destination, true);
                    }
                    File.WriteAllText(Path.Combine(temp, "version.txt"), versionHash);
                    if (Directory.Exists(appDir)) Directory.Delete(appDir, true);
                    Directory.Move(temp, appDir);
                }
                catch
                {
                    // Development / unpacked builds keep using the files next to the exe.
                    if (!Directory.Exists(renderer)) renderer = Path.Combine(baseDir, "renderer");
                    if (!File.Exists(shim)) shim = Path.Combine(baseDir, "web", "aurora-shim.js");
                }
            }

            var loaderFile = loader;
            if (!File.Exists(loaderFile))
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AuroraLauncher.webview2loader.dll");
                    if (stream != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(loaderFile)!);
                        using var output = File.Create(loaderFile);
                        stream.CopyTo(output);
                    }
                }
                catch { }
            }
            if (File.Exists(loaderFile))
            {
                try { NativeLibrary.Load(loaderFile); } catch { }
            }

            _current = new AssetBundle { RendererDir = renderer, ShimFile = shim, LoaderFile = loaderFile, VersionHash = versionHash };
            return _current;
        }

        private static string HashAssets()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AuroraLauncher.assets.zip");
                if (stream == null) return "1.1.0";
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash)[..16].ToLowerInvariant();
            }
            catch { return "1.1.0"; }
        }
    }
}
