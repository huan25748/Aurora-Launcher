using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class TranslationService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly ConcurrentDictionary<string, string?> Cache = new();
        private static readonly object Gate = new();
        private static bool _loaded;

        public static async Task<string?> Translate(string? text)
        {
            var clean = Clean(text);
            if (clean.Length < 3) return null;
            if (Regex.IsMatch(clean, "[\u4e00-\u9fff]")) return null;
            if (clean.Length > 450) clean = clean[..450];
            EnsureLoaded();
            if (Cache.TryGetValue(clean, out var cached)) return cached;
            try
            {
                var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(clean) + "&langpair=en%7Czh-CN";
                using var response = await Http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
                var translated = json?["responseData"]?["translatedText"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(translated) || translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                    translated = null;
                Cache[clean] = translated;
                Save();
                return translated;
            }
            catch
            {
                return null;
            }
        }

        private static string Clean(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var value = Regex.Replace(text, "<[^>]+>", " ");
            value = Regex.Replace(value, @"!\[[^\]]*\]\([^)]*\)", " ");
            value = Regex.Replace(value, @"\[([^\]]+)\]\([^)]*\)", "$1");
            value = Regex.Replace(value, @"[#>*`_~|]+", " ");
            return Regex.Replace(value, @"\s+", " ").Trim();
        }

        private static string FilePath => Path.Combine(AppPaths.SettingsDir, "translations.json");

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
                        if (json != null)
                            foreach (var pair in json)
                                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                                    Cache[pair.Key] = text;
                    }
                }
                catch { }
            }
        }

        private static void Save()
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.SettingsDir);
                    var json = new JsonObject();
                    foreach (var pair in Cache.Take(400)) json[pair.Key] = pair.Value;
                    File.WriteAllText(FilePath, json.ToJsonString());
                }
            }
            catch { }
        }
    }
}
