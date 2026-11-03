using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class InstallCleanup
    {
        public static void Remove(string? instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return;
            var instanceDir = Path.Combine(AppPaths.InstancesDir, instanceId);
            string? gameDir = null;
            string? versionId = null;
            try
            {
                var file = Path.Combine(instanceDir, "instance.json");
                if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject instance)
                {
                    gameDir = instance["gameDir"]?.GetValue<string>();
                    versionId = instance["versionId"]?.GetValue<string>();
                }
            }
            catch { }
            try { if (Directory.Exists(instanceDir)) Directory.Delete(instanceDir, true); } catch { }
            try
            {
                if (!string.IsNullOrWhiteSpace(gameDir) && SafeGameDir(gameDir!) && Directory.Exists(gameDir))
                    Directory.Delete(gameDir!, true);
            }
            catch { }
            try
            {
                if (!string.IsNullOrWhiteSpace(versionId))
                {
                    var versionDir = Path.Combine(AppPaths.RootDir, "versions", versionId!);
                    var shared = BridgeService.ListInstances().OfType<JsonObject>().Any(item =>
                        string.Equals(item["versionId"]?.GetValue<string>(), versionId, StringComparison.OrdinalIgnoreCase));
                    if (!shared && Directory.Exists(versionDir)) Directory.Delete(versionDir, true);
                }
            }
            catch { }
        }

        private static bool SafeGameDir(string gameDir)
        {
            try
            {
                var full = Path.GetFullPath(gameDir);
                var root = Path.GetFullPath(AppPaths.RootDir);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                if (full.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
                var instances = Path.GetFullPath(AppPaths.InstancesDir);
                var versions = Path.GetFullPath(Path.Combine(AppPaths.RootDir, "versions"));
                return full.StartsWith(instances + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(versions + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}