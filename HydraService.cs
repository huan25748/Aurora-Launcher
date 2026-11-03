using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class HydraService
    {
        private static readonly object Gate = new();
        private static bool _ready;
        private static bool _preparing;
        private static readonly SemaphoreSlim StartGate = new(1, 1);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        public static bool Ready { get { lock (Gate) return _ready; } }
        private static string ToolDir => Path.Combine(AppPaths.SettingsDir, "tools", "hydra");
        private static string ExePath => Path.Combine(ToolDir, "hydra.exe");

        public static async Task<bool> EnsureAsync(CancellationToken token = default)
        {
            if (Ready) return true;
            await StartGate.WaitAsync(token);
            try
            {
                if (Ready) return true;
                if (!File.Exists(ExePath))
                {
                    Log("Hydra 引擎未安装，本次使用内置下载引擎");
                    // Do not block the current download on fetching the engine;
                    // prepare it in the background and use the built-in engine now.
                    if (!_preparing)
                    {
                        _preparing = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                Log("开始下载 Hydra 下载引擎");
                                if (await EnsureBinaryAsync(CancellationToken.None)) { lock (Gate) _ready = true; Log("Hydra 下载引擎已就绪，下次下载生效"); }
                                else Log("Hydra 下载引擎获取失败，继续使用内置引擎");
                            }
                            catch { }
                            finally { _preparing = false; }
                        });
                    }
                    return false;
                }
                lock (Gate) _ready = true;
                return true;
            }
            finally { StartGate.Release(); }
        }

        private static async Task<bool> EnsureBinaryAsync(CancellationToken token)
        {
            if (File.Exists(ExePath)) return true;
            Directory.CreateDirectory(ToolDir);
            var urls = new[]
            {
                "https://github.com/ja7ad/hydra/releases/download/v1.2.0/hydra-cli-1.2.0-windows-amd64.zip",
                "https://ghproxy.net/https://github.com/ja7ad/hydra/releases/download/v1.2.0/hydra-cli-1.2.0-windows-amd64.zip"
            };
            foreach (var url in urls)
            {
                var zip = Path.Combine(ToolDir, "hydra.zip");
                try
                {
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode) continue;
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, token);
                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "hydra.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) continue;
                        entry.ExtractToFile(ExePath, true);
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
            if (!await EnsureAsync(token)) throw new Exception("hydra 不可用");
            var dir = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(dir);
            var part = destination + ".hydrapart";
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(800, token);
                var psi = new ProcessStartInfo(ExePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = dir
                };
                foreach (var arg in new[]
                {
                    "--no-plugins", "--no-input", "-P", dir, "-O", Path.GetFileName(part),
                    "-x", "16", "--show-progress", "--show-error", url
                }) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi) ?? throw new Exception("hydra 启动失败");
                var percent = 0d;
                var tail = "";
                void Parse(string? text)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    var combined = tail + text;
                    var matches = Regex.Matches(combined, "(\\d+(?:\\.\\d+)?)%");
                    tail = combined.Length > 64 ? combined[^64..] : combined;
                    if (matches.Count == 0) return;
                    var match = matches[^1];
                    if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) percent = value;
                }
                async Task Pump(StreamReader reader)
                {
                    var buffer = new char[512];
                    while (true)
                    {
                        var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        Parse(new string(buffer, 0, read));
                    }
                }
                var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
                var lastLength = -1L;
                var lastChange = DateTime.UtcNow;
                while (!process.HasExited)
                {
                    await Task.Delay(200, token);
                    long length = 0;
                    if (knownSize > 0 && percent > 0) length = (long)(knownSize * percent / 100d);
                    else if (knownSize <= 0)
                    {
                        try { if (File.Exists(part)) length = new FileInfo(part).Length; } catch { }
                    }
                    if (length != lastLength)
                    {
                        lastLength = length;
                        lastChange = DateTime.UtcNow;
                        onProgress?.Invoke(length, knownSize > 0 ? knownSize : 0);
                    }
                    else if ((DateTime.UtcNow - lastChange).TotalSeconds > 30)
                    {
                        try { process.Kill(true); } catch { }
                        break;
                    }
                }
                try { await pumps; } catch { }
                if (process.HasExited && process.ExitCode == 0 && File.Exists(part))
                {
                    var final = new FileInfo(part).Length;
                    if (knownSize > 0 && final != knownSize) continue;
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(part, destination);
                    onProgress?.Invoke(final, knownSize > 0 ? knownSize : final);
                    return destination;
                }
            }
            throw new Exception("hydra 下载失败");
        }

        public static void Stop() { }

        private static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(AppPaths.SettingsDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "launcher.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}