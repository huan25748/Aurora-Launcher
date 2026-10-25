using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

        public static JsonArray List(string instanceId, string kind)
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
