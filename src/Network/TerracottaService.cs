using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    /// <summary>
    /// 陶瓦联机（Terracotta）：与 HMCL 使用同一套进程与 HTTP 接口，
    /// 因此两边的房间码可以直接互通。
    /// </summary>
    internal static class TerracottaService
    {
        private static readonly HttpClient Http = CreateHttp();
        private static Process? _process;
        private static int _port;

        private static HttpClient CreateHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AuroraLauncher");
            return client;
        }

        private static string Dir => Path.Combine(AppPaths.SettingsDir, "terracotta");

        private static string? FindExe()
        {
            if (!Directory.Exists(Dir)) return null;
            foreach (var file in Directory.EnumerateFiles(Dir, "terracotta*.exe", SearchOption.AllDirectories))
                return file;
            return null;
        }

        public static async Task<JsonObject> Ensure(bool force, CancellationToken token)
        {
            var exe = FindExe();
            if (exe != null && !force)
                return new JsonObject { ["ok"] = true, ["path"] = exe };

            Directory.CreateDirectory(Dir);
            ProgressService.Begin("安装陶瓦联机");
            try
            {
                var infoStep = ProgressService.Step("获取版本信息", 1, "Gitee Releases");
                string url;
                string? lastError = null;
                url = null!;
                foreach (var endpoint in new[]
                {
                    "https://gitee.com/api/v5/repos/burningtnt/Terracotta/releases/latest",
                    "https://api.github.com/repos/burningtnt/Terracotta/releases/latest"
                })
                {
                    try
                    {
                        var release = JsonNode.Parse(await DownloadService.GetText(endpoint, token)) as JsonObject;
                        var asset = (release?["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item =>
                        {
                            var name = item["name"]?.GetValue<string>() ?? "";
                            return name.Contains("windows-x86_64", StringComparison.OrdinalIgnoreCase)
                                && name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
                        });
                        var candidate = asset?["browser_download_url"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(candidate)) { url = candidate!; break; }
                    }
                    catch (Exception ex) { lastError = ex.Message; }
                }
                if (string.IsNullOrWhiteSpace(url))
                    throw new Exception("获取陶瓦联机版本信息失败：" + (lastError ?? "没有找到 Windows 版本的下载地址"));
                ProgressService.UpdateAt(infoStep, 1, "已获取下载地址");

                var downloadStep = ProgressService.Step("下载陶瓦联机", 1, url);
                var archive = Path.Combine(Path.GetTempPath(), "terracotta-" + Guid.NewGuid().ToString("N")[..8] + ".tar.gz");
                await DownloadService.Download(url, archive, null, 0, token, (done, total) =>
                {
                    if (total > 0) ProgressService.TotalAt(downloadStep, total);
                    ProgressService.ProgressAt(downloadStep, done,
                        $"{MinecraftInstallService.FormatBytes(done)}" + (total > 0 ? $" / {MinecraftInstallService.FormatBytes(total)}" : ""));
                });
                ProgressService.UpdateAt(downloadStep, 1, "下载完成");

                var extractStep = ProgressService.Step("解压陶瓦联机", 1, "tar.gz");
                try
                {
                    var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
                    if (!File.Exists(tar)) tar = "tar.exe";
                    var info = new ProcessStartInfo(tar)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    info.ArgumentList.Add("-xzf");
                    info.ArgumentList.Add(archive);
                    info.ArgumentList.Add("-C");
                    info.ArgumentList.Add(Dir);
                    using var process = Process.Start(info) ?? throw new Exception("无法启动解压程序");
                    await process.WaitForExitAsync(token);
                    if (process.ExitCode != 0)
                        throw new Exception("解压陶瓦联机失败：" + await process.StandardError.ReadToEndAsync());
                }
                finally
                {
                    try { File.Delete(archive); } catch { }
                }
                ProgressService.UpdateAt(extractStep, 1, "解压完成");

                exe = FindExe() ?? throw new Exception("陶瓦联机解压后没有找到 terracotta*.exe");
                var canonical = Path.Combine(Dir, "terracotta.exe");
                if (!string.Equals(exe, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Copy(exe, canonical, true); exe = canonical; } catch { }
                }
                ProgressService.Complete();
                return new JsonObject { ["ok"] = true, ["path"] = exe };
            }
            catch (Exception ex)
            {
                try { ProgressService.Fail(ex.Message); } catch { }
                return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        private static async Task<bool> Alive()
        {
            if (_port <= 0) return false;
            try
            {
                using var response = await Http.GetAsync($"http://127.0.0.1:{_port}/meta");
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public static async Task<JsonObject> Start()
        {
            if (await Alive())
                return new JsonObject { ["ok"] = true, ["port"] = _port };

            var exe = FindExe() ?? throw new Exception("还没有安装陶瓦联机，请先点安装");
            var portFile = Path.Combine(Path.GetTempPath(), "aurora-terracotta-port.json");
            try { File.Delete(portFile); } catch { }
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Dir
            };
            info.ArgumentList.Add("--hmcl");
            info.ArgumentList.Add(portFile);
            _process = Process.Start(info) ?? throw new Exception("无法启动陶瓦联机");
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(100);
                if (!File.Exists(portFile)) continue;
                try
                {
                    var json = JsonNode.Parse(await File.ReadAllTextAsync(portFile)) as JsonObject;
                    _port = json?["port"]?.GetValue<int>() ?? 0;
                    if (_port > 0) return new JsonObject { ["ok"] = true, ["port"] = _port };
                }
                catch { }
            }
            throw new Exception("陶瓦联机启动超时，端口没有写入");
        }

        public static async Task<JsonNode> State()
        {
            if (!await Alive())
                return new JsonObject { ["state"] = "not-running" };
            try
            {
                var text = await Http.GetStringAsync($"http://127.0.0.1:{_port}/state");
                return JsonNode.Parse(text) ?? new JsonObject { ["state"] = "waiting" };
            }
            catch
            {
                return new JsonObject { ["state"] = "not-running" };
            }
        }

        private static async Task<JsonObject> Call(string path)
        {
            try
            {
                var text = await Http.GetStringAsync($"http://127.0.0.1:{_port}{path}");
                return new JsonObject { ["ok"] = true, ["raw"] = text };
            }
            catch (Exception ex)
            {
                return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        public static async Task<JsonObject> Host(string player)
        {
            await Start();
            return await Call("/state/scanning?player=" + Uri.EscapeDataString(player ?? ""));
        }

        public static async Task<JsonObject> Join(string room, string player)
        {
            await Start();
            return await Call("/state/guesting?room=" + Uri.EscapeDataString(room ?? "")
                + "&player=" + Uri.EscapeDataString(player ?? ""));
        }

        public static async Task<JsonObject> Reset()
        {
            if (!await Alive()) return new JsonObject { ["ok"] = true };
            return await Call("/state/ide");
        }

        public static async Task<JsonObject> Stop()
        {
            var ok = false;
            if (await Alive())
            {
                try { await Http.GetStringAsync($"http://127.0.0.1:{_port}/panic?peaceful=true"); ok = true; } catch { }
            }
            try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
            _process = null;
            _port = 0;
            return new JsonObject { ["ok"] = true, ["stopped"] = ok };
        }
    }
}
