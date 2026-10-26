using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class UpdateService
    {
        private const string ApiUrl = "https://api.github.com/repos/huan25748/Aurora-Launcher/releases/latest";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        public static async Task<JsonObject> Check(bool force)
        {
            var current = "1.1.9";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
                request.Headers.UserAgent.ParseAdd("AuroraLauncher-Updater");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode) throw new Exception($"HTTP {(int)response.StatusCode}");
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? new JsonObject();
                var tag = json["tag_name"]?.GetValue<string>() ?? json["name"]?.GetValue<string>() ?? current;
                var latest = tag.Trim().TrimStart('v', 'V');
                return new JsonObject
                {
                    ["currentVersion"] = current,
                    ["latestVersion"] = latest,
                    ["hasUpdate"] = Compare(latest, current) > 0,
                    ["releaseUrl"] = json["html_url"]?.GetValue<string>() ?? "https://github.com/huan25748/Aurora-Launcher/releases",
                    ["title"] = json["name"]?.GetValue<string>() ?? $"Aurora Launcher {latest}",
                    ["notes"] = json["body"]?.GetValue<string>() ?? "",
                    ["publishedAt"] = json["published_at"]?.GetValue<string>() ?? ""
                };
            }
            catch
            {
                throw;
            }
        }

        private static int Compare(string a, string b)
        {
            var pa = a.Split('.', '-', '+');
            var pb = b.Split('.', '-', '+');
            for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                var x = i < pa.Length ? pa[i] : "0";
                var y = i < pb.Length ? pb[i] : "0";
                if (int.TryParse(x, out var xi) && int.TryParse(y, out var yi))
                {
                    if (xi != yi) return xi.CompareTo(yi);
                }
                else
                {
                    var result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                    if (result != 0) return result;
                }
            }
            return 0;
        }
    }
}
