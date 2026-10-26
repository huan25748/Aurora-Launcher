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
                var classpath = BuildClasspath(version, gameDir, out var nativesDir);
                ProgressService.Update(1, "游戏文件已检查");
                EmitLauncher($"启动 {versionId}，Java：{java}");
                var assetsIndex = version["assetIndex"]?["id"]?.GetValue<string>() ?? version["assets"]?.GetValue<string>() ?? "legacy";
                ProgressService.Step("检查资源文件", 1, assetsIndex);
                await MinecraftInstallService.EnsureAssets(version);
                ProgressService.Update(1, "资源文件已就绪");
                var memory = instance["memory"]?.GetValue<int>() ?? (settings["defaultMemory"]?.GetValue<int>() ?? 0);
                if (memory <= 0) memory = 4096;

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
                    ["${launcher_version}"] = "1.1.9",
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
                AddJvm("-Dminecraft.launcher.version", "-Dminecraft.launcher.version=1.1.9");
                AddJvm("-Dsun.stdout.encoding", "-Dsun.stdout.encoding=UTF-8");
                AddJvm("-Dsun.stderr.encoding", "-Dsun.stderr.encoding=UTF-8");
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
                args.Add("-Dminecraft.launcher.version=1.1.9");
                var extraJvm = instance["jvmArgs"]?.GetValue<string>() ?? settings["jvmArgs"]?.GetValue<string>() ?? "";
                args.AddRange(SplitArguments(extraJvm));
                if (account["type"]?.GetValue<string>() == "authlib" && !string.IsNullOrWhiteSpace(account["authServer"]?.GetValue<string>()))
                {
                    var injector = await AuthlibInjectorService.Ensure();
                    args.Add("-javaagent:" + injector + "=" + account["authServer"]!.GetValue<string>());
                    args.Add("-Dauthlibinjector.side=client");
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
                    RedirectStandardError = true
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

                ProgressService.Complete();
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => Capture(e.Data ?? "");
                process.ErrorDataReceived += (_, e) => Capture(e.Data ?? "");
                process.Exited += (_, _) => OnExited(process);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                StartLogWatcher(gameDir, process);


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
            lock (Gate)
            {
                try
                {
                    if (_process != null && !_process.HasExited) _process.Kill(true);
                }
                catch { }
            }
        }

        private static void StartLogWatcher(string gameDir, Process process, string? outputFile = null)
        {
            var logFile = Path.Combine(gameDir, "logs", "latest.log");
            _ = Task.Run(async () =>
            {
                long offset = File.Exists(logFile) ? new FileInfo(logFile).Length : 0;
                long outOffset = outputFile != null && File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
                while (!process.HasExited)
                {
                    TryRead(logFile, ref offset);
                    if (outputFile != null) TryRead(outputFile, ref outOffset);
                    await Task.Delay(350);
                }
                TryRead(logFile, ref offset);
                if (outputFile != null) TryRead(outputFile, ref outOffset);
            });
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
                foreach (var line in text.Split('\n'))
                {
                    var clean = line.TrimEnd('\r');
                    if (clean.Length == 0) continue;
                    var level = clean.Contains("[ERROR]") || clean.Contains("Exception") ? "error" : clean.Contains("[WARN]") ? "warning" : "info";
                    EmitLog(level, clean);
                }
            }
            catch { }
        }

        private static string QuoteArgFile(string arg)
        {
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void OnExited(Process process)
        {
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
            GameEvents.Emit("game", State());
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
            nativesDir = Path.Combine(AppPaths.RootDir, "natives", "shared");
            Directory.CreateDirectory(nativesDir);

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
                                try { ZipFile.ExtractToDirectory(full, nativesDir, true); } catch { }
                            }
                        }
                    }
                }
            }
            var classpath = entries.Select(entry => entry.Full).ToList();
            // Old launcher appends the vanilla client jar after every library.
            var versionId = version["id"]?.GetValue<string>() ?? "";
            var ownJar = Path.Combine(AppPaths.RootDir, "versions", versionId, versionId + ".jar");
            var inheritedJar = !File.Exists(ownJar);
            var mainClass = version["mainClass"]?.GetValue<string>() ?? "";
            var patchedModule = mainClass.Contains("cpw.mods.bootstraplauncher", StringComparison.OrdinalIgnoreCase);
            if (clientJar != null && (!patchedModule || !inheritedJar)) classpath.Add(clientJar);
            return string.Join(Path.PathSeparator, classpath);
        }

        internal static string? FindClientJar(string versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId)) return null;
            var directory = Path.Combine(AppPaths.RootDir, "versions", versionId);
            var jar = Path.Combine(directory, versionId + ".jar");
            if (File.Exists(jar)) return jar;
            var jsonFile = Path.Combine(directory, versionId + ".json");
            if (!File.Exists(jsonFile)) return null;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(jsonFile)) as JsonObject;
                var parent = json?["inheritsFrom"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(parent) ? null : FindClientJar(parent!);
            }
            catch { return null; }
        }

        internal static JsonObject LoadVersionChain(string versionId)
        {
            var file = Path.Combine(AppPaths.RootDir, "versions", versionId, versionId + ".json");
            if (!File.Exists(file)) throw new Exception("版本文件不存在：" + versionId);
            var child = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new Exception("版本 JSON 格式不正确");
            var parentId = child["inheritsFrom"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(parentId)) return child;
            var parent = LoadVersionChain(parentId);
            var merged = (JsonObject)JsonNode.Parse(parent.ToJsonString())!;
            merged["id"] = versionId;
            if (child["mainClass"] != null) merged["mainClass"] = child["mainClass"]!.DeepCloneCompat();
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




