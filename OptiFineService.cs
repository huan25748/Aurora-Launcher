using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class OptiFineService
    {
        private const string Api = "https://bmclapi2.bangbang93.com/optifine/versionList";
        private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "cache", "optifine", "versionList.json");
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

        private static async Task<JsonArray> LoadList(CancellationToken token)
        {
            try
            {
                if (File.Exists(CacheFile))
                {
                    var cached = JsonNode.Parse(File.ReadAllText(CacheFile)) as JsonObject;
                    var at = cached?["at"]?.GetValue<long>() ?? 0;
                    if (cached?["raw"] is JsonArray rawCache && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at < Ttl.TotalMilliseconds)
                        return rawCache;
                }
            }
            catch { }
            var raw = JsonNode.Parse(await DownloadService.GetText(Api, token)) as JsonArray ?? throw new Exception("OptiFine 版本列表格式不正确");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                File.WriteAllText(CacheFile, new JsonObject
                {
                    ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["raw"] = JsonNode.Parse(raw.ToJsonString())
                }.ToJsonString());
            }
            catch { }
            return raw;
        }

        public static async Task<JsonArray> List(string mcVersion, CancellationToken token = default)
        {
            var raw = await LoadList(token);
            var result = new JsonArray();
            var seen = new HashSet<string>();
            foreach (var item in raw)
            {
                if (item is not JsonObject entry) continue;
                if (entry["mcversion"]?.GetValue<string>() != mcVersion) continue;
                var type = entry["type"]?.GetValue<string>() ?? "";
                var patch = entry["patch"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(patch)) continue;
                var id = $"{mcVersion}-OptiFine_{type}_{patch}";
                if (!seen.Add(id)) continue;
                var preview = System.Text.RegularExpressions.Regex.IsMatch(patch, "(^|_)pre", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(entry["filename"]?.GetValue<string>() ?? "", "^preview_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var typeLabel = type.Replace("HD_U_", "").Replace('_', ' ');
                result.Add(new JsonObject
                {
                    ["version"] = id,
                    ["stable"] = !preview,
                    ["mcVersion"] = mcVersion,
                    ["label"] = $"OptiFine {typeLabel} {patch}".Replace("  ", " ").Trim(),
                    ["forgeVersion"] = ParseForge(entry["forge"]?.GetValue<string>())
                });
            }
            var stable = result.OfType<JsonObject>().Where(item => item["stable"]?.GetValue<bool>() == true).Reverse().ToList();
            var previews = result.OfType<JsonObject>().Where(item => item["stable"]?.GetValue<bool>() != true).Reverse().ToList();
            return new JsonArray(stable.Concat(previews).Select(item => (JsonNode)JsonNode.Parse(item.ToJsonString())).ToArray());
        }

        public static async Task<(string File, string FileName)> DownloadFile(string mcVersion, string versionId, CancellationToken token = default)
        {
            var prefix = mcVersion + "-OptiFine_";
            var rest = versionId.StartsWith(prefix, StringComparison.Ordinal) ? versionId.Substring(prefix.Length) : versionId;
            var split = rest.LastIndexOf('_');
            if (split <= 0) throw new Exception("OptiFine 版本格式不正确");
            var type = rest.Substring(0, split);
            var patch = rest.Substring(split + 1);
            var preview = patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase);
            var fileName = $"{(preview ? "preview_" : "")}OptiFine_{mcVersion}_{type}_{patch}.jar";
            var suffix = type.Replace("HD_U_", "");
            var path = preview
                ? $"HD_U{(suffix.Length > 0 ? "_" + suffix : "")}/{Uri.EscapeDataString(patch)}"
                : $"HD_U/{Uri.EscapeDataString(patch)}";
            var url = $"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(mcVersion)}/{path}";
            var target = Path.Combine(AppPaths.SettingsDir, "cache", "optifine", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await DownloadService.Download(url, target, null, 0, token);
            return (target, fileName);
        }

        public static async Task<JsonObject> Install(string mcVersion, string versionId, string instanceId, CancellationToken token = default)
        {
            var (file, fileName) = await DownloadFile(mcVersion, versionId, token);
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("安装 OptiFine 需要先选择实例");
            var gameDir = instance["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            var target = Path.Combine(gameDir, "mods", fileName);
            ProgressService.Begin("安装 OptiFine");
            ProgressService.Step("下载 OptiFine", 1, fileName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                ProgressService.Update(1, "已放入 mods");
                ProgressService.Complete();
                return new JsonObject { ["file"] = target, ["fileName"] = fileName };
            }
            catch (Exception ex)
            {
                ProgressService.Fail(ex.Message);
                throw;
            }
        }

        private static string? ParseForge(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var text = value.Trim().Replace("Forge", "").Replace("#", "").Trim();
            return text.Length == 0 || text.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? null : text;
        }
    }
}
