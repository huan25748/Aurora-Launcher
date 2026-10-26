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
        private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "cache", "news-v5.json");

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
            var versionItems = await FetchBugjumpItems();
            foreach (var versionItem in versionItems)
                result.Add(JsonNode.Parse(versionItem.ToJsonString()));
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
                            ["content"] = entry["articleBody"]?.GetValue<string>() ?? "",
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

        private static async Task<JsonArray> FetchBugjumpItems()
        {
            var result = new JsonArray();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://news.bugjump.net/apis/versions/latest");
                request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0");
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return result;
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
                if (json == null) return result;
                var release = json["release"] as JsonObject;
                var snapshot = json["snapshot"] as JsonObject;
                if (release != null) result.Add(await VersionItem(release, "最新版本", true, "release"));
                if (snapshot != null)
                {
                    result.Add(await VersionItem(snapshot, "最新预览", false, "snapshot"));
                }
                else
                {
                    // bugjump 的 latest 只在“当前最新为快照”时返回 snapshot。
                    // 这里补上 Mojang 清单中的最新快照，正文仍从 PCL2 新闻主页数据读取。
                    try
                    {
                        var manifest = await VersionService.Manifest(false);
                        var latest = manifest["latest"]?["snapshot"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(latest))
                        {
                            result.Add(await VersionItem(new JsonObject
                            {
                                ["version-id"] = latest,
                                ["title"] = latest,
                                ["official-link"] = "https://www.minecraft.net/zh-hans/article"
                            }, "最新预览", false, "snapshot"));
                        }
                    }
                    catch { }
                }
            }
            catch { }
            if (result.Count == 0)
            {
                try
                {
                    var manifest = await VersionService.Manifest(false);
                    var release = manifest["latest"]?["release"]?.GetValue<string>() ?? "";
                    var snapshot = manifest["latest"]?["snapshot"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(release))
                        result.Add(await VersionItem(new JsonObject
                        {
                            ["version-id"] = release,
                            ["title"] = release,
                            ["official-link"] = "https://www.minecraft.net/zh-hans/article"
                        }, "最新版本", true, "release"));
                    if (!string.IsNullOrWhiteSpace(snapshot) && snapshot != release)
                        result.Add(await VersionItem(new JsonObject
                        {
                            ["version-id"] = snapshot,
                            ["title"] = snapshot,
                            ["official-link"] = "https://www.minecraft.net/zh-hans/article"
                        }, "最新预览", false, "snapshot"));
                }
                catch { }
            }
            return result;
        }

        private static async Task<JsonObject> VersionItem(JsonObject entry, string tag, bool pinned, string fallbackType)
        {
            static string Text(JsonObject obj, string key)
            {
                var node = obj[key];
                return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            }
            var version = Text(entry, "version-id");
            var title = Text(entry, "title");
            if (string.IsNullOrWhiteSpace(title)) title = version;
            var intro = Text(entry, "intro");
            var official = Text(entry, "official-link");
            if (string.IsNullOrWhiteSpace(official)) official = "https://www.minecraft.net/zh-hans/article";
            var articleIndex = official.IndexOf("/article/", StringComparison.OrdinalIgnoreCase);
            if (articleIndex >= 0)
                official = "https://www.minecraft.net/zh-hans/article/" + official[(articleIndex + 9)..];
            var content = await FetchBugjumpArticle(version, fallbackType);
            if (string.IsNullOrWhiteSpace(content)) content = intro;
            var image = Text(entry, "version-image-link");
            if (string.IsNullOrWhiteSpace(image)) image = await FetchBugjumpImage(version, fallbackType);
            return new JsonObject
            {
                ["id"] = "bugjump-" + version,
                ["title"] = $"Minecraft {title} 版本介绍",
                ["summary"] = string.IsNullOrWhiteSpace(intro) ? $"Minecraft {title} 更新内容。" : intro,
                ["content"] = content,
                ["url"] = official,
                ["image"] = image,
                ["tag"] = tag,
                ["date"] = "",
                ["pinned"] = pinned
            };
        }

        private static async Task<string> FetchBugjumpImage(string version, string fallbackType)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            try
            {
                var baseVersion = version.Split("-snapshot-", 2)[0].Split("-pre-", 2)[0].Split("-rc-", 2)[0];
                var parts = baseVersion.Split('.');
                var group = parts.Length >= 2 && parts[0] == "1" ? parts[0] + "." + parts[1] : parts[0];
                string type;
                if (version.Contains("-snapshot-")) type = "snapshot";
                else if (version.Contains("-pre-")) type = "pre-release";
                else if (version.Contains("-rc-")) type = "release-candidate";
                else type = fallbackType;
                var relative = $"Project/libraries/Versions/{group}/{baseVersion}/{type}/{version}.md";
                foreach (var url in new[]
                {
                    $"https://cdn.jsdelivr.net/gh/Light-Beacon/PCL2-NewsHomepage@main/{relative}",
                    $"https://raw.githubusercontent.com/Light-Beacon/PCL2-NewsHomepage/main/{relative}"
                })
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.2");
                        using var response = await Http.SendAsync(request);
                        if (!response.IsSuccessStatusCode) continue;
                        var text = await response.Content.ReadAsStringAsync();
                        var match = System.Text.RegularExpressions.Regex.Match(text,
                            @"^version-image-link:\s*(\S+)\s*$",
                            System.Text.RegularExpressions.RegexOptions.Multiline);
                        if (match.Success) return match.Groups[1].Value;
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        private static async Task<string> FetchBugjumpArticle(string version, string fallbackType)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            try
            {
                var baseVersion = version.Split("-snapshot-", 2)[0].Split("-pre-", 2)[0].Split("-rc-", 2)[0];
                var parts = baseVersion.Split('.');
                var group = parts.Length >= 2 && parts[0] == "1" ? parts[0] + "." + parts[1] : parts[0];
                string type;
                if (version.Contains("-snapshot-")) type = "snapshot";
                else if (version.Contains("-pre-")) type = "pre-release";
                else if (version.Contains("-rc-")) type = "release-candidate";
                else type = fallbackType;
                var relative = $"Project/libraries/Versions/{group}/{baseVersion}/{type}/{version}.md";
                var urls = new[]
                {
                    $"https://cdn.jsdelivr.net/gh/Light-Beacon/PCL2-NewsHomepage@main/{relative}",
                    $"https://raw.githubusercontent.com/Light-Beacon/PCL2-NewsHomepage/main/{relative}"
                };
                string text = "";
                foreach (var url in urls)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.1");
                        using var response = await Http.SendAsync(request);
                        if (!response.IsSuccessStatusCode) continue;
                        text = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(text)) break;
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(text)) return "";
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\A---.*?---\s*", "",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s*", "",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                text = text.Replace("**", "").Replace("`", "");
                return text.Length > 60000 ? text[..60000].TrimEnd() + "…" : text;
            }
            catch { return ""; }
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

        public static async Task<string> Article(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "";
            try
            {
                if (uri.Host.Contains("minecraft.wiki", StringComparison.OrdinalIgnoreCase))
                {
                    var title = Uri.UnescapeDataString(uri.AbsolutePath);
                    if (title.StartsWith("/w/", StringComparison.OrdinalIgnoreCase)) title = title[3..];
                    var api = "https://zh.minecraft.wiki/api.php?action=query&prop=extracts&explaintext=1&redirects=1&format=json&origin=*&titles="
                        + Uri.EscapeDataString(title);
                    using var wikiRequest = new HttpRequestMessage(HttpMethod.Get, api);
                    wikiRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
                    wikiRequest.Headers.Accept.ParseAdd("application/json");
                    using var wikiResponse = await Http.SendAsync(wikiRequest);
                    if (!wikiResponse.IsSuccessStatusCode) return "";
                    var wikiJson = JsonNode.Parse(await wikiResponse.Content.ReadAsStringAsync()) as JsonObject;
                    var wikiPage = (wikiJson?["query"]?["pages"] as JsonObject)?.FirstOrDefault().Value as JsonObject;
                    var wikiText = wikiPage?["extract"]?.GetValue<string>() ?? "";
                    wikiText = wikiText.Replace("\r\n", "\n").Trim();
                    return wikiText.Length > 30000 ? wikiText[..30000].TrimEnd() + "…" : wikiText;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return "";
                var html = await response.Content.ReadAsStringAsync();
                var article = System.Text.RegularExpressions.Regex.Match(html,
                    @"<article[^>]*>(.*?)</article>",
                    System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!article.Success)
                    article = System.Text.RegularExpressions.Regex.Match(html,
                        @"<main[^>]*>(.*?)</main>",
                        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var body = article.Success ? article.Groups[1].Value : html;
                body = System.Text.RegularExpressions.Regex.Replace(body,
                    @"<(script|style|nav|header|footer)[^>]*>.*?</\1>", "",
                    System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                body = System.Text.RegularExpressions.Regex.Replace(body, @"<[^>]+>", "\n");
                body = System.Net.WebUtility.HtmlDecode(body);
                var lines = body.Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToArray();
                var text = string.Join("\n\n", lines);
                return text.Length > 16000 ? text[..16000].TrimEnd() + "…" : text;
            }
            catch { return ""; }
        }

        private static async Task<JsonObject?> FetchWikiPage(string version, string id, string tag, string label, bool pinned)
        {
            try
            {
                var title = "Java版" + version;
                var api = "https://zh.minecraft.wiki/api.php?action=query&prop=extracts&explaintext=1&redirects=1&format=json&origin=*&titles="
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
                var content = extract.Replace("\r\n", "\n").Trim();
                var summary = content.Replace('\n', ' ').Trim();
                if (summary.Length == 0) return null;
                if (summary.Length > 240) summary = summary[..240].TrimEnd() + "…";
                if (content.Length > 16000) content = content[..16000].TrimEnd() + "…";
                return new JsonObject
                {
                    ["id"] = id,
                    ["title"] = $"Minecraft {version} {label}",
                    ["summary"] = summary,
                    ["content"] = content,
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
