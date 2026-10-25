using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class ModNamesService
    {
        private static Dictionary<string, string>? _cf;
        private static Dictionary<string, string>? _mr;
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
                }
                catch
                {
                    _cf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _mr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
