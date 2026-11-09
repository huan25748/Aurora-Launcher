using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class SkinPatchService
    {
        public static async Task<JsonObject> Install(string? instanceId = null, CancellationToken token = default)
        {
            var id = string.IsNullOrWhiteSpace(instanceId)
                ? BridgeService.GetSettings()["lastSelectedInstanceId"]?.GetValue<string>() ?? ""
                : instanceId!;
            var instance = (string.IsNullOrWhiteSpace(id) ? BridgeService.ListInstances().OfType<JsonObject>().FirstOrDefault() : BridgeService.GetInstance(id) as JsonObject)
                ?? throw new Exception("没有可用的实例");
            var loader = instance["loader"]?.GetValue<string>() ?? "vanilla";
            if (loader == "vanilla") throw new Exception("该实例没有安装模组加载器，无法使用皮肤补丁");
            var mcVersion = instance["mcVersion"]?.GetValue<string>() ?? instance["versionId"]?.GetValue<string>() ?? "";
            if (loader == "forge" && mcVersion.StartsWith("1.18", StringComparison.Ordinal))
                throw new Exception("Forge 1.18.x 与当前 CustomSkinLoader 15.x 冲突，请手动安装 14.x 版本");
            var versionId = instance["versionId"]?.GetValue<string>() ?? "";
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", versionId);
            var mods = Path.Combine(gameDir, "mods");
            Directory.CreateDirectory(mods);
            var existing = Directory.GetFiles(mods, "*.jar")
                .FirstOrDefault(file => Path.GetFileName(file).Contains("customskinloader", StringComparison.OrdinalIgnoreCase));
            string? installed = existing == null ? null : Path.GetFileName(existing);
            if (existing == null)
            {
                var url = $"https://api.modrinth.com/v2/project/customskinloader/version?loaders=%5B%22{loader}%22%5D&game_versions=%5B%22{Uri.EscapeDataString(mcVersion)}%22%5D";
                var text = await DownloadService.GetText(url, token);
                var versions = JsonNode.Parse(text) as JsonArray ?? new JsonArray();
                foreach (var item in versions.OfType<JsonObject>())
                {
                    var files = item["files"] as JsonArray;
                    var file = files?.OfType<JsonObject>().FirstOrDefault(entry => entry["primary"]?.GetValue<bool>() == true)
                        ?? files?.OfType<JsonObject>().FirstOrDefault();
                    if (file == null) continue;
                    var fileUrl = file["url"]?.GetValue<string>();
                    var fileName = file["filename"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(fileUrl) || string.IsNullOrWhiteSpace(fileName)) continue;
                    if (fileName.Contains("Universal", StringComparison.OrdinalIgnoreCase) && loader == "forge" && mcVersion.StartsWith("1.18", StringComparison.Ordinal))
                        continue;
                    var target = Path.Combine(mods, fileName!);
                    await DownloadService.Download(fileUrl!, target, file["hashes"]?["sha1"]?.GetValue<string>(),
                        file["size"]?.GetValue<long>() ?? 0, token);
                    installed = fileName;
                    break;
                }
                if (installed == null) throw new Exception("没有找到与 " + mcVersion + " / " + loader + " 兼容的皮肤补丁");
            }
            var account = AccountService.Active() as JsonObject ?? new JsonObject();
            await TransparentCapesPatchService.Ensure(instance, versionId, account, token);
            return new JsonObject { ["ok"] = true, ["file"] = installed };
        }
    }
}