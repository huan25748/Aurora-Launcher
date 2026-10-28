using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class LogService
    {
        private static string Resolve(string instanceId)
        {
            if (!string.IsNullOrWhiteSpace(instanceId)) return instanceId;
            try
            {
                var running = LaunchService.State()["instanceId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(running)) return running!;
            }
            catch { }
            return BridgeService.ListInstances().OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<string>() ?? "";
        }

        private static string LogsDir(string instanceId)
        {
            var instance = BridgeService.GetInstance(instanceId) as JsonObject;
            var gameDir = instance?["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance?["versionId"]?.GetValue<string>() ?? "");
            return Path.Combine(gameDir, "logs");
        }

        public static JsonArray Latest(string instanceId)
        {
            instanceId = Resolve(instanceId);
            var file = Path.Combine(LogsDir(instanceId), "latest.log");
            if (!File.Exists(file)) return new JsonArray();
            try
            {
                var lines = File.ReadAllLines(file).TakeLast(500);
                var result = new JsonArray();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    result.Add(new JsonObject
                    {
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["source"] = "game",
                        ["level"] = line.Contains("[ERROR]") ? "error" : line.Contains("[WARN]") ? "warning" : "info",
                        ["text"] = line
                    });
                }
                return result;
            }
            catch { return new JsonArray(); }
        }

        public static JsonArray Files(string instanceId)
        {
            instanceId = Resolve(instanceId);
            var dir = LogsDir(instanceId);
            if (!Directory.Exists(dir)) return new JsonArray();
            var result = new JsonArray();
            foreach (var file in Directory.GetFiles(dir).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var info = new FileInfo(file);
                    result.Add(new JsonObject
                    {
                        ["name"] = info.Name,
                        ["path"] = info.FullName,
                        ["size"] = info.Length,
                        ["mtime"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                    });
                }
                catch { }
            }
            return result;
        }

        public static string Read(string path, int maxBytes)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var root = Path.GetFullPath(AppPaths.RootDir);
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("日志路径不合法");
            if (!File.Exists(full)) return "";
            var info = new FileInfo(full);
            using var stream = File.OpenRead(full);
            if (maxBytes > 0 && info.Length > maxBytes) stream.Seek(info.Length - maxBytes, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Decode(memory.ToArray());
        }

        private static string Decode(byte[] bytes)
        {
            try
            {
                var strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes);
            }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(bytes); }
                catch { return Encoding.UTF8.GetString(bytes); }
            }
        }

        public static JsonObject? Analyze(string instanceId)
        {
            instanceId = Resolve(instanceId);
            var file = Path.Combine(LogsDir(instanceId), "latest.log");
            if (!File.Exists(file)) return null;
            var instance = BridgeService.GetInstance(instanceId) as JsonObject;
            // Never show another instance's crash on an instance that was never
            // launched (many portable instances share one .minecraft folder).
            if ((instance?["playCount"]?.GetValue<int>() ?? 0) <= 0 && instance?["lastPlayedAt"] == null) return null;
            try
            {
                var lastPlayed = instance?["lastPlayedAt"]?.GetValue<long>() ?? 0;
                // A shared .minecraft/logs/latest.log must not make an instance
                // that was last played long ago show a newer instance's crash.
                if (lastPlayed > 0 && File.GetLastWriteTimeUtc(file) < DateTimeOffset.FromUnixTimeMilliseconds(lastPlayed).UtcDateTime.AddHours(-2))
                    return null;
            }
            catch { }
            var evidence = new JsonArray();
            var suggestions = new JsonArray();
            var mods = new JsonArray();
            var cause = "未发现明显崩溃";
            var confidence = "low";
            var summary = "日志没有匹配到明确的崩溃特征。";
            {
                var text = "";
                try { text = Decode(File.ReadAllBytes(file)); } catch { }
                void Hit(string needle, string name, string hint, string level = "high")
                {
                    if (confidence == "high") return;
                    if (!text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return;
                    cause = name; confidence = level; summary = name + "。";
                    evidence.Add(needle);
                    suggestions.Add(hint);
                }
                Hit("OutOfMemoryError", "内存不足", "调高实例内存分配，或减少同时加载的模组。");
                Hit("UnsupportedClassVersionError", "Java 版本不匹配", "把实例 Java 切换到该 Minecraft 版本要求的版本。");
                if (text.Contains("Mixin apply failed", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("MixinTransformerError", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("MixinBootstrap", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("org.spongepowered.asm.mixin", StringComparison.OrdinalIgnoreCase))
                {
                    cause = "Mixin 注入失败";
                    confidence = "medium";
                    summary = "Mixin 注入失败。";
                    suggestions.Add("检查最近安装的模组是否与该游戏版本/加载器兼容。");
                    evidence.Add("Mixin apply failed");
                }
                if (text.Contains("MinecraftClientHttpException", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("401", StringComparison.OrdinalIgnoreCase))
                {
                    cause = "登录验证失败";
                    confidence = "high";
                    summary = "Minecraft 登录令牌验证失败（HTTP 401），不是模组或 Mixin 问题。";
                    suggestions.Clear();
                    suggestions.Add("在账户页面重新登录 Microsoft 账户。");
                    suggestions.Add("检查系统时间是否准确，然后重启启动器。");
                    evidence.Add("MinecraftClientHttpException: Status: 401");
                }
                else if (text.Contains("VarHandler is null", StringComparison.OrdinalIgnoreCase))
                {
                    cause = "Forge / ModLauncher 启动失败";
                    confidence = "medium";
                    summary = "Forge 的 ModLauncher 没有完成初始化，常见于 Java 版本不匹配或 Forge 安装文件不完整。";
                    suggestions.Clear();
                    suggestions.Add("确认该 Minecraft 版本使用的 Java 正确，必要时切换到 Java 8 或 Java 17。");
                    suggestions.Add("重新安装对应 Forge / NeoForge 版本。");
                    evidence.Add("java.lang.RuntimeException: VarHandler is null");
                }
                Hit("NoSuchMethodError", "模组 API 不兼容", "检查模组之间的版本依赖。");
                Hit("NoClassDefFoundError", "缺少依赖模组", "补齐前置模组或重新安装整合包。");
                Hit("Exception in thread", "游戏或模组抛出异常", "查看日志中第一段 Caused by 堆栈。", "medium");

                // More specific than the generic NoSuchMethodError rule: old Forge
                // ModLauncher on a newer Java 8 build, or duplicate log4j jars.
                if (text.Contains("ThrowableProxy.formatExtendedStackTraceTo", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("ManifestEntryVerifier", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("SecureJarHandler", StringComparison.OrdinalIgnoreCase))
                {
                    cause = "Forge 与 Java 版本不兼容";
                    confidence = "high";
                    summary = "由于低版本 Forge 与当前 Java 不兼容，导致游戏崩溃。";
                    suggestions.Clear();
                    suggestions.Add("更新 Forge 到 36.2.26 或更高版本。");
                    suggestions.Add("换用 1.8.0.320 以下的 Java 8（例如 1.8.0_51）。");
                    evidence.Add("低版本 Forge 与 Java 8u320 以上的内部类实现不兼容");
                }
                else if (text.Contains("NoClassDefFoundError: java/lang/Object", StringComparison.OrdinalIgnoreCase))
                {
                    cause = "Java 运行时损坏";
                    confidence = "high";
                    summary = "Java 运行时缺少基础类 java/lang/Object，说明该 Java 安装不完整。";
                    suggestions.Clear();
                    suggestions.Add("重新下载安装 Java，优先使用启动器管理的 jre-legacy。");
                    suggestions.Add("不要在运行中移动或删除 Java 安装目录。");
                    evidence.Add("java/lang/NoClassDefFoundError: java/lang/Object");
                }

                if (cause != "未发现明显崩溃" && text.Contains("Caused by:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in text.Split('\n'))
                    {
                        if (!line.Contains("Caused by:", StringComparison.OrdinalIgnoreCase)) continue;
                        evidence.Add(line.Trim());
                        if (evidence.Count > 6) break;
                    }
                }
            }
            if (evidence.Count == 0) return null;
            return new JsonObject
            {
                ["cause"] = cause,
                ["confidence"] = confidence,
                ["summary"] = summary,
                ["suggestions"] = suggestions,
                ["evidence"] = evidence,
                ["relatedMods"] = mods
            };
        }

    }
}
