using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class ForgeService
    {
        private static readonly object InstallerGate = new();
        private static Process? _installerProcess;
        private static string VersionsDir => Path.Combine(AppPaths.RootDir, "versions");

        private static int Concurrency()
        {
            try { return Math.Clamp(BridgeService.GetSettings()["maxConcurrency"]?.GetValue<int>() ?? 32, 1, 64); }
            catch { return 32; }
        }

        public static void StopInstaller()
        {
            lock (InstallerGate)
            {
                try { if (_installerProcess != null && !_installerProcess.HasExited) _installerProcess.Kill(true); } catch { }
                _installerProcess = null;
            }
        }

        public static async Task<JsonArray> List(string kind, string mcVersion, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(mcVersion)) return new JsonArray();
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (kind == "forge")
            {
                // BMCLAPI includes per-build "modified" timestamps, so sort by
                // release time instead of lexicographic version strings.
                var timed = new List<(string Version, DateTime Modified)>();
                static DateTime ParseModified(string value) =>
                    DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed)
                        ? parsed
                        : DateTime.MinValue;
                foreach (var key in new[] { mcVersion, mcVersion.Replace('-', '_') })
                {
                    try
                    {
                        var json = JsonNode.Parse(await DownloadService.GetText($"https://bmclapi2.bangbang93.com/forge/minecraft/{Uri.EscapeDataString(key)}", token)) as JsonArray;
                        if (json == null) continue;
                        foreach (var item in json)
                        {
                            if (item?["version"]?.GetValue<string>() is not string v) continue;
                            var modified = item["modified"]?.GetValue<string>() ?? "";
                            timed.Add((v, ParseModified(modified)));
                        }
                        if (timed.Count > 0) break;
                    }
                    catch { }
                }
                if (timed.Count > 0)
                {
                    return new JsonArray(timed
                        .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.OrderByDescending(item => item.Modified).First())
                        .OrderByDescending(item => item.Modified)
                        .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                        .Select(item => (JsonNode)new JsonObject
                        {
                            ["version"] = item.Version,
                            ["stable"] = !Regex.IsMatch(item.Version, "-(beta|alpha|rc)", RegexOptions.IgnoreCase),
                            ["mcVersion"] = mcVersion
                        })
                        .ToArray());
                }
                // Fallback: official maven metadata has no per-version time.
                try
                {
                    var xml = await DownloadService.GetTextOfficial("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", token);
                    foreach (Match match in Regex.Matches(xml, "<version>([^<]+)</version>"))
                    {
                        var value = match.Groups[1].Value;
                        var prefix = mcVersion + "-";
                        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            versions.Add(value.Substring(prefix.Length));
                    }
                }
                catch { }
            }
            else
            {
                var legacy = mcVersion == "1.20.1";
                var package = legacy ? "forge" : "neoforge";
                JsonObject? detail;
                try
                {
                    detail = JsonNode.Parse(await DownloadService.GetText($"https://maven.neoforged.net/api/maven/details/releases/net/neoforged/{package}", token)) as JsonObject;
                }
                catch
                {
                    detail = JsonNode.Parse(await DownloadService.GetText($"https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/{package}", token)) as JsonObject;
                }
                if (detail?["files"] is JsonArray files)
                {
                    foreach (var file in files)
                    {
                        if (file?["type"]?.GetValue<string>() != "DIRECTORY") continue;
                        var name = file["name"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (legacy)
                        {
                            if (name!.StartsWith(mcVersion + "-") && name.Contains("-47.")) versions.Add(name);
                        }
                        else
                        {
                            var match = Regex.Match(mcVersion, "^1\\.(\\d+)(?:\\.(\\d+))?");
                            if (!match.Success) continue;
                            var prefix = $"{match.Groups[1].Value}.{match.Groups[2].Value}.";
                            if (name!.StartsWith(prefix)) versions.Add(name);
                        }
                    }
                }
            }
            return new JsonArray(versions
                .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(v => (JsonNode)new JsonObject
                {
                    ["version"] = v,
                    ["stable"] = !Regex.IsMatch(v, "-(beta|alpha|rc)", RegexOptions.IgnoreCase),
                    ["mcVersion"] = mcVersion
                })
                .ToArray());
        }

        public static async Task<JsonObject> Install(string kind, string mcVersion, string? loaderVersion, string? instanceName, bool createInstance, string? optifineVersion = null, CancellationToken token = default)
        {
            ProgressService.Begin($"安装 {kind} {loaderVersion ?? ""} ({mcVersion})");
            var cleanupBefore = VersionCleanup.Snapshot();
            try
            {
            // Old launcher: if this exact loader version is already installed,
            // reuse the profile instead of running the full installer again.
            if (!string.IsNullOrWhiteSpace(loaderVersion))
            {
                var reused = FindInstalledLoader(kind, mcVersion, loaderVersion!);
                if (reused != null)
                {
                    var reuseStep = ProgressService.Step("检查已安装版本", 1, reused);
                    ProgressService.UpdateAt(reuseStep, 1, "已安装，跳过重复安装");
                    string? reuseInstance = null;
                    if (createInstance)
                        reuseInstance = MinecraftInstallService.CreateInstance(reused, instanceName ?? reused, kind, loaderVersion!, mcVersion, cleanupBefore);
                    ProgressService.Complete();
                    return new JsonObject { ["instanceId"] = reuseInstance, ["versionId"] = reused, ["loaderVersion"] = loaderVersion };
                }
            }
            ProgressService.Step($"获取 {(kind == "forge" ? "Forge" : "NeoForge")} 版本列表", 1, mcVersion);
            var list = await List(kind, mcVersion, token);
            var versions = list.OfType<JsonObject>().Select(item => item["version"]?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList();
            if (versions.Count == 0) throw new Exception($"没有找到适用于 {mcVersion} 的 {kind} 版本");
            var chosen = (!string.IsNullOrWhiteSpace(loaderVersion) ? versions.FirstOrDefault(v => v == loaderVersion) : null) ?? versions[0];
            ProgressService.Update(1, "已选择 " + chosen);

            var baseTask = MinecraftInstallService.InstallVanilla(mcVersion, false, null, token, true, false);

            var installerPath = Path.Combine(AppPaths.SettingsDir, "cache", "installers", $"{kind}-{chosen}-installer.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            var installerUrls = new List<string>();
            if (kind == "forge")
            {
                installerUrls.Add($"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{chosen}/forge-{mcVersion}-{chosen}-installer.jar");
                // Forge 1.7.10 and other legacy builds use the
                // <mc>-<loader>-<mc> directory/artifact name.
                if (!chosen.EndsWith("-" + mcVersion, StringComparison.OrdinalIgnoreCase))
                {
                    var legacy = $"{mcVersion}-{chosen}-{mcVersion}";
                    installerUrls.Add($"https://maven.minecraftforge.net/net/minecraftforge/forge/{legacy}/forge-{legacy}-installer.jar");
                }
            }
            else
            {
                installerUrls.Add(chosen.StartsWith(mcVersion + "-")
                    ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{chosen}/forge-{chosen}-installer.jar"
                    : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{chosen}/neoforge-{chosen}-installer.jar");
            }
            var installerStep = ProgressService.Step("下载安装器", 1, installerUrls[0]);
            var installerTask = Task.Run(async () =>
            {
                Exception? last = null;
                foreach (var downloadUrl in installerUrls)
                {
                    try
                    {
                        await DownloadService.Download(downloadUrl, installerPath, null, 0, token, (received, total) =>
                        {
                            if (total > 0) ProgressService.TotalAt(installerStep, total);
                            ProgressService.ProgressAt(installerStep, received, (received / 1024d / 1024d).ToString("0.0") + " MB" + (total > 0 ? " / " + (total / 1024d / 1024d).ToString("0.0") + " MB" : ""));
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        ProgressService.Detail("切换安装器下载源");
                    }
                }
                throw last ?? new Exception("安装器下载失败");
            });
            await Task.WhenAll(baseTask, installerTask);
            ProgressService.TotalAt(installerStep, 1);
            ProgressService.UpdateAt(installerStep, 1, "安装器已下载");
            EnsureLauncherProfiles();
            if (kind == "forge" && IsLegacyForgeInstaller(installerPath))
            {
                await baseTask;
                ProgressService.Step("安装旧版 Forge", 1, chosen);
                var legacyVersion = await InstallLegacyForge(installerPath, mcVersion, chosen, token);
                string? legacyInstance = null;
                if (createInstance)
                    legacyInstance = MinecraftInstallService.CreateInstance(legacyVersion, instanceName ?? legacyVersion, kind, chosen, mcVersion, cleanupBefore);
                ProgressService.Update(1, "旧版 Forge 已安装");
                ProgressService.Complete();
                return new JsonObject { ["instanceId"] = legacyInstance, ["versionId"] = legacyVersion, ["loaderVersion"] = chosen };
            }
            try { await PrefetchInstallerLibraries(installerPath, mcVersion, token); }
            catch (Exception ex) { GameEvents.Emit("log", new JsonObject { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["source"] = "launcher", ["level"] = "warning", ["text"] = "Forge 依赖预下载失败：" + ex.Message }); }

            ProgressService.Step("运行安装器", 1, chosen);
            var installed = await RunInstaller(kind, mcVersion, chosen, installerPath, token);
            ProgressService.Update(1, "安装器完成");
            try { File.Delete(installerPath); } catch { }

            (string File, string FileName)? optifine = null;
            if (kind == "forge" && !string.IsNullOrWhiteSpace(optifineVersion))
            {
                ProgressService.Step("下载 OptiFine", 1, optifineVersion);
                optifine = await OptiFineService.DownloadFile(mcVersion, optifineVersion, token);
                ProgressService.Update(1, "OptiFine 已下载");
            }

            string? instanceId = null;
            if (createInstance)
            {
                instanceId = MinecraftInstallService.CreateInstance(installed, instanceName ?? installed, kind, chosen, mcVersion, cleanupBefore);
                if (optifine != null)
                {
                    var instance = BridgeService.GetInstance(instanceId) as JsonObject;
                    var gameDir = instance?["gameDir"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(gameDir))
                    {
                        var modsDir = Path.Combine(gameDir!, "mods");
                        Directory.CreateDirectory(modsDir);
                        File.Copy(optifine.Value.File, Path.Combine(modsDir, optifine.Value.FileName), true);
                        ProgressService.Detail("OptiFine 已放入实例 mods");
                    }
                }
            }
            ProgressService.Complete();
            return new JsonObject { ["instanceId"] = instanceId, ["versionId"] = installed, ["loaderVersion"] = chosen };
            }
            catch (Exception ex)
            {
                ProgressService.Fail(ex.Message);
                throw;
            }
        }

        private static bool IsLegacyForgeInstaller(string installerPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(installerPath);
                return zip.GetEntry("version.json") == null && zip.GetEntry("install_profile.json") != null;
            }
            catch { return false; }
        }

        // Forge 1.7.10 and similar "simple installer" builds do not understand
        // --installClient. Their profile already contains the complete version
        // JSON and a universal jar, so install it directly like PCL does.
        private static async Task<string> InstallLegacyForge(string installerPath, string mcVersion, string loaderVersion, CancellationToken token)
        {
            using var zip = ZipFile.OpenRead(installerPath);
            var profileEntry = zip.GetEntry("install_profile.json") ?? throw new Exception("旧版 Forge 安装器缺少 install_profile.json");
            JsonObject profile;
            using (var stream = profileEntry.Open())
            using (var reader = new StreamReader(stream))
                profile = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject ?? throw new Exception("旧版 Forge install_profile 格式错误");
            var versionInfo = profile["versionInfo"] as JsonObject ?? throw new Exception("旧版 Forge 安装器缺少 versionInfo");
            var version = (JsonObject)JsonNode.Parse(versionInfo.ToJsonString())!;
            var versionId = version["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(versionId)) versionId = $"{mcVersion}-forge-{loaderVersion}";
            var versionDir = Path.Combine(VersionsDir, versionId!);
            Directory.CreateDirectory(versionDir);

            var libsDir = Path.Combine(AppPaths.RootDir, "libraries");
            var universalName = profile["install"]?["filePath"]?.GetValue<string>();
            var universalEntry = string.IsNullOrWhiteSpace(universalName) ? null : zip.GetEntry(universalName!);
            if (version["libraries"] is JsonArray libraries)
            {
                foreach (var item in libraries.OfType<JsonObject>())
                {
                    var name = item["name"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var isForgeUniversal = name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase)
                        && name.Split(':').Length == 3;
                    if (isForgeUniversal) name += ":universal";
                    var relative = MavenPath(name);
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    var target = Path.Combine(libsDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (isForgeUniversal && universalEntry != null)
                    {
                        item["name"] = name;
                        universalEntry.ExtractToFile(target, true);
                        continue;
                    }
                    var baseUrl = item["url"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://bmclapi2.bangbang93.com/maven/";
                    if (!File.Exists(target) || new FileInfo(target).Length == 0)
                        await DownloadService.Download(baseUrl!.TrimEnd('/') + "/" + relative, target, null, 0, token);
                }
            }
            File.WriteAllText(Path.Combine(versionDir, versionId + ".json"), version.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return versionId!;
        }

        private static async Task PrefetchInstallerLibraries(string installerPath, string mcVersion, CancellationToken token)
        {
            var librariesDir = Path.Combine(AppPaths.RootDir, "libraries");
            var tasks = new List<(string Url, string Path, string? Sha1)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? mappingsCoord = null;

            void Add(string? name, string? baseUrl, JsonNode? artifact)
            {
                string? path = artifact?["path"]?.GetValue<string>();
                string? url = artifact?["url"]?.GetValue<string>();
                var sha1 = artifact?["sha1"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(name))
                {
                    path = MavenPath(name!);
                    if (string.IsNullOrWhiteSpace(url)) url = (baseUrl ?? "https://maven.minecraftforge.net/").TrimEnd('/') + "/" + path;
                }
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url)) return;
                var full = Path.Combine(librariesDir, path!.Replace('/', Path.DirectorySeparatorChar));
                if (!seen.Add(full)) return;
                tasks.Add((url!, full, sha1));
            }

            using (var zip = ZipFile.OpenRead(installerPath))
            {
                foreach (var name in new[] { "install_profile.json", "version.json" })
                {
                    var entry = zip.GetEntry(name);
                    if (entry == null) continue;
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject;
                    mappingsCoord ??= json?["data"]?["MOJMAPS"]?["client"]?.GetValue<string>();
                    if (json?["libraries"] is JsonArray libraries)
                    {
                        foreach (var item in libraries)
                        {
                            if (item is not JsonObject library) continue;
                            Add(library["name"]?.GetValue<string>(), library["url"]?.GetValue<string>(), library["downloads"]?["artifact"]);
                        }
                    }
                    if (json?["processors"] is JsonArray processors)
                    {
                        foreach (var item in processors)
                        {
                            if (item is not JsonObject processor) continue;
                            var coords = new List<string?> { processor["jar"]?.GetValue<string>() };
                            if (processor["classpath"] is JsonArray classpath)
                                foreach (var classEntry in classpath) coords.Add(classEntry?.GetValue<string>());
                            foreach (var coord in coords)
                            {
                                if (string.IsNullOrWhiteSpace(coord)) continue;
                                Add(coord, "https://maven.minecraftforge.net/", null);
                            }
                        }
                    }
                }
            }

            ProgressService.Step("预下载 Forge 依赖", tasks.Count, tasks.Count + " 个文件");
            var done = 0;
            long downloadedBytes = 0;
            using var gate = new SemaphoreSlim(Concurrency());
            await Task.WhenAll(tasks.Select(async item =>
            {
                await gate.WaitAsync(token);
                try
                {
                    long last = 0;
                    await DownloadService.Download(item.Url, item.Path, item.Sha1, 0, token, (received, _) =>
                    {
                        var delta = received - last;
                        if (delta <= 0) return;
                        last = received;
                        var aggregate = System.Threading.Interlocked.Add(ref downloadedBytes, delta);
                        ProgressService.Progress(System.Threading.Volatile.Read(ref done),
                            $"{System.Threading.Volatile.Read(ref done)}/{tasks.Count} · {MinecraftInstallService.FormatBytes(aggregate)} · {Path.GetFileName(item.Path)}");
                    });
                }
                catch { }
                finally
                {
                    gate.Release();
                    var current = System.Threading.Interlocked.Increment(ref done);
                    ProgressService.Progress(current,
                        $"{current}/{tasks.Count} · {MinecraftInstallService.FormatBytes(System.Threading.Interlocked.Read(ref downloadedBytes))} · {Path.GetFileName(item.Path)}");
                }
            }));

            // Old launcher also pre-downloaded the client mappings referenced by
            // MOJMAPS; otherwise the installer runs DOWNLOAD_MOJMAPS again.
            if (!string.IsNullOrWhiteSpace(mappingsCoord))
            {
                try
                {
                    var relative = MavenPath(mappingsCoord.Trim('[', ']'));
                    if (!string.IsNullOrWhiteSpace(relative))
                    {
                        var dest = Path.Combine(librariesDir, relative!.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(dest))
                        {
                            var manifest = await VersionService.Manifest(false);
                            var summary = (manifest["versions"] as JsonArray)?.OfType<JsonObject>()
                                .FirstOrDefault(item => item["id"]?.GetValue<string>() == mcVersion);
                            var versionUrl = summary?["url"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(versionUrl))
                            {
                                var versionJson = JsonNode.Parse(await DownloadService.GetTextOfficial(versionUrl!, token)) as JsonObject;
                                var mappingsUrl = versionJson?["downloads"]?["client_mappings"]?["url"]?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(mappingsUrl))
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                                    await DownloadService.Download(mappingsUrl!, dest, null, 0, token);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    GameEvents.Emit("log", new JsonObject
                    {
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["source"] = "launcher",
                        ["level"] = "warning",
                        ["text"] = "预下载 Mappings 失败，安装器会重试：" + ex.Message
                    });
                }
            }
        }

        private static string? FindInstalledLoader(string kind, string mcVersion, string loaderVersion)
        {
            if (!Directory.Exists(VersionsDir)) return null;
            var needle = kind == "forge" ? "net.minecraftforge" : "net.neoforged";
            foreach (var dir in Directory.GetDirectories(VersionsDir))
            {
                var id = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(id) || !id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)) continue;
                var jsonPath = Path.Combine(dir, id + ".json");
                if (!File.Exists(jsonPath)) continue;
                try
                {
                    var json = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
                    if (json == null) continue;
                    var parent = json["inheritsFrom"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(parent) && !string.Equals(parent, mcVersion, StringComparison.OrdinalIgnoreCase)) continue;
                    if (json["libraries"] is not JsonArray libraries) continue;
                    var text = libraries.ToJsonString();
                    if (!text.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                    if (kind == "forge" && text.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kind == "neoforge" && !text.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase)) continue;
                    var paths = libraries.OfType<JsonObject>()
                        .Select(lib => lib["downloads"]?["artifact"]?["path"]?.GetValue<string>()
                            ?? (lib["name"]?.GetValue<string>() is string name ? MavenPath(name) : null))
                        .Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
                    if (paths.Count == 0) continue;
                    var missing = paths.Count(path => !File.Exists(Path.Combine(AppPaths.RootDir, "libraries", path!.Replace('/', Path.DirectorySeparatorChar))));
                    if (missing * 5 <= paths.Count) return id;
                }
                catch { }
            }
            return null;
        }

        private static async Task<string> RunInstaller(string kind, string mcVersion, string loaderVersion, string installerPath, CancellationToken token)
        {
            var java = SelectInstallerJava(mcVersion);
            EnsureLauncherProfiles();
            var before = Directory.Exists(VersionsDir)
                ? Directory.GetDirectories(VersionsDir).Select(Path.GetFileName).Where(name => name != null).ToHashSet(StringComparer.OrdinalIgnoreCase)!
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var logFile = Path.Combine(AppPaths.SettingsDir, "cache", "installers", "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            var output = new System.Text.StringBuilder();
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                // Old launcher: attempt 1 mirror, attempt 2 official, attempt 3 mirror.
                var mirror = attempt != 2;
                var info = new ProcessStartInfo
                {
                    FileName = java,
                    WorkingDirectory = AppPaths.RootDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // Old launcher decoded the installer log as UTF-8. Java 17+
                    // writes UTF-8 here even on Chinese Windows.
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                info.ArgumentList.Add("-Djava.awt.headless=true");
                info.ArgumentList.Add("-jar");
                info.ArgumentList.Add(installerPath);
                info.ArgumentList.Add("--installClient");
                info.ArgumentList.Add(AppPaths.RootDir);
                if (mirror) { info.ArgumentList.Add("--mirror"); info.ArgumentList.Add("https://bmclapi2.bangbang93.com/maven/"); }
                try
                {
                    try { File.WriteAllText(logFile, ""); } catch { }
                    long lastOutputTicks = DateTime.UtcNow.Ticks;
                    void OnLine(string? line)
                    {
                        if (line == null) return;
                        Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                        try { lock (output) File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
                        var text = line.Trim();
                        if (!Regex.IsMatch(text, "^(Extracting json|Downloading libraries|Building Processors|Task:|Downloading library from|Injecting profile|Successfully installed)", RegexOptions.IgnoreCase)
                            && !Regex.IsMatch(text, "Exception|Error|Failed|WARN", RegexOptions.IgnoreCase)) return;
                        lock (output) output.AppendLine(text);
                        // Old launcher only forwarded the filtered stage/error lines
                        // to the console; forwarding every class line starved the UI.
                        try
                        {
                            GameEvents.Emit("log", new JsonObject
                            {
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                ["source"] = "installer",
                                ["level"] = "info",
                                ["text"] = text
                            });
                        }
                        catch { }
                        ProgressService.Detail(text);
                    }
                    using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
                    process.OutputDataReceived += (_, e) => OnLine(e.Data);
                    process.ErrorDataReceived += (_, e) => OnLine(e.Data);
                    process.Start();
                    lock (InstallerGate) _installerProcess = process;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var attemptStarted = DateTime.UtcNow;
                    while (!process.HasExited)
                    {
                        await Task.Delay(400, token);
                        var last = new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc);
                        var idle = (int)(DateTime.UtcNow - last).TotalSeconds;
                        var elapsed = (int)(DateTime.UtcNow - attemptStarted).TotalSeconds;
                        if (idle > 90)
                        {
                            try { process.Kill(true); } catch { }
                            throw new Exception($"安装器已经 {idle} 秒没有输出，已停止。将自动切换镜像/下载源重试。");
                        }
                        if (elapsed > 8 * 60)
                        {
                            try { process.Kill(true); } catch { }
                            throw new Exception("安装器运行超过 8 分钟，已停止。请检查网络或切换下载源后重试。");
                        }
                        ProgressService.Detail($"{(mirror ? "镜像" : "官方源")} · 第 {attempt} 次尝试 · 已等待 {elapsed}s");
                    }
                    await Task.Delay(200, token);
                    if (process.ExitCode == 0) break;
                    lastError = new Exception("Forge 安装器退出码 " + process.ExitCode);
                    lock (output) output.AppendLine("安装器退出码：" + process.ExitCode);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 3) await Task.Delay(2000, token);
                }
            }
            if (lastError != null && !Directory.Exists(VersionsDir)) throw lastError;
            var created = Directory.Exists(VersionsDir)
                ? Directory.GetDirectories(VersionsDir).Select(Path.GetFileName).Where(name => name != null && !before.Contains(name!)).FirstOrDefault()
                : null;
            created ??= Directory.Exists(VersionsDir)
                ? Directory.GetDirectories(VersionsDir).Select(Path.GetFileName).FirstOrDefault(name => name != null && name.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase))
                : null;
            if (string.IsNullOrWhiteSpace(created))
            {
                foreach (var guess in new[]
                {
                    $"{mcVersion}-forge-{loaderVersion}",
                    $"{mcVersion}-neoforge-{loaderVersion}",
                    $"neoforge-{loaderVersion}",
                    $"{mcVersion}-forge-{loaderVersion.Split('-')[0]}"
                })
                {
                    if (!Directory.Exists(Path.Combine(VersionsDir, guess))) continue;
                    created = guess;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(created))
                throw new Exception("安装器没有生成版本配置。" + (output.Length > 1200 ? output.ToString()[^1200..] : output.ToString()));
            return created!;
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static bool ReadInstallerLog(string path, ref long offset, System.Text.StringBuilder output)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                if (info.Length <= offset) return false;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var text = reader.ReadToEnd();
                offset = info.Length;
                var happened = text.Length > 0;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var keep = Regex.IsMatch(line, "^(Extracting json|Downloading libraries|Building Processors|Task:|Downloading library from|Injecting profile|Successfully installed)", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(line, "Exception|Error|Failed|WARN", RegexOptions.IgnoreCase);
                    if (!keep) continue;
                    output.AppendLine(line);
                    ProgressService.Detail(line);
                    happened = true;
                }
                return happened;
            }
            catch { return false; }
        }

        private static string? MavenPath(string name)
        {
            var at = name.IndexOf('@');
            var ext = "jar";
            if (at >= 0) { ext = name.Substring(at + 1); name = name.Substring(0, at); }
            var parts = name.Split(':');
            if (parts.Length < 3) return null;
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : "";
            return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
        }

        private static bool IsLoaderVersion(string id, string mcVersion, string kind)
        {
            if (!id.Contains(mcVersion, StringComparison.OrdinalIgnoreCase)) return false;
            if (kind == "forge") return id.Contains("forge", StringComparison.OrdinalIgnoreCase);
            return id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) || id.Contains("forge", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<bool> WaitForExit(Process process, TimeSpan timeout, CancellationToken token)
        {
            var task = process.WaitForExitAsync(token);
            var completed = await Task.WhenAny(task, Task.Delay(timeout, token));
            return completed == task && process.HasExited;
        }

        private static string SelectInstallerJava(string mcVersion)
        {
            var required = VersionService.JavaRequirement(mcVersion);
            var list = JavaService.Scan().OfType<JsonObject>()
                .Select(item => new { Path = item["path"]?.GetValue<string>() ?? "", Major = item["major"]?.GetValue<int>() ?? 0 })
                .Where(item => item.Path.Length > 0 && File.Exists(item.Path))
                .OrderBy(item => item.Major)
                .ToList();
            return list.FirstOrDefault(item => item.Major == required)?.Path
                ?? list.LastOrDefault(item => item.Major >= required)?.Path
                ?? list.LastOrDefault()?.Path
                ?? throw new Exception("未找到可用的 Java");
        }

        private static void EnsureLauncherProfiles()
        {
            var file = Path.Combine(AppPaths.RootDir, "launcher_profiles.json");
            if (File.Exists(file)) return;
            var json = new JsonObject
            {
                ["profiles"] = new JsonObject(),
                ["settings"] = new JsonObject { ["language"] = "zh_CN" },
                ["version"] = 3
            };
            File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
