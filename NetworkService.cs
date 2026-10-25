using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class NetworkService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public static async Task<JsonArray> Test()
        {
            var endpoints = new (string Id, string Label, string Url, bool Range)[]
            {
                ("official", "Mojang 官方", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", false),
                ("bmclapi", "BMCLAPI 游戏", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", false),
                ("forge-official", "Forge 官方", "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", false),
                ("neoforge-official", "NeoForge 官方", "https://maven.neoforged.net/api/maven/details/releases/net/neoforged/neoforge", false),
                ("fabric-official", "Fabric 官方", "https://meta.fabricmc.net/v2/versions/loader/1.20.1", false),
                ("forge", "BMCLAPI Forge", "https://bmclapi2.bangbang93.com/forge/minecraft", false),
                ("neoforge", "BMCLAPI NeoForge", "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge", false),
                ("fabric", "BMCLAPI Fabric", "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/1.20.1", false),
                ("quilt", "Quilt 官方", "https://meta.quiltmc.org/v3/versions/loader/1.20.1", false),
                ("mcim", "MCIM Modrinth API", "https://mod.mcimirror.top/modrinth/v2/search?query=sodium&limit=1", false),
                ("modrinth-cdn", "Modrinth CDN", "https://cdn.modrinth.com/data/AANobbMI/versions/g6ju16VJ/sodium-fabric-0.9.3-beta.1%2Bmc26.3.jar", true),
                ("optifine-bmclapi", "BMCLAPI OptiFine API", "https://bmclapi2.bangbang93.com/optifine/versionList", false),
                ("optifine-cdn", "BMCLAPI OptiFine 文件", "https://bmclapi2.bangbang93.com/optifine/1.20.1/HD_U/I6", true),
                ("optifine-official", "OptiFine 官方", "https://optifine.net/adloadx?f=OptiFine_1.20.1_HD_U_I6.jar", false)
            };

            var tasks = new List<Task<JsonObject>>();
            foreach (var endpoint in endpoints) tasks.Add(TestOne(endpoint.Id, endpoint.Label, endpoint.Url, endpoint.Range));
            return new JsonArray((await Task.WhenAll(tasks)).Cast<JsonNode>().ToArray());
        }

        private static async Task<JsonObject> TestOne(string id, string label, string url, bool range)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
                request.Headers.Accept.ParseAdd("application/json");
                if (range) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                watch.Stop();
                var ok = response.IsSuccessStatusCode && (!range || (int)response.StatusCode == 206);
                return new JsonObject
                {
                    ["id"] = id,
                    ["label"] = label,
                    ["url"] = url,
                    ["ok"] = ok,
                    ["ms"] = watch.ElapsedMilliseconds,
                    ["status"] = (int)response.StatusCode
                };
            }
            catch (Exception ex)
            {
                watch.Stop();
                return new JsonObject
                {
                    ["id"] = id,
                    ["label"] = label,
                    ["url"] = url,
                    ["ok"] = false,
                    ["ms"] = watch.ElapsedMilliseconds,
                    ["error"] = ex.Message
                };
            }
        }
    }
}
