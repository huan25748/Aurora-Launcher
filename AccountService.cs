using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class AccountService
    {
        private static string StoreFile => Path.Combine(AppPaths.SettingsDir, "accounts.json");
        private static readonly object Gate = new();

        private static JsonObject Load()
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(StoreFile) && JsonNode.Parse(File.ReadAllText(StoreFile)) is JsonObject json)
                    {
                        if (json["accounts"] is not JsonArray) json["accounts"] = new JsonArray();
                        return json;
                    }
                }
                catch { }
                return new JsonObject { ["accounts"] = new JsonArray(), ["activeId"] = "" };
            }
        }

        private static void Save(JsonObject json)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.SettingsDir);
                File.WriteAllText(StoreFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public static JsonArray List()
        {
            var accounts = (JsonArray)Load()["accounts"]!;
            return (JsonArray)JsonNode.Parse(accounts.ToJsonString())!;
        }

        public static JsonNode? Active()
        {
            var store = Load();
            var activeId = store["activeId"]?.GetValue<string>() ?? "";
            if (store["accounts"] is not JsonArray accounts) return null;
            foreach (var item in accounts)
                if (item?["id"]?.GetValue<string>() == activeId) return item;
            return accounts.Count > 0 ? accounts[0] : null;
        }

        public static JsonNode? SetActive(string id)
        {
            var store = Load();
            store["activeId"] = id;
            Save(store);
            return Active();
        }

        public static JsonNode AddOffline(string username)
        {
            var name = string.IsNullOrWhiteSpace(username) ? "Player" : username.Trim();
            var uuid = OfflineUuid(name);
            var account = new JsonObject
            {
                ["id"] = "off-" + Guid.NewGuid().ToString("N")[..12],
                ["type"] = "offline",
                ["username"] = name,
                ["uuid"] = uuid,
                ["accessToken"] = "0",
                ["avatarUrl"] = "https://mc-heads.net/avatar/" + uuid + "/64",
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var store = Load();
            if (store["accounts"] is JsonArray accounts)
            {
                foreach (var existing in accounts)
                {
                    if (existing?["type"]?.GetValue<string>() == "offline" &&
                        string.Equals(existing["username"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                        return existing;
                }
                accounts.Add(account);
            }
            store["activeId"] = account["id"]!.GetValue<string>();
            Save(store);
            return account;
        }

        public static void AddMicrosoft(JsonObject account)
        {
            var store = Load();
            if (store["accounts"] is JsonArray accounts)
            {
                var uuid = account["uuid"]?.GetValue<string>() ?? "";
                for (var i = accounts.Count - 1; i >= 0; i--)
                    if (accounts[i]?["type"]?.GetValue<string>() == "microsoft" && accounts[i]?["uuid"]?.GetValue<string>() == uuid) accounts.RemoveAt(i);
                accounts.Add(account);
            }
            store["activeId"] = account["id"]?.GetValue<string>() ?? "";
            Save(store);
            GameEvents.Emit("accounts", List());
        }

        public static void AddAuthlib(JsonObject account)
        {
            var store = Load();
            if (store["accounts"] is JsonArray accounts) accounts.Add(account);
            store["activeId"] = account["id"]?.GetValue<string>() ?? "";
            Save(store);
            GameEvents.Emit("accounts", List());
        }

        public static void Remove(string id)
        {
            var store = Load();
            if (store["accounts"] is JsonArray accounts)
            {
                for (var i = accounts.Count - 1; i >= 0; i--)
                    if (accounts[i]?["id"]?.GetValue<string>() == id) accounts.RemoveAt(i);
            }
            if (store["activeId"]?.GetValue<string>() == id) store["activeId"] = "";
            Save(store);
        }

        private static string OfflineUuid(string name)
        {
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes("OfflinePlayer:" + name);
            var hash = md5.ComputeHash(bytes);
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
