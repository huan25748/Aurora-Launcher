using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class FabricApiService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        public static async Task<JsonArray> Versions(string mcVersion, CancellationToken token = default)
        {
            var url = "https://api.modrinth.com/v2/project/fabric-api/version?game_versions="
                + Uri.EscapeDataString("[\"" + mcVersion + "\"]")
                + "&loaders=" + Uri.EscapeDataString("[\"fabric\"]");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.2.26");
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var array = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonArray ?? new JsonArray();
            var result = new JsonArray();
            foreach (var item in array)
            {
                if (item is not JsonObject version) continue;
                var files = version["files"] as JsonArray;
                var file = files?.OfType<JsonObject>().FirstOrDefault(entry => entry["primary"]?.GetValue<bool>() == true)
                    ?? files?.OfType<JsonObject>().FirstOrDefault();
                if (file == null) continue;
                result.Add(new JsonObject
                {
                    ["version"] = version["version_number"]?.GetValue<string>() ?? version["id"]?.GetValue<string>() ?? "",
                    ["name"] = version["name"]?.GetValue<string>() ?? version["version_number"]?.GetValue<string>() ?? "",
                    ["downloadUrl"] = file["url"]?.GetValue<string>() ?? "",
                    ["fileName"] = file["filename"]?.GetValue<string>() ?? "",
                    ["size"] = file["size"]?.GetValue<long>() ?? 0
                });
            }
            return result;
        }

        public static async Task<string> Install(string mcVersion, string? version, string instanceId, CancellationToken token = default)
        {
            var versions = await Versions(mcVersion, token);
            var picked = (!string.IsNullOrWhiteSpace(version)
                ? versions.OfType<JsonObject>().FirstOrDefault(item => item["version"]?.GetValue<string>() == version)
                : null) ?? versions.OfType<JsonObject>().FirstOrDefault();
            if (picked == null) throw new Exception($"没有找到适用于 {mcVersion} 的 Fabric API");
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(gameDir)) throw new Exception("实例目录不存在");
            var name = picked["fileName"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name)) name = "fabric-api-" + (picked["version"]?.GetValue<string>() ?? "latest") + ".jar";
            var target = Path.Combine(gameDir, "mods", name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var step = ProgressService.Step("下载 Fabric API", 1, name);
            await DownloadService.Download(picked["downloadUrl"]!.GetValue<string>(), target, null,
                picked["size"]?.GetValue<long>() ?? 0, token, (done, total) =>
                    ProgressService.ProgressAt(step, total > 0 ? (double)done / total : 0, name));
            return target;
        }
    }
}
