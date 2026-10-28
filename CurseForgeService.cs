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
    internal static class CurseForgeService
    {
        private const string Api = "https://mod.mcimirror.top/curseforge/v1";
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

        private static async Task<JsonNode> Get(string path)
        {
            var url = Api + path;
            if (Cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2))
                return JsonNode.Parse(cached.Value.ToJsonString())!;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception($"CurseForge HTTP {(int)response.StatusCode}");
            var node = JsonNode.Parse(text) ?? new JsonObject();
            if (Cache.Count > 128) Cache.Clear();
            Cache[url] = (DateTime.UtcNow, JsonNode.Parse(node.ToJsonString())!);
            return node;
        }

        public static async Task<JsonObject> Search(JsonElement input)
        {
            var projectType = GetString(input, "projectType") ?? "mod";
            var classId = projectType switch { "modpack" => 4471, "resourcepack" => 12, "shader" => 6552, _ => 6 };
            var offset = GetInt(input, "offset", 0);
            var limit = Math.Clamp(GetInt(input, "limit", 20), 1, 50);
            var sort = GetString(input, "sort") ?? "downloads";
            var sortField = sort switch { "newest" => 10, "updated" => 3, "relevance" => 2, _ => 6 };
            var query = new List<string> { "gameId=432", "classId=" + classId, "index=" + offset, "pageSize=" + limit, "sortOrder=desc", "sortField=" + sortField };
            var search = GetString(input, "query");
            if (!string.IsNullOrWhiteSpace(search)) query.Add("searchFilter=" + Uri.EscapeDataString(search!));
            var mcVersion = GetString(input, "mcVersion");
            if (!string.IsNullOrWhiteSpace(mcVersion)) query.Add("gameVersion=" + Uri.EscapeDataString(mcVersion!));
            var loader = GetString(input, "loader");
            var loaderId = loader switch { "forge" => 1, "fabric" => 4, "quilt" => 5, "neoforge" => 6, _ => 0 };
            if (loaderId > 0) query.Add("modLoaderType=" + loaderId);
            var raw = await Get("/mods/search?" + string.Join("&", query)) as JsonObject ?? new JsonObject();
            var hits = new JsonArray();
            if (raw["data"] is JsonArray data)
            {
                foreach (var entry in data)
                {
                    if (entry is JsonObject mod) { var hit = MapHit(mod); hit["nameTranslation"] = ModNamesService.Chinese("curseforge", hit["slug"]?.GetValue<string>()); hits.Add(hit); }
                }
            }
            return new JsonObject
            {
                ["hits"] = hits,
                ["total"] = raw["pagination"]?["totalCount"]?.GetValue<int>() ?? 0,
                ["offset"] = offset,
                ["limit"] = limit
            };
        }

        public static async Task<JsonArray> Categories(string type)
        {
            var classId = type switch { "modpack" => 4471, "resourcepack" => 12, "shader" => 6552, _ => 6 };
            var raw = await Get($"/categories?gameId=432&classId={classId}") as JsonObject;
            var result = new JsonArray();
            if (raw?["data"] is JsonArray data)
                foreach (var entry in data)
                    if (entry is JsonObject item)
                        result.Add(new JsonObject { ["id"] = item["id"]?.GetValue<int>() ?? 0, ["name"] = item["name"]?.GetValue<string>() ?? "" });
            return result;
        }

        public static async Task<JsonObject> Project(int id)
        {
            var raw = await Get($"/mods/{id}") as JsonObject;
            if (raw?["data"] is not JsonObject data) throw new Exception("CurseForge 项目不存在");
            var hit = MapHit(data);
            hit["nameTranslation"] = ModNamesService.Chinese("curseforge", hit["slug"]?.GetValue<string>());
            hit["description"] = data["description"]?.GetValue<string>() ?? data["summary"]?.GetValue<string>() ?? "";
            hit["descriptionTranslation"] = await TranslationService.Translate(data["summary"]?.GetValue<string>() ?? data["description"]?.GetValue<string>());
            hit["screenshots"] = data["screenshots"] is JsonArray screenshots
                ? new JsonArray(screenshots.OfType<JsonObject>().Select(item => (JsonNode)(item["thumbnailUrl"]?.GetValue<string>() ?? item["url"]?.GetValue<string>() ?? "")).ToArray())
                : new JsonArray();
            hit["latestFiles"] = data["latestFiles"] is JsonArray files
                ? new JsonArray(files.OfType<JsonObject>().Select(MapFile).ToArray())
                : new JsonArray();
            return hit;
        }

        public static async Task<JsonArray> Files(int id, string? mcVersion, string? loader)
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(mcVersion)) query.Add("gameVersion=" + Uri.EscapeDataString(mcVersion!));
            var loaderId = loader switch { "forge" => 1, "fabric" => 4, "quilt" => 5, "neoforge" => 6, _ => 0 };
            if (loaderId > 0) query.Add("modLoaderType=" + loaderId);
            var path = $"/mods/{id}/files" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            var raw = await Get(path) as JsonObject;
            var result = new JsonArray();
            if (raw?["data"] is JsonArray data)
                foreach (var entry in data)
                    if (entry is JsonObject file) result.Add(MapFile(file));
            return result;
        }

        public static async Task<JsonObject> Install(JsonElement input)
        {
            ProgressService.Begin("安装 CurseForge 内容");
            var instanceId = GetString(input, "instanceId") ?? "";
            ProgressService.Step("获取文件信息", 1, "" + GetInt(input, "modId", 0));
            var file = await ResolveFile(GetInt(input, "modId", 0), GetInt(input, "fileId", 0));
            ProgressService.Update(1, "文件信息已获取");
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            ValidateFileForInstance(file, instance);
            var projectType = GetString(input, "projectType");
            if (string.IsNullOrWhiteSpace(projectType))
            {
                try { projectType = (await Project(GetInt(input, "modId", 0)))["projectType"]?.GetValue<string>(); }
                catch { }
            }
            var folder = projectType switch
            {
                "resourcepack" => "resourcepacks",
                "shader" => "shaderpacks",
                "datapack" => "datapacks",
                _ => "mods"
            };
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            var target = Path.Combine(gameDir, folder, file["fileName"]?.GetValue<string>() ?? "mod.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var downloadStep = ProgressService.Step("下载文件", 1, file["fileName"]?.GetValue<string>() ?? "");
            await DownloadFile(file, target, (received, total) =>
            {
                if (total > 0) ProgressService.TotalAt(downloadStep, total);
                ProgressService.ProgressAt(downloadStep, received,
                    $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
            });
            ProgressService.TotalAt(downloadStep, 1);
            ProgressService.UpdateAt(downloadStep, 1, "下载完成");
            ProgressService.Complete();
            return new JsonObject
            {
                ["path"] = target,
                ["installed"] = new JsonArray(file["fileName"]?.GetValue<string>() ?? "mod.jar"),
                ["skipped"] = new JsonArray()
            };
        }

        public static async Task<JsonObject> InstallToFolder(JsonElement input)
        {
            ProgressService.Begin("下载 CurseForge 文件");
            var dest = GetString(input, "destDir") ?? throw new Exception("目标文件夹为空");
            var infoStep = ProgressService.Step("获取文件信息", 1, "正在获取");
            var file = await ResolveFile(GetInt(input, "modId", 0), GetInt(input, "fileId", 0));
            ProgressService.UpdateAt(infoStep, 1, "已获取");
            Directory.CreateDirectory(dest);
            var fileName = file["fileName"]?.GetValue<string>() ?? "mod.jar";
            var target = Path.Combine(dest, fileName);
            var downloadStep = ProgressService.Step("下载文件", 1, fileName);
            await DownloadFile(file, target, (received, total) =>
            {
                if (total > 0) ProgressService.TotalAt(downloadStep, total);
                ProgressService.ProgressAt(downloadStep, received,
                    $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
            });
            ProgressService.TotalAt(downloadStep, 1);
            ProgressService.UpdateAt(downloadStep, 1, "下载完成");
            ProgressService.Complete();
            return new JsonObject { ["installed"] = new JsonArray(fileName) };
        }

        public static async Task<JsonObject> InstallPack(JsonElement input)
        {
            var modId = GetInt(input, "modId", 0);
            var fileId = GetInt(input, "fileId", 0);
            var instanceName = GetString(input, "instanceName");
            var packName = "";
            var packTitle = "安装整合包";
            try
            {
                var project = await Project(modId);
                if (project["name"]?.GetValue<string>() is string projectName && projectName.Length > 0)
                {
                    packName = projectName;
                    packTitle += " · " + projectName;
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(instanceName))
                instanceName = string.IsNullOrWhiteSpace(packName) ? "CurseForge 整合包" : packName;
            ProgressService.BeginPack(packTitle);
            var infoStep = ProgressService.Step("获取整合包文件", 1, "正在获取");
            var file = await ResolveFile(modId, fileId);
            ProgressService.UpdateAt(infoStep, 1, "已获取");
            var zipPath = Path.Combine(AppPaths.SettingsDir, "cache", "packs", file["fileName"]?.GetValue<string>() ?? $"pack-{modId}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            var packStep = ProgressService.Step("下载整合包", 1, file["fileName"]?.GetValue<string>() ?? "");
            if ((file["fileLength"]?.GetValue<long>() ?? 0) > 0) ProgressService.TotalAt(packStep, file["fileLength"]!.GetValue<long>());
            await DownloadFile(file, zipPath, (received, total) =>
            {
                if (total > 0) ProgressService.TotalAt(packStep, total);
                ProgressService.ProgressAt(packStep, received,
                    $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
            });
            ProgressService.TotalAt(packStep, 1);
            ProgressService.UpdateAt(packStep, 1, "整合包已下载");
            var temp = Path.Combine(AppPaths.SettingsDir, "cache", "packs", "extract-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temp);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, temp, true);
            var manifestPath = Path.Combine(temp, "manifest.json");
            if (!File.Exists(manifestPath)) throw new Exception("整合包缺少 manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? throw new Exception("整合包 manifest 格式错误");
            var mcVersion = manifest["minecraft"]?["version"]?.GetValue<string>() ?? "";
            var loaderText = (manifest["minecraft"]?["modLoaders"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<string>() ?? "";
            var loader = loaderText.StartsWith("fabric-") ? "fabric" : loaderText.StartsWith("quilt-") ? "quilt" : loaderText.StartsWith("neoforge-") ? "neoforge" : loaderText.StartsWith("forge-") ? "forge" : "vanilla";
            var loaderVersion = loaderText.Contains('-') ? loaderText.Substring(loaderText.IndexOf('-') + 1) : null;
            if (!string.IsNullOrWhiteSpace(loaderVersion) && loaderVersion.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
                loaderVersion = loaderVersion[(mcVersion.Length + 1)..];
            var baseStep = ProgressService.Step("安装游戏基础", 1, $"{mcVersion} {loader}");
            string instanceId;
            if (loader == "fabric" || loader == "quilt")
            {
                var result = await LoaderService.Install(loader, mcVersion, loaderVersion, instanceName, true);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            else if (loader == "forge" || loader == "neoforge")
            {
                var result = await ForgeService.Install(loader, mcVersion, loaderVersion, instanceName, true);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            else
            {
                var result = await MinecraftInstallService.InstallVanilla(mcVersion, true, instanceName);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            ProgressService.UpdateAt(baseStep, 1, "安装完成");
            ProgressService.Begin("安装 CurseForge 整合包资源");
            var downloadStep = ProgressService.Step("下载模组文件", (manifest["files"] as JsonArray)?.Count ?? 0, "");
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            var modsDir = Path.Combine(gameDir, "mods");
            Directory.CreateDirectory(modsDir);
            var missing = new JsonArray();
            var downloaded = 0;
            if (manifest["files"] is JsonArray files)
            {
                var classIds = new ConcurrentDictionary<int, Task<int>>();
                Task<int> ClassIdOf(int projectId) => classIds.GetOrAdd(projectId, async id =>
                {
                    try { return (await Project(id))["classId"]?.GetValue<int>() ?? 6; }
                    catch { return 6; }
                });
                var required = files.OfType<JsonObject>()
                    .Where(entry => entry["required"]?.GetValue<bool>() != false)
                    .ToList();
                var doneCount = 0;
                ProgressService.TotalAt(downloadStep, required.Count);
                using var gate = new System.Threading.SemaphoreSlim(8);
                await Task.WhenAll(required.Select(async entry =>
                {
                    var projectId = entry["projectID"]?.GetValue<int>() ?? 0;
                    var modFileId = entry["fileID"]?.GetValue<int>() ?? 0;
                    try
                    {
                        await gate.WaitAsync();
                        // The mirror's /download endpoint returns 404 for many
                        // pack files. Resolve the file metadata (filename and
                        // download URL) and use the same CDN fallback as single
                        // mod installation.
                        var info = await ResolveFile(projectId, modFileId);
                        var fileName = info["fileName"]?.GetValue<string>() ?? $"{projectId}-{modFileId}.jar";
                        var destDir = modsDir;
                        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        {
                            var classId = await ClassIdOf(projectId);
                            if (classId == 6) classId = 12;
                            if (classId == 4471 || classId == 17) return;
                            destDir = classId == 6552
                                ? Path.Combine(gameDir, "shaderpacks")
                                : Path.Combine(gameDir, "resourcepacks");
                        }
                        Directory.CreateDirectory(destDir);
                        await DownloadFile(info, Path.Combine(destDir, Path.GetFileName(fileName)));
                        downloaded++;
                    }
                    catch (Exception ex)
                    {
                        missing.Add(new JsonObject { ["projectId"] = projectId, ["fileId"] = modFileId, ["reason"] = ex.Message });
                    }
                    finally
                    {
                        gate.Release();
                        var done = System.Threading.Interlocked.Increment(ref doneCount);
                        ProgressService.ProgressAt(downloadStep, done, $"{done}/{required.Count}");
                    }
                }));
                ProgressService.UpdateAt(downloadStep, required.Count, "下载完成");
            }
            foreach (var folder in new[] { manifest["overrides"]?.GetValue<string>() ?? "overrides", "client-overrides" })
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
            ProgressService.Update(downloaded, "整合包文件已下载");
            ProgressService.CompletePack();
            return new JsonObject { ["instanceId"] = instanceId, ["missing"] = missing, ["files"] = downloaded };
        }

        private static async Task<JsonObject> ResolveFile(int modId, int fileId)
        {
            if (modId <= 0 || fileId <= 0) throw new Exception("CurseForge 文件参数无效");
            // Query the exact file instead of searching the unfiltered latest
            // list. The latest list is paginated; a filtered 1.20.1 Fabric file
            // may be absent there, and the old fallback silently picked 26.3.
            var raw = await Get($"/mods/{modId}/files/{fileId}") as JsonObject
                ?? throw new Exception("CurseForge 文件不存在");
            var data = raw["data"] as JsonObject ?? raw;
            return MapFile(data);
        }

        private static void ValidateFileForInstance(JsonObject file, JsonObject instance)
        {
            var versions = (file["gameVersions"] as JsonArray)?.OfType<JsonValue>()
                .Select(item => item.GetValue<string>())
                .ToList() ?? new List<string>();
            if (versions.Count == 0) return;
            var mcVersion = instance["mcVersion"]?.GetValue<string>() ?? "";
            if (mcVersion.Length > 0 && !versions.Contains(mcVersion))
                throw new Exception($"文件不适用于当前实例：文件支持 {string.Join(", ", versions.Take(6))}，实例是 {mcVersion}");
            var loader = instance["loader"]?.GetValue<string>() ?? "";
            var loaderName = loader switch
            {
                "fabric" => "Fabric",
                "quilt" => "Quilt",
                "forge" => "Forge",
                "neoforge" => "NeoForge",
                _ => ""
            };
            if (loaderName.Length > 0 && versions.Any(item => item.Contains("Fabric", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("Forge", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("Quilt", StringComparison.OrdinalIgnoreCase))
                && !versions.Any(item => item.Contains(loaderName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Exception($"文件加载器不匹配：文件支持 {string.Join(", ", versions.Take(6))}，实例是 {loaderName}");
            }
        }

        private static async Task DownloadFile(JsonObject file, string target, Action<long, long>? progress = null, System.Threading.CancellationToken token = default)
        {
            var url = file["downloadUrl"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                var fileId = file["id"]?.GetValue<int>() ?? 0;
                var fileName = file["fileName"]?.GetValue<string>() ?? "";
                url = $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";
            }
            await DownloadService.Download(url!, target, null, file["fileLength"]?.GetValue<long>() ?? 0, token, progress);
        }

        private static JsonObject MapHit(JsonObject raw)
        {
            var classId = raw["classId"]?.GetValue<int>() ?? 6;
            var projectType = classId == 4471 ? "modpack" : classId == 12 ? "resourcepack" : classId == 6552 ? "shader" : "mod";
            return new JsonObject
            {
                ["id"] = raw["id"]?.GetValue<int>() ?? 0,
                ["name"] = raw["name"]?.GetValue<string>() ?? "",
                ["slug"] = raw["slug"]?.GetValue<string>() ?? "",
                ["summary"] = raw["summary"]?.GetValue<string>() ?? "",
                ["logo"] = raw["logo"]?["thumbnailUrl"]?.GetValue<string>() ?? raw["logo"]?["url"]?.GetValue<string>(),
                ["downloads"] = raw["downloadCount"]?.GetValue<long>() ?? 0,
                ["categories"] = new JsonArray((raw["categories"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(item => (JsonNode)(item["name"]?.GetValue<string>() ?? "")).ToArray()),
                ["authors"] = new JsonArray((raw["authors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(item => (JsonNode)(item["name"]?.GetValue<string>() ?? "")).ToArray()),
                ["classId"] = classId,
                ["projectType"] = projectType,
                ["dateModified"] = raw["dateModified"]?.GetValue<string>() ?? raw["dateCreated"]?.GetValue<string>() ?? ""
            };
        }

        private static JsonObject MapFile(JsonObject raw)
        {
            var hashes = new JsonArray();
            if (raw["hashes"] is JsonArray rawHashes)
                foreach (var hash in rawHashes)
                    if (hash is JsonObject item)
                        hashes.Add(new JsonObject { ["algo"] = item["algo"]?.GetValue<int>() ?? 0, ["value"] = item["value"]?.GetValue<string>() ?? "" });
            var dependencies = new JsonArray();
            if (raw["dependencies"] is JsonArray rawDeps)
                foreach (var dep in rawDeps)
                    if (dep is JsonObject item)
                        dependencies.Add(new JsonObject { ["modId"] = item["modId"]?.GetValue<int>() ?? 0, ["relationType"] = item["relationType"]?.GetValue<int>() ?? 0 });
            return new JsonObject
            {
                ["id"] = raw["id"]?.GetValue<int>() ?? 0,
                ["modId"] = raw["modId"]?.GetValue<int>() ?? 0,
                ["displayName"] = raw["displayName"]?.GetValue<string>() ?? "",
                ["fileName"] = raw["fileName"]?.GetValue<string>() ?? "",
                ["releaseType"] = raw["releaseType"]?.GetValue<int>() ?? 1,
                ["fileDate"] = raw["fileDate"]?.GetValue<string>() ?? "",
                ["fileLength"] = raw["fileLength"]?.GetValue<long>() ?? 0,
                ["downloadCount"] = raw["downloadCount"]?.GetValue<long>() ?? 0,
                ["gameVersions"] = (raw["gameVersions"] as JsonArray)?.DeepCloneCompat() ?? new JsonArray(),
                ["downloadUrl"] = raw["downloadUrl"]?.GetValue<string>(),
                ["hashes"] = hashes,
                ["dependencies"] = dependencies
            };
        }

        private static string? GetString(JsonElement input, string name)
            => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) ? value.GetString() : null;

        private static int GetInt(JsonElement input, string name, int fallback)
            => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;
    }
}
