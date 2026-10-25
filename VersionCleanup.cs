using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class VersionCleanup
    {
        private static string VersionsDir => Path.Combine(AppPaths.RootDir, "versions");

        public static HashSet<string> Snapshot()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(VersionsDir)) return result;
            foreach (var directory in Directory.GetDirectories(VersionsDir))
                result.Add(Path.GetFileName(directory)!);
            return result;
        }

        public static void Cleanup(HashSet<string> before, string keepFolder)
        {
            if (!Directory.Exists(VersionsDir)) return;
            var instances = BridgeService.ListInstances().OfType<JsonObject>().ToList();
            bool Referenced(string folder)
            {
                foreach (var instance in instances)
                {
                    if (string.Equals(instance["versionId"]?.GetValue<string>(), folder, StringComparison.OrdinalIgnoreCase)) return true;
                    var gameDir = instance["gameDir"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(gameDir))
                    {
                        var relative = Path.GetRelativePath(Path.GetFullPath(VersionsDir), Path.GetFullPath(gameDir));
                        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                        {
                            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
                            if (string.Equals(first, folder, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                    }
                    // Non-self-contained legacy instances still launch directly from their loader folder.
                    if (instance["selfContained"]?.GetValue<bool>() != true &&
                        string.Equals(instance["sourceVersionId"]?.GetValue<string>(), folder, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            foreach (var directory in Directory.GetDirectories(VersionsDir))
            {
                var folder = Path.GetFileName(directory)!;
                if (string.Equals(folder, keepFolder, StringComparison.OrdinalIgnoreCase)) continue;
                if (before.Contains(folder)) continue;
                if (Referenced(folder)) continue;
                if (!File.Exists(Path.Combine(directory, folder + ".json"))) continue;
                try { Directory.Delete(directory, true); } catch { }
            }
        }
    }
}
