using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AuroraLauncher
{
    internal static class LanScanService
    {
        public static JsonObject Scan()
        {
            try
            {
                var state = LaunchService.State();
                var pid = state["pid"]?.GetValue<int>() ?? 0;
                if (pid <= 0)
                    return new JsonObject { ["ok"] = false, ["error"] = "游戏尚未运行，请先进入世界并点击“对局域网开放”" };
                var ip = VirtualIp();
                var ports = ListeningPorts(pid);
                if (ports.Count == 0)
                    return new JsonObject { ["ok"] = false, ["error"] = "没有发现游戏局域网端口，请先在游戏内点击“对局域网开放”", ["ip"] = ip };
                var port = ports[0];
                var address = string.IsNullOrWhiteSpace(ip) ? $":{port}" : $"{ip}:{port}";
                return new JsonObject
                {
                    ["ok"] = true,
                    ["ip"] = ip,
                    ["port"] = port,
                    ["ports"] = new JsonArray(ports.Select(item => (JsonNode)item).ToArray()),
                    ["address"] = address
                };
            }
            catch (Exception ex)
            {
                return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        private static List<int> ListeningPorts(int pid)
        {
            var result = new List<int>();
            try
            {
                var info = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var process = Process.Start(info);
                if (process == null) return result;
                var text = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                foreach (var line in text.Split('\n'))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;
                    if (!string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(parts[4], out var linePid) || linePid != pid) continue;
                    var local = parts[1];
                    var colon = local.LastIndexOf(':');
                    if (colon < 0 || !int.TryParse(local[(colon + 1)..], out var port)) continue;
                    if (port > 1024 && port < 65536 && !result.Contains(port)) result.Add(port);
                }
            }
            catch { }
            return result;
        }

        private static string VirtualIp()
        {
            // The peer table's first row is this node and its ipv4 column is
            // exactly the address assigned by EasyTier, e.g. 10.126.126.1/24.
            try
            {
                var cli = System.IO.Path.Combine(AppPaths.SettingsDir, "easytier", "bin", "easytier-cli.exe");
                if (System.IO.File.Exists(cli))
                {
                    var info = new ProcessStartInfo(cli, "peer")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var process = Process.Start(info);
                    if (process != null)
                    {
                        var text = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(2000);
                        foreach (var raw in text.Split('\n'))
                        {
                            var line = raw.Trim();
                            if (!line.StartsWith("|", StringComparison.Ordinal) || line.Contains("---")) continue;
                            var cells = line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (cells.Length == 0) continue;
                            var match = Regex.Match(cells[0], @"^(\d{1,3}(?:\.\d{1,3}){3})(?:/\d{1,2})?$");
                            if (!match.Success) continue;
                            var ip = match.Groups[1].Value;
                            if (ip.StartsWith("127.", StringComparison.Ordinal) || ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                            return ip;
                        }
                    }
                }
            }
            catch { }
            var candidates = new List<(string Ip, int Score)>();
            void Add(string ip, string prefix, bool namedEasyTier)
            {
                if (!System.Net.IPAddress.TryParse(ip, out _)) return;
                if (ip.StartsWith("127.", StringComparison.Ordinal) || ip.StartsWith("169.254.", StringComparison.Ordinal)) return;
                var score = 0;
                // EasyTier's default DHCP range is 10.126.126.0/24.
                if (ip.StartsWith("10.126.126.", StringComparison.Ordinal)) score += 100;
                if (prefix == "24") score += 60;
                if (namedEasyTier) score += 80;
                if (ip.StartsWith("10.", StringComparison.Ordinal)) score += 10;
                // Radmin VPN commonly uses 26.x and must never win.
                if (ip.StartsWith("26.", StringComparison.Ordinal)) score -= 40;
                candidates.Add((ip, score));
            }
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up) continue;
                    var name = network.Name + " " + network.Description;
                    var named = name.Contains("EasyTier", StringComparison.OrdinalIgnoreCase);
                    foreach (var item in network.GetIPProperties().UnicastAddresses)
                    {
                        if (item.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        Add(item.Address.ToString(), item.PrefixLength.ToString(), named);
                    }
                }
            }
            catch { }
            // EasyTier 的 TUN 网卡名称不固定，直接读取 CLI 的 Interface IPv4。
            try
            {
                var cli = System.IO.Path.Combine(AppPaths.SettingsDir, "easytier", "bin", "easytier-cli.exe");
                if (System.IO.File.Exists(cli))
                {
                    var info = new ProcessStartInfo(cli, "node")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var process = Process.Start(info);
                    if (process != null)
                    {
                        var text = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(2000);
                        foreach (var line in text.Split('\n'))
                        {
                            if (!line.Contains("Interface IPv4", StringComparison.OrdinalIgnoreCase)) continue;
                            var match = Regex.Match(line, @"\b(\d{1,3}(?:\.\d{1,3}){3})(?:/(\d{1,2}))?\b");
                            if (!match.Success) continue;
                            Add(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : "", true);
                        }
                    }
                }
            }
            catch { }
            return candidates.OrderByDescending(item => item.Score).Select(item => item.Ip).FirstOrDefault() ?? "";
        }
    }
}
