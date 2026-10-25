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
    internal static class LoaderService
    {
        private static string VersionsDir => Path.Combine(AppPaths.RootDir, "versions");
        private static string LibrariesDir => Path.Combine(AppPaths.RootDir, "libraries");

        private static int Concurrency()
        {
            try { return Math.Clamp(BridgeService.GetSettings()["maxConcurrency"]?.GetValue<int>() ?? 32, 1, 64); }
            catch { return 32; }
        }

        public static async Task<JsonArray> List(string kind, string mcVersion, CancellationToken token = default)
        {
            var official = kind == "quilt"
                ? $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}"
                : $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
            var mirror = kind == "quilt"
                ? $"https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}"
                : $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
            string text;
            try { text = await DownloadService.GetText(official, token); }
            catch { text = await DownloadService.GetText(mirror, token); }
            var array = JsonNode.Parse(text) as JsonArray ?? new JsonArray();
            var result = new JsonArray();
            var seen = new HashSet<string>();
            foreach (var item in array)
            {
                var loader = item?["loader"];
                var version = loader?["version"]?.GetValue<string>();
                if (string.IsNullOrEmpty(version) || !seen.Add(version)) continue;
                result.Add(new JsonObject
                {
                    ["version"] = version,
                    ["stable"] = loader?["stable"]?.GetValue<bool>() ?? true,
                    ["mcVersion"] = mcVersion
                });
            }
            return result;
        }

        public static async Task<JsonObject> Install(string kind, string mcVersion, string? loaderVersion, string? instanceName, bool createInstance, CancellationToken token = default)
        {
            ProgressService.Begin($"安装 {kind} {loaderVersion ?? ""} ({mcVersion})");
            var cleanupBefore = VersionCleanup.Snapshot();
            // The loader profile inherits from a vanilla version, so make sure it exists first.
            await MinecraftInstallService.InstallVanilla(mcVersion, false, null, token, true, false);
            ProgressService.Step($"获取 {(kind == "fabric" ? "Fabric" : "Quilt")} 版本列表", 1, mcVersion);
            var list = await List(kind, mcVersion, token);
            ProgressService.Update(1, list.Count + " 个版本");
            var versions = list.OfType<JsonObject>().ToList();
            if (versions.Count == 0) throw new Exception($"没有找到适用于 {mcVersion} 的 {kind} 版本");
            var picked = (!string.IsNullOrWhiteSpace(loaderVersion)
                ? versions.FirstOrDefault(item => item["version"]?.GetValue<string>() == loaderVersion)
                : versions.FirstOrDefault(item => item["stable"]?.GetValue<bool>() == true) ?? versions[0])
                ?? versions[0];
            var version = picked["version"]!.GetValue<string>();
            ProgressService.Step("获取加载器配置", 1, version);
            ProgressService.Detail("正在获取 " + version + " 配置");

            var baseUrl = kind == "quilt"
                ? $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(version)}/profile/json"
                : $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(version)}/profile/json";
            var profileText = await DownloadService.GetText(baseUrl, token);
            ProgressService.Update(1, "加载器配置已获取");
            var profile = JsonNode.Parse(profileText) as JsonObject ?? throw new Exception("加载器配置格式不正确");
            var profileId = profile["id"]?.GetValue<string>()
                ?? $"{kind}-loader-{version}-{mcVersion}";
            profile["id"] = profileId;

            var targetDir = Path.Combine(VersionsDir, profileId);
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, profileId + ".json"), profile.ToJsonString());

            var libraries = profile["libraries"] as JsonArray;
            if (libraries != null)
            {
                var downloads = new List<(string Url, string Path, long Size)>();
                foreach (var item in libraries)
                {
                    if (item is not JsonObject library) continue;
                    if (library["downloads"]?["artifact"] is JsonObject artifact)
                    {
                        var path = artifact["path"]?.GetValue<string>();
                        var url = artifact["url"]?.GetValue<string>();
                        var artifactSize = artifact["size"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var parsedSize) ? parsedSize : 0;
                        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(url))
                            downloads.Add((url!, Path.Combine(LibrariesDir, path!.Replace('/', Path.DirectorySeparatorChar)), artifactSize));
                        continue;
                    }
                    var name = library["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var mavenBase = library["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/";
                    var rel = MavenPath(name!);
                    if (rel == null) continue;
                    downloads.Add((mavenBase.TrimEnd('/') + "/" + rel, Path.Combine(LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar)), 0));
                }
                var totalBytes = downloads.Sum(item => Math.Max(0, item.Size));
                ProgressService.Step("下载加载器依赖", downloads.Count,
                    totalBytes > 0 ? $"{downloads.Count} 个文件 · {MinecraftInstallService.FormatBytes(totalBytes)}" : $"{downloads.Count} 个文件");
                var done = 0;
                long downloadedBytes = 0;
                using var gate = new SemaphoreSlim(Concurrency());
                await Task.WhenAll(downloads.Select(async item =>
                {
                    await gate.WaitAsync(token);
                    var ok = false;
                    try
                    {
                        long last = 0;
                        await DownloadService.Download(item.Url, item.Path, null, item.Size, token, (received, _) =>
                        {
                            var delta = received - last;
                            if (delta <= 0) return;
                            last = received;
                            var aggregate = System.Threading.Interlocked.Add(ref downloadedBytes, delta);
                            ProgressService.Progress(System.Threading.Volatile.Read(ref done),
                                $"{System.Threading.Volatile.Read(ref done)}/{downloads.Count} · " +
                                (totalBytes > 0
                                    ? $"{MinecraftInstallService.FormatBytes(aggregate)} / {MinecraftInstallService.FormatBytes(totalBytes)}"
                                    : $"已下载 {MinecraftInstallService.FormatBytes(aggregate)}") +
                                $" · {Path.GetFileName(item.Path)}");
                        });
                        ok = true;
                    }
                    finally
                    {
                        gate.Release();
                        if (ok)
                        {
                            var current = System.Threading.Interlocked.Increment(ref done);
                            var downloaded = System.Threading.Interlocked.Read(ref downloadedBytes);
                            ProgressService.Progress(current,
                                $"{current}/{downloads.Count} · " +
                                (totalBytes > 0
                                    ? $"{MinecraftInstallService.FormatBytes(downloaded)} / {MinecraftInstallService.FormatBytes(totalBytes)}"
                                    : $"已下载 {MinecraftInstallService.FormatBytes(downloaded)}") +
                                $" · {Path.GetFileName(item.Path)}");
                        }
                    }
                }));
            }

            string? instanceId = null;
            if (createInstance)
                instanceId = MinecraftInstallService.CreateInstance(profileId, instanceName ?? profileId, kind, version, mcVersion, cleanupBefore);
            ProgressService.Complete();
            return new JsonObject { ["instanceId"] = instanceId, ["versionId"] = profileId };
        }

        private static string? MavenPath(string name)
        {
            var parts = name.Split(':');
            if (parts.Length < 3) return null;
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 ? "-" + parts[3] : "";
            return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
        }
    }
}
