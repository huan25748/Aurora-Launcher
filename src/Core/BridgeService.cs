using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

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
                ["optimizeMemoryBeforeLaunch"] = true,
                ["downloadEngine"] = "builtin",
                ["modrinthSource"] = "auto",
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
                ["msClientId"] = "ff7e81d4-c1ab-4520-a957-9bccc7a2c704",
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
                            // 旧版本的默认值是 mirror，很多人没手动选过。
                            // 迁移一次到「自动」，否则会被固定走慢镜像。
                            if (stored["modrinthSource"]?.GetValue<string>() == "mirror"
                                && stored["modrinthSourceAutoMigrated"]?.GetValue<bool>() != true)
                            {
                                merged["modrinthSource"] = "auto";
                                merged["modrinthSourceAutoMigrated"] = true;
                                try { File.WriteAllText(ConfigFile, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch { }
                            }
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
                    {
                        RepairInstanceVersion(instance, file);
                        result.Add(CloneNode(instance));
                    }
                }
                catch { }
            }
            return result;
        }

        private static void RepairInstanceVersion(JsonObject instance, string file)
        {
            try
            {
                var changed = false;
                var name = instance["name"]?.GetValue<string>() ?? "";
                var versionId = instance["versionId"]?.GetValue<string>() ?? "";
                var mcVersion = instance["mcVersion"]?.GetValue<string>() ?? "";
                if (string.Equals(mcVersion, name, StringComparison.Ordinal) || string.Equals(mcVersion, versionId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(mcVersion))
                {
                    var jsonFile = Path.Combine(AppPaths.RootDir, "versions", versionId, versionId + ".json");
                    if (File.Exists(jsonFile) && JsonNode.Parse(File.ReadAllText(jsonFile)) is JsonObject json)
                    {
                        var candidate = json["clientVersion"]?.GetValue<string>() ?? json["inheritsFrom"]?.GetValue<string>() ?? versionId;
                        var match = System.Text.RegularExpressions.Regex.Match(candidate, "\\d+\\.\\d+(?:\\.\\d+)?");
                        if (match.Success) { instance["mcVersion"] = match.Value; changed = true; }
                    }
                }
                if (RepairLoaderVersion(instance)) changed = true;
                if (string.IsNullOrWhiteSpace(instance["iconPath"]?.GetValue<string>())) InstanceIconService.EnsureLocal(instance, file);
                if (changed) File.WriteAllText(file, instance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static bool RepairLoaderVersion(JsonObject instance)
        {
            try
            {
                var versionId = instance["versionId"]?.GetValue<string>() ?? "";
                var current = versionId;
                for (var depth = 0; depth < 5 && !string.IsNullOrWhiteSpace(current); depth++)
                {
                    var jsonFile = Path.Combine(AppPaths.RootDir, "versions", current, current + ".json");
                    if (!File.Exists(jsonFile)) return false;
                    var json = JsonNode.Parse(File.ReadAllText(jsonFile)) as JsonObject;
                    var args = (json?["arguments"]?["game"] as JsonArray)?.OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList() ?? new List<string>();
                    string? Next(string key)
                    {
                        var index = args.IndexOf(key);
                        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
                    }
                    var neo = Next("--fml.neoForgeVersion");
                    var forge = Next("--fml.forgeVersion");
                    var mc = Next("--fml.mcVersion");
                    if (!string.IsNullOrWhiteSpace(mc)) instance["mcVersion"] = mc;
                    if (!string.IsNullOrWhiteSpace(neo)) { instance["loader"] = "neoforge"; instance["loaderVersion"] = neo; return true; }
                    if (!string.IsNullOrWhiteSpace(forge)) { instance["loader"] = "forge"; instance["loaderVersion"] = forge; return true; }
                    var text = json?.ToJsonString() ?? "";
                    var fabric = System.Text.RegularExpressions.Regex.Match(text, "net\\.fabricmc:fabric-loader:([^:\"]+)");
                    var quilt = System.Text.RegularExpressions.Regex.Match(text, "org\\.quiltmc:quilt-loader:([^:\"]+)");
                    if (fabric.Success) { instance["loader"] = "fabric"; instance["loaderVersion"] = fabric.Groups[1].Value; return true; }
                    if (quilt.Success) { instance["loader"] = "quilt"; instance["loaderVersion"] = quilt.Groups[1].Value; return true; }
                    current = json?["inheritsFrom"]?.GetValue<string>() ?? "";
                }
            }
            catch { }
            return false;
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


        public static bool SetOnlineMode(string id, bool enabled)
        {
            if (!Directory.Exists(AppPaths.InstancesDir)) return false;
            foreach (var directory in Directory.GetDirectories(AppPaths.InstancesDir))
            {
                var file = Path.Combine(directory, "instance.json");
                if (!File.Exists(file)) continue;
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject instance) continue;
                    if (instance["id"]?.GetValue<string>() != id) continue;
                    var gameDir = instance["gameDir"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(gameDir)) return false;
                    var properties = Path.Combine(gameDir, "server.properties");
                    var lines = File.Exists(properties) ? File.ReadAllLines(properties).ToList() : new List<string>();
                    var replaced = false;
                    for (var i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].StartsWith("online-mode=", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = "online-mode=" + (enabled ? "true" : "false");
                            replaced = true;
                        }
                    }
                    if (!replaced) lines.Add("online-mode=" + (enabled ? "true" : "false"));
                    File.WriteAllLines(properties, lines);
                    return true;
                }
                catch { }
            }
            return false;
        }

        public static JsonObject AppInfo()
        {
            JsonArray instances;
            try { instances = ListInstances(); }
            catch { instances = new JsonArray(); }
            long playTime = 0;
            foreach (var item in instances)
            {
                try { playTime += item?["totalPlayTimeMs"]?.GetValue<long>() ?? 0; } catch { }
            }
            var versionsDir = Path.Combine(AppPaths.RootDir, "versions");
            var totalVersions = 0;
            try
            {
                if (Directory.Exists(versionsDir))
                    totalVersions = Directory.GetDirectories(versionsDir).Length;
            }
            catch { }
            return new JsonObject
            {
                ["version"] = VersionInfo.Format(typeof(BridgeService).Assembly.GetName().Version),
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
                case "mcmod:english":
                    return await McmodService.English(ArgString(args, 0));
                case "mcmod:search":
                    return await McmodService.Search(ArgString(args, 0), ArgString(args, 1));
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
                case "progress:cancel":
                    ProgressService.Cancel(ArgString(args, 0));
                    return new JsonObject { ["ok"] = true };
                case "app:pick-directory":
                    return SystemService.PickDirectory(ArgString(args, 0), ArgString(args, 1));
                case "app:pick-file":
                    return SystemService.PickFile(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default, args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.True);
                case "updates:check":
                    return await UpdateService.Check(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.True);
                case "updates:download":
                    return await UpdateService.DownloadAndApply(ArgString(args, 0), ArgString(args, 1));
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
                    try { InstanceArchiveService.ImportExisting(true); } catch { }
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
                    try { InstanceArchiveService.ImportExisting(true); } catch { }
                    GameEvents.Emit("settings", updated);
                    NotifyInstances();
                    return updated;
                }
                case "settings:use-portable-root":
                {
                    AppPaths.UseRoot(Path.Combine(AppPaths.BaseDir, ".minecraft"));
                    var updated = UpdateSettings(System.Text.Json.JsonSerializer.SerializeToElement(new { rootDir = AppPaths.RootDir }));
                    try { InstanceArchiveService.ImportExisting(true); } catch { }
                    GameEvents.Emit("settings", updated);
                    NotifyInstances();
                    return updated;
                }
                case "settings:system-info":
                    return SystemService.Info();
                case "memory:optimize":
                    return new JsonObject { ["freed"] = MemoryOptimizer.Optimize() };
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
                case "app:pick-folder":
                    return SystemService.PickFolder();
                case "instances:export":
                    return InstanceArchiveService.Export(ArgString(args, 0));
                case "instances:export-pack":
                {
                    if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.Object)
                    {
                        var options = JsonNode.Parse(args[0].GetRawText()) as JsonObject;
                        return await InstanceArchiveService.ExportPack(options?["id"]?.GetValue<string>() ?? "", options);
                    }
                    return await InstanceArchiveService.ExportPack(ArgString(args, 0));
                }
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
                case "curseforge:file-count":
                    return await CurseForgeService.FileCount(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetInt32() : 0);
                case "curseforge:files-page":
                    return await CurseForgeService.FilesPage(
                        args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetInt32() : 0,
                        args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 ? args[1].GetInt32() : 0,
                        args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 2 ? args[2].GetInt32() : 1000);
                case "curseforge:files":
                    return await CurseForgeService.Files(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0].GetInt32() : 0, ArgString(args, 1), ArgString(args, 2));
                case "mods:dependencies":
                    return await DependencyService.Plan(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "curseforge:install":
                    return await CurseForgeService.Install(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "curseforge:install-pack":
                    return await CurseForgeService.InstallPack(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "curseforge:install-folder":
                    return await CurseForgeService.InstallToFolder(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 ? args[0] : default);
                case "terracotta:install":
                    return await TerracottaService.Ensure(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.True, CancellationToken.None);
                case "terracotta:start":
                    return await TerracottaService.Start();
                case "terracotta:state":
                    return await TerracottaService.State();
                case "terracotta:host":
                    return await TerracottaService.Host(ArgString(args, 0));
                case "terracotta:join":
                    return await TerracottaService.Join(ArgString(args, 0), ArgString(args, 1));
                case "terracotta:reset":
                    return await TerracottaService.Reset();
                case "terracotta:stop":
                    return await TerracottaService.Stop();
                case "easytier:status":
                    return EasyTierService.Status();
                case "easytier:lan-scan":
                    return LanScanService.Scan();
                case "easytier:firewall":
                    return FirewallService.Allow();
                case "easytier:create-invite":
                    return EasyTierService.CreateInvite(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject ?? new JsonObject()
                        : new JsonObject());
                case "easytier:join-invite":
                    return await EasyTierService.JoinInvite(ArgString(args, 0));
                case "easytier:latency":
                    return await EasyTierService.Latency(ArgString(args, 0));
                case "easytier:install":
                    return await EasyTierService.Ensure(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.True, CancellationToken.None);
                case "easytier:start":
                    return await EasyTierService.Start(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject ?? new JsonObject()
                        : new JsonObject());
                case "easytier:stop":
                    return EasyTierService.Stop();
                case "easytier:peers":
                    return await EasyTierService.Peers();
                case "easytier:save":
                    return EasyTierService.Save(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject ?? new JsonObject()
                        : new JsonObject());
                case "news:article":
                    return await NewsService.Article(ArgString(args, 0));
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
                case "instances:online-mode":
                    return args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 && SetOnlineMode(args[0].GetString() ?? "", args[1].GetBoolean());
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
                case "fabricapi:versions":
                    return await FabricApiService.Versions(ArgString(args, 0));
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
                            request["createInstance"]?.GetValue<bool>() ?? true,
                            request["fabricApiVersion"]?.GetValue<string>());
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
                    return await ContentService.List(ArgString(args, 0), ArgString(args, 1));
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
                case "skin:install":
                    return await SkinPatchService.Install(ArgString(args, 0));
                case "accounts:list":
                    await AccountAvatarService.RefreshAll();
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
                case "accounts:skin":
                {
                    var request = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject
                        : null;
                    if (request == null) return null;
                    return await AccountSkinService.Apply(
                        request["accountId"]?.GetValue<string>() ?? "",
                        request["mode"]?.GetValue<string>() ?? "",
                        request["variant"]?.GetValue<string>() ?? "classic",
                        request["data"]?.GetValue<string>(),
                        request["playerName"]?.GetValue<string>() ?? "");
                }
                case "accounts:authlib-profiles":
                    return await AuthlibService.Profiles(ArgString(args, 0));
                case "accounts:authlib-switch":
                    return await AuthlibService.SwitchProfile(ArgString(args, 0), ArgString(args, 1));
                case "accounts:upload-skin":
                {
                    var request = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0
                        ? JsonNode.Parse(args[0].GetRawText()) as JsonObject
                        : null;
                    if (request == null) return null;
                    return await AccountSkinService.UploadMicrosoft(
                        request["accountId"]?.GetValue<string>() ?? "",
                        request["variant"]?.GetValue<string>() ?? "classic",
                        request["data"]?.GetValue<string>());
                }
                case "accounts:capes":
                    return await AccountSkinService.GetCapes(ArgString(args, 0));
                case "accounts:set-cape":
                    await AccountSkinService.SetCape(ArgString(args, 0), ArgString(args, 1));
                    return null;
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
