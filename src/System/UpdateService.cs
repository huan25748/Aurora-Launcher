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

        private static string CurrentVersion => VersionInfo.Format(typeof(UpdateService).Assembly.GetName().Version);

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
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("AuroraLauncher-Updater");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                if (total > 0) ProgressService.TotalAt(step, total);
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
                        ProgressService.ProgressAt(step, done,
                            total > 0
                                ? $"{done / 1024d / 1024d:0.0} / {total / 1024d / 1024d:0.0} MB"
                                : $"{done / 1024d / 1024d:0.0} MB");
                    }
                }
                if (new FileInfo(target).Length < 1024 * 1024) throw new Exception("下载文件不完整");
                using (var check = File.OpenRead(target))
                {
                    var head = new byte[2];
                    if (check.Read(head, 0, 2) < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
                        throw new Exception("下载到的不是可执行文件，更新地址可能返回了错误页面");
                }
                ProgressService.UpdateAt(step, total > 0 ? total : 1, "更新已下载");
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
            var script = Path.Combine(Path.GetTempPath(), $"aurora-update-{Guid.NewGuid():N}.ps1");
            var pid = Environment.ProcessId;
            var newPath = newFile.Replace("'", "''");
            var currentPath = current.Replace("'", "''");
            var content = $@"
$log = Join-Path $env:TEMP 'aurora-update.log'
function Log([string]$m) {{ Add-Content -LiteralPath $log -Value ((Get-Date -Format o) + ' ' + $m) }}
Log 'updater start pid={pid}'
try {{ Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{ }}
Start-Sleep -Milliseconds 800
for ($i = 0; $i -lt 120; $i++) {{
  try {{
    Copy-Item -LiteralPath '{newPath}' -Destination '{currentPath}' -Force -ErrorAction Stop
    Log 'copied'
    Start-Process -FilePath '{currentPath}'
    Log 'started'
    Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force
    exit 0
  }} catch {{
    Log ('copy failed: ' + $_.Exception.Message)
    Start-Sleep -Seconds 1
  }}
}}
Log 'failed after retries'
";
            File.WriteAllText(script, content, new System.Text.UTF8Encoding(true));
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
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
