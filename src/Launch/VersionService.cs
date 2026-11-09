using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class VersionService
    {
        private const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        private const string MirrorManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "cache", "version_manifest.json");
        private static string VersionsDir => Path.Combine(AppPaths.RootDir, "versions");

        private static async Task<string> FetchManifestText()
        {
            Exception? last = null;
            foreach (var url in new[] { OfficialManifest, MirrorManifest })
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
                    using var response = await Http.SendAsync(request);
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    return await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new Exception("无法获取 Minecraft 版本列表");
        }

        public static async Task<JsonObject> Manifest(bool force)
        {
            if (!force)
            {
                try
                {
                    if (File.Exists(CacheFile))
                    {
                        var cached = JsonNode.Parse(File.ReadAllText(CacheFile)) as JsonObject;
                        var fetchedAt = cached?["fetchedAt"]?.GetValue<long>() ?? 0;
                        if (cached?["data"] is JsonObject cachedData && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - fetchedAt < CacheTtl.TotalMilliseconds)
                            return (JsonObject)JsonNode.Parse(cachedData.ToJsonString())!;
                    }
                }
                catch { }
            }

            var text = await FetchManifestText();
            var raw = JsonNode.Parse(text) as JsonObject ?? throw new Exception("版本列表格式不正确");
            var latest = raw["latest"] as JsonObject ?? new JsonObject();
            var versions = new JsonArray();
            if (raw["versions"] is JsonArray source)
            {
                foreach (var item in source)
                {
                    if (item is not JsonObject version) continue;
                    versions.Add(new JsonObject
                    {
                        ["id"] = version["id"]?.GetValue<string>() ?? "",
                        ["type"] = version["type"]?.GetValue<string>() ?? "release",
                        ["releaseTime"] = version["releaseTime"]?.GetValue<string>() ?? "",
                        ["url"] = version["url"]?.GetValue<string>() ?? "",
                        ["sha1"] = version["sha1"]?.GetValue<string>() ?? ""
                    });
                }
            }
            var data = new JsonObject
            {
                ["latest"] = new JsonObject
                {
                    ["release"] = latest["release"]?.GetValue<string>() ?? "",
                    ["snapshot"] = latest["snapshot"]?.GetValue<string>() ?? ""
                },
                ["versions"] = versions,
                ["fetchedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                File.WriteAllText(CacheFile, new JsonObject
                {
                    ["fetchedAt"] = data["fetchedAt"]!.GetValue<long>(),
                    ["data"] = JsonNode.Parse(data.ToJsonString())
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            return data;
        }

        public static JsonArray Installed()
        {
            var result = new JsonArray();
            if (!Directory.Exists(VersionsDir)) return result;
            foreach (var directory in Directory.GetDirectories(VersionsDir))
            {
                var id = Path.GetFileName(directory);
                var file = Path.Combine(directory, id + ".json");
                if (!File.Exists(file)) continue;
                try
                {
                    var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                    if (json == null) continue;
                    var detected = DetectLoader(id, json);
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { }
                    var jar = Path.Combine(directory, id + ".jar");
                    var hasJar = File.Exists(jar);
                    if (hasJar) try { size += new FileInfo(jar).Length; } catch { }
                    result.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = json["type"]?.GetValue<string>() ?? "release",
                        ["releaseTime"] = json["releaseTime"]?.GetValue<string>() ?? "",
                        ["mcVersion"] = detected.McVersion,
                        ["loader"] = detected.Loader,
                        ["loaderVersion"] = detected.LoaderVersion,
                        ["mainClass"] = json["mainClass"]?.GetValue<string>() ?? "",
                        ["hasJar"] = hasJar,
                        ["sizeBytes"] = size
                    });
                }
                catch { }
            }
            return result;
        }

        private static (string McVersion, string Loader, string? LoaderVersion) DetectLoader(string id, JsonObject json)
        {
            var loader = "vanilla";
            string? loaderVersion = null;
            string? mcVersion = json["inheritsFrom"]?.GetValue<string>();
            if (id.Contains("OptiFine", StringComparison.OrdinalIgnoreCase)) loader = "optifine";
            else if (id.Contains("NeoForge", StringComparison.OrdinalIgnoreCase)) loader = "neoforge";
            else if (id.Contains("Forge", StringComparison.OrdinalIgnoreCase)) loader = "forge";
            else if (id.Contains("Quilt", StringComparison.OrdinalIgnoreCase)) loader = "quilt";
            else if (id.Contains("Fabric", StringComparison.OrdinalIgnoreCase)) loader = "fabric";

            if (json["libraries"] is JsonArray libraries)
            {
                foreach (var item in libraries)
                {
                    var name = item?["name"]?.GetValue<string>() ?? "";
                    var lower = name.ToLowerInvariant();
                    if (lower.StartsWith("net.neoforged:neoforge:")) { loader = "neoforge"; loaderVersion ??= name.Split(':').LastOrDefault(); }
                    else if (lower.StartsWith("net.minecraftforge:forge:")) { loader = "forge"; loaderVersion ??= name.Split(':').LastOrDefault(); }
                    else if (lower.StartsWith("net.fabricmc:fabric-loader:")) { loader = "fabric"; loaderVersion ??= name.Split(':').LastOrDefault(); }
                    else if (lower.StartsWith("org.quiltmc:quilt-loader:")) { loader = "quilt"; loaderVersion ??= name.Split(':').LastOrDefault(); }
                    else if (lower.StartsWith("optifine:optifine:")) { loader = "optifine"; loaderVersion ??= name.Split(':').LastOrDefault(); }
                }
            }
            return (mcVersion ?? id.Split('-')[0], loader, loaderVersion);
        }

        public static int JavaRequirement(string versionId)
        {
            try
            {
                var current = versionId;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current) && seen.Add(current); depth++)
                {
                    var file = Path.Combine(VersionsDir, current, current + ".json");
                    if (!File.Exists(file)) break;
                    var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                    var major = json?["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 0;
                    if (major > 0) return major;
                    current = json?["inheritsFrom"]?.GetValue<string>() ?? "";
                }
            }
            catch { }
            return FallbackJavaMajor(versionId);
        }

        private static int FallbackJavaMajor(string version)
        {
            // Loader profile ids embed the vanilla id: fabric-loader-0.16-1.21.1,
            // 1.21.1-forge-52.1.9, neoforge-21.1.80, 26.2-forge-65.1.5.
            var text = version;
            var match = System.Text.RegularExpressions.Regex.Match(text, @"(?:^|[-_])(1\.\d+(?:\.\d+)?|\d{2,4}\.\d+(?:\.\d+)?)(?:$|[-_])");
            if (match.Success) text = match.Groups[1].Value;
            var parts = text.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var major)) return 8;
            if (major != 1)
            {
                // New-style versions (e.g. 26.2) require Java 25.
                return 25;
            }
            if (!int.TryParse(parts[1], out var minor)) return 8;
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
            if (minor >= 20 && patch >= 5) return 21;
            if (minor >= 18) return 17;
            if (minor == 17) return 16;
            return 8;
        }

        public static JsonObject LoaderSize(string versionId)
        {
            var directory = Path.Combine(VersionsDir, versionId);
            long size = 0;
            long files = 0;
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; files++; } catch { }
                }
            }
            return new JsonObject { ["size"] = size, ["files"] = files };
        }

        public static void DeleteVersion(string versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId) || versionId.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                throw new Exception("版本 ID 不合法");
            var directory = Path.Combine(VersionsDir, versionId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
