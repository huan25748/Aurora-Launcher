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
                try
                {
                    Directory.Delete(directory, true);
                    GameEvents.Emit("log", new JsonObject
                    {
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["source"] = "launcher",
                        ["level"] = "info",
                        ["text"] = "安装完成后清理多余版本目录：" + folder
                    });
                }
                catch (Exception ex)
                {
                    GameEvents.Emit("log", new JsonObject
                    {
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["source"] = "launcher",
                        ["level"] = "warning",
                        ["text"] = "清理版本目录失败 " + folder + "：" + ex.Message
                    });
                }
            }
        }

        // Removes the loader/vanilla version folders that were only used to build
        // this install. PCL keeps a single versions/<instance> folder, so once the
        // self-contained instance exists its source chain is no longer needed.
        public static void CleanupSourceChain(string sourceVersionId, string keepFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceVersionId)) return;
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
                    if (instance["selfContained"]?.GetValue<bool>() != true &&
                        string.Equals(instance["sourceVersionId"]?.GetValue<string>(), folder, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            var current = sourceVersionId;
            var guard = 0;
            while (!string.IsNullOrWhiteSpace(current) && guard++ < 8)
            {
                var folder = current!;
                if (string.Equals(folder, keepFolder, StringComparison.OrdinalIgnoreCase)) break;
                var jsonFile = Path.Combine(VersionsDir, folder, folder + ".json");
                string? parent = null;
                if (File.Exists(jsonFile))
                {
                    try
                    {
                        parent = (JsonNode.Parse(File.ReadAllText(jsonFile)) as JsonObject)?["inheritsFrom"]?.GetValue<string>();
                    }
                    catch { }
                }
                if (!Referenced(folder))
                {
                    try
                    {
                        var dir = Path.Combine(VersionsDir, folder);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    }
                    catch { }
                }
                current = parent;
            }
        }
    }
}
