using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AuroraLauncher
{
    internal static class UpdateService
    {
        private const string ApiUrl = "https://gitee.com/api/v5/repos/mobai-33460/aurora-launcher/releases/latest";
        private const string ReleasesUrl = "https://gitee.com/mobai-33460/aurora-launcher/releases";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public static async Task<JsonObject> Check(bool force)
        {
            var current = CurrentVersion;
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.ParseAdd("AuroraLauncher-Updater");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new Exception($"HTTP {(int)response.StatusCode}");
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? new JsonObject();
            var tag = json["tag_name"]?.GetValue<string>() ?? json["name"]?.GetValue<string>() ?? current;
            var latest = tag.Trim().TrimStart('v', 'V');
            var assets = json["assets"] as JsonArray;
            var asset = assets?.OfType<JsonObject>().FirstOrDefault(item =>
                (item["name"]?.GetValue<string>() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            return new JsonObject
            {
                ["currentVersion"] = current,
                ["latestVersion"] = latest,
                ["hasUpdate"] = Compare(latest, current) > 0,
                ["releaseUrl"] = ReleasesUrl + "/tag/" + tag,
                ["downloadUrl"] = asset?["browser_download_url"]?.GetValue<string>() ?? "",
                ["assetName"] = asset?["name"]?.GetValue<string>() ?? "",
                ["title"] = json["name"]?.GetValue<string>() ?? $"Aurora Launcher {latest}",
                ["notes"] = json["body"]?.GetValue<string>() ?? "",
                ["publishedAt"] = json["created_at"]?.GetValue<string>() ?? ""
            };
        }

        public static async Task<JsonObject> DownloadAndApply(string url, string version)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new Exception("没有找到可下载的更新文件");
            var current = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) throw new Exception("无法定位当前启动器文件");
            var target = Path.Combine(Path.GetTempPath(), $"AuroraLauncher-{version}-{Guid.NewGuid():N}.exe");
            ProgressService.Begin("下载更新");
            var step = ProgressService.Step("下载启动器", 1, "连接 Gitee Releases");
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = File.Create(target))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        done += read;
                        ProgressService.UpdateAt(step, total > 0 ? (double)done / total : 0, "下载启动器");
                    }
                }
                if (new FileInfo(target).Length < 1024 * 1024) throw new Exception("下载文件不完整");
                WriteUpdater(target, current);
                ProgressService.Complete();
                await Task.Delay(300);
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                return new JsonObject { ["ok"] = true, ["path"] = target };
            }
            catch
            {
                ProgressService.Fail("更新下载失败");
                try { if (File.Exists(target)) File.Delete(target); } catch { }
                throw;
            }
        }

        private static void WriteUpdater(string newFile, string current)
        {
            var script = Path.Combine(Path.GetTempPath(), $"aurora-update-{Guid.NewGuid():N}.cmd");
            var pid = Environment.ProcessId;
            var content = $@"@echo off
:wait
tasklist /FI ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
copy /y ""{newFile}"" ""{current}"" >nul
start """" ""{current}""
del ""%~f0""
";
            File.WriteAllText(script, content);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
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
