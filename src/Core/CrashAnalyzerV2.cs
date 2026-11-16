using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AuroraLauncher
{
    /// <summary>
    /// 崩溃分析：先找根异常（最后一个 Caused by / Description），再按根异常匹配规则，
    /// 避免日志里附带的 401 等次要异常被当成主因。
    /// </summary>
    internal static class CrashAnalyzerV2
    {
        public static JsonObject? Analyze(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            string? description = null;
            var causes = new List<string>();
            string? firstError = null;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                    description = line["Description:".Length..].Trim();
                if (line.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase))
                    causes.Add(line["Caused by:".Length..].Trim());
                if (firstError == null && (line.Contains("Exception", StringComparison.Ordinal) || line.Contains("Error:", StringComparison.Ordinal)) && !line.StartsWith("at ", StringComparison.Ordinal))
                    firstError = line;
            }
            var root = causes.LastOrDefault() ?? firstError;
            if (string.IsNullOrWhiteSpace(root)) return null;

            var evidence = new JsonArray { root };
            var suggestions = new JsonArray();
            var mods = new JsonArray();
            string cause, summary, confidence = "high";

            bool Has(params string[] patterns) => patterns.Any(p => root.Contains(p, StringComparison.OrdinalIgnoreCase));
            void Rule(string name, string detail, string confidenceLevel, params string[] hints)
            {
                cause = name;
                summary = detail;
                confidence = confidenceLevel;
                foreach (var hint in hints) suggestions.Add(hint);
            }

            if (Has("CommandSyntaxException", "Error while building entry"))
            {
                Rule("模组命令/数据错误",
                    "某个模组在注册命令或构建数据时失败，通常是整合包数据与当前模组版本不匹配。", "high",
                    "检查最近更新的 FTB Chunks、FTB Quests、LaserIO 等模组是否与当前游戏版本匹配。",
                    "删除对应模组在 config/ 或世界 data/ 下的旧数据后重试。");
            }
            else if (Has("Mixin apply failed", "MixinTransformerError", "Mixin apply failed"))
            {
                Rule("模组注入失败", "有模组 Mixin 注入失败，通常是模组与游戏/加载器版本不匹配。", "high",
                    "检查最近新增或更新的模组，删除不兼容的模组。");
            }
            else if (Has("UnsupportedClassVersionError"))
            {
                Rule("Java 版本不匹配", "当前 Java 版本低于该模组或加载器要求。", "high",
                    "在实例设置里改用更高版本的 Java。");
            }
            else if (Has("OutOfMemoryError", "Java heap space"))
            {
                Rule("内存不足", "游戏内存不足。", "high",
                    "在实例设置中提高最大内存，或减少大型模组。");
            }
            else if (Has("NoSuchMethodError", "NoClassDefFoundError", "ClassNotFoundException", "NoSuchFieldError"))
            {
                Rule("模组版本不匹配", "某个模组调用了当前版本中不存在的方法或类，通常是模组与游戏/加载器版本不匹配。", "high",
                    "检查报错堆栈里出现的模组，更新到对应游戏版本。");
            }
            else if (Has("ModResolutionException", "Missing or unsupported mandatory dependencies", "missing dependency"))
            {
                Rule("模组依赖缺失", "整合包缺少某个模组的前置依赖。", "high",
                    "根据日志里的 missing 项补装对应前置模组。");
            }
            else if (Has("DuplicateModsFoundException", "Duplicate mods"))
            {
                Rule("模组重复", "mods 目录里存在同一个模组的多个版本。", "high",
                    "删除重复的旧版本 jar。");
            }
            else if (Has("player/attributes") && Has("Status: 401", "401"))
                return null;
            else if (Has("MinecraftClientHttpException", "Status: 401", "AuthenticationException"))
            {
                Rule("登录验证失败", "Minecraft 登录令牌验证失败（HTTP 401）。", "high",
                    "在账户页面重新登录 Microsoft 账户。",
                    "检查系统时间是否准确，然后重启启动器。");
            }
            else if (Has("OpenGL", "GLFW", "Pixel format", "Failed to create window"))
            {
                Rule("显卡/OpenGL 问题", "游戏无法创建 OpenGL 上下文或窗口。", "medium",
                    "更新显卡驱动，或关闭独显直连/覆盖层后重试。");
            }
            else
            {
                cause = "未识别的崩溃";
                summary = "根异常：" + root;
                confidence = "low";
                suggestions.Add("打开日志文件查看完整堆栈，或把日志发给我进一步分析。");
            }

            return new JsonObject
            {
                ["cause"] = cause,
                ["confidence"] = confidence,
                ["summary"] = summary!,
                ["suggestions"] = suggestions,
                ["evidence"] = evidence,
                ["relatedMods"] = mods,
                ["description"] = description ?? ""
            };
        }
    }
}
