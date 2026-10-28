using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class ModrinthService
    {
        private static readonly HttpClient Http = CreateHttp();
        private static readonly ConcurrentDictionary<string, (DateTime At, JsonNode Value)> Cache = new();

        private static HttpClient CreateHttp()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 64,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
        }

        private static string BaseUrl()
        {
            var settings = BridgeService.GetSettings();
            var source = settings["modrinthSource"]?.GetValue<string>() ?? "mirror";
            return source == "mirror"
                ? "https://mod.mcimirror.top/modrinth/v2"
                : "https://api.modrinth.com/v2";
        }

        private static async Task<JsonNode> Get(string path)
        {
            var url = BaseUrl().TrimEnd('/') + path;
            if (Cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2))
                return JsonNode.Parse(cached.Value.ToJsonString())!;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception($"Modrinth HTTP {(int)response.StatusCode}");
            var node = JsonNode.Parse(text) ?? throw new Exception("Modrinth 返回空数据");
            if (Cache.Count > 128) Cache.Clear();
            Cache[url] = (DateTime.UtcNow, JsonNode.Parse(node.ToJsonString())!);
            return node;
        }

        public static async Task<JsonObject> Search(JsonElement input)
        {
            var query = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var limit = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("limit", out var l) ? l.GetInt32() : 20;
            var offset = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            var index = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("index", out var i) ? i.GetString() ?? "relevance" : "relevance";
            var facets = new List<string>();
            if (input.ValueKind == JsonValueKind.Object)
            {
                if (input.TryGetProperty("projectType", out var pt) && !string.IsNullOrWhiteSpace(pt.GetString()))
                    facets.Add($"[[\"project_type:{pt.GetString()}\"]]");
                if (input.TryGetProperty("mcVersion", out var mc) && !string.IsNullOrWhiteSpace(mc.GetString()))
                    facets.Add($"[[\"versions:{mc.GetString()}\"]]");
                if (input.TryGetProperty("loader", out var loader) && !string.IsNullOrWhiteSpace(loader.GetString()))
                    facets.Add($"[[\"categories:{loader.GetString()}\"]]");
                if (input.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                {
                    var values = cats.EnumerateArray().Select(c => $"\"categories:{c.GetString()}\"").ToList();
                    if (values.Count > 0) facets.Add("[" + string.Join(",", values) + "]");
                }
            }
            var parameters = new List<string> { "limit=" + limit, "offset=" + offset, "index=" + Uri.EscapeDataString(index) };
            if (!string.IsNullOrWhiteSpace(query)) parameters.Add("query=" + Uri.EscapeDataString(query));
            if (facets.Count > 0)
            {
                var merged = facets.SelectMany(f => System.Text.Json.JsonSerializer.Deserialize<List<List<string>>>(f) ?? new List<List<string>>()).ToList();
                parameters.Add("facets=" + Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(merged)));
            }
            var raw = await Get("/search?" + string.Join("&", parameters)) as JsonObject ?? new JsonObject();
            if (raw["hits"] is JsonArray hits)
                foreach (var item in hits)
                    if (item is JsonObject hit && hit["slug"]?.GetValue<string>() is string slug)
                        hit["nameTranslation"] = ModNamesService.Chinese("modrinth", slug);
            raw["offset"] = offset;
            raw["limit"] = limit;
            return raw;
        }

        public static async Task<JsonObject> Project(string id)
        {
            var raw = await Get("/project/" + Uri.EscapeDataString(id)) as JsonObject ?? throw new Exception("项目不存在");
            if (raw["project_id"] == null) raw["project_id"] = raw["id"]?.GetValue<string>() ?? id;
            if (raw["follows"] == null && raw["followers"] != null) raw["follows"] = raw["followers"]!.DeepCloneCompat();
            raw["descriptionTranslation"] = await TranslationService.Translate(raw["description"]?.GetValue<string>());
            return raw;
        }

        public static async Task<JsonArray> Versions(string projectId, string? mcVersion, string? loader)
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(mcVersion)) query.Add("game_versions=" + Uri.EscapeDataString($"[\"{mcVersion}\"]"));
            if (!string.IsNullOrWhiteSpace(loader)) query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));
            var path = "/project/" + Uri.EscapeDataString(projectId) + "/version" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            return await Get(path) as JsonArray ?? new JsonArray();
        }

        public static async Task<JsonObject> Install(JsonElement input)
        {
            ProgressService.Begin("安装 Modrinth 内容");
            var projectId = GetString(input, "projectId");
            var instanceId = GetString(input, "instanceId");
            var versionId = GetString(input, "versionId");
            var targetKind = GetString(input, "targetKind");
            ProgressService.Step("获取项目信息", 1, projectId);
            var project = await Project(projectId);
            ProgressService.Update(1, "项目信息已获取");
            var kind = string.IsNullOrWhiteSpace(targetKind) ? project["project_type"]?.GetValue<string>() ?? "mod" : targetKind;
            var folder = kind switch
            {
                "resourcepack" => "resourcepacks",
                "shader" => "shaderpacks",
                "datapack" => "datapacks",
                _ => "mods"
            };
            ProgressService.Step("获取模组版本", 1, versionId);
            var version = await PickVersion(projectId, versionId);
            var file = PrimaryFile(version) ?? throw new Exception("该版本没有可下载文件");
            ProgressService.Update(1, "版本已选择");
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            var dir = Path.Combine(gameDir, folder);
            Directory.CreateDirectory(dir);
            var fileName = file["filename"]?.GetValue<string>() ?? "download.jar";
            var target = Path.Combine(dir, fileName);
            var downloadStep = ProgressService.Step("下载模组文件", 1, fileName);
            await Download(file, target, (received, total) =>
            {
                if (total > 0) ProgressService.TotalAt(downloadStep, total);
                ProgressService.ProgressAt(downloadStep, received,
                    $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
            });
            ProgressService.TotalAt(downloadStep, 1);
            ProgressService.UpdateAt(downloadStep, 1, "下载完成");
            ProgressService.Update(1, fileName);
            ProgressService.Complete();
            return new JsonObject { ["path"] = target, ["filename"] = fileName };
        }

        public static async Task<JsonObject> InstallToFolder(JsonElement input)
        {
            ProgressService.Begin("下载 Modrinth 文件");
            var projectId = GetString(input, "projectId");
            var versionId = GetString(input, "versionId");
            var destDir = GetString(input, "destDir");
            if (string.IsNullOrWhiteSpace(destDir)) throw new Exception("目标文件夹为空");
            ProgressService.Step("获取文件信息", 1, projectId);
            var version = await PickVersion(projectId, versionId);
            var file = PrimaryFile(version) ?? throw new Exception("该版本没有可下载文件");
            Directory.CreateDirectory(destDir);
            var fileName = file["filename"]?.GetValue<string>() ?? "download.jar";
            var target = Path.Combine(destDir, fileName);
            var downloadStep = ProgressService.Step("下载文件", 1, fileName);
            await Download(file, target, (received, total) =>
            {
                if (total > 0) ProgressService.TotalAt(downloadStep, total);
                ProgressService.ProgressAt(downloadStep, received,
                    $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
            });
            ProgressService.TotalAt(downloadStep, 1);
            ProgressService.UpdateAt(downloadStep, 1, "下载完成");
            ProgressService.Update(1, fileName);
            ProgressService.Complete();
            return new JsonObject { ["installed"] = new JsonArray(fileName) };
        }

        public static async Task<JsonObject> InstallPack(string projectId, string? instanceName, string? versionId)
        {
            var packTitle = "安装整合包";
            try
            {
                var project = await Project(projectId);
                if (project["title"]?.GetValue<string>() is string projectName && projectName.Length > 0)
                    packTitle += " · " + projectName;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(instanceName) && packTitle.StartsWith("安装整合包 · ", StringComparison.Ordinal))
                instanceName = packTitle["安装整合包 · ".Length..];
            ProgressService.BeginPack(packTitle);
            var versions = await Versions(projectId, null, null);
            JsonObject? version = null;
            foreach (var item in versions)
                if (item is JsonObject candidate && (string.IsNullOrWhiteSpace(versionId) || candidate["id"]?.GetValue<string>() == versionId)) { version = candidate; break; }
            version ??= versions.OfType<JsonObject>().FirstOrDefault() ?? throw new Exception("整合包没有可用版本");
            var file = PrimaryFile(version) ?? throw new Exception("整合包版本没有文件");
            var packStep = ProgressService.Step("下载整合包", 1, file["filename"]?.GetValue<string>() ?? "");
            if ((file["size"]?.GetValue<long>() ?? 0) > 0) ProgressService.TotalAt(packStep, file["size"]!.GetValue<long>());
            var zipPath = Path.Combine(AppPaths.SettingsDir, "cache", "packs", file["filename"]?.GetValue<string>() ?? projectId + ".mrpack");
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            await Download(file, zipPath, (received, total) =>
            {
                if (total > 0) ProgressService.TotalAt(packStep, total);
                ProgressService.ProgressAt(packStep, received,
                    $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
            });
            ProgressService.TotalAt(packStep, 1);
            ProgressService.UpdateAt(packStep, 1, "整合包已下载");
            var temp = Path.Combine(AppPaths.SettingsDir, "cache", "packs", "mr-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temp);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, temp, true);
            var indexPath = Path.Combine(temp, "modrinth.index.json");
            if (!File.Exists(indexPath)) throw new Exception("整合包缺少 modrinth.index.json");
            var index = JsonNode.Parse(File.ReadAllText(indexPath)) as JsonObject ?? throw new Exception("整合包索引格式错误");
            var mcVersion = index["dependencies"]?["minecraft"]?.GetValue<string>() ?? "";
            var fabricLoader = index["dependencies"]?["fabric-loader"]?.GetValue<string>();
            var quiltLoader = index["dependencies"]?["quilt-loader"]?.GetValue<string>();
            var forge = index["dependencies"]?["forge"]?.GetValue<string>();
            var neoforge = index["dependencies"]?["neoforge"]?.GetValue<string>();
            var baseStep = ProgressService.Step("安装游戏基础", 1, $"{mcVersion} {(fabricLoader != null ? "fabric" : forge != null ? "forge" : neoforge != null ? "neoforge" : quiltLoader != null ? "quilt" : "vanilla")}");
            string instanceId;
            if (!string.IsNullOrWhiteSpace(fabricLoader) || !string.IsNullOrWhiteSpace(quiltLoader))
            {
                var kind = string.IsNullOrWhiteSpace(fabricLoader) ? "quilt" : "fabric";
                var loaderVersion = kind == "fabric" ? fabricLoader : quiltLoader;
                var result = await LoaderService.Install(kind, mcVersion, loaderVersion, instanceName ?? projectId, true);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            else if (!string.IsNullOrWhiteSpace(forge) || !string.IsNullOrWhiteSpace(neoforge))
            {
                var kind = string.IsNullOrWhiteSpace(forge) ? "neoforge" : "forge";
                var loaderVersion = kind == "forge" ? forge : neoforge;
                var result = await ForgeService.Install(kind, mcVersion, loaderVersion, instanceName ?? projectId, true);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            else
            {
                var result = await MinecraftInstallService.InstallVanilla(mcVersion, true, instanceName ?? projectId);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            ProgressService.UpdateAt(baseStep, 1, "安装完成");
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            if (index["files"] is JsonArray files)
            {
                var downloadStep = ProgressService.Step("下载整合包文件", files.Count, files.Count + " 个文件");
                var doneCount = 0;
                using var gate = new System.Threading.SemaphoreSlim(8);
                await Task.WhenAll(files.OfType<JsonObject>().Select(async entry =>
                {
                    var path = entry["path"]?.GetValue<string>() ?? "";
                    var downloads = (entry["downloads"] as JsonArray)?.OfType<JsonNode>()
                        .Select(item => item.GetValue<string>())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToList() ?? new List<string>();
                    if (string.IsNullOrWhiteSpace(path) || downloads.Count == 0) return;
                    try
                    {
                        await gate.WaitAsync();
                        var target = Path.GetFullPath(Path.Combine(gameDir, path));
                        if (!target.StartsWith(Path.GetFullPath(gameDir), StringComparison.OrdinalIgnoreCase)) return;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        Exception? last = null;
                        foreach (var url in downloads)
                        {
                            try { await DownloadService.Download(url, target); last = null; break; }
                            catch (Exception ex) { last = ex; }
                        }
                        if (last != null) throw last;
                    }
                    catch { }
                    finally
                    {
                        gate.Release();
                        var done = System.Threading.Interlocked.Increment(ref doneCount);
                        ProgressService.ProgressAt(downloadStep, done, $"{done}/{(index["files"] as JsonArray)?.Count ?? 0}");
                    }
                }));
                ProgressService.UpdateAt(downloadStep, (index["files"] as JsonArray)?.Count ?? 0, "整合包文件已下载");
            }
            foreach (var folder in new[] { "overrides", "client-overrides" })
            {
                var overrides = Path.Combine(temp, folder);
                if (!Directory.Exists(overrides)) continue;
                foreach (var source in Directory.EnumerateFiles(overrides, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(overrides, source);
                    var target = Path.Combine(gameDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, true);
                }
            }
            try { Directory.Delete(temp, true); } catch { }
            try { File.Delete(zipPath); } catch { }
            ProgressService.CompletePack();
            return new JsonObject { ["instanceId"] = instanceId };
        }

        public static async Task<JsonObject> Featured(string type)
        {
            var projectType = type == "modpack" ? "modpack" : type == "resourcepack" ? "resourcepack" : "mod";
            return await Search(System.Text.Json.JsonSerializer.SerializeToElement(new { projectType, limit = 12, index = "downloads" }));
        }

        private static async Task<JsonObject> PickVersion(string projectId, string versionId)
        {
            var versions = await Versions(projectId, null, null);
            if (versions.Count == 0) throw new Exception("项目没有可用版本");
            if (!string.IsNullOrWhiteSpace(versionId))
                foreach (var item in versions)
                    if (item is JsonObject version && version["id"]?.GetValue<string>() == versionId) return version;
            return versions.OfType<JsonObject>().First();
        }

        private static JsonObject? PrimaryFile(JsonObject version)
        {
            if (version["files"] is not JsonArray files) return null;
            return files.OfType<JsonObject>().FirstOrDefault(file => file["primary"]?.GetValue<bool>() == true)
                ?? files.OfType<JsonObject>().FirstOrDefault();
        }

        private static async Task Download(JsonObject file, string target, Action<long, long>? progress = null)
        {
            var url = file["url"]?.GetValue<string>() ?? throw new Exception("下载地址为空");
            var sha1 = file["hashes"]?["sha1"]?.GetValue<string>();
            var size = file["size"]?.GetValue<long>() ?? 0;
            await DownloadService.Download(url, target, sha1, size, default, progress);
        }

        private static string GetString(JsonElement input, string name)
            => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    }
}
