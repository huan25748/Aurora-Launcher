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
                PrepareLocalFiles(gameDir, account);
                WriteConfig(gameDir, account);
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
            if (mode is "random" or "steve" or "alex" or "custom" or "premium" && File.Exists(localSkin))
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
            var type = account["type"]?.GetValue<string>() ?? "offline";
            var authServer = (account["authServer"]?.GetValue<string>() ?? "").Trim().TrimEnd('/');
            if (authServer.Contains("littleskin.cn", StringComparison.OrdinalIgnoreCase)
                && !authServer.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
                authServer += "/api/yggdrasil";

            var list = new JsonArray();
            if (mode is "random" or "steve" or "alex" or "custom" or "premium")
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
                list.Add(new JsonObject
                {
                    ["name"] = "LittleSkin",
                    ["type"] = "MojangAPI",
                    ["apiRoot"] = authServer + "/",
                    ["sessionRoot"] = authServer + "/"
                });
            }
            else
            {
                list.Add(new JsonObject { ["name"] = "Mojang", ["type"] = "MojangAPI" });
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
