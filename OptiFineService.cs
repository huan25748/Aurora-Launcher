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
    internal static class OptiFineService
    {
        private const string Api = "https://bmclapi2.bangbang93.com/optifine/versionList";
        private const string DownloadApi = "https://bmclapi2.bangbang93.com/optifine";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "optifine.json");

        public static async Task<JsonArray> List(string mcVersion)
        {
            if (string.IsNullOrWhiteSpace(mcVersion)) return new JsonArray();
            JsonArray? raw = null;
            try
            {
                if (File.Exists(CacheFile))
                {
                    var cache = JsonNode.Parse(File.ReadAllText(CacheFile)) as JsonObject;
                    var at = cache?["at"]?.GetValue<long>() ?? 0;
                    if (cache?["data"] is JsonArray cached && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at < CacheTtl.TotalMilliseconds)
                        raw = (JsonArray)JsonNode.Parse(cached.ToJsonString())!;
                }
            }
            catch { }
            if (raw == null)
            {
                try
                {
                    raw = JsonNode.Parse(await DownloadService.GetText(Api)) as JsonArray;
                    if (raw != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                        File.WriteAllText(CacheFile, new JsonObject
                        {
                            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            ["data"] = (JsonArray)JsonNode.Parse(raw.ToJsonString())!
                        }.ToJsonString());
                    }
                }
                catch { raw = new JsonArray(); }
            }

            var result = new JsonArray();
            foreach (var item in raw?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var mc = item["mcversion"]?.GetValue<string>() ?? item["mcVersion"]?.GetValue<string>() ?? "";
                if (!string.Equals(mc, mcVersion, StringComparison.OrdinalIgnoreCase)) continue;
                var type = item["type"]?.GetValue<string>() ?? "HD_U";
                var patch = item["patch"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(patch)) continue;
                var forge = item["forge"]?.GetValue<string>() ?? "";
                result.Add(new JsonObject
                {
                    ["version"] = $"{mcVersion}-OptiFine_{type}_{patch}",
                    ["label"] = string.IsNullOrWhiteSpace(forge) ? patch : $"{patch} · 需要 Forge {forge}",
                    ["type"] = type,
                    ["patch"] = patch,
                    ["forgeVersion"] = forge,
                    ["fileName"] = $"OptiFine_{mcVersion}_{type}_{patch}.jar"
                });
            }
            return result;
        }

        public static async Task<(string File, string FileName)> DownloadFile(string mcVersion, string versionId, CancellationToken token = default, Action<long, long>? onProgress = null)
        {
            var prefix = mcVersion + "-OptiFine_";
            var type = versionId.StartsWith(prefix, StringComparison.Ordinal) ? versionId[prefix.Length..] : versionId;
            if (type.StartsWith("OptiFine_", StringComparison.Ordinal))
            {
                var marker = type.IndexOf(mcVersion + "_", StringComparison.Ordinal);
                if (marker >= 0) type = type[(marker + mcVersion.Length + 1)..];
            }
            if (!type.Contains('_'))
            {
                // Old callers only knew the HD_U channel; resolve the newest
                // actual patch instead of requesting /HD_U/HD_U.
                var versions = await List(mcVersion);
                var candidate = versions.OfType<JsonObject>().FirstOrDefault();
                var full = candidate?["version"]?.GetValue<string>() ?? "";
                if (full.StartsWith(prefix, StringComparison.Ordinal)) type = full[prefix.Length..];
            }
            if (string.IsNullOrWhiteSpace(type) || type.Contains('/') || type.Contains('\\'))
                throw new Exception("OptiFine 版本格式不正确");
            var fileName = $"OptiFine_{mcVersion}_{type}.jar";
            var target = Path.Combine(AppPaths.SettingsDir, "cache", "optifine", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var patch = type.StartsWith("HD_U_", StringComparison.OrdinalIgnoreCase) ? type[5..] : type;
            var url = patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase)
                ? $"{DownloadApi}/{Uri.EscapeDataString(mcVersion)}/HD_U_{Uri.EscapeDataString(patch)}"
                : $"{DownloadApi}/{Uri.EscapeDataString(mcVersion)}/HD_U/{Uri.EscapeDataString(patch)}";
            await DownloadService.Download(url, target, null, 0, token, onProgress);
            return (target, fileName);
        }

        public static async Task<JsonObject> Install(string mcVersion, string versionId, string instanceId, CancellationToken token = default)
        {
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("安装 OptiFine 需要先选择实例");
            var gameDir = instance["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            ProgressService.Begin("安装 OptiFine");
            var step = ProgressService.Step("下载 OptiFine", 1, versionId);
            try
            {
                var (file, fileName) = await DownloadFile(mcVersion, versionId, token, (received, total) =>
                {
                    if (total > 0) ProgressService.TotalAt(step, total);
                    ProgressService.ProgressAt(step, received,
                        $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
                });
                ProgressService.TotalAt(step, 1);
                var target = Path.Combine(gameDir, "mods", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                ProgressService.UpdateAt(step, 1, "已放入 mods");
                ProgressService.Complete();
                return new JsonObject { ["file"] = target, ["fileName"] = fileName };
            }
            catch (Exception ex)
            {
                ProgressService.Fail(ex.Message);
                throw;
            }
        }
    }
}
