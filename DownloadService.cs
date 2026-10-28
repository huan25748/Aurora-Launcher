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
        private const long MultipartMinSize = 4_000_000;
        private const int MaxSegmentsPerFile = 8;
        private static readonly SemaphoreSlim SegmentGate = new(128);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 128,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        { Timeout = TimeSpan.FromMinutes(10), DefaultRequestVersion = System.Net.HttpVersion.Version20, DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower };

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

        private static string SourceMode()
        {
            try { return BridgeService.GetSettings()["downloadSource"]?.GetValue<string>() ?? "auto"; }
            catch { return "auto"; }
        }

        private static bool AutoMirrorFirst(string url)
        {
            return url.StartsWith("https://piston-meta.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://piston-data.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://launchermeta.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://launcher.mojang.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://libraries.minecraft.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://resources.download.minecraft.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.minecraftforge.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://files.minecraftforge.net/maven/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.neoforged.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.fabricmc.net/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://maven.quiltmc.org/", StringComparison.OrdinalIgnoreCase);
        }

        // Same ordering as the old launcher's candidates().
        private static string[] Candidates(string url)
        {
            var mirror = Mirror(url);
            var list = new List<string>();
            var source = SourceMode();
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
            Exception? last = null;
            for (var attempt = 0; attempt <= 2; attempt++)
            {
                foreach (var candidate in Candidates(url))
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                        request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
                        request.Headers.Accept.ParseAdd("application/json");
                        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} {candidate}");
                        return await response.Content.ReadAsStringAsync(timeout.Token);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
                if (attempt < 2) await Task.Delay(500 * (attempt + 1), token);
            }
            throw last ?? new Exception("网络请求失败");
        }

        public static async Task<string> GetTextOfficial(string url, CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
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
                if (string.IsNullOrEmpty(sha1) || CheckSha1(destination, sha1))
                {
                    if (size <= 0 || new FileInfo(destination).Length == size) return;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var candidates = Candidates(url);
            Exception? last = null;
            for (var attempt = 0; attempt <= 2; attempt++)
            {
                foreach (var candidate in candidates)
                {
                    var temp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
                    try
                    {
                        var total = size;
                        if (total < MultipartMinSize)
                        {
                            var probed = await ProbeRange(candidate, token);
                            if (probed >= MultipartMinSize) total = probed;
                        }
                        if (total >= MultipartMinSize &&
                            await DownloadMultipart(candidate, temp, total, token, onProgress))
                        {
                            if (!string.IsNullOrEmpty(sha1) && !CheckSha1(temp, sha1))
                                throw new Exception("SHA1 校验失败：" + Path.GetFileName(destination));
                            MoveIntoPlace(temp, destination);
                            return;
                        }
                        await DownloadOnce(candidate, temp, size, token, onProgress);
                        if (!string.IsNullOrEmpty(sha1) && !CheckSha1(temp, sha1))
                            throw new Exception("SHA1 校验失败：" + Path.GetFileName(destination));
                        if (size > 0 && new FileInfo(temp).Length != size)
                            throw new Exception("文件大小不匹配：" + Path.GetFileName(destination));
                        MoveIntoPlace(temp, destination);
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                }
                if (attempt < 2) await Task.Delay(500 * (attempt + 1), token);
            }
            throw new Exception($"下载失败 {Path.GetFileName(destination)}: {last?.Message}", last);
        }

        private static void MoveIntoPlace(string temp, string destination)
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temp, destination);
        }

        private static async Task DownloadOnce(string url, string part, long size, CancellationToken token, Action<long, long>? onProgress)
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} {url}");
            var contentLength = response.Content.Headers.ContentLength ?? 0;
            var total = size > 0 ? size : contentLength;
            long received = 0;
            var lastEmit = DateTime.UtcNow;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    received += read;
                    if ((DateTime.UtcNow - lastEmit).TotalMilliseconds > 120)
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
                request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != System.Net.HttpStatusCode.PartialContent) return 0;
                var range = response.Content.Headers.ContentRange;
                return range?.Length ?? 0;
            }
            catch { return 0; }
        }

        private static async Task<bool> DownloadMultipart(string url, string part, long total, CancellationToken token, Action<long, long>? onProgress)
        {
            var count = Math.Max(1, Math.Min(MaxSegmentsPerFile, (int)Math.Ceiling(total / (8d * 1024 * 1024))));
            if (count < 2) return false;
            var chunk = (long)Math.Ceiling(total / (double)count);
            var segments = Enumerable.Range(0, count).Select(i => new
            {
                Index = i,
                Start = i * chunk,
                End = i == count - 1 ? total - 1 : Math.Min(total - 1, (i + 1) * chunk - 1),
                File = $"{part}.{i}"
            }).ToArray();
            long received = 0;
            try
            {
                var tasks = segments.Select(async segment =>
                {
                    await SegmentGate.WaitAsync(token);
                    try
                    {
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                long segmentReceived = 0;
                                var written = await DownloadSegment(url, segment.File, segment.Start, segment.End, token, bytes =>
                                {
                                    var delta = bytes - segmentReceived;
                                    if (delta <= 0) return;
                                    segmentReceived = bytes;
                                    onProgress?.Invoke(Math.Min(total, Interlocked.Add(ref received, delta)), total);
                                });
                                if (written != segment.End - segment.Start + 1) throw new Exception("分段大小不匹配");
                                return;
                            }
                            catch when (attempt < 2) { await Task.Delay(300 * (attempt + 1), token); }
                        }
                        throw new Exception("分段下载失败");
                    }
                    finally { SegmentGate.Release(); }
                });
                await Task.WhenAll(tasks);
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var segment in segments)
                    {
                        await using var input = new FileStream(segment.File, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await input.CopyToAsync(output, token);
                    }
                }
                if (new FileInfo(part).Length != total) throw new Exception("合并后的文件大小不匹配");
                onProgress?.Invoke(total, total);
                return true;
            }
            finally
            {
                foreach (var segment in segments)
                    try { File.Delete(segment.File); } catch { }
            }
        }

        private static async Task<long> DownloadSegment(string url, string file, long start, long end, CancellationToken token, Action<long>? onProgress = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AuroraLauncher/1.1.0 (.NET)");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} 不支持分段下载");
            long received = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    received += read;
                    onProgress?.Invoke(received);
                }
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
}
