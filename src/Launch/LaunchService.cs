using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class LaunchService
    {
        private static readonly object Gate = new();
        private static Process? _process;
        private static JsonObject _state = new JsonObject { ["state"] = "idle" };
        private static System.Threading.CancellationTokenSource? _logWatch;
        private static volatile bool _suppressLogs;
        private static long _startedAtMs;
        private static string _runningInstanceId = "";
        private static string? _lastLogPath;

        public static JsonObject State()
        {
            lock (Gate) return (JsonObject)JsonNode.Parse(_state.ToJsonString())!;
        }

        private static string AdoptOfflineUuid(string current, string gameDir)
        {
            try
            {
                var mapped = ReadUuidMap(gameDir);
                if (mapped != null)
                {
                    SyncPlayerData(gameDir, mapped.Value.Owner, mapped.Value.Offline, false);
                    MigrateFtbTeams(gameDir, mapped.Value.Owner, mapped.Value.Offline);
                    return mapped.Value.Owner;
                }
                var saves = Path.Combine(gameDir, "saves");
                if (!Directory.Exists(saves)) return current;
                var candidates = new List<(string Uuid, DateTime Time)>();
                foreach (var world in Directory.GetDirectories(saves))
                {
                    var playerData = Path.Combine(world, "playerdata");
                    if (!Directory.Exists(playerData)) continue;
                    foreach (var file in Directory.GetFiles(playerData, "*.dat"))
                    {
                        var uuid = Path.GetFileNameWithoutExtension(file).Replace("-", "");
                        if (uuid.Length != 32 || !uuid.All(Uri.IsHexDigit)) continue;
                        candidates.Add((uuid.ToLowerInvariant(), File.GetLastWriteTimeUtc(file)));
                    }
                }
                if (candidates.Count == 0) return current;
                var normalized = current.Replace("-", "").ToLowerInvariant();
                var distinct = candidates
                    .GroupBy(item => item.Uuid)
                    .Select(group => group.OrderByDescending(item => item.Time).First())
                    .ToList();
                // Prefer the UUID that already owns the world, not the offline UUID
                // that a previous failed offline launch may have just created.
                var owner = distinct
                    .Where(item => !string.Equals(item.Uuid, normalized, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Time)
                    .FirstOrDefault();
                if (owner.Uuid != null)
                {
                    MigrateOfflinePlayerData(gameDir, owner.Uuid, normalized);
                    return owner.Uuid;
                }
                return distinct.OrderByDescending(item => item.Time).First().Uuid;
            }
            catch { return current; }
        }

        private static (string Owner, string Offline)? ReadUuidMap(string gameDir)
        {
            try
            {
                var saves = Path.Combine(gameDir, "saves");
                foreach (var name in new[] { ".aurora-offline-uuid-map" })
                {
                    var file = Path.Combine(saves, name);
                    if (!File.Exists(file)) continue;
                    string? owner = null, offline = null;
                    foreach (var line in File.ReadAllLines(file))
                    {
                        if (line.StartsWith("owner=")) owner = line[6..].Trim();
                        else if (line.StartsWith("offline=")) offline = line[8..].Trim();
                    }
                    if (owner != null && offline != null) return (owner, offline);
                }
            }
            catch { }
            return null;
        }

        private static void MigrateOfflinePlayerData(string gameDir, string ownerUuid, string offlineUuid)
        {
            try
            {
                var saves = Path.Combine(gameDir, "saves");
                if (!Directory.Exists(saves)) return;
                var owner = Guid.Parse(ownerUuid).ToString();
                var offline = Guid.Parse(offlineUuid).ToString();
                var map = Path.Combine(saves, ".aurora-offline-uuid-map");
                var firstRun = !File.Exists(map);
                if (firstRun)
                {
                    // First migration: the online player is the source of truth.
                    SyncPlayerData(gameDir, owner, offline, true);
                    File.WriteAllText(map, "owner=" + owner + "\noffline=" + offline + "\n");
                }
                else
                {
                    SyncPlayerData(gameDir, owner, offline, false);
                }
                MigrateFtbTeams(gameDir, owner, offline);
            }
            catch { }
        }

        private static void MigrateFtbTeams(string gameDir, string ownerUuid, string offlineUuid)
        {
            try
            {
                var saves = Path.Combine(gameDir, "saves");
                if (!Directory.Exists(saves)) return;
                var owner = Guid.Parse(ownerUuid).ToString();
                var offline = Guid.Parse(offlineUuid).ToString();
                foreach (var world in Directory.GetDirectories(saves))
                {
                    var playerDir = Path.Combine(world, "ftbteams", "player");
                    if (!Directory.Exists(playerDir)) continue;
                    var ownerFile = Path.Combine(playerDir, owner + ".snbt");
                    var offlineFile = Path.Combine(playerDir, offline + ".snbt");
                    try
                    {
                        if (File.Exists(ownerFile))
                        {
                            // FTB Teams looks up the player by the team file name.
                            // Move the owner's personal team to the offline player's
                            // name, keeping the original team id in the file. This
                            // avoids spawning a second empty team/island.
                            File.Copy(ownerFile, ownerFile + ".aurora-owner-backup", true);
                            File.Move(ownerFile, offlineFile, true);
                            var text = File.ReadAllText(offlineFile);
                            if (!text.Contains(offline, StringComparison.OrdinalIgnoreCase))
                            {
                                var marker = "ranks: {";
                                var index = text.IndexOf(marker, StringComparison.Ordinal);
                                if (index >= 0)
                                    text = text.Insert(index + marker.Length,
                                        "\n\t\t" + offline + ": \"owner\"");
                            }
                            File.WriteAllText(offlineFile, text);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void RestoreFtbTeams(string gameDir, string ownerUuid, string offlineUuid)
        {
            try
            {
                var saves = Path.Combine(gameDir, "saves");
                if (!Directory.Exists(saves)) return;
                var owner = Guid.Parse(ownerUuid).ToString();
                var offline = Guid.Parse(offlineUuid).ToString();
                foreach (var world in Directory.GetDirectories(saves))
                {
                    var playerDir = Path.Combine(world, "ftbteams", "player");
                    if (!Directory.Exists(playerDir)) continue;
                    var ownerFile = Path.Combine(playerDir, owner + ".snbt");
                    var offlineFile = Path.Combine(playerDir, offline + ".snbt");
                    try
                    {
                        if (!File.Exists(ownerFile) && File.Exists(offlineFile))
                            File.Copy(offlineFile, ownerFile, true);
                        if (File.Exists(offlineFile) && File.Exists(ownerFile))
                            File.Move(offlineFile, offlineFile + ".aurora-backup", true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void SyncPlayerData(string gameDir, string ownerUuid, string offlineUuid, bool preferOwner)
        {
            try
            {
                var owner = Guid.Parse(ownerUuid).ToString();
                var offline = Guid.Parse(offlineUuid).ToString();
                var saves = Path.Combine(gameDir, "saves");
                if (!Directory.Exists(saves)) return;
                foreach (var world in Directory.GetDirectories(saves))
                {
                    foreach (var (folder, extension) in new[]
                    {
                        ("playerdata", ".dat"),
                        ("playerdata", ".dat_old"),
                        ("playerdata", ".cosarmor"),
                        ("stats", ".json"),
                        ("advancements", ".json")
                    })
                    {
                        var ownerFile = Path.Combine(world, folder, owner + extension);
                        var offlineFile = Path.Combine(world, folder, offline + extension);
                        var ownerExists = File.Exists(ownerFile);
                        var offlineExists = File.Exists(offlineFile);
                        try
                        {
                            if (!ownerExists && !offlineExists) continue;
                            var ownerTime = ownerExists ? File.GetLastWriteTimeUtc(ownerFile) : DateTime.MinValue;
                            var offlineTime = offlineExists ? File.GetLastWriteTimeUtc(offlineFile) : DateTime.MinValue;
                            var ownerWins = preferOwner || !offlineExists || (ownerExists && ownerTime >= offlineTime);
                            if (ownerWins && ownerExists)
                            {
                                if (offlineExists) File.Copy(offlineFile, offlineFile + ".aurora-backup", true);
                                File.Copy(ownerFile, offlineFile, true);
                            }
                            else if (offlineExists)
                            {
                                if (ownerExists) File.Copy(ownerFile, ownerFile + ".aurora-backup", true);
                                File.Copy(offlineFile, ownerFile, true);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void ApplyFirstLaunchLanguage(JsonObject instance, string instanceId, string gameDir, JsonObject settings)
        {
            if (settings["firstLaunchChinese"]?.GetValue<bool>() != true) return;
            if ((instance["playCount"]?.GetValue<int>() ?? 0) != 0) return;
            if (instance["languageInitialized"]?.GetValue<bool>() == true) return;
            try
            {
                var file = Path.Combine(gameDir, "options.txt");
                var lines = new List<string>();
                if (File.Exists(file))
                {
                    try { lines.AddRange(File.ReadAllLines(file).Select(line => line.TrimEnd())); } catch { }
                }
                lines.RemoveAll(line => line.TrimStart().StartsWith("lang:", StringComparison.Ordinal));
                lines.Add("lang:zh_cn");
                File.WriteAllText(file, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
                var instanceFile = Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");
                if (File.Exists(instanceFile))
                {
                    var json = JsonNode.Parse(File.ReadAllText(instanceFile)) as JsonObject;
                    if (json != null)
                    {
                        json["languageInitialized"] = true;
                        File.WriteAllText(instanceFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            }
            catch { }
        }

        public static async Task<JsonObject> Start(JsonObject options)
        {
            ProgressService.Begin("启动游戏");
            lock (Gate)
            {
                if (_process != null && !_process.HasExited)
                    return new JsonObject { ["ok"] = false, ["error"] = "已有游戏正在运行" };
            }

            try
            {
                ProgressService.Step("准备启动信息", 1, "读取实例与账户");
                var instanceId = options["instanceId"]?.GetValue<string>() ?? "";
                var instance = FindInstance(instanceId) ?? throw new Exception("实例不存在");
                var versionId = instance["versionId"]?.GetValue<string>() ?? throw new Exception("实例没有关联版本");
                var version = LoadVersionChain(versionId);
                var settings = BridgeService.GetSettings();

                ProgressService.Update(1, "实例信息已读取");
                var account = AccountService.Active() as JsonObject ?? throw new Exception("请先添加一个账户");
                var username = account["username"]?.GetValue<string>() ?? "Player";
                var uuid = account["uuid"]?.GetValue<string>() ?? "";
                var accessToken = account["accessToken"]?.GetValue<string>() ?? "0";

                var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", versionId);
                Directory.CreateDirectory(gameDir);
                if (account["type"]?.GetValue<string>() == "offline")
                {
                    // PCL keeps each profile's own UUID; an offline profile is a
                    // different player and must never rewrite a Microsoft world.
                    // Use its UUID as the token, exactly like PCL's legacy login.
                    accessToken = uuid;
                }
                ApplyFirstLaunchLanguage(instance, instanceId, gameDir, settings);

                var requiredJava = version["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8;
                var (maxJava, maxJava8Build) = JavaLimits(instance, requiredJava);
                var java = SelectJava(instance, settings, requiredJava, maxJava, maxJava8Build)
                    ?? throw new Exception(requiredJava <= 8
                        ? $"当前版本的 Forge 需要 Java 8" + (maxJava8Build > 0 ? $"（8u{maxJava8Build} 及更早）" : "") +
                          "，或更高兼容版本。请到 设置 → Java 下载合适的 Java，或选择 PCL/HMCL 的 mojang-jre-legacy（8u51）"
                        : $"未找到满足 Java {requiredJava} 或更高版本的运行环境");
                if (!File.Exists(java)) throw new Exception("Java 路径不存在：" + java);
                ProgressService.Step("检查游戏文件", 1, "客户端与依赖库");
                await EnsureClientJar(version, versionId, System.Threading.CancellationToken.None);
                var classpath = BuildClasspath(version, gameDir, out var nativesDir);
                ProgressService.Update(1, "游戏文件已检查");
                EmitLauncher($"启动 {versionId}，Java：{java}");
                var assetsIndex = version["assetIndex"]?["id"]?.GetValue<string>() ?? version["assets"]?.GetValue<string>() ?? "legacy";
                ProgressService.Step("检查资源文件", 1, assetsIndex);
                await MinecraftInstallService.EnsureAssets(version);
                ProgressService.Update(1, "资源文件已就绪");
                var memory = instance["memory"]?.GetValue<int>() ?? (settings["defaultMemory"]?.GetValue<int>() ?? 0);
                if (memory <= 0)
                {
                    var loader = instance["loader"]?.GetValue<string>() ?? "vanilla";
                    memory = SystemService.AutoMemoryMB(gameDir, null, loader == "optifine");
                }

                var releaseTime = version["releaseTime"]?.GetValue<string>() ?? "";
                var features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["has_custom_resolution"] = true,
                    ["is_demo_user"] = false,
                    ["has_quick_plays_support"] = string.CompareOrdinal(releaseTime, "2023-04-04") >= 0,
                    ["is_quick_play_singleplayer"] = false,
                    ["is_quick_play_multiplayer"] = false,
                    ["is_quick_play_realms"] = false
                };
                var replacements = new Dictionary<string, string>
                {
                    ["${auth_player_name}"] = username,
                    ["${version_name}"] = versionId,
                    ["${game_directory}"] = gameDir,
                    ["${assets_root}"] = Path.Combine(AppPaths.RootDir, "assets"),
                    ["${assets_index_name}"] = assetsIndex,
                    ["${auth_uuid}"] = uuid,
                    ["${auth_access_token}"] = accessToken,
                    ["${access_token}"] = accessToken,
                    ["${auth_session}"] = accessToken,
                    // PCL always passes "msa". Using "legacy" makes the integrated
                    // server recompute the UUID from the username, so an offline
                    // account can never open a world created by a Microsoft account.
                    ["${user_type}"] = "msa",
                    ["${libraries_directory}"] = Path.Combine(AppPaths.RootDir, "libraries"),
                    ["${game_assets}"] = Path.Combine(AppPaths.RootDir, "assets", "virtual", "legacy"),
                    ["${version_type}"] = version["type"]?.GetValue<string>() ?? "release",
                    ["${natives_directory}"] = nativesDir,
                    ["${launcher_name}"] = "AuroraLauncher",
                    ["${launcher_version}"] = "2.1.3",
                    ["${classpath}"] = classpath,
                    ["${classpath_separator}"] = Path.PathSeparator.ToString(),
                    ["${library_directory}"] = Path.Combine(AppPaths.RootDir, "libraries"),
                    ["${resolution_width}"] = (options["width"]?.GetValue<int>() ?? instance["width"]?.GetValue<int>() ?? 854).ToString(),
                    ["${resolution_height}"] = (options["height"]?.GetValue<int>() ?? instance["height"]?.GetValue<int>() ?? 480).ToString(),
                    ["${clientid}"] = "",
                    ["${auth_xuid}"] = "",
                    ["${user_properties}"] = "{}",
                    ["${quickPlayPath}"] = Path.Combine(AppPaths.SettingsDir, "quickplay.log"),
                    ["${quickPlaySingleplayer}"] = "",
                    ["${quickPlayMultiplayer}"] = "",
                    ["${quickPlayRealms}"] = ""
                };

                var args = new List<string>();
                var jvmArgs = ReadArguments(version["arguments"]?["jvm"] as JsonArray, features);
                if (jvmArgs.Count == 0)
                {
                    jvmArgs.Add("-Djava.library.path=${natives_directory}");
                    jvmArgs.Add("-cp");
                    jvmArgs.Add("${classpath}");
                }
                void AddJvm(string prefix, string value)
                {
                    if (!jvmArgs.Any(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        jvmArgs.Add(value);
                }
                AddJvm("-Dfml.ignoreInvalidMinecraftCertificates", "-Dfml.ignoreInvalidMinecraftCertificates=true");
                AddJvm("-Dfml.ignorePatchDiscrepancies", "-Dfml.ignorePatchDiscrepancies=true");
                AddJvm("-Dlog4j2.formatMsgNoLookups", "-Dlog4j2.formatMsgNoLookups=true");
                AddJvm("-Dminecraft.launcher.brand", "-Dminecraft.launcher.brand=AuroraLauncher");
                AddJvm("-Dminecraft.launcher.version", "-Dminecraft.launcher.version=2.1.3");
                AddJvm("-Dos.name", "-Dos.name=Windows 10");
                AddJvm("-Dos.version", "-Dos.version=10.0");
                AddJvm("-XX:-OmitStackTraceInFastThrow", "-XX:-OmitStackTraceInFastThrow");
                foreach (var raw in jvmArgs)
                    args.Add(Replace(raw, replacements));
                if (!args.Any(item => item.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)))
                    args.Insert(0, $"-Xmx{memory}M");
                if (!args.Any(item => item.StartsWith("-Djava.library.path=", StringComparison.OrdinalIgnoreCase)))
                {
                    args.Add("-Djava.library.path=" + nativesDir);
                    args.Add("-cp");
                    args.Add(classpath);
                }
                try
                {
                    var skinMods = Path.Combine(gameDir, "mods");
                    var hasSkinPatch = Directory.Exists(skinMods)
                        && Directory.GetFiles(skinMods, "*.jar").Any(file => Path.GetFileName(file).Contains("customskinloader", StringComparison.OrdinalIgnoreCase));
                    if (hasSkinPatch)
                    {
                        var skinConfig = Path.Combine(gameDir, "CustomSkinLoader", "CustomSkinLoader.json");
                        var managed = true;
                        if (File.Exists(skinConfig))
                        {
                            try
                            {
                                managed = JsonNode.Parse(File.ReadAllText(skinConfig))?["auroraManaged"]?.GetValue<bool>() == true;
                            }
                            catch { managed = false; }
                        }
                        if (managed) await TransparentCapesPatchService.Ensure(instance, versionId, account);
                        else TransparentCapesPatchService.AlignSource(gameDir, account);
                    }
                }
                catch { }
                var loaderKind = instance["loader"]?.GetValue<string>() ?? "";
                if (loaderKind == "fabric" || loaderKind == "quilt")
                {
                    var sourceJar = FindClientJar(versionId);
                    if (sourceJar != null)
                    {
                        var localJar = Path.Combine(gameDir, "client.jar");
                        try { File.Copy(sourceJar, localJar, true); } catch { }
                        args.Add("-Dfabric.gameJarPath=" + localJar);
                    }
                }
                args.Add("-Dminecraft.launcher.brand=AuroraLauncher");
                args.Add("-Dminecraft.launcher.version=2.1.3");
                var extraJvm = instance["jvmArgs"]?.GetValue<string>() ?? settings["jvmArgs"]?.GetValue<string>() ?? "";
                args.AddRange(SplitArguments(extraJvm));
                if (account["type"]?.GetValue<string>() == "authlib" && !string.IsNullOrWhiteSpace(account["authServer"]?.GetValue<string>()))
                {
                    var injector = await AuthlibInjectorService.Ensure();
                    var authServer = account["authServer"]!.GetValue<string>().Trim().TrimEnd('/');
                    if (authServer.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
                        authServer = authServer[..^"/authserver".Length].TrimEnd('/');
                    if (authServer.Contains("littleskin.cn", StringComparison.OrdinalIgnoreCase)
                        && !authServer.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
                        authServer += "/api/yggdrasil";
                    var prefetched = await AuthlibInjectorService.Prefetched(authServer);
                    args.Add("-javaagent:" + injector + "=" + authServer);
                    args.Add("-Dauthlibinjector.side=client");
                    if (prefetched != null) args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + prefetched);
                    // LittleSkin and most Yggdrasil services use the Windows root store.
                    args.Add("-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT");
                }

                var mainClass = version["mainClass"]?.GetValue<string>() ?? throw new Exception("版本缺少 mainClass");
                args.Add(mainClass);

                var gameArgs = ReadArguments(version["arguments"]?["game"] as JsonArray, features);
                if (gameArgs.Count == 0)
                {
                    var legacy = version["minecraftArguments"]?.GetValue<string>() ?? "";
                    gameArgs.AddRange(SplitArguments(legacy));
                }
                if (gameArgs.Count == 0)
                {
                    gameArgs.AddRange(new[]
                    {
                        "--username", "${auth_player_name}", "--version", "${version_name}",
                        "--gameDir", "${game_directory}", "--assetsDir", "${assets_root}",
                        "--assetIndex", "${assets_index_name}", "--uuid", "${auth_uuid}",
                        "--accessToken", "${auth_access_token}", "--userType", "${user_type}",
                        "--versionType", "${version_type}"
                    });
                }
                foreach (var raw in gameArgs)
                    args.Add(Replace(raw, replacements));

                var width = options["width"]?.GetValue<int>() ?? instance["width"]?.GetValue<int>() ?? 854;
                var height = options["height"]?.GetValue<int>() ?? instance["height"]?.GetValue<int>() ?? 480;
                if (!args.Contains("--width")) { args.Add("--width"); args.Add(width.ToString()); }
                if (!args.Contains("--height")) { args.Add("--height"); args.Add(height.ToString()); }
                var fullscreen = options["fullscreen"]?.GetValue<bool>() ?? instance["fullscreen"]?.GetValue<bool>() ?? false;
                if (fullscreen) args.Add("--fullscreen");
                var server = options["server"]?.GetValue<string>() ?? instance["serverToJoin"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(server)) { args.Add("--server"); args.Add(server!); }
                var extraGame = instance["gameArgs"]?.GetValue<string>() ?? settings["gameArgs"]?.GetValue<string>() ?? "";
                args.AddRange(SplitArguments(extraGame));

                var startInfo = new ProcessStartInfo
                {
                    FileName = java,
                    WorkingDirectory = gameDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                foreach (var arg in args) startInfo.ArgumentList.Add(arg);

                var gameLogFile = Path.Combine(AppPaths.SettingsDir, "logs", "game-" + versionId + ".log");
                Directory.CreateDirectory(Path.GetDirectoryName(gameLogFile)!);
                try { File.WriteAllText(gameLogFile, ""); } catch { }
                void Capture(string line)
                {
                    if (line == null) return;
                    try { lock (Gate) File.AppendAllText(gameLogFile, line + Environment.NewLine); } catch { }
                    GameEvents.Emit("log", new JsonObject
                    {
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["source"] = "game",
                        ["level"] = "info",
                        ["text"] = line
                    });
                }

                if (settings["optimizeMemoryBeforeLaunch"]?.GetValue<bool>() != false)
                {
                    try { await Task.Run(() => MemoryOptimizer.Optimize()); } catch { }
                }
                ProgressService.Complete();
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => Capture(e.Data ?? "");
                process.ErrorDataReceived += (_, e) => Capture(e.Data ?? "");
                process.Exited += (_, _) => OnExited(process);
                process.Start();
                PerformanceTuner.Apply(process, startInfo.FileName ?? java);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _suppressLogs = false;
                StartLogWatcher(gameDir, process);


                _startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _runningInstanceId = instanceId;
                lock (Gate)
                {
                    _process = process;
                    _state = new JsonObject
                    {
                        ["state"] = "running",
                        ["instanceId"] = instanceId,
                        ["instanceName"] = instance["name"]?.GetValue<string>() ?? instanceId,
                        ["pid"] = process.Id,
                        ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }
                GameEvents.Emit("game", State());
                try
                {
                    var instanceFile = Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");
                    if (File.Exists(instanceFile) && JsonNode.Parse(File.ReadAllText(instanceFile)) is JsonObject record)
                    {
                        record["playCount"] = (record["playCount"]?.GetValue<int>() ?? 0) + 1;
                        record["lastPlayedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        File.WriteAllText(instanceFile, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        GameEvents.Emit("instances", BridgeService.ListInstances());
                    }
                }
                catch { }
                return new JsonObject { ["ok"] = true, ["pid"] = process.Id };
            }
            catch (Exception ex)
            {
                ProgressService.Fail(ex.Message);
                EmitLauncher("启动失败：" + ex.Message);
                lock (Gate) _state = new JsonObject { ["state"] = "idle" };
                GameEvents.Emit("game", State());
                return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        public static void Stop()
        {
            Process? process;
            lock (Gate) process = _process;
            try { _logWatch?.Cancel(); } catch { }
            FlushTail(20);
            _suppressLogs = true;
            // Reset the UI first: users expect the stop button and the log panel
            // to react immediately even if the JVM takes a while to die.
            lock (Gate)
            {
                _process = null;
                _state = new JsonObject { ["state"] = "idle" };
            }
            GameEvents.Emit("game", State());
            try
            {
                if (process != null && !process.HasExited) process.Kill(true);
            }
            catch { }
            try { process?.WaitForExit(2000); } catch { }
            GameEvents.Emit("game", State());
        }

        private static void FlushTail(int maxLines)
        {
            var path = _lastLogPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!File.Exists(path)) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var start = Math.Max(0, stream.Length - 64 * 1024);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var text = reader.ReadToEnd();
                var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
                if (lines.Count == 0) return;
                if (lines.Count > maxLines) lines = lines.Skip(lines.Count - maxLines).ToList();
                var batch = new JsonArray();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var line in lines)
                {
                    var level = line.Contains("[ERROR]") || line.Contains("Exception") ? "error" : line.Contains("[WARN]") ? "warning" : "info";
                    batch.Add(new JsonObject { ["ts"] = now, ["source"] = "game", ["level"] = level, ["text"] = line });
                }
                GameEvents.Emit("logs", batch);
            }
            catch { }
        }

        private static void StartLogWatcher(string gameDir, Process process, string? outputFile = null)
        {
            var logFile = Path.Combine(gameDir, "logs", "latest.log");
            _lastLogPath = logFile;
            try { _logWatch?.Cancel(); } catch { }
            var cts = new System.Threading.CancellationTokenSource();
            _logWatch = cts;
            _ = Task.Run(async () =>
            {
                var token = cts.Token;
                long offset = File.Exists(logFile) ? new FileInfo(logFile).Length : 0;
                long outOffset = outputFile != null && File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
                while (!token.IsCancellationRequested && !process.HasExited)
                {
                    TryRead(logFile, ref offset);
                    if (outputFile != null) TryRead(outputFile, ref outOffset);
                    try { await Task.Delay(150, token); } catch { break; }
                }
                if (!token.IsCancellationRequested)
                {
                    TryRead(logFile, ref offset);
                    if (outputFile != null) TryRead(outputFile, ref outOffset);
                }
            }, cts.Token);
        }

        private static void TryRead(string path, ref long offset)
        {
            try
            {
                if (!File.Exists(path)) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < offset) offset = 0;
                if (stream.Length <= offset) return;
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                offset = stream.Position;
                var batch = new JsonArray();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var line in text.Split('\n'))
                {
                    var clean = line.TrimEnd('\r');
                    if (clean.Length == 0) continue;
                    var level = clean.Contains("[ERROR]") || clean.Contains("Exception") ? "error" : clean.Contains("[WARN]") ? "warning" : "info";
                    batch.Add(new JsonObject { ["ts"] = now, ["source"] = "game", ["level"] = level, ["text"] = clean });
                }
                // One WebView2 message per read instead of one per line: a burst of
                // startup lines used to queue hundreds of Dispatcher callbacks and
                // the console kept scrolling long after the game had exited.
                if (_suppressLogs) return;
                while (batch.Count > 200) batch.RemoveAt(0);
                if (batch.Count > 0) GameEvents.Emit("logs", batch);
            }
            catch { }
        }

        private static string QuoteArgFile(string arg)
        {
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void OnExited(Process process)
        {
            try { _logWatch?.Cancel(); } catch { }
            FlushTail(20);
            _suppressLogs = true;
            var code = process.ExitCode;
            lock (Gate)
            {
                _state = new JsonObject
                {
                    ["state"] = code == 0 ? "exited" : "crashed",
                    ["exitCode"] = code,
                    ["pid"] = process.Id,
                    ["instanceId"] = _state["instanceId"]?.GetValue<string>(),
                    ["instanceName"] = _state["instanceName"]?.GetValue<string>()
                };
                _process = null;
            }
            EmitLog("error", $"游戏进程退出，代码 {code}");
            AddPlayTime(_runningInstanceId, _startedAtMs);
            _startedAtMs = 0;
            _runningInstanceId = "";
            GameEvents.Emit("game", State());
        }

        private static void AddPlayTime(string instanceId, long startedAtMs)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || startedAtMs <= 0) return;
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAtMs;
            if (elapsed < 1000) return;
            try
            {
                var file = Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");
                if (!File.Exists(file)) return;
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject record) return;
                record["totalPlayTimeMs"] = (record["totalPlayTimeMs"]?.GetValue<long>() ?? 0) + elapsed;
                File.WriteAllText(file, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                GameEvents.Emit("instances", BridgeService.ListInstances());
            }
            catch { }
        }

        private static void EmitLauncher(string text)
        {
            try
            {
                var dir = Path.Combine(AppPaths.SettingsDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "launcher.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine);
            }
            catch { }
            GameEvents.Emit("log", new JsonObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["source"] = "launcher",
                ["level"] = "info",
                ["text"] = text
            });
        }

        private static void EmitLog(string level, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            GameEvents.Emit("log", new JsonObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["source"] = "game",
                ["level"] = level,
                ["text"] = text
            });
        }

        private static JsonObject? FindInstance(string id)
        {
            foreach (var item in BridgeService.ListInstances())
                if (item is JsonObject obj && obj["id"]?.GetValue<string>() == id) return (JsonObject)JsonNode.Parse(obj.ToJsonString())!;
            return null;
        }

        private static (int MaxMajor, int MaxJava8Build) JavaLimits(JsonObject instance, int required)
        {
            // Mirrors PCL's ModLaunch.McLaunchJava rules.
            var max = 999;
            var maxBuild = 0;
            var mc = instance["mcVersion"]?.GetValue<string>() ?? instance["versionId"]?.GetValue<string>() ?? "";
            var loader = instance["loader"]?.GetValue<string>() ?? "";
            var forgeText = instance["loaderVersion"]?.GetValue<string>()
                ?? instance["sourceVersionId"]?.GetValue<string>()?.Split('-').LastOrDefault() ?? "";
            Version? mcVersion = Version.TryParse(mc, out var parsedMc) ? parsedMc : null;
            Version? forgeVersion = Version.TryParse(forgeText, out var parsedForge) ? parsedForge : null;
            if (loader == "forge")
            {
                if (mcVersion != null && mcVersion.Major == 1 && (mcVersion.Minor == 6 || mcVersion.Minor == 7))
                {
                    max = 7;
                }
                else if (mcVersion != null && mcVersion.Major == 1 && mcVersion.Minor <= 12)
                {
                    max = 8;
                }
                else if (mcVersion != null && mcVersion.Major == 1 && mcVersion.Minor <= 14)
                {
                    max = mcVersion.Minor == 13 || mcVersion.Minor == 14 ? 10 : 8;
                }
                else if (mcVersion != null && mcVersion.Major == 1 && mcVersion.Minor == 15)
                {
                    max = 15;
                }
                if (mcVersion != null && mcVersion.Major == 1 && mcVersion.Minor == 16
                    && forgeVersion != null && forgeVersion >= new Version(34, 0, 0) && forgeVersion <= new Version(36, 2, 25))
                {
                    // PCL: 1.16 Forge 34.x ~ 36.2.25 max Java 8u321.
                    max = 8;
                    maxBuild = 321;
                }
            }
            var gameDir = instance["gameDir"]?.GetValue<string>() ?? "";
            var hasOptiFine = false;
            try
            {
                var mods = Path.Combine(gameDir, "mods");
                hasOptiFine = Directory.Exists(mods) && Directory.GetFiles(mods, "*OptiFine*.jar").Length > 0;
            }
            catch { }
            if (hasOptiFine && mcVersion != null && mcVersion.Major == 1)
            {
                if (mcVersion.Minor < 7) max = 8;
                else if (mcVersion.Minor is >= 8 and <= 11) max = 8;
                else if (mcVersion.Minor == 12) max = 8;
                else if (mcVersion.Minor == 18) max = Math.Min(max, 18);
            }
            if (max < required) max = required;
            return (max, maxBuild);
        }

        private static string? SelectJava(JsonObject instance, JsonObject settings, int required, int maxMajor, int maxJava8Build)
        {
            var explicitPath = instance["javaPath"]?.GetValue<string>() ?? settings["javaPath"]?.GetValue<string>();
            var candidates = JavaService.Scan().OfType<JsonObject>()
                .Select(item => new
                {
                    Path = item["path"]?.GetValue<string>() ?? "",
                    Major = item["major"]?.GetValue<int>() ?? 0,
                    Build = item["build"]?.GetValue<int>() ?? 0,
                    Managed = item["managed"]?.GetValue<bool>() == true
                })
                .Where(item => !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                .OrderBy(item => item.Major)
                .ThenByDescending(item => item.Managed)
                .ToList();
            bool Compatible(int major, int build) =>
                major >= required && major <= maxMajor &&
                !(major == 8 && maxJava8Build > 0 && build > maxJava8Build);
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                var explicitInfo = candidates.FirstOrDefault(item =>
                    string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(explicitPath), StringComparison.OrdinalIgnoreCase));
                if (explicitInfo != null && Compatible(explicitInfo.Major, explicitInfo.Build))
                    return explicitPath;
            }
            var exact = candidates.FirstOrDefault(item => item.Major == required && Compatible(item.Major, item.Build));
            if (exact != null) return exact.Path;
            if (maxMajor <= 8) return null;
            return candidates
                .Where(item => Compatible(item.Major, item.Build))
                .OrderBy(item => item.Major)
                .ThenByDescending(item => item.Managed)
                .FirstOrDefault()?.Path;
        }

        private static int CompareMavenVersion(string a, string b)
        {
            static int[] Parts(string text) => text.Split('.', '-', '_')
                .Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
            var left = Parts(a);
            var right = Parts(b);
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var x = i < left.Length ? left[i] : 0;
                var y = i < right.Length ? right[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static async Task EnsureClientJar(JsonObject version, string versionId, System.Threading.CancellationToken token)
        {
            if (HasSrgClient(version)) return;
            var bootstrap = (version["mainClass"]?.GetValue<string>() ?? "")
                .Contains("cpw.mods.bootstraplauncher", StringComparison.OrdinalIgnoreCase);
            var directory = Path.Combine(AppPaths.RootDir, "versions", versionId);
            var jar = Path.Combine(directory, versionId + ".jar");
            if (File.Exists(jar) && new FileInfo(jar).Length > 0) return;
            var client = version["downloads"]?["client"] as JsonObject;
            var url = client?["url"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(url)) return;
            Directory.CreateDirectory(directory);
            // PCL materialises the version folder on launch: <version>/<version>.jar
            // plus the JSON. For BootstrapLauncher versions the jar is only a record
            // and must NOT be added to the classpath (that caused the Forge crash).
            EmitLauncher((bootstrap ? "正在生成版本目录客户端文件：" : "客户端核心文件缺失，正在下载：") + versionId);
            await DownloadService.Download(url, jar, client?["sha1"]?.GetValue<string>(),
                client?["size"]?.GetValue<long>() ?? 0, token);
        }

        private static bool HasSrgClient(JsonObject version)
        {
            if (version["libraries"] is not JsonArray libraries) return false;
            foreach (var item in libraries.OfType<JsonObject>())
            {
                var name = item["name"]?.GetValue<string>() ?? "";
                if (name.StartsWith("net.minecraft:client:", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string BuildClasspath(JsonObject version, string gameDir, out string nativesDir)
        {
            var librariesDir = Path.Combine(AppPaths.RootDir, "libraries");
            var entries = new List<(string Identity, string Version, string Full)>();
            var entryIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void AddEntry(string identity, string version, string full)
            {
                if (!File.Exists(full)) return;
                if (entryIndex.TryGetValue(identity, out var index))
                {
                    // PCL keeps the higher version when the same group:artifact appears
                    // in both the parent and the loader layer (e.g. log4j 2.8.1/2.11.2).
                    if (CompareMavenVersion(version, entries[index].Version) > 0)
                        entries[index] = (identity, version, full);
                }
                else { entryIndex[identity] = entries.Count; entries.Add((identity, version, full)); }
            }
            string IdentityOf(string name)
            {
                var parts = name.Split(':');
                return parts.Length >= 2 ? parts[0] + ":" + parts[1] + ":" + (parts.Length > 3 ? parts[3] : "") : name;
            }
            string VersionOf(string name)
            {
                var parts = name.Split(':');
                return parts.Length >= 3 ? parts[2] : "";
            }
            var clientJar = FindClientJar(version["id"]?.GetValue<string>() ?? "");
            // PCL keeps natives inside the version folder: versions/<id>/<id>-natives.
            var versionFolder = version["id"]?.GetValue<string>() ?? "shared";
            nativesDir = Path.Combine(AppPaths.RootDir, "versions", versionFolder, versionFolder + "-natives");
            Directory.CreateDirectory(nativesDir);
            try
            {
                var legacyNatives = Path.Combine(AppPaths.RootDir, "versions", versionFolder + "-natives");
                if (Directory.Exists(legacyNatives)) Directory.Delete(legacyNatives, true);
            }
            catch { }

            if (version["libraries"] is JsonArray libraries)
            {
                foreach (var item in libraries)
                {
                    if (item is not JsonObject library) continue;
                    if (!RulesAllow(library["rules"] as JsonArray)) continue;
                    var name = library["name"]?.GetValue<string>() ?? "";
                    var identity = IdentityOf(name);
                    if (library["downloads"]?["artifact"]?["path"] is JsonNode pathNode)
                    {
                        var path = pathNode.GetValue<string>();
                        AddEntry(identity, VersionOf(name), Path.Combine(librariesDir, path.Replace('/', Path.DirectorySeparatorChar)));
                    }
                    else if (!string.IsNullOrEmpty(name))
                    {
                        var rel = MavenPath(name);
                        if (!string.IsNullOrEmpty(rel))
                            AddEntry(identity, VersionOf(name), Path.Combine(librariesDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    }
                    if (library["natives"] is JsonObject natives && library["downloads"]?["classifiers"] is JsonObject classifiers)
                    {
                        var classifier = natives["windows"]?.GetValue<string>()?.Replace("${arch}", "64");
                        if (!string.IsNullOrEmpty(classifier) && classifiers[classifier]?["path"] is JsonNode nativePath)
                        {
                            var full = Path.Combine(librariesDir, nativePath.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(full))
                            {
                                try
                                {
                                    using var zip = ZipFile.OpenRead(full);
                                    foreach (var entry in zip.Entries)
                                    {
                                        if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                                        var destination = Path.Combine(nativesDir, entry.Name);
                                        entry.ExtractToFile(destination, true);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            var classpath = entries.Select(entry => entry.Full).ToList();
            // PCL keeps the instance jar at the very end of the classpath so
            // OptiFine's patcher can read obfuscated vanilla classes. The
            // OptiFine library itself belongs second-to-last.
            var versionId = version["id"]?.GetValue<string>() ?? "";
            var optiFineIndex = -1;
            for (var i = 0; i < classpath.Count; i++)
            {
                if (classpath[i].Replace('\\', '/').Contains("/optifine/OptiFine/", StringComparison.OrdinalIgnoreCase))
                {
                    optiFineIndex = i;
                    break;
                }
            }
            string? optiFineEntry = null;
            if (optiFineIndex >= 0)
            {
                optiFineEntry = classpath[optiFineIndex];
                classpath.RemoveAt(optiFineIndex);
            }
            if (optiFineEntry != null)
                classpath.Insert(Math.Max(0, classpath.Count - 1), optiFineEntry);
            if (clientJar != null) classpath.Add(clientJar);
            EmitLauncher($"启动 classpath：{classpath.Count} 项，实例 jar：{(clientJar != null ? "有" : "无")}，natives：{nativesDir}");
            return string.Join(Path.PathSeparator, classpath);
        }

        internal static string? FindClientJar(string versionId)
            => FindClientJarFrom(Path.Combine(AppPaths.RootDir, "versions"), versionId);

        internal static string? FindClientJarFrom(string versionsRoot, string versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId)) return null;
            var directory = Path.Combine(versionsRoot, versionId);
            var jar = Path.Combine(directory, versionId + ".jar");
            if (File.Exists(jar)) return jar;
            var jsonFile = Path.Combine(directory, versionId + ".json");
            if (!File.Exists(jsonFile)) return null;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(jsonFile)) as JsonObject;
                var parent = json?["inheritsFrom"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(parent) ? null : FindClientJarFrom(versionsRoot, parent!);
            }
            catch { return null; }
        }


        private static void RepairBrokenLegacyForge(JsonObject version)
        {
            if (!string.Equals(version["mainClass"]?.GetValue<string>(), "net.minecraft.launchwrapper.Launch", StringComparison.Ordinal)
                || version["minecraftArguments"] is not JsonValue launchArgs
                || !launchArgs.TryGetValue<string>(out var args)
                || !args.Contains("FMLTweaker", StringComparison.OrdinalIgnoreCase))
                return;
            var hasForgeLibrary = (version["libraries"] as JsonArray)?.OfType<JsonObject>().Any(item =>
            {
                var name = item["name"]?.GetValue<string>() ?? "";
                return name.Contains("forge", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(":fml:", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase);
            }) == true;
            if (hasForgeLibrary) return;
            // An imported legacy instance may only contain the Forge launcher
            // arguments but not the Forge/FML library. Fall back to vanilla
            // instead of failing with ClassNotFoundException: FMLTweaker.
            version["mainClass"] = "net.minecraft.client.Minecraft";
            version["minecraftArguments"] = args
                .Replace("--tweakClass cpw.mods.fml.common.launcher.FMLTweaker", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        private static void RepairLegacyForgeArguments(JsonObject version)
        {
            if (!string.Equals(version["mainClass"]?.GetValue<string>(), "net.minecraft.launchwrapper.Launch", StringComparison.Ordinal)
                || version["minecraftArguments"] is not JsonValue launchArgs
                || !launchArgs.TryGetValue<string>(out var args)
                || args.Contains("--tweakClass", StringComparison.OrdinalIgnoreCase))
                return;
            version["minecraftArguments"] = args + " --tweakClass cpw.mods.fml.common.launcher.FMLTweaker";
        }

        private static void RepairLegacyForgeMainClass(JsonObject version)
        {
            if (!string.Equals(version["mainClass"]?.GetValue<string>(), "net.minecraft.client.Minecraft", StringComparison.Ordinal)) return;
            var args = version["minecraftArguments"]?.GetValue<string>() ?? "";
            if (!args.Contains("--tweakClass", StringComparison.OrdinalIgnoreCase)) return;
            if (version["libraries"] is not JsonArray libraries) return;
            bool Exists(string name)
            {
                var rel = MavenPath(name);
                return !string.IsNullOrEmpty(rel)
                    && File.Exists(Path.Combine(AppPaths.RootDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar)));
            }
            var hasLaunchwrapper = false;
            var hasForge = false;
            foreach (var item in libraries.OfType<JsonObject>())
            {
                var name = item["name"]?.GetValue<string>() ?? "";
                if (name.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase) && Exists(name)) hasLaunchwrapper = true;
                if (name.Contains("minecraftforge:forge", StringComparison.OrdinalIgnoreCase) && Exists(name)) hasForge = true;
            }
            if (hasLaunchwrapper && hasForge) version["mainClass"] = "net.minecraft.launchwrapper.Launch";
        }

        // PCL: when Forge/LiteLoader and OptiFine share a launchwrapper version,
        // the OptiFine tweaker must be OptiFineForgeTweaker and must come last,
        // otherwise FML crashes before OptiFine gets a chance to load.
        private static void RepairOptiFineTweaker(JsonObject version)
        {
            if (version["minecraftArguments"] is not JsonValue launchArgs
                || !launchArgs.TryGetValue<string>(out var args)
                || !args.Contains("OptiFine", StringComparison.OrdinalIgnoreCase)) return;
            var libraries = version["libraries"] as JsonArray;
            var hasForge = libraries?.OfType<JsonObject>().Any(item =>
            {
                var name = item["name"]?.GetValue<string>() ?? "";
                return name.Contains("minecraftforge:forge", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("litemod", StringComparison.OrdinalIgnoreCase);
            }) == true;
            if (!hasForge) return;
            if (args.Contains("optifine.OptiFineForgeTweaker", StringComparison.OrdinalIgnoreCase))
            {
                args = args.Replace(" --tweakClass optifine.OptiFineForgeTweaker", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("--tweakClass optifine.OptiFineForgeTweaker ", "", StringComparison.OrdinalIgnoreCase);
                version["minecraftArguments"] = args.Trim() + " --tweakClass optifine.OptiFineForgeTweaker";
                return;
            }
            if (!args.Contains("optifine.OptiFineTweaker", StringComparison.OrdinalIgnoreCase)) return;
            args = args.Replace(" --tweakClass optifine.OptiFineTweaker", "", StringComparison.OrdinalIgnoreCase)
                .Replace("--tweakClass optifine.OptiFineTweaker ", "", StringComparison.OrdinalIgnoreCase);
            version["minecraftArguments"] = args.Trim() + " --tweakClass optifine.OptiFineForgeTweaker";
        }

        internal static JsonObject LoadVersionChain(string versionId)
            => LoadVersionChainFrom(Path.Combine(AppPaths.RootDir, "versions"), versionId);

        internal static JsonObject LoadVersionChainFrom(string versionsRoot, string versionId)
        {
            var file = Path.Combine(versionsRoot, versionId, versionId + ".json");
            if (!File.Exists(file)) throw new Exception("版本文件不存在：" + versionId);
            var child = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new Exception("版本 JSON 格式不正确");
            var parentId = child["inheritsFrom"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(parentId))
            {
                RepairLegacyForgeArguments(child);
                RepairBrokenLegacyForge(child);
                RepairLegacyForgeMainClass(child);
                RepairOptiFineTweaker(child);
                return child;
            }
            var parent = LoadVersionChainFrom(versionsRoot, parentId);
            var merged = (JsonObject)JsonNode.Parse(parent.ToJsonString())!;
            merged["id"] = versionId;
            if (child["mainClass"] != null) merged["mainClass"] = child["mainClass"]!.DeepCloneCompat();
            // Legacy Forge (1.7.10 and older) uses minecraftArguments with
            // --tweakClass; it must override the vanilla argument string.
            if (child["minecraftArguments"] != null)
                merged["minecraftArguments"] = child["minecraftArguments"]!.DeepCloneCompat();
            RepairLegacyForgeArguments(merged);
            RepairBrokenLegacyForge(merged);
            RepairLegacyForgeMainClass(merged);
            RepairOptiFineTweaker(merged);
            if (child["libraries"] is JsonArray childLibs)
            {
                var libs = new JsonArray();
                if (merged["libraries"] is JsonArray parentLibs)
                    foreach (var item in parentLibs) libs.Add(item?.DeepCloneCompat());
                foreach (var item in childLibs) libs.Add(item?.DeepCloneCompat());
                merged["libraries"] = libs;
            }
            if (child["arguments"] is JsonObject childArgs && merged["arguments"] is JsonObject parentArgs)
            {
                foreach (var category in new[] { "jvm", "game" })
                {
                    if (childArgs[category] is not JsonArray childList) continue;
                    var combined = new JsonArray();
                    if (parentArgs[category] is JsonArray parentList)
                        foreach (var item in parentList) combined.Add(item?.DeepCloneCompat());
                    foreach (var item in childList) combined.Add(item?.DeepCloneCompat());
                    parentArgs[category] = combined;
                }
            }
            if (child["assetIndex"] != null) merged["assetIndex"] = child["assetIndex"]!.DeepCloneCompat();
            if (child["assets"] != null) merged["assets"] = child["assets"]!.DeepCloneCompat();
            if (child["javaVersion"] != null) merged["javaVersion"] = child["javaVersion"]!.DeepCloneCompat();
            if (child["type"] != null) merged["type"] = child["type"]!.DeepCloneCompat();
            return merged;
        }

        private static List<string> ReadArguments(JsonArray? array, Dictionary<string, bool>? features = null)
        {
            var result = new List<string>();
            if (array == null) return result;
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text)) result.Add(text);
                else if (item is JsonObject obj)
                {
                    if (!RulesAllow(obj["rules"] as JsonArray, features)) continue;
                    var valueNode = obj["value"];
                    if (valueNode is JsonValue single && single.TryGetValue<string>(out var one)) result.Add(one);
                    else if (valueNode is JsonArray values)
                        foreach (var entry in values)
                            if (entry is JsonValue v && v.TryGetValue<string>(out var s)) result.Add(s);
                }
            }
            return result;
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

        private static bool RulesAllow(JsonArray? rules, Dictionary<string, bool>? features = null)
        {
            if (rules == null || rules.Count == 0) return true;
            var matched = false;
            var allowed = false;
            foreach (var item in rules)
            {
                if (item is not JsonObject rule) continue;
                var os = rule["os"] as JsonObject;
                if (os != null)
                {
                    var name = os["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name) && name != "windows") continue;
                    var arch = os["arch"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(arch) && arch != "x86" && arch != "x86_64") continue;
                }
                if (rule["features"] is JsonObject ruleFeatures)
                {
                    var matches = true;
                    foreach (var pair in ruleFeatures)
                    {
                        var wanted = pair.Value?.GetValue<bool>() ?? false;
                        var actual = features != null && features.TryGetValue(pair.Key, out var value) && value;
                        if (wanted != actual) { matches = false; break; }
                    }
                    if (!matches) continue;
                }
                matched = true;
                allowed = (rule["action"]?.GetValue<string>() ?? "allow") == "allow";
            }
            return matched && allowed;
        }

        private static string Replace(string text, Dictionary<string, string> values)
        {
            var result = text;
            foreach (var pair in values) result = result.Replace(pair.Key, pair.Value);
            return result;
        }

        private static List<string> SplitArguments(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? new List<string>()
                : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }

    internal static class JsonCloneCompat
    {
        public static JsonNode? DeepCloneCompat(this JsonNode? node)
            => node == null ? null : JsonNode.Parse(node.ToJsonString());
    }
}




