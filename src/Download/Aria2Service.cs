using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class Aria2Service
    {
        private static readonly object Gate = new();
        private static Process? _daemon;
        private static string _secret = "";
        private static int _port;
        private static bool _ready;
        private static readonly SemaphoreSlim StartGate = new(1, 1);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public static bool Ready { get { lock (Gate) return _ready; } }
        private static string ToolDir => Path.Combine(AppPaths.SettingsDir, "tools", "aria2");
        private static string ExePath => Path.Combine(ToolDir, "aria2c.exe");

        public static async Task<bool> EnsureAsync(CancellationToken token = default)
        {
            if (Ready) return true;
            await StartGate.WaitAsync(token);
            try
            {
                if (Ready) return true;
                if (!await EnsureBinaryAsync(token)) return false;
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                var secret = Guid.NewGuid().ToString("N");
                var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = ToolDir };
                foreach (var arg in new[] { "--enable-rpc", "--rpc-listen-all=false", $"--rpc-listen-port={port}", $"--rpc-secret={secret}", "--continue=true", "--max-connection-per-server=16", "--split=16", "--min-split-size=1M", "--file-allocation=none", "--auto-file-renaming=false", "--allow-overwrite=false", "--console-log-level=warn", "--max-tries=3" })
                    psi.ArgumentList.Add(arg);
                var process = Process.Start(psi);
                if (process == null) return false;
                for (var i = 0; i < 60; i++)
                {
                    await Task.Delay(150, token);
                    try
                    {
                        if (await Rpc("aria2.getVersion", new JsonArray(), port, secret, token) != null)
                        {
                            lock (Gate) { _daemon = process; _secret = secret; _port = port; _ready = true; }
                            return true;
                        }
                    }
                    catch { }
                }
                try { process.Kill(true); } catch { }
                return false;
            }
            catch { return false; }
            finally { StartGate.Release(); }
        }

        private static async Task<bool> EnsureBinaryAsync(CancellationToken token)
        {
            if (File.Exists(ExePath)) return true;
            Directory.CreateDirectory(ToolDir);
            var urls = new[]
            {
                "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip",
                "https://ghproxy.net/https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip"
            };
            foreach (var url in urls)
            {
                var zip = Path.Combine(ToolDir, "aria2.zip");
                try
                {
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode) continue;
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, token);
                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "aria2c.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) continue;
                        entry.ExtractToFile(ExePath, true);
                        foreach (var dll in archive.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                            dll.ExtractToFile(Path.Combine(ToolDir, dll.Name), true);
                    }
                    try { File.Delete(zip); } catch { }
                    return File.Exists(ExePath);
                }
                catch { }
            }
            return false;
        }

        public static async Task<string> DownloadAsync(string url, string destination, long knownSize, Action<long, long>? onProgress, CancellationToken token)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!await EnsureAsync(token)) throw new Exception("aria2 不可用");
                var dir = Path.GetDirectoryName(destination)!;
                var name = Path.GetFileName(destination);
                Directory.CreateDirectory(dir);
                var payload = new JsonArray(new JsonArray(url), new JsonObject { ["dir"] = dir, ["out"] = name, ["continue"] = "true" });
                var added = await Rpc("aria2.addUri", payload, _port, _secret, token) ?? throw new Exception("aria2 添加任务失败");
                var gid = added["result"]?.GetValue<string>() ?? throw new Exception("aria2 未返回任务 ID");
                var total = knownSize;
                long lastCompleted = -1;
                var lastChange = DateTime.UtcNow;
                try
                {
                    while (true)
                    {
                        await Task.Delay(250, token);
                        var status = await Rpc("aria2.tellStatus", new JsonArray(gid), _port, _secret, token);
                        var result = status?["result"] as JsonObject ?? throw new Exception("aria2 任务丢失");
                        var state = result["status"]?.GetValue<string>() ?? "error";
                        var completed = long.TryParse(result["completedLength"]?.GetValue<string>(), out var c) ? c : 0;
                        if (total <= 0) long.TryParse(result["totalLength"]?.GetValue<string>(), out total);
                        if (completed != lastCompleted)
                        {
                            lastCompleted = completed;
                            lastChange = DateTime.UtcNow;
                            onProgress?.Invoke(completed, total > 0 ? total : 0);
                        }
                        else if ((DateTime.UtcNow - lastChange).TotalSeconds > 30)
                        {
                            try { await Rpc("aria2.remove", new JsonArray(gid), _port, _secret, token); } catch { }
                            break;
                        }
                        if (state == "complete" || (total > 0 && completed >= total))
                        {
                            for (var wait = 0; wait < 20 && !File.Exists(destination); wait++) await Task.Delay(100, token);
                            if (!File.Exists(destination)) throw new Exception("aria2 完成后文件不存在");
                            var length = new FileInfo(destination).Length;
                            if (total > 0 && length != total) throw new Exception("aria2 文件大小不匹配");
                            onProgress?.Invoke(length, total > 0 ? total : length);
                            return destination;
                        }
                        if (state == "error" || state == "removed") throw new Exception("aria2 下载失败");
                    }
                }
                catch { }
            }
            throw new Exception("aria2 下载失败");
        }

        private static async Task<JsonObject?> Rpc(string method, JsonNode parameters, int port, string secret, CancellationToken token)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = method,
                ["params"] = new JsonArray("token:" + secret, JsonNode.Parse(parameters.ToJsonString())!)
            };
            var response = await Http.PostAsync($"http://127.0.0.1:{port}/jsonrpc", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), token);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonObject;
            if (json?["error"] != null) throw new Exception(json["error"]?["message"]?.GetValue<string>() ?? "aria2 RPC 错误");
            return json;
        }

        public static void Stop()
        {
            lock (Gate)
            {
                try { _daemon?.Kill(true); } catch { }
                _daemon = null;
                _ready = false;
            }
        }
    }
}