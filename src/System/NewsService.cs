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
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "cache", "news-v18.json");

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
                        if (cached?["items"] is JsonArray items && items.Count > 0)
                        {
                            var fresh = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at < 30 * 60 * 1000;
                            if (fresh) return (JsonArray)JsonNode.Parse(items.ToJsonString())!;
                            // Serve stale data immediately, then refresh the cache
                            // in the background so opening the home page never blocks.
                            _ = Task.Run(() => List(true));
                            return (JsonArray)JsonNode.Parse(items.ToJsonString())!;
                        }
                    }
                }
                catch { }
            }
            var result = new JsonArray();
            // Start the two independent remote fetches together so a slow
            // source cannot push the whole news list past the bridge timeout.
            var versionTask = FetchBugjumpItems();
            var latestTask = FetchLatestNewsItem();
            var versionItems = await versionTask;
            foreach (var versionItem in versionItems)
                result.Add(JsonNode.Parse(versionItem.ToJsonString()));
            try
            {
                var latest = await latestTask;
                if (latest != null) result.Add(latest);
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://launchercontent.mojang.com/v2/news.json");
                request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
                using var response = await Http.SendAsync(request);
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
                if (json?["entries"] is JsonArray entries)
                {
                    var news = entries.OfType<JsonObject>()
                        .Where(item => (item["newsType"] as JsonArray)?.Any(kind => kind?.GetValue<string>() == "News page") == true)
                        .OrderByDescending(item => item["date"]?.GetValue<string>() ?? "")
                        .Take(12).ToList();
                    if (news.Count == 0) news = entries.OfType<JsonObject>().Take(12).ToList();
                    var index = 0;
                    foreach (var entry in news)
                    {
                        if (index >= 12) break;
                        var imagePath = entry["image"]?["url"]?.GetValue<string>()
                            ?? entry["playPageImage"]?["url"]?.GetValue<string>()
                            ?? entry["newsPageImage"]?["url"]?.GetValue<string>();
                        var image = string.IsNullOrEmpty(imagePath) ? null : imagePath!.StartsWith("http") ? imagePath : "https://launchercontent.mojang.com" + imagePath;
                        result.Add(new JsonObject
                        {
                            ["id"] = entry["id"]?.GetValue<string>() ?? "mojang-" + index,
                            ["title"] = entry["title"]?.GetValue<string>() ?? "",
                            ["summary"] = entry["text"]?.GetValue<string>() ?? entry["category"]?.GetValue<string>() ?? "",
                            ["content"] = entry["articleBody"]?.GetValue<string>() ?? "",
                            ["url"] = entry["readMoreLink"]?.GetValue<string>() ?? entry["newsPage"]?.GetValue<string>() ?? "",
                            ["officialUrl"] = entry["readMoreLink"]?.GetValue<string>() ?? entry["newsPage"]?.GetValue<string>() ?? "",
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
                var manifest = await VersionService.Manifest(false);
                var releaseVersion = manifest["latest"]?["release"]?.GetValue<string>() ?? "";
                var snapshotVersion = manifest["latest"]?["snapshot"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(releaseVersion))
                {
                    result.Add(await VersionItem(new JsonObject
                    {
                        ["version-id"] = releaseVersion,
                        ["title"] = releaseVersion,
                        ["official-link"] = "https://www.minecraft.net/zh-hans/article"
                    }, "最新版本", true, "release"));
                }
                if (!string.IsNullOrWhiteSpace(snapshotVersion) && snapshotVersion != releaseVersion)
                {
                    result.Add(await VersionItem(new JsonObject
                    {
                        ["version-id"] = snapshotVersion,
                        ["title"] = snapshotVersion,
                        ["official-link"] = "https://www.minecraft.net/zh-hans/article"
                    }, "最新预览", false, "snapshot"));
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
            // Official Mojang patch notes only.
            var content = await FetchPatchNote(version);
            var image = Text(entry, "version-image-link");
            if (string.IsNullOrWhiteSpace(image)) image = await FetchPatchImage(version);
            if (string.IsNullOrWhiteSpace(content)) content = intro;
            if (string.IsNullOrWhiteSpace(content)) content = "该版本暂未收录详细更新日志。";
            if (official.EndsWith("/article", StringComparison.OrdinalIgnoreCase))
                official += "/minecraft-java-edition-" + version.Replace(".", "-");
            return new JsonObject
            {
                ["id"] = "bugjump-" + version,
                ["title"] = $"Minecraft {title} 版本介绍",
                ["summary"] = string.IsNullOrWhiteSpace(intro) ? $"Minecraft {title} 更新内容。" : intro,
                ["content"] = content,
                ["contentZh"] = null,
                ["contentEn"] = content,
                ["url"] = official,
                ["officialUrl"] = official,
                ["wikiUrl"] = "https://zh.minecraft.wiki/w/" + Uri.EscapeDataString("Java版" + version),
                ["image"] = image,
                ["tag"] = tag,
                ["date"] = "",
                ["pinned"] = pinned
            };
        }

        private static async Task<JsonObject?> FetchLatestNewsItem()
        {
            try
            {
                // Read the LATEST NEWS grid directly instead of downloading the
                // whole homepage first; this keeps news:list inside its timeout.
                var query = "/content/minecraftnet/language-masters/en-us/jcr:content/root/container/image_grid_a_copy_64.articles.page-1.json";
                using var gridRequest = new HttpRequestMessage(HttpMethod.Get, "https://www.minecraft.net" + query);
                gridRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var gridResponse = await Http.SendAsync(gridRequest);
                if (!gridResponse.IsSuccessStatusCode) return null;
                var grid = JsonNode.Parse(await gridResponse.Content.ReadAsStringAsync()) as JsonObject;
                var first = (grid?["article_grid"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                var tile = first?["default_tile"] as JsonObject;
                if (tile == null) return null;
                var url = first?["article_url"]?.GetValue<string>() ?? "";
                var image = tile["image"]?["imageURL"]?.GetValue<string>() ?? "";
                if (url.StartsWith("/")) url = "https://www.minecraft.net" + url;
                if (image.StartsWith("/")) image = "https://www.minecraft.net" + image;
                var latestContent = await FetchOfficialArticle(url);
                return new JsonObject
                {
                    ["id"] = "latest-" + url.GetHashCode().ToString("x"),
                    ["title"] = tile["title"]?.GetValue<string>() ?? "",
                    ["summary"] = tile["sub_header"]?.GetValue<string>() ?? "",
                    ["content"] = latestContent,
                    ["contentEn"] = latestContent,
                    ["url"] = url,
                    ["officialUrl"] = url,
                    ["image"] = image,
                    ["tag"] = "最新",
                    ["date"] = "",
                    ["pinned"] = false
                };
            }
            catch { return null; }
        }

        private static async Task<string> FetchOfficialArticle(string url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !url.Contains("minecraft.net/", StringComparison.OrdinalIgnoreCase) ||
                !url.Contains("/article/", StringComparison.OrdinalIgnoreCase))
                return "";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return "";
                var html = await response.Content.ReadAsStringAsync();
                var start = html.IndexOf("MC_articleGridA_container", StringComparison.Ordinal);
                if (start < 0) start = html.IndexOf("<article", StringComparison.OrdinalIgnoreCase);
                if (start < 0) return "";
                var textStart = html.IndexOf("<div class=\"article-text\"", start, StringComparison.Ordinal);
                if (textStart >= 0) start = textStart;
                var end = html.IndexOf("MC_globalFooter", start, StringComparison.Ordinal);
                if (end <= start) end = Math.Min(html.Length, start + 60000);
                var text = HtmlToText(html[start..end]);
                // Stop before official boilerplate (links, share, newest news,
                // disclaimers) that follows every article body.
                foreach (var marker in new[] { "Helpful links:", "Share this story", "Newest News", "Disclaimers:" })
                {
                    var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        text = text[..index].TrimEnd();
                        break;
                    }
                }
                return text.Length > 120000 ? text[..120000].TrimEnd() + "…" : text;
            }
            catch { return ""; }
        }

        private static JsonArray? _patchEntries;

        private static async Task<string> FetchPatchImage(string version)
        {
            try
            {
                if (_patchEntries == null)
                {
                    using var listRequest = new HttpRequestMessage(HttpMethod.Get,
                        "https://launchercontent.mojang.com/v2/javaPatchNotes.json");
                    listRequest.Headers.UserAgent.ParseAdd("AuroraLauncher/1.3.12");
                    using var listResponse = await Http.SendAsync(listRequest);
                    if (!listResponse.IsSuccessStatusCode) return "";
                    var listJson = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync()) as JsonObject;
                    _patchEntries = listJson?["entries"] as JsonArray;
                }
                var normalized = version.Replace(".", "-");
                var entry = _patchEntries?.OfType<JsonObject>().FirstOrDefault(item =>
                    item["version"]?.GetValue<string>() == version ||
                    item["id"]?.GetValue<string>() == normalized);
                var image = entry?["image"]?["url"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(image)) return "";
                return image.StartsWith("http") ? image : "https://launchercontent.mojang.com" + image;
            }
            catch { return ""; }
        }

        private static async Task<string> FetchPatchNote(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            try
            {
                if (_patchEntries == null)
                {
                    using var listRequest = new HttpRequestMessage(HttpMethod.Get,
                        "https://launchercontent.mojang.com/v2/javaPatchNotes.json");
                    listRequest.Headers.UserAgent.ParseAdd("AuroraLauncher/1.3.4");
                    using var listResponse = await Http.SendAsync(listRequest);
                    if (!listResponse.IsSuccessStatusCode) return "";
                    var listJson = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync()) as JsonObject;
                    _patchEntries = listJson?["entries"] as JsonArray;
                }
                if (_patchEntries == null) return "";
                var normalized = version.Replace(".", "-");
                var entry = _patchEntries.OfType<JsonObject>().FirstOrDefault(item =>
                    item["version"]?.GetValue<string>() == version ||
                    item["id"]?.GetValue<string>() == normalized);
                var path = entry?["contentPath"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(path)) return "";
                using var contentRequest = new HttpRequestMessage(HttpMethod.Get,
                    "https://launchercontent.mojang.com/v2/" + path.TrimStart('/'));
                contentRequest.Headers.UserAgent.ParseAdd("AuroraLauncher/1.3.4");
                using var contentResponse = await Http.SendAsync(contentRequest);
                if (!contentResponse.IsSuccessStatusCode) return "";
                var contentJson = JsonNode.Parse(await contentResponse.Content.ReadAsStringAsync()) as JsonObject;
                var html = contentJson?["body"]?.GetValue<string>() ?? "";
                var text = HtmlToText(html);
                return text.Length > 200000 ? text[..200000].TrimEnd() + "…" : text;
            }
            catch { return ""; }
        }

        private static string HtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var text = System.Text.RegularExpressions.Regex.Replace(html,
                @"<(script|style)[^>]*>.*?</\1>", "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<li[^>]*>", "\n• ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"</?(h1|h2|h3|h4|p|ul|ol|div|br)[^>]*>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            for (var decode = 0; decode < 3; decode++)
            {
                var decoded = System.Net.WebUtility.HtmlDecode(text);
                if (decoded == text) break;
                text = decoded;
            }
            var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            return string.Join("\n", lines);
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
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                        using var response = await Http.SendAsync(request, cts.Token);
                        if (!response.IsSuccessStatusCode) continue;
                        var text = await response.Content.ReadAsStringAsync(cts.Token);
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
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                        using var response = await Http.SendAsync(request, cts.Token);
                        if (!response.IsSuccessStatusCode) continue;
                        text = await response.Content.ReadAsStringAsync(cts.Token);
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
                for (var decode = 0; decode < 3; decode++)
                {
                    var decoded = System.Net.WebUtility.HtmlDecode(text);
                    if (decoded == text) break;
                    text = decoded;
                }
                return text.Length > 200000 ? text[..200000].TrimEnd() + "…" : text;
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
                // Official articles are loaded lazily when the user opens the
                // modal; the LATEST NEWS list itself stays fast.
                return await FetchOfficialArticle(url);
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
