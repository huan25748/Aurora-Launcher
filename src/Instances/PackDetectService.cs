using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class PackDetectService
    {
        /// <summary>
        /// 识别其他启动器留下的整合包来源信息。
        /// 目前支持 Prism / MultiMC 的 instance.cfg（ManagedPack* 字段）。
        /// </summary>
        public static JsonObject? Detect(string instanceId)
        {
            var instance = BridgeService.GetInstance(instanceId) as JsonObject;
            if (instance == null) return null;
            var gameDir = instance["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            foreach (var dir in new[] { Path.Combine(AppPaths.InstancesDir, instanceId), gameDir })
            {
                var cfg = Path.Combine(dir, "instance.cfg");
                if (!File.Exists(cfg)) continue;
                try
                {
                    var map = File.ReadAllLines(cfg)
                        .Select(line => line.Split('=', 2))
                        .Where(parts => parts.Length == 2)
                        .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    if (!map.TryGetValue("ManagedPackID", out var projectId) || string.IsNullOrWhiteSpace(projectId)) continue;
                    map.TryGetValue("ManagedPackType", out var type);
                    map.TryGetValue("ManagedPackVersionID", out var versionId);
                    var source = (type ?? "").ToLowerInvariant().Contains("curse") ? "curseforge"
                        : (type ?? "").ToLowerInvariant().Contains("modrinth") ? "modrinth" : "";
                    if (string.IsNullOrWhiteSpace(source)) continue;
                    return new JsonObject
                    {
                        ["source"] = source,
                        ["projectId"] = projectId,
                        ["versionId"] = versionId ?? "",
                        ["detectedFrom"] = "instance.cfg"
                    };
                }
                catch { }
            }
            foreach (var dir in new[] { Path.Combine(AppPaths.InstancesDir, instanceId), gameDir })
            {
                try
                {
                    var mr = Path.Combine(dir, "modrinth.index.json");
                    if (File.Exists(mr) && JsonNode.Parse(File.ReadAllText(mr)) is JsonObject index)
                    {
                        return new JsonObject
                        {
                            ["source"] = "modrinth",
                            ["name"] = index["name"]?.GetValue<string>() ?? "",
                            ["versionId"] = index["versionId"]?.GetValue<string>() ?? "",
                            ["detectedFrom"] = "modrinth.index.json"
                        };
                    }
                    var cf = Path.Combine(dir, "manifest.json");
                    if (File.Exists(cf) && JsonNode.Parse(File.ReadAllText(cf)) is JsonObject manifest)
                    {
                        return new JsonObject
                        {
                            ["source"] = "curseforge",
                            ["name"] = manifest["name"]?.GetValue<string>() ?? "",
                            ["versionId"] = "",
                            ["detectedFrom"] = "manifest.json"
                        };
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
