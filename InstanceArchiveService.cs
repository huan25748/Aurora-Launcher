using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

        public static JsonObject ExportPack(string id)
        {
            var instance = Instance(id);
            var gameDir = GameDir(instance);
            var dir = Path.Combine(AppPaths.SettingsDir, "packs");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{SafeName(instance["name"]?.GetValue<string>() ?? id)}-modpack-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
            var folders = new[] { "mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks", "datapacks" };
            var files = 0;
            foreach (var folder in folders)
            {
                var from = Path.Combine(gameDir, folder);
                if (!Directory.Exists(from)) continue;
                foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(gameDir, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, relative);
                    files++;
                }
            }
            foreach (var name in new[] { "options.txt", "optionsof.txt" })
            {
                var file = Path.Combine(gameDir, name);
                if (File.Exists(file)) archive.CreateEntryFromFile(file, name);
            }
            var manifest = new JsonObject
            {
                ["format"] = "aurora-modpack-v1",
                ["name"] = instance["name"]?.GetValue<string>() ?? id,
                ["mcVersion"] = instance["mcVersion"]?.GetValue<string>() ?? "",
                ["loader"] = instance["loader"]?.GetValue<string>() ?? "vanilla",
                ["loaderVersion"] = instance["loaderVersion"]?.GetValue<string>() ?? "",
                ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["launcher"] = "Aurora Launcher"
            };
            using (var stream = archive.CreateEntry("manifest.json").Open())
            using (var writer = new StreamWriter(stream))
                writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var size = new FileInfo(target).Length;
            return new JsonObject { ["path"] = target, ["size"] = size, ["files"] = files };
        }

        public static async Task<JsonObject?> Import()
        {
            var selected = SystemService.PickFile(JsonSerializer.SerializeToElement(new[] { new { name = "整合包 / 实例压缩包", extensions = new[] { "zip", "mrpack" } } }), false);
            var paths = selected["paths"] as JsonArray;
            if (paths == null || paths.Count == 0) return null;
            var source = paths[0]?.GetValue<string>() ?? throw new Exception("文件路径为空");
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
                    ["name"] = Path.GetFileNameWithoutExtension(source),
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
