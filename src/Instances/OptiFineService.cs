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
                var forge = (item["forge"]?.GetValue<string>() ?? "").Trim();
                if (forge.StartsWith("Forge", StringComparison.OrdinalIgnoreCase))
                    forge = forge.Substring(5).Trim();
                if (forge.StartsWith("Forge", StringComparison.OrdinalIgnoreCase))
                    forge = forge.Substring(5).Trim();
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
            try
            {
                var page = await DownloadService.GetText("https://optifine.net/downloads");
                var available = System.Text.RegularExpressions.Regex
                    .Matches(page, "OptiFine_[A-Za-z0-9_.]+\\.jar")
                    .Select(m => m.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (available.Count > 0)
                {
                    var filtered = new JsonArray();
                    foreach (var item in result.OfType<JsonObject>())
                    {
                        var file = item["fileName"]?.GetValue<string>() ?? "";
                        if (available.Contains(file)) filtered.Add(JsonNode.Parse(item.ToJsonString())!);
                    }
                    result = filtered;
                }
            }
            catch { }
            return result;
        }

        private static async Task DownloadDirect(string url, string target, CancellationToken token, Action<long, long>? onProgress)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
            using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            var last = DateTime.UtcNow;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 120)
                {
                    last = DateTime.UtcNow;
                    onProgress?.Invoke(done, total > 0 ? total : done);
                }
            }
            onProgress?.Invoke(done, total > 0 ? total : done);
        }

        private static async Task DownloadOfficialOptiFine(string name, string target, CancellationToken token, Action<long, long>? onProgress)
        {
            using var handler = new System.Net.Http.HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };
            using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var pageUrl = "https://optifine.net/adloadx?f=" + Uri.EscapeDataString(name);
            var page = await client.GetStringAsync(pageUrl, token);
            var match = System.Text.RegularExpressions.Regex.Match(page, "downloadx\\?f=[^\"'\\s]+");
            if (!match.Success) throw new Exception("OptiFine 官网未找到下载令牌：" + name);
            var downloadUrl = "https://optifine.net/" + match.Value.Replace("&amp;", "&");
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, downloadUrl);
            request.Headers.Referrer = new Uri(pageUrl);
            using var response = await client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            if (total > 0 && total < 1024 * 1024) throw new Exception("OptiFine 官网返回的不是完整 jar：" + name);
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (onProgress != null) onProgress(total > 0 ? Math.Min(total, done) : done, total);
            }
            if (new FileInfo(target).Length < 1024 * 1024) throw new Exception("OptiFine 官方文件不完整：" + name);
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
            var listed = await List(mcVersion);
            JsonObject? entry = null;
            foreach (var item in listed.OfType<JsonObject>())
            {
                var version = item["version"]?.GetValue<string>() ?? "";
                if (string.Equals(version, versionId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(version, prefix + type, StringComparison.OrdinalIgnoreCase))
                {
                    entry = item;
                    break;
                }
            }
            string channel;
            string patch;
            if (entry != null)
            {
                channel = entry["type"]?.GetValue<string>() ?? "HD_U";
                patch = entry["patch"]?.GetValue<string>() ?? "";
            }
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(type, "^(.*?)_?(pre\\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    channel = match.Groups[1].Value.TrimEnd('_');
                    patch = match.Groups[2].Value;
                }
                else if (type.StartsWith("HD_U_", StringComparison.OrdinalIgnoreCase))
                {
                    channel = "HD_U";
                    patch = type[5..];
                }
                else
                {
                    channel = type;
                    patch = "";
                }
            }
            if (string.IsNullOrWhiteSpace(patch))
                throw new Exception("OptiFine 版本格式不正确");
            var fileName = $"OptiFine_{mcVersion}_{channel}_{patch}.jar";
            var target = Path.Combine(AppPaths.SettingsDir, "cache", "optifine", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var candidates = new List<string> { patch };
            var stable = patch.Contains("_pre", StringComparison.OrdinalIgnoreCase) ? patch[..patch.IndexOf("_pre", StringComparison.OrdinalIgnoreCase)] : "";
            if (!string.IsNullOrWhiteSpace(stable)) candidates.Add(stable);
            foreach (var item in listed.OfType<JsonObject>())
            {
                var listedPatch = item["patch"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(listedPatch) || listedPatch.Contains("_pre", StringComparison.OrdinalIgnoreCase)) continue;
                if (!candidates.Contains(listedPatch)) candidates.Add(listedPatch);
            }
            var source = "auto";
            try { source = BridgeService.GetSettings()["optifineSource"]?.GetValue<string>() ?? "auto"; } catch { }
            var useMirror = source != "official";
            var useOfficial = source != "bmclapi";
            Exception? lastError = null;
            foreach (var candidate in candidates)
            {
                var officialName = (candidate.StartsWith("pre", StringComparison.OrdinalIgnoreCase) ? "preview_" : "")
                    + $"OptiFine_{mcVersion}_{channel}_{candidate}.jar";
                var url = $"{DownloadApi}/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(channel)}/{Uri.EscapeDataString(candidate)}";
                if (useMirror)
                {
                    try
                    {
                        using var mirrorTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        // A single slow mirror used to stall for 90s; use the parallel
                        // download engine (same as game libraries) and fall through to
                        // optifine.net quickly when the mirror is unhealthy.
                        mirrorTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                        await DownloadService.Download(url, target, null, 0, mirrorTimeout.Token,
                            (received, total) => onProgress?.Invoke(received, total));
                        return (target, fileName);
                    }
                    catch (Exception ex) { lastError = ex; }
                }
                if (useOfficial)
                {
                    try
                    {
                        await DownloadOfficialOptiFine(officialName, target, token, onProgress);
                        return (target, fileName);
                    }
                    catch (Exception ex) { lastError = ex; }
                }
            }
            throw new Exception("OptiFine 下载失败：BMCLAPI 未镜像该预览版 " + patch +
                (string.IsNullOrWhiteSpace(stable) ? "" : "，请改用正式版 " + stable) + "。", lastError);
        }

        private static string FolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
            return new string(chars).Trim().TrimEnd('.');
        }

        public static string OptiFineEdition(string versionId)
        {
            var marker = "OptiFine_";
            var index = versionId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var edition = index >= 0 ? versionId[(index + marker.Length)..] : versionId;
            return edition.StartsWith("HD_U_", StringComparison.OrdinalIgnoreCase) ? edition[5..] : edition;
        }

        public static string VersionName(string mcVersion, string? loader, string? loaderVersion, string optifineVersionId)
        {
            var edition = OptiFineEdition(optifineVersionId);
            var loaderName = (loader ?? "vanilla").ToLowerInvariant() switch
            {
                "forge" => "Forge",
                "neoforge" => "NeoForge",
                "fabric" => "Fabric",
                "quilt" => "Quilt",
                _ => ""
            };
            return loaderName.Length > 0 && !string.IsNullOrWhiteSpace(loaderVersion)
                ? $"{mcVersion}-{loaderName}_{loaderVersion}-OptiFine_{edition}"
                : $"{mcVersion}-OptiFine_{edition}";
        }

        // Turns the instance game folder into a PCL-style standalone OptiFine version:
        // versions/<mc>-<Loader>_<ver>-OptiFine_<edition> with mods/OptiFine jar inside.
        public static string ApplyToInstance(string instanceId, string optifineFile, string optifineFileName, string mcVersion, string optifineVersionId)
        {
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            var gameDir = instance["gameDir"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                throw new Exception("实例目录不存在");
            var loader = instance["loader"]?.GetValue<string>();
            var loaderVersion = instance["loaderVersion"]?.GetValue<string>();
            var targetName = VersionName(mcVersion, loader, loaderVersion, optifineVersionId);
            var versionsDir = Path.Combine(AppPaths.RootDir, "versions");
            var targetDir = Path.Combine(versionsDir, FolderName(targetName));
            if (!string.Equals(Path.GetFullPath(gameDir!), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase)
                && !Directory.Exists(targetDir))
            {
                Directory.Move(gameDir!, targetDir);
                gameDir = targetDir;
            }
            var mods = Path.Combine(gameDir!, "mods");
            Directory.CreateDirectory(mods);
            File.Copy(optifineFile, Path.Combine(mods, optifineFileName), true);
            var currentName = Path.GetFileName(gameDir!)!;
            foreach (var jsonPath in Directory.GetFiles(gameDir!, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(jsonPath);
                if (!string.Equals(name, Path.GetFileName(gameDir), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var versionJson = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
                    if (versionJson == null) continue;
                    versionJson["id"] = currentName;
                    File.WriteAllText(Path.Combine(gameDir!, currentName + ".json"), versionJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    if (!string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase)) File.Delete(jsonPath);
                }
                catch { }
            }
            var instanceFile = Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");
            if (File.Exists(instanceFile))
            {
                var record = JsonNode.Parse(File.ReadAllText(instanceFile)) as JsonObject;
                if (record != null)
                {
                    record["versionId"] = currentName;
                    record["gameDir"] = gameDir;
                    record["optifineVersion"] = optifineVersionId;
                    File.WriteAllText(instanceFile, record.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }
            BridgeService.NotifyInstances();
            return gameDir!;
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
                var loader = instance["loader"]?.GetValue<string>() ?? "vanilla";
                if (loader is "vanilla" or "" or "optifine")
                {
                    // PCL parity: without a mod loader OptiFine must be installed as a
                    // standalone version by running the official installer.
                    ProgressService.Step("运行 OptiFine 安装器", 1, versionId);
                    var installed = InstallAsVersion(mcVersion, versionId, instanceId, gameDir, file, token);
                    ProgressService.Update(1, "OptiFine 已安装");
                    ProgressService.Complete();
                    return installed;
                }
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

        private static void CopyTree(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file);
                var target = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        private static void EnsureBaseVersion(string mcVersion, string gameDir)
        {
            var baseDir = Path.Combine(AppPaths.RootDir, "versions", mcVersion);
            var baseJson = Path.Combine(baseDir, mcVersion + ".json");
            var baseJar = Path.Combine(baseDir, mcVersion + ".jar");
            Directory.CreateDirectory(baseDir);
            if (!File.Exists(baseJson))
            {
                var instanceJson = Directory.EnumerateFiles(gameDir, "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(file => !Path.GetFileName(file).Equals("instance.json", StringComparison.OrdinalIgnoreCase));
                if (instanceJson != null)
                {
                    var node = JsonNode.Parse(File.ReadAllText(instanceJson)) as JsonObject;
                    if (node != null)
                    {
                        node["id"] = mcVersion;
                        File.WriteAllText(baseJson, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            }
            if (!File.Exists(baseJar))
            {
                var instanceJar = Directory.EnumerateFiles(gameDir, "*.jar", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (instanceJar != null) File.Copy(instanceJar, baseJar, true);
            }
            if (!File.Exists(baseJson)) throw new Exception("缺少原版版本 JSON，无法运行 OptiFine 安装器");
        }

        private static JsonObject InstallAsVersion(string mcVersion, string versionId, string instanceId, string gameDir, string optifineFile, CancellationToken token)
        {
            var tempHome = Path.Combine(Path.GetTempPath(), "aurora-optifine-" + Guid.NewGuid().ToString("N")[..8]);
            var tempMc = Path.Combine(tempHome, ".minecraft");
            try
            {
                // Seed the installer with a vanilla version built from the instance
                // folder, so nothing has to be written into the real versions/ area.
                var baseDir = Path.Combine(tempMc, "versions", mcVersion);
                Directory.CreateDirectory(baseDir);
                var instanceJson = Directory.EnumerateFiles(gameDir, "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(file => !Path.GetFileName(file).Equals("instance.json", StringComparison.OrdinalIgnoreCase));
                if (instanceJson == null) throw new Exception("缺少版本 JSON，无法运行 OptiFine 安装器");
                var baseNode = JsonNode.Parse(File.ReadAllText(instanceJson)) as JsonObject ?? throw new Exception("版本 JSON 格式不正确");
                baseNode["id"] = mcVersion;
                File.WriteAllText(Path.Combine(baseDir, mcVersion + ".json"),
                    baseNode.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                var instanceJar = Directory.EnumerateFiles(gameDir, "*.jar", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (instanceJar != null) File.Copy(instanceJar, Path.Combine(baseDir, mcVersion + ".jar"), true);
                File.WriteAllText(Path.Combine(tempMc, "launcher_profiles.json"),
                    "{\"profiles\":{},\"settings\":{},\"version\":3}");

                ProgressService.Detail("运行 optifine.Installer");
                var java = ForgeService.SelectInstallerJava(mcVersion);
                var info = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = java,
                    WorkingDirectory = tempHome,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                info.ArgumentList.Add("-Duser.home=" + tempHome);
                info.ArgumentList.Add("--add-exports");
                info.ArgumentList.Add("cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
                info.ArgumentList.Add("-cp");
                info.ArgumentList.Add(optifineFile);
                info.ArgumentList.Add("optifine.Installer");
                info.Environment["appdata"] = tempHome;
                using var process = System.Diagnostics.Process.Start(info)!;
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var started = DateTime.UtcNow;
                while (!process.HasExited)
                {
                    token.ThrowIfCancellationRequested();
                    if ((DateTime.UtcNow - started).TotalSeconds > 180)
                    {
                        try { process.Kill(true); } catch { }
                        throw new Exception("OptiFine 安装器运行超过 3 分钟");
                    }
                    System.Threading.Thread.Sleep(200);
                }
                if (process.ExitCode != 0) throw new Exception("OptiFine 安装器退出码 " + process.ExitCode);
                var generatedRoot = Path.Combine(tempMc, "versions");
                var generated = Directory.Exists(generatedRoot)
                    ? Directory.GetDirectories(generatedRoot).Select(Path.GetFileName)
                        .FirstOrDefault(name => name != null && name.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
                    : null;
                if (string.IsNullOrWhiteSpace(generated)) throw new Exception("OptiFine 安装器没有生成版本目录");
                CopyTree(Path.Combine(tempMc, "libraries"), Path.Combine(AppPaths.RootDir, "libraries"));
                var targetVersionDir = Path.Combine(AppPaths.RootDir, "versions", generated!);
                CopyTree(Path.Combine(generatedRoot, generated!), targetVersionDir);
                try
                {
                    var merged = LaunchService.LoadVersionChainFrom(generatedRoot, generated!);
                    merged["id"] = generated;
                    merged.Remove("inheritsFrom");
                    File.WriteAllText(Path.Combine(targetVersionDir, generated + ".json"),
                        merged.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    var clientJar = LaunchService.FindClientJarFrom(generatedRoot, generated!);
                    if (clientJar != null)
                        File.Copy(clientJar, Path.Combine(targetVersionDir, generated + ".jar"), true);
                }
                catch { }
                var instanceDir = Path.Combine(AppPaths.InstancesDir, instanceId);
                var instanceFile = Path.Combine(instanceDir, "instance.json");
                var record = JsonNode.Parse(File.ReadAllText(instanceFile)) as JsonObject ?? new JsonObject();
                if (!string.Equals(Path.GetFullPath(gameDir), Path.GetFullPath(targetVersionDir), StringComparison.OrdinalIgnoreCase))
                {
                    CopyTree(gameDir, targetVersionDir);
                    try
                    {
                        var versionsRoot = Path.GetFullPath(Path.Combine(AppPaths.RootDir, "versions"));
                        if (Path.GetFullPath(gameDir).StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase))
                            Directory.Delete(gameDir, true);
                    }
                    catch { }
                }
                record["versionId"] = generated;
                record["gameDir"] = targetVersionDir;
                record["optifineVersion"] = versionId;
                record["selfContained"] = true;
                File.WriteAllText(instanceFile, record.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                BridgeService.NotifyInstances();
                return new JsonObject { ["versionId"] = generated, ["path"] = targetVersionDir };
            }
            finally
            {
                try { Directory.Delete(tempHome, true); } catch { }
            }
        }
    }
}
