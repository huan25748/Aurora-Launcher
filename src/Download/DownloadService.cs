using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class DownloadService
    {
        private const long MultipartMinSize = 256_000;
        private const int MaxSegmentsPerFile = 12;
        private const int MaxFileConnections = 12;
        private const long AdaptiveSegmentTarget = 8L * 1024 * 1024;
        private const long SlowChunkSpeed = 80L * 1024;
        private const long MinSplitRemaining = 2L * 1024 * 1024;
        private const int SlowChunkSeconds = 4;
        private static readonly SemaphoreSlim GlobalConnectionGate = new(64, 64);
        private const int SegmentChunkSize = 512 * 1024;
        private static long ChunkSizeFor(long total, bool aggressive)
        {
            // 模组/整合包 CDN：单连接限速明显，小文件也拆细。
            // 游戏本体与加载器：保持原来的 1MB 分段，细分只会增加请求开销。
            if (!aggressive) return 1024 * 1024;
            if (total <= 1_000_000) return 64 * 1024;
            if (total < 8_000_000) return 256 * 1024;
            if (total < 64_000_000) return 512 * 1024;
            return 1024 * 1024;
        }

        private static readonly SemaphoreSlim SegmentGate = new(128);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 128,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            HttpFactory.Configure(handler);
            var client = new HttpClient(handler)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpFactory.UserAgent);
            return client;
        }

        private static readonly HttpClient DirectHttp = CreateDirect();

        private static HttpClient CreateDirect()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 128,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            HttpFactory.ForceIPv4(handler);
            var client = new HttpClient(handler)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpFactory.UserAgent);
            return client;
        }

        private static HttpClient Pick(HttpRequestMessage request) =>
            HttpFactory.IsDomestic(request.RequestUri?.ToString() ?? "") ? DirectHttp : Http;

        public static string Mirror(string url)
        {
            // Old launcher: version JSON packages are not mirrored by BMCLAPI.
            if (url.Contains("/v1/packages/", StringComparison.OrdinalIgnoreCase)) return url;
            return url
                .Replace("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/")
                .Replace("https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/")
                .Replace("https://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/")
                .Replace("https://launcher.mojang.com/", "https://bmclapi2.bangbang93.com/")
                .Replace("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/")
                .Replace("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/libraries/")
                .Replace("https://maven.minecraftforge.net/", "https://bmclapi2.bangbang93.com/maven/")
                .Replace("https://files.minecraftforge.net/maven/", "https://bmclapi2.bangbang93.com/maven/")
                .Replace("https://maven.neoforged.net/releases/", "https://bmclapi2.bangbang93.com/maven/")
                .Replace("https://maven.neoforged.net/api/", "https://bmclapi2.bangbang93.com/neoforge/meta/api/")
                .Replace("https://maven.fabricmc.net/", "https://bmclapi2.bangbang93.com/maven/")
                .Replace("https://maven.quiltmc.org/repository/release/", "https://bmclapi2.bangbang93.com/maven/")
                .Replace("https://maven.quiltmc.org/", "https://bmclapi2.bangbang93.com/maven/")
                .Replace("https://meta.fabricmc.net/", "https://bmclapi2.bangbang93.com/fabric-meta/")
                .Replace("https://meta.quiltmc.org/", "https://bmclapi2.bangbang93.com/quilt-meta/")
                .Replace("https://api.modrinth.com/", "https://mod.mcimirror.top/modrinth/")
                .Replace("https://cdn.modrinth.com/", "https://mod.mcimirror.top/")
                .Replace("https://edge.forgecdn.net/", "https://mod.mcimirror.top/")
                .Replace("https://mediafilez.forgecdn.net/", "https://mod.mcimirror.top/");
        }

        private static string EngineMode()
        {
            try { var value = BridgeService.GetSettings()["downloadEngine"]?.GetValue<string>() ?? "aria2"; if (value == "idm" || value == "aria2") return "hydra";
                return value; }
            catch { return "idm"; }
        }

        private static string SourceMode()
        {
            try { return BridgeService.GetSettings()["downloadSource"]?.GetValue<string>() ?? "auto"; }
            catch { return "auto"; }
        }

        private static string CategoryFor(string url)
        {
            if (url.Contains("minecraftforge.net", StringComparison.OrdinalIgnoreCase)) return "forge";
            if (url.Contains("neoforged.net", StringComparison.OrdinalIgnoreCase)) return "neoforge";
            if (url.Contains("fabricmc.net", StringComparison.OrdinalIgnoreCase)) return "fabric";
            if (url.Contains("quiltmc.org", StringComparison.OrdinalIgnoreCase)) return "quilt";
            return "vanilla";
        }

        private static string SourceModeFor(string url)
        {
            try
            {
                var settings = BridgeService.GetSettings();
                var category = CategoryFor(url);
                var perCategory = settings["downloadSources"]?[category]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(perCategory))
                    return perCategory!;
                return settings["downloadSource"]?.GetValue<string>() ?? "auto";
            }
            catch { return "auto"; }
        }

        private static bool AutoMirrorFirst(string url)
        {
            return url.StartsWith("https://piston-meta.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://piston-data.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://launchermeta.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://launcher.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.minecraftforge.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://files.minecraftforge.net/maven/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.neoforged.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.fabricmc.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.quiltmc.org/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://cdn.modrinth.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://api.modrinth.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://edge.forgecdn.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://mediafilez.forgecdn.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://media.forgecdn.net/", StringComparison.OrdinalIgnoreCase);
        }

        // Modrinth / CurseForge CDN files use their own segmented single-source
        // downloader; multi-source aggregation is reserved for Mojang/loader files.
        private static bool IsContentCdn(string url)
        {
            return url.StartsWith("https://cdn.modrinth.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://edge.forgecdn.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://mediafilez.forgecdn.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://media.forgecdn.net/", StringComparison.OrdinalIgnoreCase);
        }

        // Same ordering as the old launcher's candidates().
        private static string[] Candidates(string url)
        {
            var mirror = Mirror(url);
            var list = new List<string>();
            var source = SourceModeFor(url);
            if (source == "official") list.Add(url);
            else if (source == "mirror")
            {
                list.Add(mirror);
                list.Add(url);
            }
            else if (mirror != url && AutoMirrorFirst(url))
            {
                list.Add(mirror);
                list.Add(url);
            }
            else
            {
                list.Add(url);
                if (mirror != url) list.Add(mirror);
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static async Task<string> GetText(string url, CancellationToken token = default)
        {
            var last = new Exception("网络请求失败");
            for (var attempt = 0; attempt <= 2; attempt++)
            {
                foreach (var candidate in Candidates(url))
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(8));
                        using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                        request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
                        request.Headers.Accept.ParseAdd("application/json");
                        using var response = await Pick(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} {candidate}");
                        return await response.Content.ReadAsStringAsync(timeout.Token);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
                if (attempt < 2) await Task.Delay(400 * (attempt + 1), token);
            }
            throw last;
        }

        public static async Task<string> GetTextOfficial(string url, CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
            using var response = await Pick(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} {url}");
            return await response.Content.ReadAsStringAsync(token);
        }

        public static async Task Download(string url, string destination, string? sha1 = null, long size = 0, CancellationToken token = default, Action<long, long>? onProgress = null)
        {
            var key = Path.GetFullPath(destination);
            var gate = FileGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            try { await DownloadCore(url, destination, sha1, size, token, onProgress); }
            finally { gate.Release(); }
        }

        private static async Task DownloadCore(string url, string destination, string? sha1, long size, CancellationToken token, Action<long, long>? onProgress)
        {
            if (File.Exists(destination))
            {
                var existing = new FileInfo(destination).Length;
                if (!string.IsNullOrEmpty(sha1) && CheckSha1(destination, sha1)) return;
                if (size > 0 && existing == size) return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (EngineMode() == "hydra" || EngineMode() == "aria2")
            {
                foreach (var ariaUrl in Candidates(url))
                {
                    try
                    {
                        if (await ProbeRange(ariaUrl, token) <= 0) continue;
                        await HydraService.DownloadAsync(ariaUrl, destination, size, onProgress, token);
                        if (!string.IsNullOrEmpty(sha1) && !CheckSha1(destination, sha1))
                            throw new Exception("SHA1 校验失败：" + Path.GetFileName(destination));
                        if (size > 0 && new FileInfo(destination).Length != size)
                            throw new Exception("文件大小不匹配：" + Path.GetFileName(destination));
                        try { if (File.Exists(destination + ".aria2")) File.Delete(destination + ".aria2"); } catch { }
                        return;
                    }
                    catch { }
                }
            }
            var candidates = Candidates(url);
            // 小文件（资源对象、配置、图标等）直接单连接下载：
            // 不做多源探测、不做分段，否则 5000+ 个小文件会被探测和 8 段请求拖死。
            if (size > 0 && size <= 1_500_000)
            {
                Exception? smallLast = null;
                foreach (var candidate in candidates)
                {
                    var smallTemp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
                    try
                    {
                        using var smallTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        smallTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                        await DownloadOnce(candidate, smallTemp, size, smallTimeout.Token, onProgress, false);
                        if (!string.IsNullOrEmpty(sha1) && !CheckSha1(smallTemp, sha1))
                            throw new Exception("SHA1 校验失败：" + Path.GetFileName(destination));
                        if (size > 0 && new FileInfo(smallTemp).Length != size)
                            throw new Exception("文件大小不匹配：" + Path.GetFileName(destination));
                        MoveIntoPlace(smallTemp, destination);
                        return;
                    }
                    catch (Exception ex)
                    {
                        smallLast = ex is OperationCanceledException && !token.IsCancellationRequested
                            ? new SlowSourceException("下载源超时，已换源重试")
                            : ex;
                        try { if (File.Exists(smallTemp)) File.Delete(smallTemp); } catch { }
                    }
                }
                if (smallLast != null) throw smallLast;
            }
            var contentCdn = IsContentCdn(url);
            var minMultipart = contentCdn ? MultipartMinSize : 4_000_000;
            Exception? last = null;
            for (var attempt = 0; attempt <= 2; attempt++)
            {
                foreach (var candidate in candidates)
                {
                    string candidateHost;
                    try { candidateHost = new Uri(candidate).Host; } catch { candidateHost = ""; }
                    if (attempt < 2 && candidateHost.Length > 0
                        && SlowHosts.TryGetValue(candidateHost, out var slowUntil) && slowUntil > DateTime.UtcNow)
                        continue;
                    var temp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
                    try
                    {
                        var total = size;
                        if (total < minMultipart && candidates.Length < 2)
                        {
                            var probed = await ProbeRange(candidate, token);
                            if (probed >= minMultipart) total = probed;
                        }
                        if (total > 0 && total < minMultipart && candidates.Length >= 2 && EngineMode() != "normal")
                        {
                            var (smallHealthy, smallTotal) = await HealthySources(candidates, token);
                            if (smallTotal > 0) total = smallTotal;
                            if (smallHealthy.Length >= 2 &&
                                await DownloadMultipartMulti(smallHealthy, temp, total, token, onProgress, contentCdn))
                            {
                                if (!string.IsNullOrEmpty(sha1) && !CheckSha1(temp, sha1))
                                    throw new Exception("SHA1 校验失败：" + Path.GetFileName(destination));
                                if (size > 0 && new FileInfo(temp).Length != size)
                                    throw new Exception("文件大小不匹配：" + Path.GetFileName(destination));
                                MoveIntoPlace(temp, destination);
                                return;
                            }
                        }
                        var multipart = false;
                        if (total >= minMultipart && EngineMode() != "normal")
                        {
                            if (candidates.Length == 1)
                            {
                                // 单一 CDN 地址：按主机缓存 Range 支持情况。
                                // 部分 CDN（如 forgecdn 的部分节点）对 Range 返回 404，
                                // 强行分段只会白等重试，直接单连接更快。
                                var host = new Uri(candidates[0]).Host;
                                if (!RangeSupport.TryGetValue(host, out var supportsRange))
                                {
                                    supportsRange = await ProbeRange(candidates[0], token) > 0;
                                    RangeSupport[host] = supportsRange;
                                }
                                if (supportsRange)
                                    multipart = await DownloadMultipart(candidates[0], temp, total, token, onProgress, contentCdn);
                            }
                            else
                            {
                                var (healthy, probedTotal) = await HealthySources(candidates, token);
                                if (probedTotal >= minMultipart && total < minMultipart) total = probedTotal;
                                if (healthy.Length >= 2)
                                    multipart = await DownloadMultipartMulti(healthy, temp, total, token, onProgress, contentCdn);
                                else if (healthy.Length == 1)
                                    multipart = await DownloadMultipart(healthy[0], temp, total, token, onProgress, contentCdn);
                            }
                        }
                        if (multipart)
                        {
                            if (!string.IsNullOrEmpty(sha1) && !CheckSha1(temp, sha1))
                                throw new Exception("SHA1 校验失败：" + Path.GetFileName(destination));
                            MoveIntoPlace(temp, destination);
                            return;
                        }
                        await DownloadOnce(candidate, temp, size, token, onProgress, attempt == 2);
                        if (!string.IsNullOrEmpty(sha1) && !CheckSha1(temp, sha1))
                            throw new Exception("SHA1 校验失败：" + Path.GetFileName(destination));
                        if (size > 0 && new FileInfo(temp).Length != size)
                            throw new Exception("文件大小不匹配：" + Path.GetFileName(destination));
                        MoveIntoPlace(temp, destination);
                        return;
                    }
                    catch (SlowSourceException slow)
                    {
                        last = slow;
                        if (candidateHost.Length > 0) SlowHosts[candidateHost] = DateTime.UtcNow.AddMinutes(5);
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                    catch (Exception ex)
                    {
                        last = ex is OperationCanceledException && !token.IsCancellationRequested
                            ? new SlowSourceException("下载源超时，已换源重试")
                            : ex;
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                }
                if (attempt < 2) await Task.Delay(500 * (attempt + 1), token);
            }
            try { if (File.Exists(destination + ".aria2part")) File.Delete(destination + ".aria2part"); if (File.Exists(destination + ".aria2part.aria2")) File.Delete(destination + ".aria2part.aria2"); } catch { }
            throw new Exception($"下载失败 {Path.GetFileName(destination)}: {last?.Message}", last);
        }

        private static void MoveIntoPlace(string temp, string destination)
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temp, destination);
        }

        private static async Task DownloadOnce(string url, string part, long size, CancellationToken token, Action<long, long>? onProgress, bool allowSlow = true)
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await Pick(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} {url}");
            var contentLength = response.Content.Headers.ContentLength ?? 0;
            var total = size > 0 ? size : contentLength;
            long received = 0;
            var lastEmit = DateTime.UtcNow;
            await using (var input = await response.Content.ReadAsStreamAsync(headerTimeout.Token))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    received += read;
                    if ((DateTime.UtcNow - lastEmit).TotalMilliseconds > 150)
                    {
                        lastEmit = DateTime.UtcNow;
                        onProgress?.Invoke(received, total > 0 ? total : received);
                    }
                }
            }
            if (received == 0) throw new Exception("空响应");
            onProgress?.Invoke(received, total > 0 ? total : received);
            if (size > 0 && received != size) throw new Exception($"文件大小不匹配：期望 {size}，实际 {received}");
        }

        private static async Task<long> ProbeRange(string url, CancellationToken token)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await Pick(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != System.Net.HttpStatusCode.PartialContent) return 0;
                var range = response.Content.Headers.ContentRange;
                return range?.Length ?? 0;
            }
            catch { return 0; }
        }

        private static readonly ConcurrentDictionary<string, DateTime> DeadSources = new();
        private static readonly ConcurrentDictionary<string, (double Kbps, DateTime At)> SpeedCache = new();

        /// <summary>小样本测速（128 KB），用于把带宽更多地分给快的源；同主机缓存 10 分钟。</summary>
        private static async Task<double> SourceSpeedKbps(string url, CancellationToken token)
        {
            string host;
            try { host = new Uri(url).Host; } catch { return 1; }
            if (SpeedCache.TryGetValue(host, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
                return cached.Kbps;
            var kbps = 1.0;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 131071);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var response = await Pick(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if ((int)response.StatusCode == 206)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                    var buffer = new byte[65536];
                    long total = 0;
                    int read;
                    while (total < 131072 && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 131072 - total)), cts.Token)) > 0)
                        total += read;
                    sw.Stop();
                    if (total > 0 && sw.Elapsed.TotalSeconds > 0.01)
                        kbps = Math.Max(1, total / 1024.0 / sw.Elapsed.TotalSeconds);
                }
                else if (!response.IsSuccessStatusCode) kbps = 0.5;
            }
            catch { kbps = 0.5; }
            SpeedCache[host] = (kbps, DateTime.UtcNow);
            return kbps;
        }
        private static readonly ConcurrentDictionary<string, DateTime> SlowHosts = new();
        private static readonly ConcurrentDictionary<string, bool> RangeSupport = new(StringComparer.OrdinalIgnoreCase);

        private static async Task<(string[] Urls, long Total)> HealthySources(string[] candidates, CancellationToken token)
        {
            var healthy = new List<string>();
            long total = 0;
            foreach (var candidate in candidates)
            {
                if (DeadSources.TryGetValue(candidate, out var until) && until > DateTime.UtcNow) continue;
                try
                {
                    using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probeTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    var length = await ProbeRange(candidate, probeTimeout.Token);
                    if (length > 0)
                    {
                        healthy.Add(candidate);
                        DeadSources.TryRemove(candidate, out _);
                        total = Math.Max(total, length);
                    }
                }
                catch
                {
                    DeadSources[candidate] = DateTime.UtcNow.AddMinutes(5);
                }
            }
            return (healthy.ToArray(), total);
        }

        /// <summary>
        /// Split the file across every healthy source at the same time. Segments are
        /// assigned round-robin and a failed segment retries on the next source, so
        /// one dead/slow mirror no longer stalls the whole download.
        /// </summary>
        private static async Task<bool> DownloadMultipartMulti(string[] urls, string part, long total, CancellationToken token, Action<long, long>? onProgress, bool aggressive = true)
            => await DownloadAdaptiveRange(urls, part, total, token, onProgress, aggressive);

        private static async Task<bool> DownloadMultipart(string url, string part, long total, CancellationToken token, Action<long, long>? onProgress, bool aggressive = true)
            => await DownloadAdaptiveRange(new[] { url }, part, total, token, onProgress, aggressive);

        private sealed class RangeChunk
        {
            public long Start;
            public long End;
            public int Source;
        }

        private sealed class RangeUnsupportedException : Exception
        {
            public RangeUnsupportedException() : base("下载源不支持 Range") { }
        }

        // 按 PCL 的思路：8MB 起步分段、单文件最多 12 条连接、全局最多 64 条连接，
        // 慢段不换源而是把剩余部分再对半拆开。
        private static async Task<bool> DownloadAdaptiveRange(string[] urls, string part, long total,
            CancellationToken token, Action<long, long>? onProgress, bool aggressive)
        {
            if (urls.Length == 0 || total <= 0) return false;
            var target = aggressive ? ChunkSizeFor(total, true) : AdaptiveSegmentTarget;
            var count = (int)Math.Min(MaxFileConnections, Math.Max(1L, (long)Math.Ceiling(total / (double)target)));
            // 多源同时下载：至少拆成源数量 × 4 段，保证每条源都有活干。
            if (urls.Length > 1)
                count = Math.Max(count, Math.Min(MaxFileConnections, urls.Length * 4));
            var size = (long)Math.Ceiling(total / (double)count);
            var queue = new ConcurrentQueue<RangeChunk>();
            var sourceWeights = new List<int>();
            if (urls.Length > 1)
            {
                // 先测速，再按速度排序，并按速度分配分段权重；
                // 避免慢源固定分到 1/N 的段，把整体速度拖住。
                var speeds = new double[urls.Length];
                for (var i = 0; i < urls.Length; i++) speeds[i] = await SourceSpeedKbps(urls[i], token);
                var order = Enumerable.Range(0, urls.Length).OrderByDescending(i => speeds[i]).ToArray();
                urls = order.Select(i => urls[i]).ToArray();
                for (var i = 0; i < urls.Length; i++)
                {
                    var weight = Math.Clamp((int)Math.Round(speeds[order[i]] / 200.0), 1, 8);
                    for (var w = 0; w < weight; w++) sourceWeights.Add(i);
                }
            }
            var index = 0;
            for (long start = 0; start < total; start += size)
            {
                var source = urls.Length == 1 ? 0 : sourceWeights[index % sourceWeights.Count];
                index++;
                queue.Enqueue(new RangeChunk { Start = start, End = Math.Min(total, start + size) - 1, Source = source });
            }

            long received = 0;
            long lastReport = Environment.TickCount64;
            void Report(bool force)
            {
                var now = Environment.TickCount64;
                if (!force && now - lastReport < 120) return;
                lastReport = now;
                onProgress?.Invoke(Interlocked.Read(ref received), total);
            }

            try
            {
                await using var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
                stream.SetLength(total);
                var handle = stream.SafeFileHandle;
                var workers = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
                {
                    while (queue.TryDequeue(out var chunk))
                    {
                        await DownloadChunkAsync(urls, handle, queue, chunk, token, addBytes =>
                        {
                            Interlocked.Add(ref received, addBytes);
                            Report(false);
                        });
                    }
                }, token)).ToArray();
                await Task.WhenAll(workers);
                await stream.FlushAsync();
            }
            catch (RangeUnsupportedException)
            {
                return false;
            }
            Report(true);
            if (Interlocked.Read(ref received) < total)
                throw new Exception("分段下载未完成");
            return true;
        }

        private static async Task DownloadChunkAsync(string[] urls, Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            ConcurrentQueue<RangeChunk> queue, RangeChunk chunk, CancellationToken token, Action<long> addBytes)
        {
            var position = chunk.Start;
            var attempt = 0;
            while (position <= chunk.End)
            {
                var url = urls[(chunk.Source + attempt) % urls.Length];
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                long requestBytes = 0;
                var requestStart = position;
                try
                {
                    await GlobalConnectionGate.WaitAsync(token);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
                        request.Headers.AcceptEncoding.ParseAdd("identity");
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(position, chunk.End);
                        using var response = await Pick(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            throw new RangeUnsupportedException();
                        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                        var buffer = new byte[81920];
                        while (position <= chunk.End)
                        {
                            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            readTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                            var want = (int)Math.Min(buffer.Length, chunk.End - position + 1);
                            var read = await input.ReadAsync(buffer.AsMemory(0, want), readTimeout.Token);
                            if (read <= 0) break;
                            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, token);
                            position += read;
                            requestBytes += read;
                            addBytes(read);
                            var elapsed = stopwatch.Elapsed.TotalSeconds;
                            if (elapsed >= SlowChunkSeconds && requestBytes / elapsed < SlowChunkSpeed)
                            {
                                var remaining = chunk.End - position + 1;
                                if (remaining >= MinSplitRemaining)
                                {
                                    var half = remaining / 2;
                                    queue.Enqueue(new RangeChunk { Start = position, End = position + half - 1, Source = 0 });
                                    queue.Enqueue(new RangeChunk { Start = position + half, End = chunk.End, Source = 0 });
                                    return;
                                }
                            }
                        }
                        if (position <= chunk.End && position == requestStart)
                            throw new Exception("分段下载无进展");
                    }
                    finally
                    {
                        GlobalConnectionGate.Release();
                    }
                }
                catch (RangeUnsupportedException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (attempt >= 2) throw;
                    attempt++;
                    await Task.Delay(300 * attempt, token);
                }
            }
        }

        private static async Task<long> DownloadSegment(string url, string file, long start, long end, CancellationToken token, Action<long>? onProgress = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(300));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(HttpFactory.UserAgent);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
            using var response = await Pick(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} 不支持分段下载");
            long received = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long reported = 0;
                var lastTick = Environment.TickCount64;
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    received += read;
                    var now = Environment.TickCount64;
                    if (received - reported >= 131072 || now - lastTick >= 200)
                    {
                        reported = received;
                        lastTick = now;
                        onProgress?.Invoke(received);
                    }
                }
                onProgress?.Invoke(received);
            }
            return received;
        }

        private static bool CheckSha1(string file, string sha1)
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var sha = SHA1.Create();
                var hash = sha.ComputeHash(stream);
                var text = Convert.ToHexString(hash).ToLowerInvariant();
                return text == sha1.ToLowerInvariant();
            }
            catch { return false; }
        }
    }

    internal sealed class SlowSourceException : Exception
    {
        public SlowSourceException(string message) : base(message) { }
    }
}
