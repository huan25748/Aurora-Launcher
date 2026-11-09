using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class FabricApiService
    {
        // CurseForge project id of Fabric API.
        private const int ProjectId = 306612;

        public static async Task<JsonArray> Versions(string mcVersion, CancellationToken token = default)
        {
            var result = new JsonArray();
            var modrinthByFile = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var array = await ModrinthService.Versions("fabric-api", mcVersion, "fabric");
                foreach (var item in array)
                {
                    if (item is not JsonObject version) continue;
                    if (!MatchesMc(version, mcVersion)) continue;
                    var files = version["files"] as JsonArray;
                    var file = files?.OfType<JsonObject>().FirstOrDefault(entry => entry["primary"]?.GetValue<bool>() == true)
                        ?? files?.OfType<JsonObject>().FirstOrDefault();
                    if (file == null) continue;
                    var fileName = file["filename"]?.GetValue<string>() ?? "";
                    if (fileName.Length == 0) continue;
                    var entry = new JsonObject
                    {
                        ["id"] = version["id"]?.GetValue<string>() ?? "",
                        ["modrinthVersionId"] = version["id"]?.GetValue<string>() ?? "",
                        ["version"] = version["version_number"]?.GetValue<string>() ?? "",
                        ["name"] = version["name"]?.GetValue<string>() ?? version["version_number"]?.GetValue<string>() ?? "",
                        ["downloadUrl"] = file["url"]?.GetValue<string>() ?? "",
                        ["fileName"] = fileName,
                        ["size"] = file["size"]?.GetValue<long>() ?? 0
                    };
                    result.Add(entry);
                    modrinthByFile[fileName] = entry;
                }
            }
            catch { }
            try
            {
                var files = await CurseForgeService.Files(ProjectId, mcVersion, "fabric");
                foreach (var item in files)
                {
                    if (item is not JsonObject file) continue;
                    if (!MatchesMc(file, mcVersion)) continue;
                    var id = file["id"]?.GetValue<int>() ?? 0;
                    if (id <= 0) continue;
                    var fileName = file["fileName"]?.GetValue<string>() ?? "";
                    if (modrinthByFile.TryGetValue(fileName, out var known))
                    {
                        known["cfFileId"] = id.ToString();
                        continue;
                    }
                    result.Add(new JsonObject
                    {
                        ["id"] = id.ToString(),
                        ["cfFileId"] = id.ToString(),
                        ["version"] = file["displayName"]?.GetValue<string>() ?? fileName,
                        ["name"] = file["displayName"]?.GetValue<string>() ?? fileName,
                        ["downloadUrl"] = file["downloadUrl"]?.GetValue<string>() ?? "",
                        ["fileName"] = fileName,
                        ["size"] = file["fileLength"]?.GetValue<long>() ?? 0
                    });
                }
            }
            catch { }
            return result;
        }

        private static bool MatchesMc(JsonObject file, string mcVersion)
        {
            if (string.IsNullOrWhiteSpace(mcVersion)) return false;
            if (file["gameVersions"] is JsonArray cfVersions)
                return cfVersions.Any(v => string.Equals(v?.GetValue<string>(), mcVersion, StringComparison.OrdinalIgnoreCase));
            if (file["game_versions"] is JsonArray versions)
                return versions.Any(v => string.Equals(v?.GetValue<string>(), mcVersion, StringComparison.OrdinalIgnoreCase));
            return false;
        }

        private static JsonArray ExactOnly(JsonArray source, string mcVersion)
        {
            var exact = new JsonArray();
            foreach (var item in source)
                if (item is JsonObject file && MatchesMc(file, mcVersion))
                    exact.Add(JsonNode.Parse(file.ToJsonString()));
            return exact;
        }

        public static async Task<string> Install(string mcVersion, string? version, string instanceId, CancellationToken token = default)
        {
            var versions = await Versions(mcVersion, token);
            var picked = (!string.IsNullOrWhiteSpace(version)
                ? versions.OfType<JsonObject>().FirstOrDefault(item =>
                    item["version"]?.GetValue<string>() == version ||
                    item["fileName"]?.GetValue<string>() == version)
                : null) ?? versions.OfType<JsonObject>().FirstOrDefault();
            if (picked == null) throw new Exception($"没有找到适用于 {mcVersion} 的 Fabric API");

            // 优先 CurseForge 下载；失败再走 Modrinth 下载。
            var cfFileId = picked["cfFileId"]?.GetValue<string>() ?? "";
            if (int.TryParse(cfFileId, out var fileId) && fileId > 0)
            {
                try
                {
                    var request = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["instanceId"] = instanceId,
                        ["modId"] = ProjectId,
                        ["fileId"] = fileId,
                        ["projectType"] = "mod"
                    });
                    var installed = await CurseForgeService.Install(request);
                    return installed["path"]?.GetValue<string>() ?? "";
                }
                catch { }
            }

            var modrinthVersionId = picked["modrinthVersionId"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(modrinthVersionId))
            {
                var modrinthResult = await ModrinthService.InstallVersionFile("fabric-api", modrinthVersionId, instanceId, "mod", "下载 Fabric API");
                return modrinthResult["path"]?.GetValue<string>() ?? "";
            }

            var url = picked["downloadUrl"]?.GetValue<string>() ?? "";
            var fileName = picked["fileName"]?.GetValue<string>() ?? "fabric-api.jar";
            if (string.IsNullOrWhiteSpace(url)) throw new Exception("Fabric API 没有可用的下载地址");
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            var mods = Path.Combine(gameDir, "mods");
            Directory.CreateDirectory(mods);
            var target = Path.Combine(mods, fileName);
            await DownloadService.Download(url, target, null, picked["size"]?.GetValue<long>() ?? 0, token);
            return target;
        }
    }
}
