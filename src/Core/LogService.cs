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
            if ((instance?["playCount"]?.GetValue<int>() ?? 0) <= 0 && instance?["lastPlayedAt"] == null) return null;
            try
            {
                var lastPlayed = instance?["lastPlayedAt"]?.GetValue<long>() ?? 0;
                if (lastPlayed > 0 && File.GetLastWriteTimeUtc(file) < DateTimeOffset.FromUnixTimeMilliseconds(lastPlayed).UtcDateTime.AddHours(-2))
                    return null;
            }
            catch { }
            var evidence = new JsonArray();
            var suggestions = new JsonArray();
            var mods = new JsonArray();
            var cause = "";
            var summary = "";
            var confidence = "high";
            try
            {
                var text = "";
                try { text = Decode(File.ReadAllBytes(file)); } catch { }
                try
                {
                    var name = instance?["name"]?.GetValue<string>() ?? "";
                    var captured = Path.Combine(AppPaths.SettingsDir, "logs", "game-" + name + ".log");
                    if (File.Exists(captured)) text += "\n" + Decode(File.ReadAllBytes(captured));
                }
                catch { }
                try
                {
                    var crashDir = Path.Combine(LogsDir(instanceId), "..", "crash-reports");
                    if (Directory.Exists(crashDir))
                    {
                        var newest = Directory.GetFiles(crashDir, "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                        if (newest != null) text += "\n" + Decode(File.ReadAllBytes(newest));
                    }
                }
                catch { }
                string? LineOf(string token)
                {
                    foreach (var line in text.Split('\n'))
                        if (line.Contains(token, StringComparison.OrdinalIgnoreCase)) return line.Trim();
                    return null;
                }
                bool All(params string[] patterns) => patterns.All(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
                void Match(string name, string detail, string[] hints, params string[] patterns)
                {
                    if (cause.Length > 0) return;
                    if (!All(patterns)) return;
                    cause = name;
                    summary = detail;
                    foreach (var hint in hints) suggestions.Add(hint);
                    foreach (var pattern in patterns) evidence.Add(LineOf(pattern) ?? pattern);
                }
                void MatchAny(string name, string detail, string[] hints, params string[] patterns)
                {
                    if (cause.Length > 0) return;
                    var hit = patterns.FirstOrDefault(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) return;
                    cause = name;
                    summary = detail;
                    foreach (var hint in hints) suggestions.Add(hint);
                    evidence.Add(LineOf(hit) ?? hit);
                }

                MatchAny("登录验证失败", "Minecraft 登录令牌验证失败（HTTP 401），不是模组或 Mixin 问题。",
                    new[] { "在账户页面重新登录 Microsoft 账户。", "检查系统时间是否准确，然后重启启动器。" },
                    "MinecraftClientHttpException: Status: 401");

                Match("模组与加载器模块冲突", "有模组与当前加载器存在模块冲突。",
                    new[] { "删除不兼容的模组（例如 Forge 1.18.x 上的 CustomSkinLoader 15.x），或改用与该加载器兼容的版本。" },
                    "java.lang.module.ResolutionException", "export package");

                MatchAny("Java 版本过高", "当前 Java 版本对该游戏或加载器来说过高。",
                    new[] { "在启动设置中改用较低版本的 Java 后再试。" },
                    "Unable to make protected final java.lang.Class java.lang.ClassLoader.defineClass",
                    "java.lang.NoSuchFieldException: ucp",
                    "because module java.base does not export",
                    "java.lang.ClassNotFoundException: jdk.nashorn.api.scripting.NashornScriptEngineFactory",
                    "java.lang.ClassNotFoundException: java.lang.invoke.LambdaMetafactory");

                MatchAny("Forge 参数重复", "实例 JSON 中包含多个 Forge 版本参数。",
                    new[] { "在实例设置中重置或重新安装 Forge，不要使用其他启动器生成的实例 JSON。" },
                    "Found multiple arguments for option fml.forgeVersion, but you asked for only one");

                MatchAny("显卡驱动不支持 OpenGL", "显卡驱动可能不支持游戏所需的 OpenGL 版本。",
                    new[] { "尝试更新或回退显卡驱动；必要时改用更低版本的 Java。" },
                    "The driver does not appear to support OpenGL");

                MatchAny("使用了 JDK", "游戏可能正在使用 JDK，或当前 Java 版本过高。",
                    new[] { "在启动设置中改用常规 JRE 或其他适合该实例的 Java 后再试。" },
                    "java.lang.ClassCastException: java.base/jdk",
                    "java.lang.ClassCastException: class jdk.");

                MatchAny("OptiFine 与 Forge 不兼容", "OptiFine 与当前 Forge 或 Minecraft 版本可能不兼容。",
                    new[] { "检查 OptiFine 与 Forge 的兼容版本，并重新安装匹配的版本。" },
                    "TRANSFORMER/net.optifine/net.optifine.reflect.Reflector.<clinit>(Reflector.java",
                    "java.lang.NoSuchMethodError: 'void net.minecraft.client.renderer.texture.SpriteContents.<init>",
                    "java.lang.NoSuchMethodError: 'java.lang.String com.mojang.blaze3d.systems.RenderSystem.getBackendDescription",
                    "java.lang.NoSuchMethodError: 'void net.minecraft.client.renderer.block.model.BakedQuad.<init>",
                    "java.lang.NoSuchMethodError: 'void net.minecraftforge.client.gui.overlay.ForgeGui.renderSelectedItemName",
                    "java.lang.NoSuchMethodError: 'void net.minecraft.server.level.DistanceManager",
                    "java.lang.NoSuchMethodError: 'net.minecraft.network.chat.FormattedText net.minecraft.client.gui.Font.ellipsize");

                MatchAny("使用了 OpenJ9", "当前使用的 OpenJ9 Java 可能与游戏、模组或加载器不兼容。",
                    new[] { "在启动设置中改用 HotSpot Java 后再试。" },
                    "Open J9 is not supported", "OpenJ9 is incompatible", ".J9VMInternals.");

                Match("OptiFine 世界加载崩溃", "当前 OptiFine 版本可能导致世界加载失败。",
                    new[] { "尝试更换 OptiFine 版本，或暂时移除 OptiFine 后再进入世界。" },
                    "java.lang.NoSuchMethodError: net.minecraft.world.server.ChunkManager$ProxyTicketManager.shouldForceTicks(J)Z",
                    "OptiFine");

                MatchAny("模组文件被解压", "检测到已被解压的模组文件。",
                    new[] { "删除 mods 文件夹中已被解压的模组文件夹，并放入完整的 .jar 模组文件。" },
                    "The directories below appear to be extracted jar files. Fix this before you continue.",
                    "Extracted mod jars found, loading will NOT continue");

                Match("缺少 MixinBootstrap", "缺少 MixinBootstrap，游戏无法继续加载。",
                    new[] { "检查 MixinBootstrap、模组加载器和相关前置是否已完整安装。" },
                    "java.lang.ClassNotFoundException: org.spongepowered.asm.launch.MixinTweaker");

                MatchAny("像素格式不受支持", "显卡驱动或显示模式不支持游戏需要的像素格式。",
                    new[] { "更新显卡驱动；若使用远程桌面或虚拟机，改用本地环境测试。" },
                    "Couldn't set pixel format", "Pixel format not accelerated");

                Match("Shaders 模组与 OptiFine 冲突", "同时安装了 Shaders 模组与 OptiFine。",
                    new[] { "删除 Shaders Mod。OptiFine 已提供光影支持。" },
                    "java.lang.RuntimeException: Shaders Mod detected. Please remove it, OptiFine has built-in support for shaders.");

                MatchAny("旧版 Forge 与 Java 不兼容", "旧版 Forge 与当前 Java 版本不兼容。",
                    new[] { "尝试升级 Forge，或改用较低版本的 Java。" },
                    "java.lang.NoSuchMethodError: sun.security.util.ManifestEntryVerifier",
                    "ThrowableProxy.formatExtendedStackTraceTo");

                MatchAny("OpenGL 1282 错误", "光影或资源包可能触发了 OpenGL 1282 错误。",
                    new[] { "关闭光影，并移除最近添加的资源包后再试。" },
                    "1282: Invalid operation");

                MatchAny("资源包分辨率过高", "资源包分辨率可能过高，或显卡性能不足。",
                    new[] { "移除高清资源包，或降低资源包分辨率；也可以更新显卡驱动。" },
                    "Maybe try a lower resolution resourcepack?");

                MatchAny("Java 版本不匹配", "当前 Java 版本与游戏或模组不兼容。",
                    new[] { "根据 Minecraft 版本和模组要求，在启动设置中选择合适的 Java。" },
                    "Unsupported class file major version", "Unsupported major.minor version",
                    "UnsupportedClassVersionError");

                MatchAny("Night Config 配置读取错误", "Night Config 读取配置时发生错误。",
                    new[] { "尝试删除相关模组配置文件，或安装 Night Config Fixes。" },
                    "com.electronwill.nightconfig.core.io.ParsingException: Not enough data available");

                MatchAny("Forge 安装不完整", "Forge 安装不完整，缺少必要文件。",
                    new[] { "在实例设置中重置或重新安装 Forge；不要在打包实例时丢失 libraries 目录。" },
                    "Cannot find launch target fmlclient, unable to launch",
                    "Invalid paths argument, contained no existing paths");

                MatchAny("模组文件名不合法", "某个模组文件名包含 Java 不支持的特殊字符。",
                    new[] { "移除或重命名异常的模组文件，使文件名仅包含英文字母、数字、短横线和下划线。" },
                    "Invalid module name: '' is not a Java identifier");

                MatchAny("模组需要 Java 11", "部分已安装模组需要 Java 11 或更高版本。",
                    new[] { "在启动设置中改用 Java 11 或更高版本后再试。" },
                    "has been compiled by a more recent version of the Java Runtime (class file version 55.0), this version of the Java Runtime only recognizes class file versions up to",
                    "java.lang.RuntimeException: java.lang.NoSuchMethodException: no such method: sun.misc.Unsafe.defineAnonymousClass(Class,byte[],Object[])Class/invokeVirtual",
                    "java.lang.IllegalArgumentException: The requested compatibility level JAVA_11 could not be set. Level is not supported by the active JRE or ASM version");

                MatchAny("32 位 Java 内存上限", "32 位 Java 无法分配设置的内存。",
                    new[] { "安装并在启动设置中选择 64 位 Java 后再试。" },
                    "Invalid maximum heap size");

                MatchAny("模组数量超过 ID 限制", "模组数量过多，超出了旧版 Minecraft 的 ID 限制。",
                    new[] { "减少模组数量，或为旧版 Minecraft 安装能够扩展 ID 限制的修复模组。" },
                    "maximum id range exceeded");

                MatchAny("手动触发调试崩溃", "这是手动触发的调试崩溃，游戏本身通常没有问题。",
                    new[] { "取消触发调试崩溃的操作后重新启动游戏。" },
                    "Manually triggered debug crash");

                MatchAny("内存不足", "Minecraft 内存不足，无法继续运行。",
                    new[] { "在启动设置中增加游戏分配内存，并移除配置要求较高的资源包、模组或光影。" },
                    "java.lang.OutOfMemoryError", "OutOfMemoryError");

                if (cause.Length == 0 && (text.Contains("Mixin apply failed", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("MixinTransformerError", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("org.spongepowered.asm.mixin", StringComparison.OrdinalIgnoreCase)))
                {
                    cause = "Mixin 注入失败";
                    confidence = "medium";
                    summary = "部分模组的 Mixin 注入失败。";
                    suggestions.Add("逐步禁用模组，以定位与当前环境不兼容或存在问题的模组。");
                    evidence.Add(LineOf("Mixin apply failed") ?? "Mixin 注入失败");
                }

                if (cause.Length == 0) return null;

                if (text.Contains("Caused by:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in text.Split('\n'))
                    {
                        if (!line.Contains("Caused by:", StringComparison.OrdinalIgnoreCase)) continue;
                        var trimmed = line.Trim();
                        if (!evidence.OfType<JsonValue>().Any(v => v?.GetValue<string>() == trimmed))
                            evidence.Add(trimmed.Length > 240 ? trimmed[..240] : trimmed);
                        if (evidence.Count >= 6) break;
                    }
                }
            }
            catch { return null; }
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
