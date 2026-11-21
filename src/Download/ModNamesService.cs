using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;

namespace AuroraLauncher
{
    internal static class ModNamesService
    {
        private static Dictionary<string, string>? _cf;
        private static Dictionary<string, string>? _mr;
        private static Dictionary<string, string>? _cn;
        private static readonly object Gate = new();

        public static void Warm()
        {
            Ensure();
        }

        private static void Ensure()
        {
            if (_cf != null && _mr != null) return;
            lock (Gate)
            {
                if (_cf != null && _mr != null) return;
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AuroraLauncher.mcmod-names.json");
                    if (stream == null) throw new Exception("missing");
                    using var reader = new StreamReader(stream);
                    var json = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
                    _cf = Read(json?["cf"] as JsonObject);
                    _mr = Read(json?["mr"] as JsonObject);
                    _cn = Read(json?["cn"] as JsonObject);
                }
                catch
                {
                    _cf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _mr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _cn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        private static Dictionary<string, string> Read(JsonObject? obj)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj == null) return result;
            foreach (var pair in obj)
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    result[pair.Key.Trim().ToLowerInvariant()] = text;
            return result;
        }

        public static string? Chinese(string source, string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;
            Ensure();
            var key = slug!.Trim().ToLowerInvariant();
            var map = source == "curseforge" ? _cf! : _mr!;
            return map.TryGetValue(key, out var text) ? text : null;
        }

        /// <summary>用本地 MC 百科中文名表反查英文名，不做机器翻译。</summary>
        public static string? English(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            Ensure();
            var text = query.Trim();
            if (_cn!.TryGetValue(text, out var exact)) return exact;
            string? best = null;
            var bestScore = -1;
            foreach (var pair in _cn)
            {
                if (pair.Key.Length < 2) continue;
                var hit = text.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Contains(text, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;
                var score = pair.Key.Length * 10;
                if (text.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)) score += 5;
                if (score > bestScore) { bestScore = score; best = pair.Value; }
            }
            return best;
        }

        /// <summary>中文名反查项目 slug（用于 CurseForge 搜索）。</summary>
        public static string? Slug(string source, string chinese)
        {
            if (string.IsNullOrWhiteSpace(chinese)) return null;
            Ensure();
            var map = source == "curseforge" ? _cf! : _mr!;
            var query = chinese.Trim();
            string? best = null;
            var bestScore = -1;
            foreach (var pair in map)
            {
                var value = pair.Value ?? "";
                var score = -1;
                if (string.Equals(value, query, StringComparison.OrdinalIgnoreCase)) score = 10000;
                else
                {
                    var first = value.Split('(')[0].Trim();
                    if (string.Equals(first, query, StringComparison.OrdinalIgnoreCase)) score = 9000;
                    else if (value.Contains(query, StringComparison.OrdinalIgnoreCase)) score = 1000 - pair.Key.Length;
                }
                if (score <= bestScore) continue;
                // 数字 ID 不能作为搜索词。
                if (!pair.Key.Any(char.IsLetter)) continue;
                bestScore = score;
                best = pair.Key;
            }
            return best;
        }

        public static void RememberChinese(string chinese, string english)
        {
            if (string.IsNullOrWhiteSpace(chinese) || string.IsNullOrWhiteSpace(english)) return;
            Ensure();
            _cn![chinese.Trim()] = english.Trim();
        }

        public static JsonObject Translate(JsonArray queries)
        {
            var result = new JsonObject();
            foreach (var item in queries)
            {
                if (item is not JsonObject query) continue;
                var name = query["name"]?.GetValue<string>();
                var slug = query["slug"]?.GetValue<string>();
                var source = query["source"]?.GetValue<string>() ?? "modrinth";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var chinese = Chinese(source, slug);
                if (!string.IsNullOrWhiteSpace(chinese)) result[name!] = chinese;
            }
            return result;
        }
    }
}
