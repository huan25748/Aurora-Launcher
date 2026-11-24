using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class InstanceArchiveService
    {
        private static JsonObject Instance(string id)
        {
            return BridgeService.GetInstance(id) as JsonObject ?? throw new Exception("实例不存在");
        }

        private static string GameDir(JsonObject instance)
            => instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");

        public static JsonObject Export(string id)
        {
            var instance = Instance(id);
            var gameDir = GameDir(instance);
            var dir = Path.Combine(AppPaths.SettingsDir, "exports");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{SafeName(instance["name"]?.GetValue<string>() ?? id)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            ZipFile.CreateFromDirectory(gameDir, target, CompressionLevel.Fastest, true);
            return new JsonObject { ["path"] = target };
        }

        private static async Task<System.Collections.Generic.Dictionary<string, JsonObject>> ResolveMods(string gameDir)
        {
            var result = new System.Collections.Generic.Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            var modsDir = Path.Combine(gameDir, "mods");
            if (!Directory.Exists(modsDir)) return result;
            var jars = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.AllDirectories).Take(400).ToList();
            var hashes = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var jar in jars)
            {
                try
                {
                    using var stream = File.OpenRead(jar);
                    using var sha = SHA1.Create();
                    hashes[Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant()] = Path.GetRelativePath(gameDir, jar).Replace('\\', '/');
                }
                catch { }
            }
            if (hashes.Count == 0) return result;
            var versions = await ModrinthService.VersionsByHashes(hashes.Keys);
            foreach (var pair in versions)
            {
                if (!hashes.TryGetValue(pair.Key, out var relative)) continue;
                var entry = new JsonObject
                {
                    ["path"] = relative,
                    ["fileSize"] = pair.Value["files"]?[0]?["size"]?.GetValue<long>() ?? 0,
                    ["downloads"] = new JsonArray(pair.Value["files"]?[0]?["url"]?.GetValue<string>() ?? ""),
                    ["hashes"] = new JsonObject
                    {
                        ["sha1"] = pair.Key,
                        ["sha512"] = pair.Value["files"]?[0]?["hashes"]?["sha512"]?.GetValue<string>() ?? ""
                    },
                    ["env"] = new JsonObject { ["client"] = "required", ["server"] = "required" }
                };
                result[relative] = entry;
            }
            return result;
        }

        private static JsonArray BuildResolvedFiles(System.Collections.Generic.Dictionary<string, JsonObject> resolved)
        {
            var array = new JsonArray();
            foreach (var entry in resolved.Values) array.Add(JsonNode.Parse(entry.ToJsonString()));
            return array;
        }

        private static readonly HttpClient ExportHttp = CreateExportHttp();

        private static HttpClient CreateExportHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AuroraLauncher/2.5.0");
            return client;
        }

        private static bool IsExportCandidate(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".zip" && ext != ".rar" && ext != ".jar" && ext != ".disabled" && ext != ".old") return false;
            var lower = path.Replace('\\', '/').ToLowerInvariant();
            return lower.Contains("/mods/") || lower.Contains("/packs/") || lower.Contains("/openloader/") || lower.Contains("/resource") || lower.Contains("/shaderpacks/") || lower.Contains("/resourcepacks/");
        }

        private static uint Murmur2(byte[] data)
        {
            var filtered = new List<byte>(data.Length);
            foreach (var b in data)
                if (b != 9 && b != 10 && b != 13 && b != 32) filtered.Add(b);
            var buf = filtered.ToArray();
            var h = (uint)(1 ^ buf.Length);
            var i = 0;
            for (; i <= buf.Length - 4; i += 4)
            {
                var k = (uint)(buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16) | (buf[i + 3] << 24));
                k *= 0x5BD1E995; k ^= k >> 24; k *= 0x5BD1E995;
                h *= 0x5BD1E995; h ^= k;
            }
            switch (buf.Length - i)
            {
                case 3: h ^= (uint)(buf[i] | (buf[i + 1] << 8)); h ^= (uint)buf[i + 2] << 16; h *= 0x5BD1E995; break;
                case 2: h ^= (uint)(buf[i] | (buf[i + 1] << 8)); h *= 0x5BD1E995; break;
                case 1: h ^= buf[i]; h *= 0x5BD1E995; break;
            }
            h ^= h >> 13; h *= 0x5BD1E995; h ^= h >> 15;
            return h;
        }

        private static void CopyTree(string from, string to, bool root)
        {
            Directory.CreateDirectory(to);
            foreach (var dir in Directory.EnumerateDirectories(from))
            {
                var name = Path.GetFileName(dir);
                if (root)
                {
                    // 完全按 PCL 的导出白名单复制。
                    var allowed = new[] { "mods", "coremods", "lib", "addons", "multiblocked", "modpack-update-checker", "global_packs", "global_resource_packs", "global_data_packs", "optional_data_packs", "maps", "mods-resourcepacks", "matmos", "resource_assorts", "patchouli_books", "openloader", "worldshape", "resources", "scripts", "structures", "fontfiles", "oresources", "packmenu", "craftpresence", "pointblanks", "config", "CustomSkinLoader", "PCL", "resourcepacks", "texturepacks", "shaderpacks" };
                    if (name.Equals("journeymap", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var sub in new[] { "config", "server" })
                        {
                            var source = Path.Combine(from, sub);
                            if (Directory.Exists(source)) CopyTree(source, Path.Combine(to, sub), false);
                        }
                        continue;
                    }
                    if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase) && !name.StartsWith("kubejs", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("template", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (root && (name.EndsWith("-natives", StringComparison.OrdinalIgnoreCase) || name.StartsWith("natives-", StringComparison.OrdinalIgnoreCase))) continue;
                CopyTree(dir, Path.Combine(to, name), false);
            }
            foreach (var file in Directory.EnumerateFiles(from))
            {
                var fileName = Path.GetFileName(file);
                if (root)
                {
                    var keep = new[] { "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat", "icon.png" };
                    if (!keep.Contains(fileName, StringComparer.OrdinalIgnoreCase)) continue;
                }
                File.Copy(file, Path.Combine(to, fileName), true);
            }
        }

        public static async Task<JsonObject> ExportPack(string id, JsonObject? options = null)
        {
            var instance = Instance(id);
            var gameDir = GameDir(instance);
            var name = options?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) name = instance["name"]?.GetValue<string>() ?? id;
            var version = options?["version"]?.GetValue<string>() ?? "1.0.0";
            var summary = options?["summary"]?.GetValue<string>() ?? "";
            var format = (options?["format"]?.GetValue<string>() ?? "mrpack").ToLowerInvariant();
            var extension = format == "zip" ? "zip" : "mrpack";
            var outputDir = options?["outputDir"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = Path.Combine(AppPaths.SettingsDir, "packs");
            Directory.CreateDirectory(outputDir);
            var target = Path.Combine(outputDir, SafeName(name!) + "." + extension);
            var temp = Path.Combine(Path.GetTempPath(), "aurora-export-" + Guid.NewGuid().ToString("N")[..8]);
            var packDir = Path.Combine(temp, "modpack");
            var overrides = Path.Combine(packDir, "overrides");
            try
            {
                CopyTree(gameDir, overrides, true);
                // 明确按 PCL 的规则复制旅行地图配置，避免白名单分支漏掉。
                foreach (var sub in new[] { "config", "server" })
                {
                    var source = Path.Combine(gameDir, "journeymap", sub);
                    if (Directory.Exists(source)) CopyTree(source, Path.Combine(overrides, "journeymap", sub), false);
                }
                // PCL 的排除规则。
                foreach (var file in Directory.EnumerateFiles(Path.Combine(overrides, "mods"), "*", SearchOption.TopDirectoryOnly))
                    if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(file); } catch { }
                Remove("mods/.connector");
                Remove("mods/mcef-libraries");
                Remove("mods/mcef-cache");
                Remove("config/accountsx");
                Remove("config/jei/world");
                void KeepOnly(string relative, string keep)
                {
                    var dir = Path.Combine(overrides, relative);
                    if (!Directory.Exists(dir)) return;
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        if (!string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase))
                            try { File.Delete(file); } catch { }
                }
                KeepOnly("config/worldedit", "worldedit.properties");
                KeepOnly("config/spark", "config.json");
                foreach (var dir in Directory.EnumerateDirectories(overrides, "kubejs*", SearchOption.TopDirectoryOnly))
                {
                    Remove(Path.GetRelativePath(overrides, Path.Combine(dir, "probe")));
                    Remove(Path.GetRelativePath(overrides, Path.Combine(dir, "exported")));
                    try { File.Delete(Path.Combine(dir, "jsconfig.json")); } catch { }
                    try { File.Delete(Path.Combine(dir, "README.txt")); } catch { }
                }
                bool Include(string key, bool fallback) =>
                    options?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
                void Remove(string relative)
                {
                    try
                    {
                        var path = Path.Combine(overrides, relative);
                        if (Directory.Exists(path)) Directory.Delete(path, true);
                        else if (File.Exists(path)) File.Delete(path);
                    }
                    catch { }
                }
                if (!Include("includeMods", true)) Remove("mods");
                if (!Include("includeResourcePacks", true)) Remove("resourcepacks");
                if (!Include("includeShaderPacks", true)) Remove("shaderpacks");
                if (!Include("includeSaves", true)) Remove("saves");
                if (!Include("includeScreenshots", false)) Remove("screenshots");
                if (!Include("includeConfig", true)) Remove("config");
                if (!Include("includeSaves", true)) Remove("saves");
                if (!Include("includeResourcePacks", true)) Remove("resourcepacks");
                if (!Include("includeShaderPacks", true)) Remove("shaderpacks");
                if (!Include("includeScreenshots", false)) Remove("screenshots");
                // PCL 默认导出 journeymap/config 与 journeymap/server，地图数据需单独勾选。
                if (!Include("includeMapData", false)) { Remove("journeymap/data"); Remove("xaero"); Remove("XaeroWaypoints"); Remove("XaeroWorldMap"); }
                Remove("journeymap/logs");
                if (!Include("includeLogs", false)) { Remove("logs"); Remove("crash-reports"); }
                if (!Include("includeServers", true)) Remove("servers.dat");
                if (!Include("includeOptions", true)) { Remove("options.txt"); Remove("optionsof.txt"); }
                try
                {
                    // 只保留 PCL 导出会带的根目录文件。
                    var keepRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "options.txt", "optionsof.txt", "servers.dat" };
                    foreach (var file in Directory.GetFiles(overrides, "*", SearchOption.TopDirectoryOnly))
                        if (!keepRoot.Contains(Path.GetFileName(file))) File.Delete(file);
                }
                catch { }
                // Resolve downloads exactly like PCL: hash every jar/zip and ask
                // Modrinth + CurseForge; resolved files leave overrides.
                var downloads = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var cfHashes = new Dictionary<uint, string>();
                var candidates = Directory.EnumerateFiles(overrides, "*", SearchOption.AllDirectories).Where(IsExportCandidate).ToList();
                foreach (var file in candidates)
                {
                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(file);
                        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
                        var rel = Path.GetRelativePath(overrides, file).Replace('\\', '/');
                        hashes[sha1] = rel;
                        cfHashes[Murmur2(bytes)] = rel;
                    }
                    catch { }
                }
                try
                {
                    var modrinth = await ModrinthService.VersionsByHashes(hashes.Keys);
                    foreach (var pair in modrinth)
                    {
                        if (!hashes.ContainsKey(pair.Key)) continue;
                        var url = pair.Value["files"]?[0]?["url"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            if (!downloads.TryGetValue(pair.Key, out var list)) downloads[pair.Key] = list = new List<string>();
                            list.Add(url!);
                        }
                    }
                }
                catch { }
                try
                {
                    var body = JsonSerializer.Serialize(new { fingerprints = cfHashes.Keys.ToArray() });
                    using var response = await HttpFactory.Client.PostAsync("https://mod.mcimirror.top/curseforge/v1/fingerprints/432",
                        new StringContent(body, Encoding.UTF8, "application/json"));
                    var text = await response.Content.ReadAsStringAsync();
                    var parsed = JsonNode.Parse(text);
                    try { var logDir = Path.Combine(AppPaths.SettingsDir, "logs"); Directory.CreateDirectory(logDir); File.AppendAllText(Path.Combine(logDir, "export.log"), DateTime.Now.ToString("HH:mm:ss") + " cf fingerprints=" + cfHashes.Count + " response=" + text[..Math.Min(200, text.Length)] + Environment.NewLine); } catch { }
                    if (parsed is JsonObject cf && cf["data"]?["exactMatches"] is JsonArray matches)
                    {
                        foreach (var item in matches.OfType<JsonObject>())
                        {
                            var url = item["file"]?["downloadUrl"]?.GetValue<string>();
                            var sha1 = (item["file"]?["hashes"] as JsonArray)?.OfType<JsonObject>()
                                .FirstOrDefault(hash => hash["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>() ?? "";
                            if (string.IsNullOrWhiteSpace(url) || !hashes.TryGetValue(sha1, out var rel)) continue;
                            if (!downloads.TryGetValue(sha1, out var list)) downloads[sha1] = list = new List<string>();
                            if (!list.Contains(url!)) list.Add(url!);
                            var mirror = url!.Replace("edge.forgecdn.net", "mediafilez.forgecdn.net");
                            if (!list.Contains(mirror)) list.Add(mirror);
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { var logDir = Path.Combine(AppPaths.SettingsDir, "logs"); Directory.CreateDirectory(logDir); File.AppendAllText(Path.Combine(logDir, "export.log"), DateTime.Now.ToString("HH:mm:ss") + " cf failed: " + ex.Message + Environment.NewLine); } catch { }
                }
                var files = new JsonArray();
                foreach (var pair in downloads)
                {
                    if (!hashes.TryGetValue(pair.Key, out var rel)) continue;
                    var file = Path.Combine(overrides, rel);
                    if (!File.Exists(file)) continue;
                    var sha512 = Convert.ToHexString(SHA512.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant();
                    files.Add(new JsonObject
                    {
                        ["path"] = rel,
                        ["hashes"] = new JsonObject { ["sha1"] = pair.Key, ["sha512"] = sha512 },
                        ["downloads"] = new JsonArray(pair.Value
                            .SelectMany(url => new[] { url, url.Replace("edge.forgecdn.net", "mediafilez.forgecdn.net").Replace("mediafilez.forgecdn.net", "mediafilez.forgecdn.net") })
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(url => url.Contains("mediafilez.forgecdn.net") ? 0 : url.Contains("edge.forgecdn.net") ? 1 : 2)
                            .Select(url => (JsonNode)url).ToArray()),
                        ["fileSize"] = new FileInfo(file).Length
                    });
                    try { File.Delete(file); } catch { }
                }
                var dependencies = new JsonObject { ["minecraft"] = instance["mcVersion"]?.GetValue<string>() ?? "" };
                var loader = instance["loader"]?.GetValue<string>() ?? "vanilla";
                var loaderVersion = instance["loaderVersion"]?.GetValue<string>() ?? "";
                if (loaderVersion.Length > 0 && loader != "vanilla")
                {
                    var key = loader switch { "forge" => "forge", "neoforge" => "neoforge", "fabric" => "fabric-loader", "quilt" => "quilt-loader", _ => "" };
                    if (key.Length > 0) dependencies[key] = loaderVersion;
                }
                var index = new JsonObject
                {
                    ["formatVersion"] = 1,
                    ["game"] = "minecraft",
                    ["versionId"] = version,
                    ["name"] = name,
                    ["summary"] = summary,
                    ["files"] = files,
                    ["dependencies"] = dependencies
                };
                using (var stream = File.Create(Path.Combine(packDir, "modrinth.index.json")))
                using (var writer = new StreamWriter(stream))
                    writer.Write(index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(target)) File.Delete(target);
                ZipFile.CreateFromDirectory(packDir, target);
                return new JsonObject { ["path"] = target, ["size"] = new FileInfo(target).Length, ["files"] = files.Count, ["format"] = format };
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        public static async Task<JsonObject?> Import()
        {
            var selected = SystemService.PickFile(JsonSerializer.SerializeToElement(new[] { new { name = "整合包 / 实例压缩包", extensions = new[] { "zip", "mrpack" } } }), false);
            var paths = selected["paths"] as JsonArray;
            if (paths == null || paths.Count == 0) return null;
            var source = paths[0]?.GetValue<string>() ?? throw new Exception("文件路径为空");
            return await ImportFromPath(source);
        }

        /// <summary>按文件路径导入整合包（拖拽、命令行、文件关联共用）。</summary>
        public static async Task<JsonObject?> ImportFromPath(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new Exception("整合包文件不存在：" + source);
            var isModrinthPack = false;
            try
            {
                using var archive = ZipFile.OpenRead(source);
                isModrinthPack = archive.GetEntry("modrinth.index.json") != null;
            }
            catch { }
            if (isModrinthPack)
            {
                var imported = await LocalModpackService.Import(source, Path.GetFileNameWithoutExtension(source));
                return imported;
            }
            var name = Path.GetFileNameWithoutExtension(source);
            PackMeta? pack = null;
            try
            {
                using var archive = ZipFile.OpenRead(source);
                if (archive.GetEntry("manifest.json") is { } manifestEntry)
                {
                    using var stream = manifestEntry.Open();
                    using var reader = new StreamReader(stream);
                    var manifest = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
                    if (manifest != null)
                    {
                        var format = manifest["format"]?.GetValue<string>();
                        if (string.Equals(format, "aurora-modpack-v1", StringComparison.OrdinalIgnoreCase))
                        {
                            pack = new PackMeta
                            {
                                McVersion = manifest["mcVersion"]?.GetValue<string>() ?? "",
                                Loader = manifest["loader"]?.GetValue<string>() ?? "vanilla",
                                LoaderVersion = manifest["loaderVersion"]?.GetValue<string>() ?? "",
                                Prefix = ""
                            };
                        }
                        else if (string.Equals(manifest["manifestType"]?.GetValue<string>(), "minecraftModpack", StringComparison.OrdinalIgnoreCase))
                        {
                            var mcVersion = manifest["minecraft"]?["version"]?.GetValue<string>() ?? "";
                            var loaderId = ((manifest["minecraft"]?["modLoaders"] ?? manifest["minecraft"]?["loaders"]) as JsonArray)?.OfType<JsonObject>()
                                .Select(item => item["id"]?.GetValue<string>() ?? "").FirstOrDefault(id => id.Length > 0) ?? "";
                            var split = loaderId.IndexOf('-');
                            var loader = split > 0 ? loaderId[..split] : "";
                            var loaderVersion = split > 0 ? loaderId[(split + 1)..] : "";
                            pack = new PackMeta { McVersion = mcVersion, Loader = loader, LoaderVersion = loaderVersion, Prefix = "overrides/" };
                        }
                    }
                }
                else if (archive.GetEntry("mmc-pack.json") is { } mmcEntry)
                {
                    using var stream = mmcEntry.Open();
                    using var reader = new StreamReader(stream);
                    var mmc = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
                    if (mmc?["components"] is JsonArray components)
                    {
                        var mcVersion = "";
                        var loader = "";
                        var loaderVersion = "";
                        foreach (var component in components.OfType<JsonObject>())
                        {
                            var uid = component["uid"]?.GetValue<string>() ?? "";
                            var version = component["version"]?.GetValue<string>() ?? "";
                            switch (uid)
                            {
                                case "net.minecraft": mcVersion = version; break;
                                case "net.minecraftforge": loader = "forge"; loaderVersion = version; break;
                                case "net.neoforged": loader = "neoforge"; loaderVersion = version; break;
                                case "net.fabricmc.fabric-loader": loader = "fabric"; loaderVersion = version; break;
                                case "org.quiltmc.quilt-loader": loader = "quilt"; loaderVersion = version; break;
                            }
                        }
                        pack = new PackMeta { McVersion = mcVersion, Loader = loader, LoaderVersion = loaderVersion, Prefix = ".minecraft/" };
                    }
                }
            }
            catch { }
            if (pack != null && !string.IsNullOrWhiteSpace(pack.McVersion))
                return await InstallPackWithMeta(source, pack, name);
            if (pack != null && string.IsNullOrWhiteSpace(pack.McVersion) && string.Equals(pack.Loader, "vanilla", StringComparison.OrdinalIgnoreCase))
            {
                // Cannot infer the game version from this archive; fall back to the
                // generic import instead of guessing.
                pack = null;
            }
            var id = "inst-" + Guid.NewGuid().ToString("N")[..12];
            var target = Path.Combine(AppPaths.InstancesDir, id);
            Directory.CreateDirectory(target);
            ExtractSafe(source, target);
            JsonObject instance;
            var instanceFile = Path.Combine(target, "instance.json");
            if (File.Exists(instanceFile))
            {
                instance = (JsonObject)JsonNode.Parse(File.ReadAllText(instanceFile))!;
            }
            else
            {
                instance = new JsonObject
                {
                    ["name"] = name,
                    ["versionId"] = "",
                    ["mcVersion"] = "",
                    ["loader"] = "vanilla",
                    ["icon"] = "📦",
                    ["color"] = "ocean",
                    ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["totalPlayTimeMs"] = 0,
                    ["playCount"] = 0
                };
            }
            foreach (var sub in new[] { "mods", "saves", "resourcepacks", "shaderpacks", "logs" })
                Directory.CreateDirectory(Path.Combine(target, sub));
            instance["id"] = id;
            instance["gameDir"] = target;
            File.WriteAllText(instanceFile, instance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            BridgeService.NotifyInstances();
            return instance;
        }

        private sealed class PackMeta
        {
            public string McVersion { get; set; } = "";
            public string Loader { get; set; } = "vanilla";
            public string LoaderVersion { get; set; } = "";
            public string Prefix { get; set; } = "overrides/";
        }

        private static async Task<JsonObject> InstallPackWithMeta(string source, PackMeta pack, string name)
        {
            ProgressService.Begin("导入整合包");
            string instanceId;
            var kind = pack.Loader.ToLowerInvariant();
            if (kind is "forge" or "neoforge")
            {
                var result = await ForgeService.Install(kind, pack.McVersion, pack.LoaderVersion, name, true);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            else if (kind is "fabric" or "quilt")
            {
                var result = await LoaderService.Install(kind, pack.McVersion, pack.LoaderVersion, name, true);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            else
            {
                var result = await MinecraftInstallService.InstallVanilla(pack.McVersion, true, name);
                instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
            }
            var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
            var gameDir = instance["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            ProgressService.Step("解压整合包内容", 1, Path.GetFileName(source));
            using (var archive = ZipFile.OpenRead(source))
            {
                var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "manifest.json", "modrinth.index.json", "mmc-pack.json", "instance.cfg", "instance.json" };
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var full = entry.FullName.Replace('\\', '/');
                    if (skipped.Contains(full)) continue;
                    var relative = full;
                    if (!string.IsNullOrEmpty(pack.Prefix))
                    {
                        if (!full.StartsWith(pack.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        relative = full[pack.Prefix.Length..];
                    }
                    if (relative.Length == 0) continue;
                    var target = Path.GetFullPath(Path.Combine(gameDir, relative));
                    if (!target.StartsWith(Path.GetFullPath(gameDir), StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                }
            }
            instance["mcVersion"] = pack.McVersion;
            instance["loader"] = string.IsNullOrWhiteSpace(pack.Loader) ? "vanilla" : pack.Loader;
            instance["loaderVersion"] = pack.LoaderVersion;
            var instanceFile = Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");
            File.WriteAllText(instanceFile, instance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            ProgressService.Update(1, "整合包已导入");
            ProgressService.Complete();
            BridgeService.NotifyInstances();
            return instance;
        }

        public static int ImportExisting(bool includeLooseVersions = true)
        {
            var versionsDir = Path.Combine(AppPaths.RootDir, "versions");
            if (!Directory.Exists(versionsDir)) return 0;
            var instances = BridgeService.ListInstances().OfType<JsonObject>().ToList();
            var existing = instances
                .Select(item => new { VersionId = item["versionId"]?.GetValue<string>() ?? "", Instance = item })
                .Where(item => item.VersionId.Length > 0)
                .GroupBy(item => item.VersionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Instance, StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var directory in Directory.GetDirectories(versionsDir))
            {
                var id = Path.GetFileName(directory);
                if (!File.Exists(Path.Combine(directory, id + ".json"))) continue;
                var (mcVersion, loader, loaderVersion) = DetectVersionInfo(id, directory);
                if (existing.TryGetValue(id, out var current))
                {
                    // Repair older imports that were classified as vanilla before
                    // NeoForge/Forge detection was fixed.
                    if (current["imported"]?.GetValue<bool>() == true
                        && (current["loader"]?.GetValue<string>() != loader
                            || current["loaderVersion"]?.GetValue<string>() != loaderVersion))
                    {
                        current["loader"] = loader;
                        current["loaderVersion"] = loaderVersion;
                        current["mcVersion"] = mcVersion;
                        current["icon"] = loader switch
                        {
                            "fabric" => "🧵",
                            "quilt" => "🧶",
                            "forge" => "🔨",
                            "neoforge" => "⚙️",
                            "optifine" => "🎨",
                            _ => "🧊"
                        };
                        var file = Path.Combine(AppPaths.InstancesDir, current["id"]?.GetValue<string>() ?? "", "instance.json");
                        if (File.Exists(file))
                            File.WriteAllText(file, current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        count++;
                    }
                    continue;
                }
                // Aurora's own instance folders can be restored even when other instances exist.
                var saved = Path.Combine(directory, "instance.json");
                if (!includeLooseVersions && !File.Exists(saved)) continue;
                var gameDir = DetectGameDir(id);
                if (CreateImportedInstance(id, id, gameDir, mcVersion, loader, loaderVersion))
                {
                    existing[id] = new JsonObject();
                    count++;
                }
            }
            if (count > 0) BridgeService.NotifyInstances();
            return count;
        }

        private static bool CreateImportedInstance(string versionId, string name, string gameDir, string mcVersion, string loader, string? loaderVersion)
        {
            try
            {
                var instancesDir = AppPaths.InstancesDir;
                Directory.CreateDirectory(instancesDir);
                var id = "inst-" + Guid.NewGuid().ToString("N")[..12];
                var directory = Path.Combine(instancesDir, id);
                Directory.CreateDirectory(directory);
                var json = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["versionId"] = versionId,
                    ["sourceVersionId"] = versionId,
                    ["selfContained"] = false,
                    ["imported"] = true,
                    ["mcVersion"] = mcVersion,
                    ["loader"] = loader,
                    ["loaderVersion"] = loaderVersion,
                    ["gameDir"] = gameDir,
                    ["icon"] = loader switch
                    {
                        "fabric" => "🧵",
                        "quilt" => "🧶",
                        "forge" => "🔨",
                        "neoforge" => "⚙️",
                        "optifine" => "🎨",
                        _ => "🧊"
                    },
                    ["color"] = "ocean",
                    ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["totalPlayTimeMs"] = 0,
                    ["playCount"] = 0
                };
                File.WriteAllText(Path.Combine(directory, "instance.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string DetectGameDir(string versionId)
        {
            var versionPath = Path.Combine(AppPaths.RootDir, "versions", versionId);
            var markers = new[] { "mods", "saves", "resourcepacks", "shaderpacks", "options.txt", "logs", "crash-reports" };
            var isolated = markers.Any(marker => Directory.Exists(Path.Combine(versionPath, marker)) || File.Exists(Path.Combine(versionPath, marker)));
            var rootMarkers = new[] { "mods", "saves", "resourcepacks", "options.txt" };
            var rootUsed = rootMarkers.Any(marker => Directory.Exists(Path.Combine(AppPaths.RootDir, marker)) || File.Exists(Path.Combine(AppPaths.RootDir, marker)));
            return isolated || !rootUsed ? versionPath : AppPaths.RootDir;
        }

        private static (string McVersion, string Loader, string? LoaderVersion) DetectVersionInfo(string id, string versionDir)
        {
            var jsonPath = Path.Combine(versionDir, id + ".json");
            JsonObject? json = null;
            try { json = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject; } catch { }
            var lower = id.ToLowerInvariant();
            string loader = "vanilla";
            string? loaderVersion = null;
            if (lower.Contains("neoforge"))
            {
                loader = "neoforge";
                var index = lower.IndexOf("neoforge", StringComparison.Ordinal);
                loaderVersion = id[(index + "neoforge".Length)..].TrimStart('-', '_', ' ');
            }
            else if (lower.Contains("forge"))
            {
                loader = "forge";
                var index = lower.IndexOf("forge", StringComparison.Ordinal);
                loaderVersion = id[(index + "forge".Length)..].TrimStart('-', '_', ' ');
            }
            else if (lower.StartsWith("fabric-loader-"))
            {
                loader = "fabric";
                var rest = id["fabric-loader-".Length..];
                var dash = rest.LastIndexOf('-');
                loaderVersion = dash > 0 ? rest[..dash] : rest;
                id = dash > 0 ? rest[(dash + 1)..] : id;
            }
            else if (lower.StartsWith("quilt-loader-"))
            {
                loader = "quilt";
                var rest = id["quilt-loader-".Length..];
                var dash = rest.LastIndexOf('-');
                loaderVersion = dash > 0 ? rest[..dash] : rest;
                id = dash > 0 ? rest[(dash + 1)..] : id;
            }
            else
            {
                var mainClass = json?["mainClass"]?.GetValue<string>() ?? "";
                var librariesText = json?["libraries"]?.ToJsonString() ?? "";
                if (mainClass.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase))
                    loader = librariesText.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "neoforge" : "forge";
                else if (mainClass.Contains("fabric", StringComparison.OrdinalIgnoreCase)) loader = "fabric";
                else if (mainClass.Contains("quilt", StringComparison.OrdinalIgnoreCase)) loader = "quilt";
            }
            var mcMatch = System.Text.RegularExpressions.Regex.Match(id, @"^(\d+\.\d+(?:\.\d+)?)");
            var mcVersion = mcMatch.Success ? mcMatch.Groups[1].Value : id;
            if (json?["inheritsFrom"]?.GetValue<string>() is string parent && parent.Length > 0)
                mcVersion = parent;
            return (mcVersion, loader, loaderVersion);
        }

        private static string SafeName(string name)
        {
            var result = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
            return result.Length == 0 ? "instance" : result;
        }

        private static void ExtractSafe(string zip, string target)
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }
    }
}
