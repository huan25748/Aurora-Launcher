using System;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class AuthlibInjectorService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static string JarPath => Path.Combine(AppPaths.RootDir, "libraries", "authlib-injector", "authlib-injector.jar");

        public static async Task<string?> Prefetched(string server)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, server);
                request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0");
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync();
                return string.IsNullOrWhiteSpace(text) ? null : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
            }
            catch { return null; }
        }

        public static async Task<string> Ensure()
        {
            if (File.Exists(JarPath)) return JarPath;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://authlib-injector.yushi.moe/artifact/latest.json");
            request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0");
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception("authlib-injector 版本获取失败");
            var json = JsonNode.Parse(text) as JsonObject ?? throw new Exception("authlib-injector 版本格式错误");
            var url = json["download_url"]?.GetValue<string>() ?? throw new Exception("authlib-injector 下载地址为空");
            Directory.CreateDirectory(Path.GetDirectoryName(JarPath)!);
            await DownloadService.Download(url, JarPath);
            return JarPath;
        }
    }
}
