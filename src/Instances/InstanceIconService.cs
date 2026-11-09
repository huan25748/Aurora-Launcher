using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class InstanceIconService
    {
        public static void EnsureLocal(JsonObject instance, string instanceFile)
        {
            try
            {
                var existing = instance["iconPath"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing)) return;
                var gameDir = instance["gameDir"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return;
                foreach (var name in new[] { "pack.png", "icon.png", "logo.png", "cover.png" })
                {
                    var path = Path.Combine(gameDir, name);
                    if (!File.Exists(path)) continue;
                    instance["iconPath"] = path;
                    File.WriteAllText(instanceFile, instance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return;
                }
            }
            catch { }
        }

        public static async Task EnsureRemote(string instanceId, string gameDir, JsonObject? project)
        {
            try
            {
                var instanceFile = Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");
                if (!File.Exists(instanceFile)) return;
                var instance = JsonNode.Parse(File.ReadAllText(instanceFile)) as JsonObject;
                if (instance == null) return;
                EnsureLocal(instance, instanceFile);
                if (File.Exists(instance["iconPath"]?.GetValue<string>() ?? "")) return;
                var url = project?["icon_url"]?.GetValue<string>() ?? project?["logo"]?["thumbnailUrl"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(url)) return;
                var dir = Path.Combine(AppPaths.InstancesDir, instanceId);
                var target = Path.Combine(dir, "icon.png");
                await DownloadService.Download(url, target);
                instance["iconPath"] = target;
                File.WriteAllText(instanceFile, instance.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}