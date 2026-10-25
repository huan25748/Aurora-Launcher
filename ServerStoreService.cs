using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class ServerStoreService
    {
        private static string FileFor(string instanceId)
        {
            var dir = Path.Combine(AppPaths.SettingsDir, "servers");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, instanceId + ".json");
        }

        public static JsonArray List(string instanceId)
        {
            try
            {
                var file = FileFor(instanceId);
                if (!File.Exists(file)) return new JsonArray();
                return JsonNode.Parse(File.ReadAllText(file)) as JsonArray ?? new JsonArray();
            }
            catch { return new JsonArray(); }
        }

        private static void Save(string instanceId, JsonArray list)
        {
            File.WriteAllText(FileFor(instanceId), list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public static JsonArray Add(string instanceId, JsonElement entry)
        {
            var list = (JsonArray)JsonNode.Parse(List(instanceId).ToJsonString())!;
            var ip = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("ip", out var ipElement) ? ipElement.GetString() ?? "" : "";
            ip = ip.Trim();
            if (ip.Length == 0) throw new Exception("服务器地址不能为空");
            var name = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? ip : ip;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i]?["ip"]?.GetValue<string>() == ip)
                {
                    list[i] = new JsonObject { ["name"] = name, ["ip"] = ip };
                    Save(instanceId, list);
                    return list;
                }
            }
            list.Add(new JsonObject { ["name"] = name, ["ip"] = ip });
            Save(instanceId, list);
            return list;
        }

        public static JsonArray Remove(string instanceId, string ip)
        {
            var list = (JsonArray)JsonNode.Parse(List(instanceId).ToJsonString())!;
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i]?["ip"]?.GetValue<string>() == ip) list.RemoveAt(i);
            Save(instanceId, list);
            return list;
        }
    }
}
