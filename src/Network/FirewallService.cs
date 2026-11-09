using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class FirewallService
    {
        public static JsonObject Allow()
        {
            try
            {
                var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var core = Path.Combine(AppPaths.SettingsDir, "easytier", "bin", "easytier-core.exe");
                if (File.Exists(core)) programs.Add(core);
                var state = LaunchService.State();
                var pid = state["pid"]?.GetValue<int>() ?? 0;
                if (pid > 0)
                {
                    try
                    {
                        using var game = Process.GetProcessById(pid);
                        var file = game.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) programs.Add(file);
                    }
                    catch { }
                }
                foreach (var root in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
                    Path.Combine(AppPaths.SettingsDir, "java")
                })
                {
                    if (!Directory.Exists(root)) continue;
                    try
                    {
                        foreach (var file in Directory.GetFiles(root, "java*.exe", SearchOption.AllDirectories).Take(8))
                            programs.Add(file);
                    }
                    catch { }
                }
                if (programs.Count == 0)
                    return new JsonObject { ["ok"] = false, ["error"] = "没有找到 Java 或 EasyTier 程序路径" };
                var script = new StringBuilder();
                foreach (var program in programs)
                {
                    var name = "Aurora Launcher - " + Path.GetFileName(program);
                    script.AppendLine($"netsh advfirewall firewall delete rule name=\"{name}\" >nul 2>&1");
                    script.AppendLine($"netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{program}\" enable=yes profile=any");
                }
                var info = new ProcessStartInfo("cmd.exe", "/c " + script)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var process = Process.Start(info);
                if (process == null) return new JsonObject { ["ok"] = false, ["error"] = "无法启动防火墙设置" };
                process.WaitForExit(15000);
                return new JsonObject
                {
                    ["ok"] = process.ExitCode == 0,
                    ["programs"] = new JsonArray(programs.Select(item => (JsonNode)item).ToArray()),
                    ["error"] = process.ExitCode == 0 ? "" : "防火墙规则添加失败，可能被组策略锁定"
                };
            }
            catch (Exception ex)
            {
                return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
        }
    }
}
