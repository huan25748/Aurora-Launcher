using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
                Timeout = TimeSpan.FromSeconds(120),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
        }

        private static string BaseUrl()
        {
            var settings = BridgeService.GetSettings();
            var source = settings["modrinthSource"]?.GetValue<string>() ?? "auto";
            if (source == "mirror") return "https://mod.mcimirror.top/modrinth/v2";
            return "https://api.modrinth.com/v2";
        }

        private static async Task<JsonNode?> GetFast(string path)
        {
            var settings = BridgeService.GetSettings();
            var mode = settings["modrinthSource"]?.GetValue<string>() ?? "auto";
            var bases = mode switch
            {
                "official" => new[] { "https://api.modrinth.com/v2" },
                "mirror" => new[] { "https://mod.mcimirror.top/modrinth/v2" },
                _ => new[] { "https://api.modrinth.com/v2", "https://mod.mcimirror.top/modrinth/v2" }
            };
            if (bases.Length == 1)
            {
                try
                {
                    var text = await Http.GetStringAsync(bases[0] + path);
                    return JsonNode.Parse(text);
                }
                catch (Exception ex) { Log("single source failed " + bases[0] + path + " : " + ex.Message); }
                // 选定的源不可用时回退到另一个，避免版本列表直接变空。
                var fallback = bases[0].Contains("mcimirror", StringComparison.OrdinalIgnoreCase)
                    ? "https://api.modrinth.com/v2"
                    : "https://mod.mcimirror.top/modrinth/v2";
                try
                {
                    var text = await Http.GetStringAsync(fallback + path);
                    return JsonNode.Parse(text);
                }
                catch { return null; }
            }
            using var cts = new CancellationTokenSource();
            var tasks = bases.Select(async b =>
            {
                var text = await Http.GetStringAsync(b + path, cts.Token);
                return JsonNode.Parse(text);
            }).ToList();
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                try
                {
                    var node = await done;
                    if (node != null) { cts.Cancel(); return node; }
                }
                catch { }
            }
            return null;
        }

        private static async Task<JsonNode?> Get(string path)
        {
            var key = "get:" + path;
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Value;
            var node = await GetFast(path);
            if (node == null)
            {
                var alternate = BaseUrl().Contains("mcimirror", StringComparison.OrdinalIgnoreCase)
                    ? "https://api.modrinth.com/v2"
                    : "https://mod.mcimirror.top/modrinth/v2";
                try
                {
                    node = JsonNode.Parse(await Http.GetStringAsync(alternate + path));
                }
                catch { }
            }
            if (node != null) Cache[key] = (DateTime.UtcNow, node);
            return node;
        }

        public static async Task<JsonObject> Search(JsonElement input)
        {
            var parameters = new List<string>();
            var query = GetString(input, "query");
            if (!string.IsNullOrWhiteSpace(query)) parameters.Add("query=" + Uri.EscapeDataString(query));
            var projectType = GetString(input, "projectType");
            if (string.IsNullOrWhiteSpace(projectType)) projectType = "mod";
            if (projectType == "modpack") parameters.Add("facets=" + Uri.EscapeDataString("[[\"project_type:modpack\"]]"));
            else parameters.Add("facets=" + Uri.EscapeDataString("[[\"project_type:" + projectType + "\"]]"));
            var mcVersion = GetString(input, "mcVersion");
            if (!string.IsNullOrWhiteSpace(mcVersion))
                parameters.Add("facets=" + Uri.EscapeDataString("[[\"versions:" + mcVersion + "\"]]"));
            var loader = GetString(input, "loader");
            if (!string.IsNullOrWhiteSpace(loader))
                parameters.Add("facets=" + Uri.EscapeDataString("[[\"categories:" + loader + "\"]]"));
            var index = GetString(input, "index");
            if (!string.IsNullOrWhiteSpace(index)) parameters.Add("index=" + Uri.EscapeDataString(index));
            var limit = 20;
            var offset = 0;
            if (input.ValueKind == JsonValueKind.Object)
            {
                if (input.TryGetProperty("limit", out var limitNode) && limitNode.TryGetInt32(out var limitValue)) limit = limitValue;
                if (input.TryGetProperty("offset", out var offsetNode) && offsetNode.TryGetInt32(out var offsetValue)) offset = offsetValue;
            }
            parameters.Add("limit=" + Math.Clamp(limit, 1, 50));
            parameters.Add("offset=" + Math.Max(0, offset));
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
            var raw = await Get("/project/" + Uri.EscapeDataString(id)) as JsonObject
                ?? throw new Exception("项目不存在");
            if (raw["slug"]?.GetValue<string>() is string slug)
                raw["nameTranslation"] = ModNamesService.Chinese("modrinth", slug);
            return raw;
        }

        public static async Task<Dictionary<string, string>> Icons(IEnumerable<string> hashes)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = hashes.Where(h => h.Length == 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(250).ToList();
            if (list.Count == 0) return result;
            try
            {
                var body = JsonSerializer.Serialize(new { hashes = list, algorithm = "sha1" });
                using var response = await Http.PostAsync(BaseUrl().TrimEnd('/') + "/version_files",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode) return result;
                var text = await response.Content.ReadAsStringAsync();
                if (JsonNode.Parse(text) is not JsonObject versions) return result;
                var projectIds = versions.Select(pair => pair.Value?["project_id"]?.GetValue<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                if (projectIds.Count == 0) return result;
                var projectsUrl = BaseUrl().TrimEnd('/') + "/projects?ids=" +
                    Uri.EscapeDataString(JsonSerializer.Serialize(projectIds));
                if (JsonNode.Parse(await Http.GetStringAsync(projectsUrl)) is not JsonArray projects) return result;
                var icons = projects.OfType<JsonObject>()
                    .ToDictionary(p => p["id"]?.GetValue<string>() ?? "", p => p["icon_url"]?.GetValue<string>() ?? "");
                foreach (var pair in versions)
                {
                    var projectId = pair.Value?["project_id"]?.GetValue<string>() ?? "";
                    if (icons.TryGetValue(projectId, out var icon) && !string.IsNullOrWhiteSpace(icon))
                        result[pair.Key] = icon;
                }
            }
            catch { }
            return result;
        }

        public static async Task<Dictionary<string, JsonObject>> VersionsByHashes(IEnumerable<string> hashes)
        {
            var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            var list = hashes.Where(h => h.Length == 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();
            if (list.Count == 0) return result;
            try
            {
                var body = JsonSerializer.Serialize(new { hashes = list, algorithm = "sha1" });
                using var response = await Http.PostAsync("https://api.modrinth.com/v2/version_files",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode) return result;
                var text = await response.Content.ReadAsStringAsync();
                if (JsonNode.Parse(text) is not JsonObject versions) return result;
                foreach (var pair in versions)
                    if (pair.Value is JsonObject version) result[pair.Key] = version;
            }
            catch { }
            return result;
        }


        internal static void Log(string text)
        {
            try
            {
                var dir = Path.Combine(AppPaths.SettingsDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "modrinth.log"),
                    DateTime.Now.ToString("HH:mm:ss") + " " + text + Environment.NewLine);
            }
            catch { }
        }

        public static async Task<JsonArray> Versions(string projectId, string? mcVersion, string? loader)
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(mcVersion)) query.Add("game_versions=" + Uri.EscapeDataString($"[\"{mcVersion}\"]"));
            if (!string.IsNullOrWhiteSpace(loader)) query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));
            var path = "/project/" + Uri.EscapeDataString(projectId) + "/version" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            return await GetFast(path) as JsonArray ?? new JsonArray();
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

        public static async Task<JsonObject> InstallVersionFile(string projectId, string versionId, string instanceId, string targetKind, string label = "下载模组文件")
        {
            var owns = !ProgressService.IsActive;
            if (owns) ProgressService.Begin("下载 Modrinth 文件");
            ProgressService.Step("获取模组版本", 1, versionId);
            var version = await PickVersion(projectId, versionId);
            var file = PrimaryFile(version) ?? throw new Exception("该版本没有可下载文件");
            var kind = string.IsNullOrWhiteSpace(targetKind) ? "mod" : targetKind;
            var folder = kind switch
            {
                "resourcepack" => "resourcepacks",
                "shader" => "shaderpacks",
                "datapack" => "datapacks",
                _ => "mods"
            };
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            var dir = Path.Combine(gameDir, folder);
            Directory.CreateDirectory(dir);
            var fileName = file["filename"]?.GetValue<string>() ?? "download.jar";
            var target = Path.Combine(dir, fileName);
            var downloadStep = ProgressService.Step(label, 1, fileName);
            await Download(file, target, (received, total) =>
            {
                if (total > 0) ProgressService.TotalAt(downloadStep, total);
                ProgressService.ProgressAt(downloadStep, received,
                    $"{MinecraftInstallService.FormatBytes(received)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
            });
            ProgressService.TotalAt(downloadStep, 1);
            ProgressService.UpdateAt(downloadStep, 1, "下载完成");
            if (owns) ProgressService.Complete();
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
            JsonObject? projectInfo = null;
            try
            {
                var project = projectInfo = await Project(projectId);
                if (project["title"]?.GetValue<string>() is string projectName && projectName.Length > 0)
                    packTitle += " · " + projectName;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(instanceName) && packTitle.StartsWith("安装整合包 · ", StringComparison.Ordinal))
                instanceName = packTitle["安装整合包 · ".Length..];
            ProgressService.BeginPack(packTitle);
            string? createdInstanceId = null;
            try
            {
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
            var extractStep = ProgressService.Step("解压整合包", 1, "准备解压");
            var extractTotal = 0;
            ArchiveService.ExtractWithProgress(zipPath, temp, (done, total) =>
            {
                extractTotal = total;
                if (total > 0) ProgressService.TotalAt(extractStep, total);
                ProgressService.ProgressAt(extractStep, done, done + "/" + total);
            });
            ProgressService.UpdateAt(extractStep, extractTotal > 0 ? extractTotal : 1, "解压完成");
            var indexPath = Path.Combine(temp, "modrinth.index.json");
            JsonObject index;
            if (File.Exists(indexPath))
            {
                index = JsonNode.Parse(File.ReadAllText(indexPath)) as JsonObject ?? throw new Exception("整合包索引格式错误");
            }
            else
            {
                var cfManifest = Path.Combine(temp, "manifest.json");
                var altManifest = Path.Combine(temp, "minecraftinstance.json");
                string detectedMc = "";
                string loaderId = "";
                if (File.Exists(cfManifest) && JsonNode.Parse(File.ReadAllText(cfManifest)) is JsonObject cf)
                {
                    detectedMc = cf["minecraft"]?["version"]?.GetValue<string>() ?? "";
                    loaderId = (cf["minecraft"]?["modLoaders"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<string>() ?? "";
                }
                else if (File.Exists(altManifest) && JsonNode.Parse(File.ReadAllText(altManifest)) is JsonObject alt)
                {
                    detectedMc = alt["gameVersion"]?.GetValue<string>() ?? alt["baseModLoader"]?["minecraftVersion"]?.GetValue<string>() ?? "";
                    loaderId = alt["baseModLoader"]?["name"]?.GetValue<string>() ?? "";
                }
                if (string.IsNullOrWhiteSpace(detectedMc)) throw new Exception("整合包缺少 modrinth.index.json 或可识别的 manifest.json");
                var deps = new JsonObject { ["minecraft"] = detectedMc };
                if (loaderId.StartsWith("fabric-")) deps["fabric-loader"] = loaderId["fabric-".Length..];
                else if (loaderId.StartsWith("quilt-")) deps["quilt-loader"] = loaderId["quilt-".Length..];
                else if (loaderId.StartsWith("neoforge-")) deps["neoforge"] = loaderId["neoforge-".Length..];
                else if (loaderId.StartsWith("forge-")) deps["forge"] = loaderId["forge-".Length..];
                index = new JsonObject { ["dependencies"] = deps, ["files"] = new JsonArray() };
            }
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
            createdInstanceId = instanceId;
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            await InstanceIconService.EnsureRemote(instanceId, instance["gameDir"]?.GetValue<string>() ?? "", projectInfo);
            ProgressService.UpdateAt(baseStep, 1, "安装完成");
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            if (index["files"] is JsonArray files)
            {
                var downloadStep = ProgressService.Step("下载整合包文件", files.Count, files.Count + " 个文件");
                var doneCount = 0;
                using var gate = new System.Threading.SemaphoreSlim(8);
                long packTotal = files.OfType<JsonObject>().Sum(f => f["fileSize"]?.GetValue<long>() ?? 0);
                long packDone = 0;
                if (packTotal > 0) ProgressService.TotalAt(downloadStep, packTotal);
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
                        long lastReceived = 0;
                        foreach (var url in downloads)
                        {
                            try
                            {
                                await DownloadService.Download(url, target, null, 0, default, (received, total) =>
                                {
                                    var delta = Math.Max(0, received - lastReceived);
                                    lastReceived = received;
                                    var aggregate = System.Threading.Interlocked.Add(ref packDone, delta);
                                    var text = Path.GetFileName(path);
                                    if (packTotal > 0)
                                        text += " · " + MinecraftInstallService.FormatBytes(aggregate) + " / " + MinecraftInstallService.FormatBytes(packTotal);
                                    ProgressService.ProgressAt(downloadStep, packTotal > 0 ? aggregate : doneCount, text);
                                });
                                last = null; break;
                            }
                            catch (Exception ex) { last = ex; }
                        }
                        if (last != null) throw last;
                    }
                    catch { }
                    finally
                    {
                        gate.Release();
                        var done = System.Threading.Interlocked.Increment(ref doneCount);
                        if (packTotal <= 0) ProgressService.ProgressAt(downloadStep, done, $"{done}/{(index["files"] as JsonArray)?.Count ?? 0}");
                    }
                }));
                ProgressService.UpdateAt(downloadStep, packTotal > 0 ? packTotal : (index["files"] as JsonArray)?.Count ?? 0, "整合包文件已下载");
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
            catch
            {
                InstallCleanup.Remove(createdInstanceId);
                throw;
            }
        }

        public static async Task<JsonObject> Featured(string type)
        {
            var projectType = type == "modpack" ? "modpack" : type == "resourcepack" ? "resourcepack" : "mod";
            return await Search(System.Text.Json.JsonSerializer.SerializeToElement(new { projectType, limit = 12, index = "downloads" }));
        }

        public static async Task<JsonObject> VersionById(string versionId)
            => await Get("/version/" + Uri.EscapeDataString(versionId)) as JsonObject ?? throw new Exception("版本不存在");

        public static Task<JsonObject> VersionDetails(string projectId, string versionId) => PickVersion(projectId, versionId);

        private static async Task<JsonObject> PickVersion(string projectId, string versionId)
        {
            // 有明确版本 ID 时直接查单个版本，避免为了一个文件拉取几 MB 的版本列表。
            Log("pick project=" + projectId + " version=" + versionId);
            if (!string.IsNullOrWhiteSpace(versionId))
            {
                try
                {
                    var single = await VersionById(versionId);
                    Log("single version ok, project_id=" + (single["project_id"]?.GetValue<string>() ?? ""));
                    if (string.IsNullOrWhiteSpace(projectId)
                        || single["project_id"]?.GetValue<string>() == projectId)
                        return single;
                }
                catch (Exception ex) { Log("single version failed: " + ex.Message); }
            }
            var versions = await Versions(projectId, null, null);
            Log("fallback versions count=" + versions.Count);
            if (versions.Count == 0) throw new Exception("项目没有可用版本（project=" + projectId + " version=" + versionId + "）");
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

