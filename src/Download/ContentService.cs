using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;

namespace AuroraLauncher
{
    internal static class ContentService
    {
        private static string GameDir(string instanceId)
        {
            var instance = BridgeService.GetInstance(instanceId) as JsonObject
                ?? throw new Exception("实例不存在");
            return instance["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
        }

        private static string ContentDir(string instanceId, string kind)
        {
            var dir = Path.Combine(GameDir(instanceId), kind);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static Dictionary<string, string>? _iconCache;

        private static string IconCachePath => Path.Combine(AppPaths.RootDir, ".aurora", "content-icons.json");

        private static Dictionary<string, string> LoadIconCache()
        {
            if (_iconCache != null) return _iconCache;
            try
            {
                if (File.Exists(IconCachePath))
                    _iconCache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IconCachePath)) ?? new();
            }
            catch { }
            return _iconCache ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveIconCache(Dictionary<string, string> cache)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IconCachePath)!);
                File.WriteAllText(IconCachePath, JsonSerializer.Serialize(cache));
            }
            catch { }
        }

        private static Dictionary<string, (long Size, long Mtime, string Hash)>? _hashCache;

        private static string HashCachePath => Path.Combine(AppPaths.RootDir, ".aurora", "content-hashes.json");

        private static Dictionary<string, (long Size, long Mtime, string Hash)> LoadHashCache()
        {
            if (_hashCache != null) return _hashCache;
            try
            {
                if (File.Exists(HashCachePath))
                    _hashCache = JsonSerializer.Deserialize<Dictionary<string, (long Size, long Mtime, string Hash)>>(File.ReadAllText(HashCachePath));
            }
            catch { }
            return _hashCache ??= new Dictionary<string, (long Size, long Mtime, string Hash)>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveHashCache(Dictionary<string, (long Size, long Mtime, string Hash)> cache)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HashCachePath)!);
                File.WriteAllText(HashCachePath, JsonSerializer.Serialize(cache));
            }
            catch { }
        }

        private static string Sha1(string file)
        {
            try
            {
                var info = new FileInfo(file);
                var cache = LoadHashCache();
                if (cache.TryGetValue(file, out var entry) && entry.Size == info.Length && entry.Mtime == info.LastWriteTimeUtc.Ticks)
                    return entry.Hash;
                using var stream = File.OpenRead(file);
                using var sha = SHA1.Create();
                var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                cache[file] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
                SaveHashCache(cache);
                return hash;
            }
            catch { return ""; }
        }

        private static readonly Dictionary<string, (long Mtime, string? Icon)> ModIconCache = new(StringComparer.OrdinalIgnoreCase);

        private static string? ModIcon(string file)
        {
            try
            {
                var stamp = new FileInfo(file);
                if (ModIconCache.TryGetValue(file, out var cached) && cached.Mtime == stamp.LastWriteTimeUtc.Ticks)
                    return cached.Icon;
                using var zip = ZipFile.OpenRead(file);
                string? path = null;
                var fabric = zip.GetEntry("fabric.mod.json");
                if (fabric != null)
                {
                    using var reader = new StreamReader(fabric.Open());
                    var json = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
                    path = json?["icon"]?.GetValue<string>();
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    foreach (var tomlName in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
                    {
                        var toml = zip.GetEntry(tomlName);
                        if (toml == null) continue;
                        using var reader = new StreamReader(toml.Open());
                        var text = string.Join("\n", reader.ReadToEnd().Split('\n').Where(line => !line.TrimStart().StartsWith("#")));
                        var match = System.Text.RegularExpressions.Regex.Match(text, "logoFile\\s*=\\s*[\"']([^\"']+)[\"']");
                        if (match.Success) { path = match.Groups[1].Value; break; }
                    }
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    var info = zip.GetEntry("mcmod.info");
                    if (info != null)
                    {
                        using var reader = new StreamReader(info.Open());
                        var json = JsonNode.Parse(reader.ReadToEnd());
                        path = (json as JsonArray)?[0]?["logoFile"]?.GetValue<string>();
                    }
                }
                if (!string.IsNullOrWhiteSpace(path) && zip.GetEntry(path!.Replace('\\', '/')) == null) path = null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    foreach (var candidate in new[] { "icon.png", "logo.png" })
                    {
                        if (zip.GetEntry(candidate) != null) { path = candidate; break; }
                    }
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = zip.Entries.FirstOrDefault(entry =>
                        System.Text.RegularExpressions.Regex.IsMatch(entry.FullName, "^assets/[^/]+/(icon|logo)\\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))?.FullName;
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    var rootImages = zip.Entries.Where(entry => !entry.FullName.Contains('/') && entry.Length < 2 * 1024 * 1024 &&
                        System.Text.RegularExpressions.Regex.IsMatch(entry.FullName, @"\.(png|jpe?g|gif|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
                    path = rootImages.FirstOrDefault(entry => System.Text.RegularExpressions.Regex.IsMatch(entry.FullName, "icon|logo|cover|avatar", System.Text.RegularExpressions.RegexOptions.IgnoreCase))?.FullName
                        ?? rootImages.FirstOrDefault()?.FullName;
                }
                if (string.IsNullOrWhiteSpace(path)) return null;
                var entry = zip.GetEntry(path!.Replace('\\', '/')) ?? zip.GetEntry(path.TrimStart('/'));
                if (entry == null || entry.Length > 2 * 1024 * 1024) return null;
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var data = "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
                ModIconCache[file] = (stamp.LastWriteTimeUtc.Ticks, data);
                return data;
            }
            catch { return null; }
        }

        private static string? PackIcon(string file)
        {
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var entry = zip.GetEntry("pack.png") ?? zip.GetEntry("icon.png");
                if (entry == null || entry.Length > 1024 * 1024) return null;
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
            }
            catch { return null; }
        }

        public static async Task<JsonArray> List(string instanceId, string kind)
        {
            var dir = ContentDir(instanceId, kind);
            var result = new JsonArray();
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                var enabled = true;
                var display = name;
                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = false;
                    display = name.Substring(0, name.Length - ".disabled".Length);
                }
                if (kind == "mods" && !display.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                if (kind != "mods" && !display.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                long size = 0;
                long mtime = 0;
                try { var info = new FileInfo(file); size = info.Length; mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(); } catch { }
                result.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(display),
                    ["fileName"] = display,
                    ["path"] = file,
                    ["enabled"] = enabled,
                    ["size"] = size,
                    ["mtime"] = mtime,
                    ["kind"] = kind
                });
            }
            try
            {
                foreach (var item in result.OfType<JsonObject>())
                {
                    var path = item["path"]?.GetValue<string>() ?? "";
                    var icon = kind == "mods" ? ModIcon(path) : PackIcon(path);
                    if (icon != null) item["iconData"] = icon;
                }
                if (kind == "mods")
                {
                    var icons = LoadIconCache();
                    var missing = new List<string>();
                    foreach (var item in result.OfType<JsonObject>())
                    {
                        if (item["iconData"] != null || item["icon"] != null) continue;
                        var hash = Sha1(item["path"]?.GetValue<string>() ?? "");
                        if (hash.Length != 40) continue;
                        if (icons.TryGetValue(hash, out var online)) item["icon"] = online;
                        else missing.Add(hash);
                    }
                    if (missing.Count > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var found = await ModrinthService.Icons(missing);
                                if (found.Count == 0) return;
                                var current = LoadIconCache();
                                foreach (var pair in found) current[pair.Key] = pair.Value;
                                SaveIconCache(current);
                            }
                            catch { }
                        });
                    }
                }
            }
            catch { }
            return new JsonArray(result.OfType<JsonObject>()
                .OrderBy(item => item["name"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .Select(item => JsonNode.Parse(item.ToJsonString())).ToArray());
        }

        public static void Toggle(string instanceId, string kind, string fileName)
        {
            var dir = ContentDir(instanceId, kind);
            var enabled = Path.Combine(dir, fileName);
            var disabled = enabled + ".disabled";
            if (File.Exists(enabled)) File.Move(enabled, disabled);
            else if (File.Exists(disabled)) File.Move(disabled, enabled);
        }

        public static void Remove(string instanceId, string kind, string fileName)
        {
            var file = Path.Combine(ContentDir(instanceId, kind), fileName);
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(file + ".disabled")) File.Delete(file + ".disabled");
        }

        public static int Import(string instanceId, string kind)
        {
            var files = Array.Empty<string>();
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择文件",
                    Multiselect = true,
                    Filter = kind == "mods" ? "模组|*.jar" : "压缩包|*.zip"
                };
                if (dialog.ShowDialog() == true) files = dialog.FileNames;
            });
            var target = ContentDir(instanceId, kind);
            var count = 0;
            foreach (var source in files)
            {
                try
                {
                    var destination = Path.Combine(target, Path.GetFileName(source));
                    File.Copy(source, destination, true);
                    count++;
                }
                catch { }
            }
            return count;
        }

        public static JsonArray Worlds(string instanceId)
        {
            var dir = Path.Combine(GameDir(instanceId), "saves");
            Directory.CreateDirectory(dir);
            var result = new JsonArray();
            foreach (var folder in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(folder);
                long size = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                        try { size += new FileInfo(file).Length; } catch { }
                }
                catch { }
                long mtime = 0;
                try { mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder)).ToUnixTimeMilliseconds(); } catch { }
                result.Add(new JsonObject
                {
                    ["name"] = name,
                    ["folderName"] = name,
                    ["path"] = folder,
                    ["size"] = size,
                    ["mtime"] = mtime
                });
            }
            return new JsonArray(result.OfType<JsonObject>()
                .OrderByDescending(item => item["mtime"]?.GetValue<long>() ?? 0)
                .Select(item => JsonNode.Parse(item.ToJsonString())).ToArray());
        }

        public static JsonArray Screenshots(string instanceId)
        {
            var dir = Path.Combine(GameDir(instanceId), "screenshots");
            Directory.CreateDirectory(dir);
            var result = new JsonArray();
            foreach (var file in Directory.GetFiles(dir).Where(file => file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
            {
                var info = new FileInfo(file);
                result.Add(new JsonObject
                {
                    ["fileName"] = info.Name,
                    ["path"] = file,
                    ["size"] = info.Length,
                    ["mtime"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                });
            }
            return new JsonArray(result.OrderByDescending(item => item?["mtime"]?.GetValue<long>() ?? 0).ToArray());
        }

        public static string BackupWorld(string instanceId, string folderName)
        {
            var source = Path.Combine(GameDir(instanceId), "saves", folderName);
            if (!Directory.Exists(source)) throw new Exception("世界不存在");
            var backupDir = Path.Combine(GameDir(instanceId), "backups");
            Directory.CreateDirectory(backupDir);
            var target = Path.Combine(backupDir, $"{folderName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            ZipFile.CreateFromDirectory(source, target, CompressionLevel.Fastest, true);
            return target;
        }

        public static void DeleteWorld(string instanceId, string folderName)
        {
            var folder = Path.Combine(GameDir(instanceId), "saves", folderName);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
