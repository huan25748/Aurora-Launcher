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
    internal static class MinecraftInstallService
    {
        private static string VersionsDir => Path.Combine(AppPaths.InstallRoot, "versions");
        private static string LibrariesDir => Path.Combine(AppPaths.RootDir, "libraries");
        private static string AssetsDir => Path.Combine(AppPaths.RootDir, "assets");

        internal static int Concurrency()
        {
            try { return Math.Clamp(BridgeService.GetSettings()["maxConcurrency"]?.GetValue<int>() ?? 16, 1, 64); }
            catch { return 32; }
        }

        public static async Task<JsonObject> InstallVanilla(string versionId, bool createInstance, string? instanceName, CancellationToken token = default, bool reportProgress = true, bool includeAssets = true)
        {
            var ownsProgress = false;
            var infoStep = "";
            var cleanupBefore = createInstance ? VersionCleanup.Snapshot() : null;
            string? installRoot = null;
            string? installFolder = null;
            var completed = false;
            if (createInstance && string.IsNullOrWhiteSpace(AppPaths.InstallRootOverride))
            {
                installFolder = ReserveFolder(string.IsNullOrWhiteSpace(instanceName) ? versionId : instanceName!);
                installRoot = Path.Combine(AppPaths.RootDir, "versions", installFolder);
                Directory.CreateDirectory(Path.Combine(installRoot, "versions"));
                AppPaths.InstallRootOverride = installRoot;
            }
            try
            {
            if (reportProgress)
            {
                if (!ProgressService.IsActive) { ProgressService.Begin($"安装 {versionId}"); ownsProgress = true; }
                infoStep = ProgressService.Step("获取版本信息", 1, versionId);
            }
            if (string.IsNullOrWhiteSpace(versionId) || versionId.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                throw new Exception("版本 ID 不合法");

            var manifest = await VersionService.Manifest(false);
            var summary = (manifest["versions"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(item => item["id"]?.GetValue<string>() == versionId);
            if (summary == null) throw new Exception($"版本列表中不存在 {versionId}");

            var versionUrl = summary["url"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(versionUrl)) throw new Exception("版本 JSON 地址为空");
            var versionText = await DownloadService.GetText(versionUrl, token);
            var version = JsonNode.Parse(versionText) as JsonObject ?? throw new Exception("版本 JSON 格式不正确");

            var versionDir = Path.Combine(VersionsDir, versionId);
            Directory.CreateDirectory(versionDir);
            var versionFile = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(versionFile, versionText);
            if (reportProgress) ProgressService.UpdateAt(infoStep, 1, "版本信息已获取");

            var client = version["downloads"]?["client"];
            Task? clientTask = null;
            var clientStep = "";
            long clientTotal = 0;
            if (client != null)
            {
                var url = client["url"]?.GetValue<string>() ?? "";
                var sha1 = client["sha1"]?.GetValue<string>();
                var size = client["size"]?.GetValue<long>() ?? 0;
                clientTotal = size;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var clientPath = Path.Combine(versionDir, versionId + ".jar");
                    if (reportProgress) clientStep = ProgressService.Step("下载客户端", size > 0 ? size : 1, Path.GetFileName(clientPath));
                    clientTask = DownloadService.Download(url, clientPath, sha1, size, token, (received, total) =>
                    {
                        if (reportProgress) ProgressService.ProgressAt(clientStep, received, FormatBytes(received) + " / " + FormatBytes(total));
                    });
                }
            }

            var downloads = new List<(string Url, string Path, string? Sha1, long Size)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddLibrary(JsonNode? downloadsNode, string fallbackUrl = "")
            {
                if (downloadsNode == null) return;
                var url = downloadsNode["url"]?.GetValue<string>() ?? fallbackUrl;
                var path = downloadsNode["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url)) return;
                var full = Path.Combine(LibrariesDir, path.Replace('/', Path.DirectorySeparatorChar));
                if (!seen.Add(full)) return;
                downloads.Add((url, full, downloadsNode["sha1"]?.GetValue<string>(), downloadsNode["size"]?.GetValue<long>() ?? 0));
            }

            if (version["libraries"] is JsonArray libraries)
            {
                foreach (var item in libraries)
                {
                    if (item is not JsonObject library) continue;
                    if (!RulesAllow(library["rules"] as JsonArray)) continue;
                    AddLibrary(library["downloads"]?["artifact"]);
                    if (library["downloads"]?["classifiers"] is JsonObject classifiers && library["natives"] is JsonObject natives)
                    {
                        var classifier = natives["windows"]?.GetValue<string>()?.Replace("${arch}", "64");
                        if (!string.IsNullOrEmpty(classifier) && classifiers[classifier] is JsonObject native)
                            AddLibrary(native);
                    }
                }
            }

            var libraryStep = reportProgress ? ProgressService.Step("下载依赖库", downloads.Count, downloads.Count + " 个文件") : "";
            var libraryTask = RunDownloads(downloads, Concurrency(), token, (done, detail) => { if (reportProgress) ProgressService.ProgressAt(libraryStep, done, detail); });

            Task? loggingTask = null;
            var loggingStep = "";
            if (version["logging"]?["client"]?["file"] is JsonObject logFile)
            {
                var logUrl = logFile["url"]?.GetValue<string>() ?? "";
                var logId = logFile["id"]?.GetValue<string>()
                    ?? (string.IsNullOrWhiteSpace(logUrl) ? "" : Path.GetFileName(new Uri(logUrl, UriKind.Absolute).LocalPath));
                if (!string.IsNullOrWhiteSpace(logUrl) && !string.IsNullOrWhiteSpace(logId))
                {
                    var logPath = Path.Combine(AssetsDir, "log_configs", logId);
                    if (!File.Exists(logPath))
                    {
                        if (reportProgress) loggingStep = ProgressService.Step("下载日志配置", 1, logId);
                        loggingTask = DownloadService.Download(logUrl, logPath, logFile["sha1"]?.GetValue<string>(), logFile["size"]?.GetValue<long>() ?? 0, token);
                    }
                }
            }

            var parallelTasks = new List<Task> { libraryTask };
            if (clientTask != null) parallelTasks.Add(clientTask);
            if (loggingTask != null) parallelTasks.Add(loggingTask);
            await Task.WhenAll(parallelTasks);
            if (reportProgress)
            {
                if (clientStep.Length > 0) ProgressService.UpdateAt(clientStep, clientTotal > 0 ? clientTotal : 1, "客户端已下载");
                ProgressService.UpdateAt(libraryStep, downloads.Count, "依赖库已下载");
                if (loggingStep.Length > 0) ProgressService.UpdateAt(loggingStep, 1, "日志配置已下载");
            }

            if (includeAssets && version["assetIndex"] is JsonObject assetIndex)
            {
                var indexUrl = assetIndex["url"]?.GetValue<string>() ?? "";
                var indexId = assetIndex["id"]?.GetValue<string>() ?? version["assets"]?.GetValue<string>() ?? "legacy";
                var indexPath = Path.Combine(AssetsDir, "indexes", indexId + ".json");
                if (!string.IsNullOrWhiteSpace(indexUrl))
                {
                    if (reportProgress) ProgressService.Step("获取资源索引", 1, indexId);
                    if (reportProgress) ProgressService.Detail("正在下载 " + indexId + ".json");
                    var indexText = await DownloadService.GetText(indexUrl, token);
                    Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
                    File.WriteAllText(indexPath, indexText);
                    if (reportProgress) ProgressService.Update(1, "资源索引已下载");
                    var indexJson = JsonNode.Parse(indexText) as JsonObject;
                    if (indexJson?["objects"] is JsonObject objects)
                    {
                        var assets = new List<(string Url, string Path, string? Sha1, long Size)>();
                        foreach (var pair in objects)
                        {
                            var hash = pair.Value?["hash"]?.GetValue<string>();
                            var size = pair.Value?["size"]?.GetValue<long>() ?? 0;
                            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;
                            var assetPath = Path.Combine(AssetsDir, "objects", hash.Substring(0, 2), hash);
                            if (!seen.Add(assetPath)) continue;
                            if (File.Exists(assetPath) && new FileInfo(assetPath).Length == size) continue;
                            assets.Add(($"https://resources.download.minecraft.net/{hash.Substring(0, 2)}/{hash}", assetPath, hash, size));
                        }
                        if (reportProgress) ProgressService.Step("下载游戏资源", assets.Count, assets.Count + " 个资源");
                        await RunDownloads(assets, Concurrency(), token, null, (done, bytes, total, name) =>
                        {
                            if (reportProgress) ProgressService.Progress(done, $"{done}/{assets.Count} · 已下载 {FormatBytes(bytes)} · {name}");
                        }, allowPartial: true);
                        if (reportProgress) ProgressService.Update(assets.Count, "资源已下载");
                    }
                }
            }

            string? instanceId = null;
            if (createInstance)
            {
                Directory.CreateDirectory(Path.Combine(versionDir, "mods"));
                Directory.CreateDirectory(Path.Combine(versionDir, "saves"));
                Directory.CreateDirectory(Path.Combine(versionDir, "resourcepacks"));
                Directory.CreateDirectory(Path.Combine(versionDir, "shaderpacks"));
                Directory.CreateDirectory(Path.Combine(versionDir, "logs"));
                instanceId = CreateInstance(versionId, instanceName ?? versionId, "vanilla", null, null, cleanupBefore,
                    installFolder, installRoot != null ? Path.Combine(installRoot, "versions") : null);
            }
            if (reportProgress && ownsProgress) ProgressService.Complete();

            completed = true;
            return new JsonObject { ["instanceId"] = instanceId, ["versionId"] = versionId };
            }
            finally
            {
                if (installRoot != null)
                {
                    AppPaths.InstallRootOverride = null;
                    if (completed)
                    {
                        try { Directory.Delete(Path.Combine(installRoot, "versions"), true); } catch { }
                        try { File.Delete(Path.Combine(installRoot, "launcher_profiles.json")); } catch { }
                    }
                    else
                    {
                        try { Directory.Delete(installRoot, true); } catch { }
                    }
                }
            }
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / 1024d / 1024d).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024d).ToString("0") + " KB";
            return bytes + " B";
        }

        public static async Task EnsureAssets(JsonObject version, CancellationToken token = default)
        {
            var assetIndex = version["assetIndex"] as JsonObject;
            var source = version;
            while (assetIndex == null && source["inheritsFrom"]?.GetValue<string>() is string parentId && !string.IsNullOrWhiteSpace(parentId))
            {
                var parentFile = Path.Combine(VersionsDir, parentId, parentId + ".json");
                if (!File.Exists(parentFile)) break;
                source = JsonNode.Parse(File.ReadAllText(parentFile)) as JsonObject ?? new JsonObject();
                assetIndex = source["assetIndex"] as JsonObject;
            }
            if (assetIndex == null) return;
            var indexUrl = assetIndex["url"]?.GetValue<string>() ?? "";
            var indexId = assetIndex["id"]?.GetValue<string>() ?? source["assets"]?.GetValue<string>() ?? "legacy";
            if (string.IsNullOrWhiteSpace(indexUrl)) return;
            var indexPath = Path.Combine(AssetsDir, "indexes", indexId + ".json");
            string? indexText = null;
            if (!File.Exists(indexPath))
            {
                ProgressService.Step("获取资源索引", 1, indexId);
                indexText = await DownloadService.GetText(indexUrl, token);
                Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
                File.WriteAllText(indexPath, indexText);
                ProgressService.Update(1, "资源索引已下载");
            }
            indexText ??= File.ReadAllText(indexPath);
            var indexJson = JsonNode.Parse(indexText) as JsonObject;
            if (indexJson?["objects"] is not JsonObject objects) return;
            var assets = new List<(string Url, string Path, string? Sha1, long Size)>();
            foreach (var pair in objects)
            {
                var hash = pair.Value?["hash"]?.GetValue<string>();
                var size = pair.Value?["size"]?.GetValue<long>() ?? 0;
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;
                var assetPath = Path.Combine(AssetsDir, "objects", hash.Substring(0, 2), hash);
                if (File.Exists(assetPath) && new FileInfo(assetPath).Length == size) continue;
                assets.Add(($"https://resources.download.minecraft.net/{hash.Substring(0, 2)}/{hash}", assetPath, hash, size));
            }
            if (assets.Count == 0) return;
            ProgressService.Step("下载游戏资源", assets.Count, assets.Count + " 个资源");
            await RunDownloads(assets, Concurrency(), token, null, (done, bytes, total, name) =>
            {
                ProgressService.Progress(done, $"{done}/{assets.Count} · 已下载 {FormatBytes(bytes)} · {name}");
            }, allowPartial: true);
            ProgressService.Update(assets.Count, "资源已下载");
        }

        private static async Task RunDownloads(List<(string Url, string Path, string? Sha1, long Size)> files, int concurrency, CancellationToken token, Action<int, string>? progress = null, Action<int, long, long, string>? byteProgress = null, bool allowPartial = false)
        {
            var totalBytes = files.Sum(file => Math.Max(0, file.Size));
            string SizeText(long downloaded) => totalBytes > 0
                ? $"{FormatBytes(Math.Min(downloaded, totalBytes))} / {FormatBytes(totalBytes)}"
                : $"已下载 {FormatBytes(downloaded)}";

            using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
            var done = 0;
            long downloadedBytes = 0;
            var failed = new System.Collections.Concurrent.ConcurrentBag<(string Url, string Path, string? Sha1, long Size)>();
            var fileMax = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            await Task.WhenAll(files.Select(async file =>
            {
                await gate.WaitAsync(token);
                var ok = false;
                try
                {
                    long last = 0;
                    var name = Path.GetFileName(file.Path);
                    await DownloadService.Download(file.Url, file.Path, file.Sha1, file.Size, token, (received, _) =>
                    {
                        var key = file.Path;
                        var prevMax = fileMax.GetOrAdd(key, 0L);
                        if (received <= prevMax) return;
                        var delta = received - prevMax;
                        fileMax[key] = received;
                        last = received;
                        var aggregate = System.Threading.Interlocked.Add(ref downloadedBytes, delta);
                        var completed = System.Threading.Volatile.Read(ref done);
                        progress?.Invoke(completed, $"{completed}/{files.Count} · {SizeText(aggregate)} · {name}");
                        byteProgress?.Invoke(completed, Math.Min(aggregate, totalBytes), totalBytes, name);
                    });
                    ok = true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    var valid = false;
                    try { valid = File.Exists(file.Path) && (file.Size <= 0 || new FileInfo(file.Path).Length == file.Size); } catch { }
                    if (valid) ok = true; else failed.Add(file);
                }
                finally
                {
                    gate.Release();
                    if (ok)
                    {
                        var finished = System.Threading.Interlocked.Increment(ref done);
                        progress?.Invoke(finished,
                            $"{finished}/{files.Count} · {SizeText(System.Threading.Interlocked.Read(ref downloadedBytes))} · {Path.GetFileName(file.Path)}");
                    }
                }
            }));

            if (failed.Count == 0) return;

            // 第一次失败通常不是文件本身坏了，而是同一时间并发连接太多被 CDN 重置。
            // 这里只再试一次，并用较低的并发，等同于“重新点一次安装”，但不用用户手动操作。
            var retryFiles = failed.ToList();
            failed = new System.Collections.Concurrent.ConcurrentBag<(string Url, string Path, string? Sha1, long Size)>();
            progress?.Invoke(done, $"重试剩余 {retryFiles.Count} 个文件");
            using (var retryGate = new SemaphoreSlim(Math.Min(4, Math.Max(1, concurrency))))
            {
                await Task.WhenAll(retryFiles.Select(async file =>
                {
                    await retryGate.WaitAsync(token);
                    var ok = false;
                    try
                    {
                        await DownloadService.Download(file.Url, file.Path, file.Sha1, file.Size, token);
                        ok = true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch
                    {
                        var valid = false;
                        try { valid = File.Exists(file.Path) && (file.Size <= 0 || new FileInfo(file.Path).Length == file.Size); } catch { }
                        if (valid) ok = true; else failed.Add(file);
                    }
                    finally
                    {
                        retryGate.Release();
                        if (ok)
                        {
                            var finished = System.Threading.Interlocked.Increment(ref done);
                            progress?.Invoke(finished, $"{finished}/{files.Count} · {Path.GetFileName(file.Path)}");
                        }
                    }
                }));
            }

            if (failed.Count == 0) return;
            if (allowPartial)
            {
                var names = string.Join("、", failed.Take(5).Select(f => Path.GetFileName(f.Path)));
                GameEvents.Emit("log", new JsonObject
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["source"] = "launcher",
                    ["level"] = "warning",
                    ["text"] = $"有 {failed.Count} 个游戏资源未能下载，已跳过（不影响启动）：{names}"
                });
                return;
            }
            throw new Exception($"{failed.Count} 个文件下载失败，请重新启动安装续传");
        }

        private static bool RulesAllow(JsonArray? rules)
        {
            if (rules == null || rules.Count == 0) return true;
            var allowed = false;
            foreach (var item in rules)
            {
                if (item is not JsonObject rule) continue;
                var action = rule["action"]?.GetValue<string>() ?? "allow";
                var os = rule["os"] as JsonObject;
                if (os != null)
                {
                    var name = os["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name) && name != "windows") continue;
                    var arch = os["arch"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(arch) && arch != "x86" && arch != "x86_64") continue;
                }
                var features = rule["features"] as JsonObject;
                if (features != null)
                {
                    var match = true;
                    foreach (var feature in features)
                    {
                        var wanted = feature.Value?.GetValue<bool>() ?? false;
                        if (wanted) match = false;
                    }
                    if (!match) continue;
                }
                allowed = action == "allow";
            }
            return allowed;
        }

        public static string CreateInstance(string versionId, string name, string loader = "vanilla", string? loaderVersion = null, string? mcVersion = null, HashSet<string>? cleanupBefore = null, string? forcedFolder = null, string? sourceVersionsRoot = null)
        {
            var instances = AppPaths.InstancesDir;
            Directory.CreateDirectory(instances);
            if (ProgressService.IsActive) ProgressService.Step("创建实例", 1, "写入自包含版本");
            var id = "inst-" + Guid.NewGuid().ToString("N")[..12];
            var directory = Path.Combine(instances, id);
            Directory.CreateDirectory(directory);
            try { File.WriteAllText(Path.Combine(directory, ".pclignore"), "安装中，完成后自动删除"); } catch { }

            // Old Aurora creates a self-contained version folder under versions/<name>:
            // merged version JSON plus the client jar. The installer's intermediate
            // versions can then be removed without breaking the instance.
            var sourceVersions = sourceVersionsRoot ?? Path.Combine(AppPaths.InstallRoot, "versions");
            var outputVersions = Path.Combine(AppPaths.RootDir, "versions");
            Directory.CreateDirectory(outputVersions);
            var chain = LaunchService.LoadVersionChainFrom(sourceVersions, versionId);
            var baseName = SafeFolderName(name);
            var folder = forcedFolder ?? baseName;
            var suffix = 2;
            if (forcedFolder == null)
            {
                while (Directory.Exists(Path.Combine(outputVersions, folder)))
                {
                    folder = baseName + "-" + suffix;
                    suffix++;
                }
            }
            var gameDir = Path.Combine(outputVersions, folder);
            Directory.CreateDirectory(gameDir);
            var standalone = (JsonObject)chain.DeepCloneCompat()!;
            standalone["id"] = folder;
            standalone.Remove("inheritsFrom");
            var instanceJar = Path.Combine(gameDir, folder + ".jar");
            if (!File.Exists(instanceJar) || new FileInfo(instanceJar).Length == 0)
            {
                // PCL always materialises the instance jar (vanilla client jar) next
                // to the merged JSON, including Forge/NeoForge BootstrapLauncher.
                var sourceJar = LaunchService.FindClientJarFrom(sourceVersions, versionId);
                if (sourceJar != null)
                {
                    try { File.Copy(sourceJar, instanceJar, true); } catch { }
                }
                else
                {
                    var client = standalone["downloads"]?["client"] as JsonObject;
                    var url = client?["url"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        try
                        {
                            DownloadService.Download(url, instanceJar, client?["sha1"]?.GetValue<string>(),
                                client?["size"]?.GetValue<long>() ?? 0, System.Threading.CancellationToken.None)
                                .GetAwaiter().GetResult();
                        }
                        catch { }
                    }
                }
            }
            File.WriteAllText(Path.Combine(gameDir, folder + ".json"), standalone.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            foreach (var sub in new[] { "mods", "saves", "resourcepacks", "shaderpacks", "logs", "crash-reports", "config" })
                Directory.CreateDirectory(Path.Combine(gameDir, sub));

            var json = new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["versionId"] = folder,
                ["sourceVersionId"] = versionId,
                ["selfContained"] = true,
                ["mcVersion"] = mcVersion ?? versionId,
                ["loader"] = loader,
                ["loaderVersion"] = loaderVersion,
                ["gameDir"] = gameDir,
                ["icon"] = "🧊",
                ["color"] = "ocean",
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["totalPlayTimeMs"] = 0,
                ["playCount"] = 0
            };
            try { File.Delete(Path.Combine(directory, ".pclignore")); } catch { }
            File.WriteAllText(Path.Combine(directory, "instance.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (cleanupBefore != null) VersionCleanup.Cleanup(cleanupBefore, folder);
            if (!string.Equals(Path.GetFullPath(sourceVersions), Path.GetFullPath(outputVersions), StringComparison.OrdinalIgnoreCase))
                try { VersionCleanup.CleanupSourceChain(versionId, folder); } catch { }
            if (ProgressService.IsActive) ProgressService.Update(1, "实例已创建");
            BridgeService.NotifyInstances();
            return id;
        }

        public static string ReserveFolder(string name)
        {
            var versionsDir = Path.Combine(AppPaths.RootDir, "versions");
            Directory.CreateDirectory(versionsDir);
            var baseName = SafeFolderName(name);
            var folder = baseName;
            var suffix = 2;
            while (Directory.Exists(Path.Combine(versionsDir, folder))) { folder = baseName + "-" + suffix; suffix++; }
            Directory.CreateDirectory(Path.Combine(versionsDir, folder));
            return folder;
        }

        private static string SafeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = string.Concat((name ?? "").Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (result.Length == 0) result = "instance";
            if (result.Length > 48) result = result[..48].TrimEnd();
            return result;
        }
    }
}
