using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class McmodService
    {
        private static HttpClient Http => HttpFactory.For("https://www.mcmod.cn/");
        private static readonly ConcurrentDictionary<string, (DateTime At, string Url)> Cache = new();

        private static HttpClient CreateHttp()
        {
            var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) };
            HttpFactory.Configure(handler);
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpFactory.UserAgent);
            return client;
        }

        public static async Task<string> English(string query)
        {
            var url = await Search(query);
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                var html = await Http.GetStringAsync(url);
                var match = System.Text.RegularExpressions.Regex.Match(html, "<title>[^<]*?\\(([^()]+)\\)\\s*-\\s*MC百科");
                if (!match.Success) return "";
                var name = System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, "^\\[.*?\\]\\s*", "").Trim();
                return name;
            }
            catch { return ""; }
        }

        public static async Task<string> Search(string query, string? alternative = null)
        {
            foreach (var candidate in Candidates(query, alternative))
            {
                var url = await SearchOne(candidate);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
            return "";
        }

        private static IEnumerable<string> Candidates(string query, string? alternative)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in new[] { query, alternative })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var value = raw.Trim();
                foreach (var candidate in Variants(value))
                    if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                        yield return candidate;
            }
        }

        private static IEnumerable<string> Variants(string value)
        {
            yield return value;
            // Emoji / dingbats are common in CurseForge titles but never part of a
            // mcmod entry name.
            var noEmoji = Regex.Replace(value, @"[\p{So}\p{Sk}\p{Cs}\uFE0F]+", "").Trim();
            if (noEmoji.Length > 0) yield return noEmoji;
            // Normalize full-width brackets, then split "中文 (English)".
            var normalized = noEmoji.Replace('（', '(').Replace('）', ')').Replace('［', '[').Replace('］', ']').Trim();
            if (normalized.Length > 0) yield return normalized;
            var bracket = Regex.Match(normalized, @"^(.+?)\s*[(\[【](.+?)[)\]】]\s*$");
            if (bracket.Success)
            {
                yield return bracket.Groups[1].Value.Trim();
                yield return bracket.Groups[2].Value.Trim();
            }
            var noBracket = Regex.Replace(normalized, @"\s*[(\[【].*?[)\]】]\s*", " ").Trim();
            if (noBracket.Length > 0) yield return noBracket;
            // Letters/digits/CJK only: "（玉 🔍）" -> "玉", "Jade 🔍" -> "Jade".
            var plain = Regex.Replace(value, @"[^\p{L}\p{N}]+", " ").Trim();
            if (plain.Length > 0) yield return plain;
            foreach (var piece in plain.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (piece.Length >= 2) yield return piece;
        }

        private static async Task<string> SearchOne(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";
            if (Cache.TryGetValue(query, out var hit))
            {
                if (!string.IsNullOrEmpty(hit.Url) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(6)) return hit.Url;
                if (string.IsNullOrEmpty(hit.Url) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return "";
            }
            try
            {
                var html = await Http.GetStringAsync("https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(query));
                var match = Regex.Match(html, "/class/(\\d+)\\.html");
                var url = match.Success ? "https://www.mcmod.cn/class/" + match.Groups[1].Value + ".html" : "";
                Cache[query] = (DateTime.UtcNow, url);
                return url;
            }
            catch
            {
                Cache[query] = (DateTime.UtcNow, "");
                return "";
            }
        }
    }
}
