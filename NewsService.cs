using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class NewsService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "cache", "news-v2.json");

        public static async Task<JsonArray> List(bool force)
        {
            if (!force)
            {
                try
                {
                    if (File.Exists(CacheFile))
                    {
                        var cached = JsonNode.Parse(File.ReadAllText(CacheFile)) as JsonObject;
                        var at = cached?["at"]?.GetValue<long>() ?? 0;
                        if (cached?["items"] is JsonArray items && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at < 30 * 60 * 1000)
                            return (JsonArray)JsonNode.Parse(items.ToJsonString())!;
                    }
                }
                catch { }
            }
            var result = new JsonArray();
            var wikiItems = await FetchWikiItems();
            foreach (var wiki in wikiItems) result.Add(wiki);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://launchercontent.mojang.com/v2/news.json");
                request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0");
                using var response = await Http.SendAsync(request);
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
                if (json?["entries"] is JsonArray entries)
                {
                    var index = 0;
                    foreach (var item in entries)
                    {
                        if (item is not JsonObject entry || index >= 12) break;
                        var imagePath = entry["image"]?["url"]?.GetValue<string>();
                        var image = string.IsNullOrEmpty(imagePath) ? null : imagePath!.StartsWith("http") ? imagePath : "https://launchercontent.mojang.com" + imagePath;
                        result.Add(new JsonObject
                        {
                            ["id"] = entry["id"]?.GetValue<string>() ?? "mojang-" + index,
                            ["title"] = entry["title"]?.GetValue<string>() ?? "",
                            ["summary"] = entry["text"]?.GetValue<string>() ?? entry["category"]?.GetValue<string>() ?? "",
                            ["url"] = entry["readMoreLink"]?.GetValue<string>() ?? entry["newsPage"]?.GetValue<string>() ?? "",
                            ["date"] = entry["date"]?.GetValue<string>() ?? "",
                            ["image"] = image,
                            ["tag"] = entry["tag"]?.GetValue<string>() ?? entry["category"]?.GetValue<string>() ?? "",
                            ["pinned"] = index == 0
                        });
                        index++;
                    }
                }
            }
            catch { }
            if (result.Count == 0)
            {
                result.Add(new JsonObject { ["id"] = "welcome", ["title"] = "欢迎使用 Aurora Launcher", ["summary"] = "全新的界面与更快的下载体验。", ["tag"] = "公告", ["pinned"] = true });
                result.Add(new JsonObject { ["id"] = "fabric-tip", ["title"] = "小提示：一键安装模组加载器", ["summary"] = "安装游戏时可以选择 Fabric、Quilt、Forge 或 NeoForge。", ["tag"] = "技巧" });
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                File.WriteAllText(CacheFile, new JsonObject
                {
                    ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["items"] = JsonNode.Parse(result.ToJsonString())
                }.ToJsonString());
            }
            catch { }
            return result;
        }

        private static async Task<JsonArray> FetchWikiItems()
        {
            var result = new JsonArray();
            try
            {
                var manifest = await VersionService.Manifest(false);
                var release = manifest["latest"]?["release"]?.GetValue<string>() ?? "";
                var snapshot = manifest["latest"]?["snapshot"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(release))
                {
                    var item = await FetchWikiPage(release, "wiki-release-" + release, "最新版本", "版本介绍", true);
                    if (item != null) result.Add(item);
                }
                if (!string.IsNullOrWhiteSpace(snapshot) && snapshot != release)
                {
                    var item = await FetchWikiPage(snapshot, "wiki-preview-" + snapshot, "最新预览", "预览版介绍", false);
                    if (item != null) result.Add(item);
                }
            }
            catch { }
            return result;
        }

        private static async Task<JsonObject?> FetchWikiPage(string version, string id, string tag, string label, bool pinned)
        {
            try
            {
                var title = "Java版" + version;
                var api = "https://zh.minecraft.wiki/api.php?action=query&prop=extracts&exintro=1&explaintext=1&redirects=1&format=json&origin=*&titles="
                    + Uri.EscapeDataString(title);
                using var request = new HttpRequestMessage(HttpMethod.Get, api);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
                var pages = json?["query"]?["pages"] as JsonObject;
                var page = pages?.FirstOrDefault().Value as JsonObject;
                var extract = page?["extract"]?.GetValue<string>() ?? "";
                extract = extract.Replace('\n', ' ').Trim();
                if (extract.Length == 0) return null;
                if (extract.Length > 240) extract = extract[..240].TrimEnd() + "…";
                return new JsonObject
                {
                    ["id"] = id,
                    ["title"] = $"Minecraft {version} {label}",
                    ["summary"] = extract,
                    ["url"] = "https://zh.minecraft.wiki/w/" + Uri.EscapeDataString(title),
                    ["tag"] = tag,
                    ["date"] = "",
                    ["image"] = null,
                    ["pinned"] = pinned
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
