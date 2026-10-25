using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class BridgeService
    {
        private static readonly object Gate = new();

        private static string ConfigFile => Path.Combine(AppPaths.SettingsDir, "config.json");

        private static JsonNode? CloneNode(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

        public static JsonObject DefaultSettings()
        {
            return new JsonObject
            {
                ["rootDir"] = AppPaths.RootDir,
                ["language"] = "zh-CN",
                ["theme"] = "system",
                ["accent"] = "aurora",
                ["downloadSource"] = "auto",
                ["modrinthSource"] = "mirror",
                ["optifineSource"] = "auto",
                ["downloadSources"] = new JsonObject(),
                ["maxConcurrency"] = 32,
                ["downloadThreads"] = 128,
                ["proxyEnabled"] = false,
                ["proxyHost"] = "127.0.0.1",
                ["proxyPort"] = 7890,
                ["minimizeOnLaunch"] = false,
                ["showConsoleOnLaunch"] = true,
                ["keepLauncherOpen"] = true,
                ["closeBehavior"] = "exit",
                ["defaultMemory"] = 0,
                ["autoMemory"] = true,
                ["jvmArgs"] = "",
                ["gameArgs"] = "",
                ["javaPath"] = "",
                ["msClientId"] = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb",
                ["enableStats"] = true,
                ["lastSelectedInstanceId"] = "",
                ["onboardingDone"] = true,
                ["checkUpdates"] = true,
                ["downloadAssets"] = true,
                ["downloadNatives"] = true,
                ["useAuthlibForOffline"] = false,
                ["autoImportVersions"] = true,
                ["firstLaunchChinese"] = true
            };
        }

        private static JsonObject LoadSettings()
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(ConfigFile))
                    {
                        if (JsonNode.Parse(File.ReadAllText(ConfigFile)) is JsonObject stored)
                        {
                            var merged = DefaultSettings();
                            foreach (var pair in stored)
                                merged[pair.Key] = CloneNode(pair.Value);
                            return merged;
                        }
                    }
                }
                catch
                {
                    // A damaged config should not prevent the launcher from starting.
                }
                return DefaultSettings();
            }
        }

        private static JsonObject SaveSettings(JsonObject settings)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.SettingsDir);
                File.WriteAllText(ConfigFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return settings;
            }
        }

        public static JsonObject GetSettings() => LoadSettings();

        public static JsonObject UpdateSettings(JsonElement patch)
        {
            var settings = LoadSettings();
            if (patch.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in patch.EnumerateObject())
                    settings[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            return SaveSettings(settings);
        }

        public static JsonObject ResetSettings() => SaveSettings(DefaultSettings());

        public static void NotifyInstances()
        {
            GameEvents.Emit("instances", ListInstances());
        }

        public static JsonArray ListInstances()
        {
            var result = new JsonArray();
            if (!Directory.Exists(AppPaths.InstancesDir)) return result;
            foreach (var directory in Directory.GetDirectories(AppPaths.InstancesDir).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var file = Path.Combine(directory, "instance.json");
                if (!File.Exists(file)) continue;
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject instance)
                        result.Add(CloneNode(instance));
                }
                catch { }
            }
            return result;
        }

        public static JsonNode? GetInstance(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (var instance in ListInstances())
            {
                if (instance is JsonObject obj && obj["id"]?.GetValue<string>() == id)
                    return CloneNode(obj);
            }
            return null;
        }

        public static JsonNode? UpdateInstance(string id, JsonElement patch)
        {
            if (!Directory.Exists(AppPaths.InstancesDir)) return null;
            foreach (var directory in Directory.GetDirectories(AppPaths.InstancesDir))
            {
                var file = Path.Combine(directory, "instance.json");
                if (!File.Exists(file)) continue;
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject instance) continue;
                    if (instance["id"]?.GetValue<string>() != id) continue;
                    if (patch.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in patch.EnumerateObject())
                            instance[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    }
                    File.WriteAllText(file, instance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return CloneNode(instance);
                }
                catch { }
            }
            return null;
        }

        public static JsonObject AppInfo()
        {
            var instances = ListInstances();
            long playTime = 0;
            foreach (var item in instances)
            {
                try { playTime += item?["totalPlayTimeMs"]?.GetValue<long>() ?? 0; } catch { }
            }
            var versionsDir = Path.Combine(AppPaths.RootDir, "versions");
            var totalVersions = Directory.Exists(versionsDir)
                ? Directory.GetDirectories(versionsDir).Length
                : 0;
            return new JsonObject
            {
                ["version"] = "1.1.0",
                ["electron"] = "",
                ["chrome"] = "",
                ["node"] = "",
                ["platform"] = "win32",
                ["arch"] = "x64",
                ["rootDir"] = AppPaths.RootDir,
                ["versionsDir"] = versionsDir,
                ["assetsDir"] = Path.Combine(AppPaths.RootDir, "assets"),
                ["librariesDir"] = Path.Combine(AppPaths.RootDir, "libraries"),
                ["instancesDir"] = AppPaths.InstancesDir,
                ["totalInstances"] = instances.Count,
                ["totalVersions"] = totalVersions,
                ["totalPlayTimeMs"] = playTime
            };
        }

        private static string ArgString(JsonElement args, int index)
            => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > index ? args[index].GetString() ?? "" : "";

        private static JsonObject InstanceSize(string id)
        {
            long size = 0;
            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
                try { foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { try { size += new FileInfo(file).Length; } catch { } } } catch { }
            }
            var instance = GetInstance(id) as JsonObject;
            Add(Path.Combine(AppPaths.InstancesDir, id));
            Add(instance?["gameDir"]?.GetValue<string>());
            return new JsonObject { ["size"] = size };
        }

        public static JsonArray LatestLogs()
        {
            return new JsonArray();
        }

        public static async System.Threading.Tasks.Task<JsonNode?> Dispatch(string channel, JsonElement args)
        {
            switch (channel)
            {
                case "modrinth:search":
                    return await ModrinthService.Search(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "modrinth:project":
                    return await ModrinthService.Project(ArgString(args, 0));
                case "modrinth:versions":
                    return await ModrinthService.Versions(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2));
                case "modrinth:install":
                    return await ModrinthService.Install(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "modrinth:install-folder":
                    return await ModrinthService.InstallToFolder(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "modrinth:install-pack":
                    return await ModrinthService.InstallPack(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2));
                case "modrinth:featured":
                    return await ModrinthService.Featured(ArgString(args, 0));
                case "servers:ping":
                    return await ServerPingService.Ping(ArgString(args, 0));
                case "app:pick-directory":
                    return SystemService.PickDirectory(ArgString(args, 0), ArgString(args, 1));
                case "app:pick-file":
                    return SystemService.PickFile(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default, args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.True);
                case "updates:check":
                    return await UpdateService.Check(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.True);
                case "modnames:translate":
                {
                    var queries = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonArray
                        : null;
                    return queries == null ? new JsonObject() : ModNamesService.Translate(queries);
                }
                case "network:test":
                    return await NetworkService.Test();
                case "app:info":
                    return AppInfo();
                case "settings:get":
                    return GetSettings();
                case "settings:update":
                {
                    if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() == 0) return GetSettings();
                    var updated = UpdateSettings(args[0]);
                    var root = updated["rootDir"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(root)) AppPaths.UseRoot(root!);
                    GameEvents.Emit("settings", updated);
                    NotifyInstances();
                    return updated;
                }
                case "settings:pick-root":
                {
                    var chosen = SystemService.PickFolder();
                    if (string.IsNullOrWhiteSpace(chosen)) return GetSettings();
                    AppPaths.UseRoot(chosen);
                    try { File.WriteAllText(Path.Combine(chosen, ".aurora-root"), ""); } catch { }
                    var updated = UpdateSettings(System.Text.Json.JsonSerializer.SerializeToElement(new { rootDir = chosen }));
                    GameEvents.Emit("settings", updated);
                    NotifyInstances();
                    return updated;
                }
                case "settings:use-portable-root":
                {
                    AppPaths.UseRoot(Path.Combine(AppPaths.BaseDir, ".minecraft"));
                    var updated = UpdateSettings(System.Text.Json.JsonSerializer.SerializeToElement(new { rootDir = AppPaths.RootDir }));
                    GameEvents.Emit("settings", updated);
                    NotifyInstances();
                    return updated;
                }
                case "settings:system-info":
                    return SystemService.Info();
                case "storage:stats":
                    return SystemService.StorageStats();
                case "storage:clean":
                    return SystemService.Clean();
                case "storage:prune":
                    return SystemService.Prune();
                case "settings:reset":
                    return ResetSettings();
                case "instances:list":
                    return ListInstances();
                case "instances:get":
                    return GetInstance(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? args[0].GetString() ?? ""
                        : "");
                case "instances:create":
                {
                    var input = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject
                        : null;
                    if (input == null) return null;
                    var versionId = input["versionId"]?.GetValue<string>() ?? input["sourceVersionId"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(versionId)) throw new Exception("缺少要创建的版本");
                    var name = input["name"]?.GetValue<string>() ?? versionId;
                    var id = MinecraftInstallService.CreateInstance(versionId, name, input["loader"]?.GetValue<string>() ?? "vanilla", input["loaderVersion"]?.GetValue<string>(), input["mcVersion"]?.GetValue<string>());
                    NotifyInstances();
                    return GetInstance(id);
                }
                case "instances:remove":
                {
                    var id = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "" : "";
                    var deleteFiles = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.True;
                    var instance = GetInstance(id) as JsonObject;
                    if (instance == null) return null;
                    var versionsRoot = Path.GetFullPath(Path.Combine(AppPaths.RootDir, "versions"));
                    var versionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var current = instance["versionId"]?.GetValue<string>() ?? instance["sourceVersionId"]?.GetValue<string>() ?? "";
                    while (!string.IsNullOrWhiteSpace(current) && versionIds.Add(current))
                    {
                        var file = Path.Combine(versionsRoot, current, current + ".json");
                        if (!File.Exists(file)) break;
                        try { current = (JsonNode.Parse(File.ReadAllText(file)) as JsonObject)?["inheritsFrom"]?.GetValue<string>() ?? ""; }
                        catch { break; }
                    }
                    var directory = Path.Combine(AppPaths.InstancesDir, id);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    if (deleteFiles)
                    {
                        var gameDir = instance["gameDir"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(gameDir))
                        {
                            var full = Path.GetFullPath(gameDir);
                            if ((full.StartsWith(Path.GetFullPath(AppPaths.InstancesDir), StringComparison.OrdinalIgnoreCase) ||
                                 full.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase)) && Directory.Exists(full))
                                Directory.Delete(full, true);
                        }
                        var remaining = ListInstances().OfType<JsonObject>().ToList();
                        bool Referenced(string versionId)
                        {
                            foreach (var item in remaining)
                            {
                                var cursor = item["versionId"]?.GetValue<string>() ?? item["sourceVersionId"]?.GetValue<string>() ?? "";
                                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                while (!string.IsNullOrWhiteSpace(cursor) && seen.Add(cursor))
                                {
                                    if (string.Equals(cursor, versionId, StringComparison.OrdinalIgnoreCase)) return true;
                                    var jsonFile = Path.Combine(versionsRoot, cursor, cursor + ".json");
                                    if (!File.Exists(jsonFile)) break;
                                    try { cursor = (JsonNode.Parse(File.ReadAllText(jsonFile)) as JsonObject)?["inheritsFrom"]?.GetValue<string>() ?? ""; }
                                    catch { break; }
                                }
                            }
                            return false;
                        }
                        foreach (var versionId in versionIds)
                        {
                            if (Referenced(versionId)) continue;
                            var dir = Path.Combine(versionsRoot, versionId);
                            if (File.Exists(Path.Combine(dir, versionId + ".json"))) Directory.Delete(dir, true);
                        }
                    }
                    NotifyInstances();
                    return null;
                }
                case "instances:copy":
                {
                    var id = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "" : "";
                    var name = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 ? args[1].GetString() ?? "" : "";
                    var instance = GetInstance(id) as JsonObject;
                    if (instance == null) return null;
                    var newId = "inst-" + Guid.NewGuid().ToString("N")[..12];
                    var directory = Path.Combine(AppPaths.InstancesDir, newId);
                    Directory.CreateDirectory(directory);
                    var copy = (JsonObject)JsonNode.Parse(instance.ToJsonString())!;
                    copy["id"] = newId;
                    copy["name"] = string.IsNullOrWhiteSpace(name) ? (instance["name"]?.GetValue<string>() ?? "") + " 副本" : name;
                    copy["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    copy["totalPlayTimeMs"] = 0;
                    copy["playCount"] = 0;
                    File.WriteAllText(Path.Combine(directory, "instance.json"), copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    NotifyInstances();
                    return GetInstance(newId);
                }
                case "instances:export":
                    return InstanceArchiveService.Export(ArgString(args, 0));
                case "instances:export-pack":
                    return InstanceArchiveService.ExportPack(ArgString(args, 0));
                case "instances:import":
                    return await InstanceArchiveService.Import();
                case "instances:import-existing":
                    return InstanceArchiveService.ImportExisting();
                case "servers:list":
                    return NbtServersService.List(ArgString(args, 0));
                case "servers:add":
                    return NbtServersService.Add(ArgString(args, 0), args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 ? args[1] : default);
                case "servers:remove":
                    return NbtServersService.Remove(ArgString(args, 0), ArgString(args, 1));
                case "curseforge:search":
                    return await CurseForgeService.Search(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "curseforge:categories":
                    return await CurseForgeService.Categories(ArgString(args, 0));
                case "curseforge:project":
                    return await CurseForgeService.Project(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetInt32() : 0);
                case "curseforge:files":
                    return await CurseForgeService.Files(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetInt32() : 0, ArgString(args, 1), ArgString(args, 2));
                case "curseforge:install":
                    return await CurseForgeService.Install(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "curseforge:install-pack":
                    return await CurseForgeService.InstallPack(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "curseforge:install-folder":
                    return await CurseForgeService.InstallToFolder(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "news:list":
                    return await NewsService.List(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.True);
                case "instances:size":
                {
                    var id = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "" : "";
                    return InstanceSize(id);
                }
                case "instances:open-folder":
                {
                    var id = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "" : "";
                    var sub = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 ? args[1].GetString() ?? "" : "";
                    var instance = GetInstance(id) as JsonObject;
                    var gameDir = instance?["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance?["versionId"]?.GetValue<string>() ?? "");
                    var directory = string.IsNullOrWhiteSpace(sub) ? gameDir : Path.Combine(gameDir, sub);
                    Directory.CreateDirectory(directory);
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true }); } catch { }
                    return null;
                }
                case "instances:update":
                    return args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1
                        ? UpdateInstance(args[0].GetString() ?? "", args[1])
                        : null;
                case "logs:latest":
                    return LogService.Latest(ArgString(args, 0));
                case "logs:files":
                    return LogService.Files(ArgString(args, 0));
                case "logs:read":
                    return LogService.Read(ArgString(args, 0), args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 ? args[1].GetInt32() : 0);
                case "logs:analyze":
                    return LogService.Analyze(ArgString(args, 0));
                case "java:list":
                case "java:scan":
                    return JavaService.Scan();
                case "java:download":
                    return await JavaRuntimeService.Download(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetInt32() : 21);
                case "java:delete":
                    if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0)
                        JavaRuntimeService.Delete(args[0].GetString() ?? "");
                    return null;
                case "versions:manifest":
                    return await VersionService.Manifest(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.True);
                case "versions:installed":
                    return VersionService.Installed();
                case "versions:loaders":
                {
                    var kind = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "" : "";
                    var mc = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 ? args[1].GetString() ?? "" : "";
                    if (kind == "optifine") return await OptiFineService.List(mc);
                    return kind == "fabric" || kind == "quilt"
                        ? await LoaderService.List(kind, mc)
                        : await ForgeService.List(kind, mc);
                }
                case "install:loader":
                {
                    var request = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject
                        : null;
                    if (request == null) return null;
                    var kind = request["loader"]?.GetValue<string>() ?? "";
                    if (kind == "optifine")
                        return await OptiFineService.Install(
                            request["mcVersion"]?.GetValue<string>() ?? "",
                            request["loaderVersion"]?.GetValue<string>() ?? request["optifineVersion"]?.GetValue<string>() ?? "",
                            request["instanceId"]?.GetValue<string>() ?? "");
                    if (kind == "fabric" || kind == "quilt")
                        return await LoaderService.Install(
                            kind,
                            request["mcVersion"]?.GetValue<string>() ?? "",
                            request["loaderVersion"]?.GetValue<string>(),
                            request["instanceName"]?.GetValue<string>(),
                            request["createInstance"]?.GetValue<bool>() ?? true);
                    if (kind == "forge" || kind == "neoforge")
                        return await ForgeService.Install(
                            kind,
                            request["mcVersion"]?.GetValue<string>() ?? "",
                            request["loaderVersion"]?.GetValue<string>(),
                            request["instanceName"]?.GetValue<string>(),
                            request["createInstance"]?.GetValue<bool>() ?? true,
                            kind == "forge" ? request["optifineVersion"]?.GetValue<string>() : null);
                    throw new Exception("未知的加载器：" + kind);
                }
                case "versions:java-requirement":
                    return VersionService.JavaRequirement(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "" : "");
                case "versions:loader-size":
                    return VersionService.LoaderSize(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "" : "");
                case "versions:delete":
                    if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0)
                        VersionService.DeleteVersion(args[0].GetString() ?? "");
                    return null;
                case "install:vanilla":
                    if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 1) return null;
                    return await MinecraftInstallService.InstallVanilla(
                        args[0].GetString() ?? "",
                        args.GetArrayLength() < 2 || args[1].ValueKind != JsonValueKind.False,
                        args.GetArrayLength() > 2 ? args[2].GetString() : null);
                case "content:list":
                    return ContentService.List(ArgString(args, 0), ArgString(args, 1));
                case "content:worlds":
                    return ContentService.Worlds(ArgString(args, 0));
                case "content:screenshots":
                    return ContentService.Screenshots(ArgString(args, 0));
                case "content:toggle":
                    ContentService.Toggle(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2));
                    return null;
                case "content:remove":
                    ContentService.Remove(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2));
                    return null;
                case "content:import":
                    return ContentService.Import(ArgString(args, 0), ArgString(args, 1));
                case "content:backup-world":
                    return new JsonObject { ["path"] = ContentService.BackupWorld(ArgString(args, 0), ArgString(args, 1)) };
                case "content:delete-world":
                    ContentService.DeleteWorld(ArgString(args, 0), ArgString(args, 1));
                    return null;
                case "accounts:list":
                    return AccountService.List();
                case "accounts:active":
                    return AccountService.Active();
                case "accounts:set-active":
                    return args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? AccountService.SetActive(args[0].GetString() ?? "")
                        : null;
                case "accounts:start-microsoft":
                    return await MicrosoftAuthService.Start();
                case "accounts:poll-microsoft":
                    return await MicrosoftAuthService.Poll(ArgString(args, 0));
                case "accounts:cancel-microsoft":
                    MicrosoftAuthService.Cancel(ArgString(args, 0));
                    return null;
                case "accounts:add-authlib":
                    return await AuthlibService.Add(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2));
                case "accounts:add-offline":
                    return AccountService.AddOffline(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetString() ?? "Player" : "Player");
                case "accounts:refresh":
                {
                    var id = ArgString(args, 0);
                    var account = AccountService.List().OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<string>() == id);
                    if (account == null) return null;
                    if (account["type"]?.GetValue<string>() == "microsoft") return await MicrosoftAuthService.Refresh(account);
                    return account;
                }
                case "accounts:remove":
                    if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0)
                        AccountService.Remove(args[0].GetString() ?? "");
                    return null;
                case "launch:start":
                    var launchOptions = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject
                        : null;
                    return await LaunchService.Start(launchOptions ?? new JsonObject());
                case "launch:stop":
                    LaunchService.Stop();
                    return null;
                case "launch:state":
                    return LaunchService.State();
                default:
                    return null;
            }
        }
    }
}
