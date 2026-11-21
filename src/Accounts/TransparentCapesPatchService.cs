using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class TransparentCapesPatchService
    {
        // CustomSkinLoader also provides the transparent-cape render patch and
        // local skin/cape files that offline accounts need.
        private const string Project = "customskinloader";

        public static async Task Ensure(JsonObject instance, string versionId, JsonObject account, CancellationToken token = default)
        {
            try
            {
                var loader = instance["loader"]?.GetValue<string>() ?? "vanilla";
                if (loader != "forge" && loader != "fabric" && loader != "quilt" && loader != "neoforge") return;
                var mcVersion = instance["mcVersion"]?.GetValue<string>()
                    ?? instance["sourceVersionId"]?.GetValue<string>()
                    ?? versionId;
                if (string.IsNullOrWhiteSpace(mcVersion)) return;

                var gameDir = instance["gameDir"]?.GetValue<string>()
                    ?? Path.Combine(AppPaths.RootDir, "versions", versionId);
                // CustomSkinLoader 15.x universal crashes Forge 1.18.x with a
                // module ResolutionException (exports net.minecraft.client.renderer).
                if (loader == "forge" && mcVersion.StartsWith("1.18", StringComparison.Ordinal))
                {
                    try
                    {
                        foreach (var old in Directory.GetFiles(Path.Combine(gameDir, "mods"), "CustomSkinLoader_Universal*.jar"))
                            File.Delete(old);
                    }
                    catch { }
                    return;
                }
                var type = account["type"]?.GetValue<string>() ?? "";
                if (type == "microsoft")
                {
                    // 正版账户完全交给游戏/CSL 自身处理，启动器不再写入任何皮肤配置。
                    return;
                }
                PrepareLocalFiles(gameDir, account);
                WriteConfig(gameDir, account);
            }
            catch { }
        }

        private static JsonObject ThirdPartyEntry(string authServer)
        {
            var host = "";
            try { host = new Uri(authServer).Host.ToLowerInvariant(); } catch { }
            if (host.Contains("littleskin.cn"))
            {
                return new JsonObject
                {
                    ["name"] = "LittleSkin",
                    ["type"] = "CustomSkinAPI",
                    ["apiRoot"] = authServer.TrimEnd('/') + "/",
                    ["sessionRoot"] = authServer.TrimEnd('/') + "/",
                    ["root"] = "https://littleskin.cn/csl/"
                };
            }
            return new JsonObject
            {
                ["name"] = "ThirdParty",
                ["type"] = "MojangAPI",
                ["apiRoot"] = authServer.TrimEnd('/') + "/",
                ["sessionRoot"] = authServer.TrimEnd('/') + "/"
            };
        }

        private static string NormalizeAuthServer(string raw)
        {
            var authServer = (raw ?? "").Trim().TrimEnd('/');
            if (authServer.Contains("littleskin.cn", StringComparison.OrdinalIgnoreCase)
                && !authServer.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
                authServer += "/api/yggdrasil";
            return authServer;
        }

        /// <summary>
        /// 启动前把已有（可能来自 PCL/HMCL 或旧账户）的 CustomSkinLoader 配置对齐当前账户。
        /// 微软账户只保留 Mojang 官方源，避免继续从 LittleSkin 等第三方源取皮肤。
        /// </summary>
        public static void AlignSource(string gameDir, JsonObject account)
        {
            try
            {
                var type = account["type"]?.GetValue<string>() ?? "";
                if (type != "microsoft" && type != "authlib") return;
                var dir = Path.Combine(gameDir, "CustomSkinLoader");
                var file = Path.Combine(dir, "CustomSkinLoader.json");
                var config = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;

                if (type == "microsoft")
                {
                    if (config == null) return;
                    config["loadlist"] = new JsonArray { new JsonObject { ["name"] = "Mojang", ["type"] = "MojangAPI" } };
                }
                else
                {
                    var authServer = NormalizeAuthServer(account["authServer"]?.GetValue<string>() ?? "");
                    if (string.IsNullOrWhiteSpace(authServer)) return;
                    var host = "";
                    try { host = new Uri(authServer).Host; } catch { }
                    var list = config?["loadlist"] as JsonArray;
                    var already = list?.OfType<JsonObject>().Any(item =>
                        (item["apiRoot"]?.GetValue<string>() ?? "").Contains(host, StringComparison.OrdinalIgnoreCase)
                        || (item["root"]?.GetValue<string>() ?? "").Contains(host, StringComparison.OrdinalIgnoreCase)) == true;
                    if (already) return;
                    if (config == null)
                    {
                        Directory.CreateDirectory(dir);
                        config = new JsonObject { ["version"] = "15.1", ["buildNumber"] = 0 };
                    }
                    config["loadlist"] = new JsonArray { ThirdPartyEntry(authServer) };
                }
                config["auroraManaged"] = true;
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                try
                {
                    var cache = Path.Combine(dir, "cache");
                    if (Directory.Exists(cache)) Directory.Delete(cache, true);
                }
                catch { }
            }
            catch { }
        }

        private static void RepairMojangSource(string gameDir)
        {
            var dir = Path.Combine(gameDir, "CustomSkinLoader");
            var file = Path.Combine(dir, "CustomSkinLoader.json");
            try
            {
                JsonObject? config = null;
                if (File.Exists(file))
                    config = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (config == null)
                {
                    config = new JsonObject
                    {
                        ["version"] = "15.0.1",
                        ["buildNumber"] = 0,
                        ["loadlist"] = new JsonArray()
                    };
                }
                var list = config["loadlist"] as JsonArray;
                if (list == null)
                {
                    list = new JsonArray();
                    config["loadlist"] = list;
                }
                JsonObject? mojang = null;
                foreach (var item in list.OfType<JsonObject>())
                    if (string.Equals(item["type"]?.GetValue<string>(), "MojangAPI", StringComparison.OrdinalIgnoreCase))
                    {
                        mojang = item;
                        break;
                    }
                if (mojang == null)
                {
                    mojang = new JsonObject { ["name"] = "Mojang", ["type"] = "MojangAPI" };
                    list.Insert(0, mojang);
                }
                mojang["apiRoot"] = "https://api.mojang.com/";
                mojang["sessionRoot"] = "https://sessionserver.mojang.com/";
                config["auroraManaged"] = true;
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static void PrepareLocalFiles(string gameDir, JsonObject account)
        {
            var id = account["id"]?.GetValue<string>() ?? "";
            var user = account["username"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(user)) return;
            var root = Path.Combine(gameDir, "CustomSkinLoader", "LocalSkin");
            var skins = Path.Combine(root, "skins");
            var capes = Path.Combine(root, "capes");
            Directory.CreateDirectory(skins);
            Directory.CreateDirectory(capes);
            var localSkin = AccountSkinService.SkinPath(id);
            var mode = account["skinMode"]?.GetValue<string>() ?? "";
            var targetSkin = Path.Combine(skins, user + ".png");
            if (File.Exists(localSkin))
                File.Copy(localSkin, targetSkin, true);
            else if (File.Exists(targetSkin))
                File.Delete(targetSkin);

        }

        private static void WriteConfig(string gameDir, JsonObject account)
        {
            var dir = Path.Combine(gameDir, "CustomSkinLoader");
            var file = Path.Combine(dir, "CustomSkinLoader.json");
            Directory.CreateDirectory(dir);
            var id = account["id"]?.GetValue<string>() ?? "";
            var mode = account["skinMode"]?.GetValue<string>() ?? "";
            var user = account["username"]?.GetValue<string>() ?? "";
            var type = account["type"]?.GetValue<string>() ?? "offline";
            var authServer = (account["authServer"]?.GetValue<string>() ?? "").Trim().TrimEnd('/');
            if (authServer.Contains("littleskin.cn", StringComparison.OrdinalIgnoreCase)
                && !authServer.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
                authServer += "/api/yggdrasil";

            var list = new JsonArray();
            var localSkinFile = Path.Combine(gameDir, "CustomSkinLoader", "LocalSkin", "skins", user + ".png");
            if (File.Exists(localSkinFile))
            {
                list.Add(new JsonObject
                {
                    ["name"] = "LocalSkin",
                    ["type"] = "Legacy",
                    ["model"] = "auto",
                    ["skin"] = "LocalSkin/skins/{USERNAME}.png",
                    ["cape"] = "LocalSkin/capes/{USERNAME}.png",
                    ["elytra"] = "LocalSkin/elytras/{USERNAME}.png"
                });
            }
            if (type == "authlib" && !string.IsNullOrWhiteSpace(authServer))
            {
                list.Add(ThirdPartyEntry(authServer));
            }
            else
            {
                if (type == "offline")
                {
                    // HMCL 的离线皮肤用 LittleSkin 时，其他同样使用 LittleSkin 的玩家能看到皮肤；
                    // 这里按用户名向 LittleSkin 的 CustomSkinAPI 查询，补上多人联机时其他玩家的皮肤。
                    list.Add(new JsonObject
                    {
                        ["name"] = "LittleSkin",
                        ["type"] = "CustomSkinAPI",
                        ["root"] = "https://littleskin.cn/csl/"
                    });
                }
                list.Add(new JsonObject
                {
                    ["name"] = "Mojang",
                    ["type"] = "MojangAPI",
                    ["apiRoot"] = "https://api.mojang.com/",
                    ["sessionRoot"] = "https://sessionserver.mojang.com/"
                });
                list.Add(new JsonObject { ["name"] = "BlessingSkin", ["type"] = "CustomSkinAPI", ["root"] = "https://skin.prinzeugen.net/" });
                list.Add(new JsonObject { ["name"] = "ElyBy", ["type"] = "ElyByAPI", ["root"] = "http://skinsystem.ely.by/textures/" });
                list.Add(new JsonObject { ["name"] = "SkinMe", ["type"] = "UniSkinAPI", ["root"] = "http://www.skinme.cc/uniskin/" });
            }

            var config = new JsonObject
            {
                ["version"] = "15.1",
                ["buildNumber"] = 0,
                ["auroraManaged"] = true,
                ["loadlist"] = list,
                ["enableTransparentSkin"] = true,
                ["forceLoadAllTextures"] = true,
                ["enableCape"] = true,
                ["threadPoolSize"] = 8
            };
            File.WriteAllText(file, config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
