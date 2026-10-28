using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class EasyTierService
    {
        private sealed record RelayNode(string Code, string Contributor, string Endpoint, string Info, string Requirements, string Label);

        private static readonly RelayNode[] RelayNodes =
        {
            new("LCT", "凉城听暖", "https://et-public.lctn.top", "云南中国联通", "限速中转 · 禁海外", "凉城听暖云南节点·禁海外"),
            new("WA", "为爱，唯爱", "tcp://easytier.weiai.org.cn:11010", "厦门中国电信", "可中转", "唯爱厦门节点"),
            new("HB", "海波", "tcp://225284.xyz:11010", "上海中国电信", "可中转 · 安全模式", "海波上海节点"),
            new("FX", "饭小米", "tcp://et1.fuis.top:11010", "上海火山云", "可中转", "饭小米上海节点"),
            new("JT", "Just the Two of Us", "tcp://183.230.36.171:11010", "重庆中国移动跨网专线", "可中转", "Just The Two of Us重庆节点"),
            new("XY", "夏夜", "tcp://et.sumnight.online:11012", "成都阿里云", "可中转", "夏夜成都节点"),
            new("CK", "ChinoKou", "wss://et.chinokou.cn", "EdgeOne 全球可用区", "限速中转", "ChinoKou全球节点"),
            new("YH", "宇智波.希", "tcp://et.basd1.de:11010", "襄阳中国电信", "可中转", "宇智波·希中国节点"),
            new("DT", "电獭", "tcp://et-cn-02.kivotos.online:11010", "杭州阿里云", "限速中转", "电獭杭州节点"),
            new("HW", "[海外]海波", "tcp://us01.225284.xyz:11010", "美国德雷珀 cogentco", "可中转", "海波海外节点")
        };

        // A1 invite codes used positional indexes; keep this order so old codes
        // still resolve after the relay list changed.
        private static readonly string[] LegacyEndpoints =
        {
            "tcp://easytier-us.slarker.me:11010",
            "wss://easytier-us.slarker.me:11011",
            "tcp://easytier.weiai.org.cn:11010",
            "tcp://225284.xyz:11010",
            "udp://us01.225284.xyz:11010",
            "tcp://public.easytier.top:11010",
            "udp://public.easytier.top:11010"
        };

        private static string DefaultServer => RelayNodes[0].Endpoint;
        private static readonly object Gate = new();
        private static readonly SemaphoreSlim InstallLock = new(1, 1);
        private static Process? _process;
        private static string _log = "";
        private static double _installProgress;
        private static string _installDetail = "";

        private static string Dir => Path.Combine(AppPaths.SettingsDir, "easytier");
        private static string BinDir => Path.Combine(Dir, "bin");
        private static string CorePath => Path.Combine(BinDir, "easytier-core.exe");
        private static string CliPath => Path.Combine(BinDir, "easytier-cli.exe");
        private static string ConfigFile => Path.Combine(AppPaths.SettingsDir, "easytier.json");

        private static JsonObject LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFile) && JsonNode.Parse(File.ReadAllText(ConfigFile)) is JsonObject json)
                    return json;
            }
            catch { }
            return new JsonObject
            {
                ["networkName"] = "aurora-" + Guid.NewGuid().ToString("N")[..8],
                ["networkSecret"] = "",
                ["server"] = DefaultServer,
                ["customServers"] = new JsonArray()
            };
        }

        private static void SaveConfig(JsonObject json)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.SettingsDir);
                File.WriteAllText(ConfigFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static JsonObject Status()
        {
            var config = LoadConfig();
            var running = false;
            lock (Gate)
            {
                try { running = _process != null && !_process.HasExited; } catch { }
            }
            return new JsonObject
            {
                ["installed"] = File.Exists(CorePath) && File.Exists(CliPath),
                ["running"] = running,
                ["version"] = File.Exists(Path.Combine(Dir, "version.txt")) ? File.ReadAllText(Path.Combine(Dir, "version.txt")).Trim() : "",
                ["networkName"] = config["networkName"]?.GetValue<string>() ?? "",
                ["networkSecret"] = config["networkSecret"]?.GetValue<string>() ?? "",
                ["server"] = config["server"]?.GetValue<string>() ?? DefaultServer,
                ["servers"] = Servers(config),
                ["installProgress"] = _installProgress,
                ["installDetail"] = _installDetail,
                ["log"] = _log
            };
        }

        private static JsonArray Servers(JsonObject config)
        {
            var result = new JsonArray();
            foreach (var node in RelayNodes)
            {
                result.Add(new JsonObject
                {
                    ["id"] = node.Code.ToLowerInvariant(),
                    ["code"] = node.Code,
                    ["name"] = node.Contributor,
                    ["label"] = node.Label,
                    ["contributor"] = node.Contributor,
                    ["info"] = node.Info,
                    ["requirements"] = node.Requirements,
                    ["endpoint"] = node.Endpoint,
                    ["builtin"] = true
                });
            }
            if (config["customServers"] is JsonArray custom)
            {
                foreach (var item in custom)
                {
                    if (item is not JsonObject obj) continue;
                    var endpoint = obj["endpoint"]?.GetValue<string>() ?? "";
                    if (endpoint.Length == 0) continue;
                    result.Add(new JsonObject
                    {
                        ["id"] = "custom-" + endpoint.GetHashCode(),
                        ["name"] = "自定义服务器",
                        ["contributor"] = "自定义服务器",
                        ["info"] = "由你添加",
                        ["requirements"] = "需自行确认可中转",
                        ["endpoint"] = endpoint,
                        ["builtin"] = false
                    });
                }
            }
            return result;
        }

        public static JsonObject Save(JsonObject config)
        {
            var current = LoadConfig();
            foreach (var key in new[] { "networkName", "networkSecret", "server" })
            {
                if (config[key]?.GetValue<string>() is string value && value.Trim().Length > 0)
                    current[key] = value.Trim();
            }
            if (config["customServers"] is JsonArray custom)
                current["customServers"] = JsonNode.Parse(custom.ToJsonString());
            SaveConfig(current);
            return current;
        }

        public static async Task<JsonObject> Latency(string endpoint)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var host = HostOf(endpoint);
                var port = PortOf(endpoint);
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connect, Task.Delay(3500));
                if (completed != connect) throw new TimeoutException();
                await connect;
                stopwatch.Stop();
                return new JsonObject { ["ok"] = true, ["latency"] = (int)stopwatch.ElapsedMilliseconds, ["endpoint"] = endpoint };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new JsonObject { ["ok"] = false, ["latency"] = -1, ["endpoint"] = endpoint, ["error"] = ex.Message };
            }
        }

        private static string HostOf(string endpoint)
        {
            var value = endpoint.Contains("://", StringComparison.Ordinal) ? endpoint[(endpoint.IndexOf("://", StringComparison.Ordinal) + 3)..] : endpoint;
            var colon = value.LastIndexOf(':');
            return colon > 0 ? value[..colon].Trim('/', '\\') : value;
        }

        private static int PortOf(string endpoint)
        {
            var value = endpoint.Contains("://", StringComparison.Ordinal) ? endpoint[(endpoint.IndexOf("://", StringComparison.Ordinal) + 3)..] : endpoint;
            var colon = value.LastIndexOf(':');
            return colon > 0 && int.TryParse(value[(colon + 1)..], out var port) ? port : 11010;
        }

        public static async Task<JsonObject> Ensure(bool force = false, CancellationToken token = default)
        {
            await InstallLock.WaitAsync(token);
            try
            {
                if (!force && File.Exists(CorePath) && File.Exists(CliPath)
                    && File.Exists(Path.Combine(BinDir, "Packet.dll"))
                    && File.Exists(Path.Combine(BinDir, "wintun.dll"))
                    && File.Exists(Path.Combine(BinDir, "WinDivert64.sys")))
                    return new JsonObject { ["ok"] = true, ["installed"] = true };
                Directory.CreateDirectory(Dir);
                _installProgress = 1;
                _installDetail = "获取 EasyTier 版本信息";
                ProgressService.Begin("安装 EasyTier");
                ProgressService.Step("下载 EasyTier", 1, "获取最新版本");
                string tag = "v2.6.4";
                string? url = null;
                string? giteeUrl = null;
                long size = 32657720;
                try
                {
                    var release = JsonNode.Parse(await DownloadService.GetText("https://gitee.com/api/v5/repos/easytier/EasyTier/releases/latest", token)) as JsonObject;
                    tag = release?["tag_name"]?.GetValue<string>() ?? tag;
                    var assets = release?["assets"] as JsonArray;
                    var asset = assets?.OfType<JsonObject>().FirstOrDefault(item =>
                    {
                        var name = item["name"]?.GetValue<string>() ?? "";
                        return name.StartsWith("easytier-windows-x86_64-", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    });
                    giteeUrl = asset?["browser_download_url"]?.GetValue<string>();
                    url = giteeUrl;
                }
                catch { }
                if (string.IsNullOrWhiteSpace(url))
                {
                    try
                    {
                        var release = JsonNode.Parse(await DownloadService.GetText("https://api.github.com/repos/EasyTier/EasyTier/releases/latest", token)) as JsonObject;
                        tag = release?["tag_name"]?.GetValue<string>() ?? tag;
                        var assets = release?["assets"] as JsonArray;
                        var asset = assets?.OfType<JsonObject>().FirstOrDefault(item =>
                        {
                            var name = item["name"]?.GetValue<string>() ?? "";
                            return name.StartsWith("easytier-windows-x86_64-", StringComparison.OrdinalIgnoreCase)
                                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                        });
                        url = asset?["browser_download_url"]?.GetValue<string>();
                        size = asset?["size"]?.GetValue<long>() ?? size;
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(url))
                    url = "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-x86_64-v2.6.4.zip";
                var zip = Path.Combine(Dir, "easytier.zip");
                var cachedZip = File.Exists(zip) && new FileInfo(zip).Length > 1024 * 1024;
                if (!cachedZip)
                {
                    var candidates = new List<string>();
                    var githubFallback = url.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                        ? url
                        : "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-x86_64-v2.6.4.zip";
                    if (!string.IsNullOrWhiteSpace(giteeUrl))
                        candidates.Add(giteeUrl);
                    candidates.Add("https://ghfast.top/" + githubFallback);
                    candidates.Add("https://gh-proxy.com/" + githubFallback);
                    candidates.Add(githubFallback);
                    if (!string.IsNullOrWhiteSpace(url)) candidates.Add(url);
                    Exception? lastError = null;
                    foreach (var candidate in candidates.Distinct())
                    {
                        try
                        {
                            _installDetail = "下载 EasyTier...";
                            await DownloadService.Download(candidate, zip, null, size, token, (done, total) =>
                            {
                                _installProgress = total > 0 ? 5 + Math.Min(85, (double)done / total * 85) : 5;
                                _installDetail = total > 0 ? $"下载 EasyTier {_installProgress:0}%" : "下载 EasyTier";
                                ProgressService.Update(total > 0 ? (double)done / total : 0, "下载 EasyTier");
                            });
                            lastError = null;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                        }
                    }
                    if (lastError != null) throw lastError;
                }
                else
                {
                    _installProgress = 90;
                    _installDetail = "使用已下载的安装包";
                }
                _installProgress = 92;
                _installDetail = "解压运行文件";
                var installStep = ProgressService.Step("安装 EasyTier", 1, "解压运行文件");
                var extract = Path.Combine(Dir, "extract-" + Guid.NewGuid().ToString("N")[..6]);
                if (Directory.Exists(extract)) Directory.Delete(extract, true);
                ZipFile.ExtractToDirectory(zip, extract);
                Directory.CreateDirectory(BinDir);
                foreach (var file in Directory.GetFiles(extract, "*", SearchOption.AllDirectories))
                {
                    // The release zip has one top-level folder. Flatten it so
                    // Packet.dll / wintun.dll sit next to easytier-core.exe.
                    var target = Path.Combine(BinDir, Path.GetFileName(file));
                    File.Copy(file, target, true);
                }
                if (!File.Exists(CorePath) || !File.Exists(CliPath)) throw new Exception("EasyTier 压缩包缺少运行文件");
                File.WriteAllText(Path.Combine(Dir, "version.txt"), tag);
                try { Directory.Delete(extract, true); } catch { }
                _installProgress = 100;
                _installDetail = "安装完成";
                ProgressService.UpdateAt(installStep, 1, "安装完成");
                ProgressService.Complete();
                return new JsonObject { ["ok"] = true, ["installed"] = true, ["version"] = tag };
            }
            catch (Exception ex)
            {
                _installProgress = 0;
                _installDetail = "安装失败：" + ex.Message;
                try { ProgressService.Fail(ex.Message); } catch { }
                throw;
            }
            finally
            {
                InstallLock.Release();
            }
        }

        public static JsonObject CreateInvite(JsonObject input)
        {
            var server = input["server"]?.GetValue<string>() ?? DefaultServer;
            // 12 random bytes are enough for a room token and keep the code short.
            var token = Base64Url(RandomNumberGenerator.GetBytes(9));
            var name = "aurora-" + token[..Math.Min(8, token.Length)].ToLowerInvariant();
            var secret = "aurora-" + token;
            var node = RelayNodes.FirstOrDefault(item => string.Equals(item.Endpoint, server, StringComparison.OrdinalIgnoreCase));
            var code = node != null
                ? $"A2.{node.Code}.{token}"
                : $"A2.c.{Base64Url(Encoding.UTF8.GetBytes(server))}.{token}";
            return new JsonObject { ["ok"] = true, ["code"] = code, ["networkName"] = name, ["networkSecret"] = secret };
        }

        public static async Task<JsonObject> JoinInvite(string code)
        {
            JsonObject? payload;
            try { payload = ParseInvite(code); }
            catch { return new JsonObject { ["ok"] = false, ["error"] = "邀请码格式不正确" }; }
            if (payload == null) return new JsonObject { ["ok"] = false, ["error"] = "邀请码格式不正确" };
            var name = payload["n"]?.GetValue<string>() ?? "";
            var secret = payload["s"]?.GetValue<string>() ?? "";
            var server = payload["e"]?.GetValue<string>() ?? DefaultServer;
            if (name.Length == 0 || secret.Length == 0)
                return new JsonObject { ["ok"] = false, ["error"] = "邀请码内容不完整" };
            var start = await Start(new JsonObject { ["networkName"] = name, ["networkSecret"] = secret, ["server"] = server });
            if (start["ok"]?.GetValue<bool>() != true) return start;
            // The public relay has no room registry, so the only reliable
            // availability check is to join and look for another node.
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(1000);
                var peers = await Peers();
                if (HasOtherPeer(peers))
                    return new JsonObject { ["ok"] = true, ["networkName"] = name, ["peers"] = peers };
            }
            // Do not tear down a working VPN just because the peer table has
            // not updated yet. Keep the room joined and let the UI show the
            // warning; the host may still be relaying.
            return new JsonObject
            {
                ["ok"] = true,
                ["warning"] = "暂未在成员列表中发现房主，请确认房主在线；房间连接已建立，可稍后刷新成员",
                ["networkName"] = name
            };
        }

        private static JsonObject? ParseInvite(string code)
        {
            code = (code ?? "").Trim();
            if (code.StartsWith("AURORA1.", StringComparison.Ordinal))
            {
                var text = Encoding.UTF8.GetString(Base64UrlDecode(code[8..]));
                return JsonNode.Parse(text) as JsonObject;
            }
            var parts = code.Split('.');
            if (parts.Length != 3 && parts.Length != 4) return null;
            var token = parts[^1];
            if (token.Length < 8) return null;
            string server;
            if (code.StartsWith("A2.", StringComparison.Ordinal) && parts.Length == 3)
            {
                var node = RelayNodes.FirstOrDefault(item => string.Equals(item.Code, parts[1], StringComparison.OrdinalIgnoreCase));
                if (node == null) return null;
                server = node.Endpoint;
            }
            else if (code.StartsWith("A2.", StringComparison.Ordinal) && parts.Length == 4)
            {
                if (parts[1] != "c") return null;
                server = Encoding.UTF8.GetString(Base64UrlDecode(parts[2]));
            }
            else if (code.StartsWith("A1.", StringComparison.Ordinal) && parts.Length == 3)
            {
                if (!int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var index)
                    || index < 0 || index >= LegacyEndpoints.Length) return null;
                server = LegacyEndpoints[index];
            }
            else if (code.StartsWith("A1.", StringComparison.Ordinal) && parts.Length == 4)
            {
                if (parts[1] != "c") return null;
                server = Encoding.UTF8.GetString(Base64UrlDecode(parts[2]));
            }
            else return null;
            return new JsonObject
            {
                ["n"] = "aurora-" + token[..Math.Min(8, token.Length)].ToLowerInvariant(),
                ["s"] = "aurora-" + token,
                ["e"] = server
            };
        }

        private static bool HasOtherPeer(string peers)
        {
            foreach (var raw in peers.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("|", StringComparison.Ordinal) || line.Contains("---") || line.Contains("ipv4", StringComparison.OrdinalIgnoreCase)) continue;
                var cells = line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (cells.Length < 2) continue;
                var host = cells[1];
                if (host.Contains("Local", StringComparison.OrdinalIgnoreCase)) continue;
                if (host.Contains("PublicServer", StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }

        private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string text)
        {
            text = text.Replace('-', '+').Replace('_', '/');
            switch (text.Length % 4)
            {
                case 2: text += "=="; break;
                case 3: text += "="; break;
            }
            return Convert.FromBase64String(text);
        }

        public static async Task<JsonObject> Start(JsonObject input)
        {
            if (!File.Exists(CorePath)) return new JsonObject { ["ok"] = false, ["error"] = "请先安装 EasyTier" };
            var config = LoadConfig();
            var name = input["networkName"]?.GetValue<string>() ?? config["networkName"]?.GetValue<string>() ?? "";
            var secret = input["networkSecret"]?.GetValue<string>() ?? config["networkSecret"]?.GetValue<string>() ?? "";
            var server = input["server"]?.GetValue<string>() ?? config["server"]?.GetValue<string>() ?? DefaultServer;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(secret))
                return new JsonObject { ["ok"] = false, ["error"] = "请填写房间名称和房间密码" };
            if (string.IsNullOrWhiteSpace(server))
                server = DefaultServer;
            Stop();
            config["networkName"] = name;
            config["networkSecret"] = secret;
            config["server"] = server;
            SaveConfig(config);
            var args = new List<string> { "-d", "--network-name", name, "--network-secret", secret };
            foreach (var endpoint in server.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                args.Add("-p");
                args.Add(endpoint.Trim());
            }
            // A core left behind by a previous launcher session keeps 11010
            // occupied, so the newly started instance exits immediately.
            foreach (var stale in Process.GetProcessesByName("easytier-core"))
            {
                try { stale.Kill(true); } catch { }
            }
            await Task.Delay(400);

            var elevated = !IsAdministrator();
            var logFile = Path.Combine(Dir, "start.log");
            try { if (File.Exists(logFile)) File.Delete(logFile); } catch { }
            var argLine = string.Join(" ", args.Select(Quote));
            var info = elevated
                ? new ProcessStartInfo("cmd.exe", "/c \"" + Quote(CorePath) + " " + argLine + " > " + Quote(logFile) + " 2>&1\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = BinDir
                }
                : new ProcessStartInfo(CorePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = BinDir,
                    Arguments = argLine
                };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var output = new StringBuilder();
            if (!elevated)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            }
            process.Start();
            if (!elevated)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            lock (Gate)
            {
                _process = process;
                _log = elevated ? "已请求管理员权限，等待 EasyTier 启动…" : output.ToString();
            }
            await Task.Delay(2200);
            process.Refresh();
            if (elevated && File.Exists(logFile))
            {
                try { output.Append(File.ReadAllText(logFile)); } catch { }
            }
            lock (Gate) _log = output.ToString();
            if (process.HasExited)
            {
                lock (Gate) _process = null;
                var text = output.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text)) text = "进程已退出，可能是 UAC 被取消或端口 11010 被占用";
                return new JsonObject { ["ok"] = false, ["error"] = "EasyTier 启动失败：" + text };
            }
            return new JsonObject { ["ok"] = true, ["running"] = true, ["networkName"] = name, ["server"] = server };
        }

        public static JsonObject Stop()
        {
            lock (Gate)
            {
                var pid = 0;
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        pid = _process.Id;
                        _process.Kill(true);
                    }
                }
                catch
                {
                    // An elevated EasyTier process cannot be killed from a
                    // non-elevated launcher. Ask Windows to do it (UAC prompt).
                    try
                    {
                        if (pid > 0)
                        {
                            var info = new ProcessStartInfo("taskkill.exe", "/F /T /PID " + pid)
                            {
                                UseShellExecute = true,
                                Verb = "runas",
                                CreateNoWindow = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            Process.Start(info)?.WaitForExit(5000);
                        }
                    }
                    catch { }
                }
                _process = null;
            }
            return new JsonObject { ["ok"] = true, ["running"] = false };
        }

        private static bool IsAdministrator()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static async Task<string> Peers()
        {
            if (!File.Exists(CliPath) || _process == null || _process.HasExited) return "";
            try
            {
                var info = new ProcessStartInfo(CliPath, "peer")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = BinDir
                };
                using var process = Process.Start(info);
                if (process == null) return "";
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                return output;
            }
            catch { return ""; }
        }

        private static string Quote(string value) => value.Contains(' ') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    }
}
