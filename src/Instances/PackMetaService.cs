using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class PackMetaService
    {
        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

        private static string InstanceFile(string instanceId)
            => Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");

        public static void Save(string instanceId, string source, string projectId, string versionId, IEnumerable<string> mods)
        {
            var dir = Path.Combine(AppPaths.InstancesDir, instanceId);
            if (!Directory.Exists(dir)) return;
            var meta = new JsonObject
            {
                ["packSource"] = source,
                ["packProjectId"] = projectId,
                ["packVersionId"] = versionId,
                ["packMods"] = new JsonArray(mods.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(item => (JsonNode)JsonValue.Create(item)).ToArray()),
                ["packUpdatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            // 独立文件优先，避免安装过程中实例 json 被其它流程重写时丢失来源。
            try { File.WriteAllText(Path.Combine(dir, "pack.json"), meta.ToJsonString(Pretty)); } catch { }
            try
            {
                var file = Path.Combine(dir, "instance.json");
                if (!File.Exists(file)) return;
                var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (json == null) return;
                MergeInto(json, dir);
                File.WriteAllText(file, json.ToJsonString(Pretty));
            }
            catch { }
        }

        public static void MergeInto(JsonObject instance, string instanceDir)
        {
            try
            {
                var file = Path.Combine(instanceDir, "pack.json");
                if (!File.Exists(file)) return;
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject meta) return;
                foreach (var key in new[] { "packSource", "packProjectId", "packVersionId", "packMods", "packUpdatedAt" })
                    if (meta[key] != null) instance[key] = meta[key]!.DeepCloneCompat();
            }
            catch { }
        }

        public static JsonObject? Load(string instanceId)
        {
            try
            {
                var file = InstanceFile(instanceId);
                if (!File.Exists(file)) return null;
                var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (json == null) return null;
                MergeInto(json, Path.Combine(AppPaths.InstancesDir, instanceId));
                if (json["packSource"] == null || json["packProjectId"] == null) return null;
                return json;
            }
            catch { return null; }
        }

        /// <summary>更新时永不覆盖/删除的路径（大小写不敏感）。</summary>
        public static bool IsProtected(string relative)
        {
            var path = relative.Replace('\\', '/').TrimStart('/');
            var segments = path.Split('/');
            if (segments.Length == 0) return false;
            var top = segments[0].ToLowerInvariant();
            if (top is "saves" or "shaderpacks" or "resourcepacks" or "screenshots" or "local") return true;
            if (top.StartsWith("xaero", StringComparison.Ordinal) || top.StartsWith("journeymap", StringComparison.Ordinal)) return true;
            if (top == "options.txt" || top == "optionsof.txt" || top == "optionsshaders.txt") return true;
            if (top == "ftbchunks" || path.Contains("ftbchunks", StringComparison.OrdinalIgnoreCase)) return true;
            // 只保留小地图/地图类配置，其余 config 跟随整合包更新。
            if (top == "config" && (path.Contains("xaero", StringComparison.OrdinalIgnoreCase) || path.Contains("journeymap", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }
    }
}
