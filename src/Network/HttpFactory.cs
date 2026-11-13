using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AuroraLauncher
{
    /// <summary>
    /// 统一构造 HttpClient：
    /// - 启动器设置里启用代理时，所有请求走该代理；
    /// - 未启用时，国内镜像（BMCLAPI / MCIM 等）强制直连，避免系统里残留的失效代理把下载拖死；
    /// - 其余请求保持系统代理（VPN / Clash 等），与 PCL、SJMCL 行为一致。
    /// </summary>
    internal static class HttpFactory
    {
        private static readonly object Gate = new();
        private static HttpClient? _client;
        private static HttpClient? _direct;
        private static string _signature = "";

        public static string UserAgent =>
            "AuroraLauncher/" + VersionInfo.Format(typeof(HttpFactory).Assembly.GetName().Version) +
            " (+https://github.com/huan25748/Aurora-Launcher)";

        public static HttpClient Client
        {
            get
            {
                lock (Gate)
                {
                    var (enabled, host, port) = Proxy();
                    var signature = enabled ? $"{host}:{port}" : "system";
                    if (_client != null && signature == _signature) return _client;

                    var handler = new SocketsHttpHandler
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(8),
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };
                    ForceIPv4(handler);
                    if (enabled && port > 0)
                    {
                        handler.Proxy = new WebProxy($"http://{host}:{port}");
                        handler.UseProxy = true;
                    }
                    else handler.UseProxy = false; // 默认直连，忽略系统里可能残留的失效代理（与 SJMCL/reqwest 一致）
                    _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                    _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                    _signature = signature;
                    return _client;
                }
            }
        }

        public static HttpClient Direct
        {
            get
            {
                lock (Gate)
                {
                    if (_direct != null) return _direct;
                    var handler = new SocketsHttpHandler
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(8),
                        UseProxy = false,
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };
                    ForceIPv4(handler);
                    _direct = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                    _direct.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                    return _direct;
                }
            }
        }

        public static HttpClient For(string url)
        {
            var (enabled, _, _) = Proxy();
            if (enabled) return Client;
            return IsDomestic(url) ? Direct : Client;
        }

        /// <summary>
        /// 只连 IPv4。部分网络到镜像/官方源的 IPv6 路由不可用，.NET 默认会先尝试 IPv6，
        /// 连接超时后整个请求就失败了；curl / PCL / SJMCL 会快速回退 IPv4，所以它们正常。
        /// </summary>
        public static void ForceIPv4(SocketsHttpHandler handler)
        {
            handler.ConnectCallback = async (context, token) =>
            {
                var entry = context.DnsEndPoint;
                IPAddress? address = null;
                if (IPAddress.TryParse(entry.Host, out var literal))
                {
                    address = literal;
                }
                else
                {
                    var addresses = await Dns.GetHostAddressesAsync(entry.Host, token).ConfigureAwait(false);
                    address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                        ?? addresses.FirstOrDefault();
                }
                if (address == null) throw new IOException("无法解析主机：" + entry.Host);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, entry.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        public static bool IsDomestic(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host;
            return host.EndsWith("bangbang93.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("mcimirror.top", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("mcmod.cn", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("lss233.com", StringComparison.OrdinalIgnoreCase);
        }

        public static void Configure(SocketsHttpHandler handler)
        {
            ForceIPv4(handler);
            var (enabled, host, port) = Proxy();
            if (enabled && port > 0)
            {
                handler.Proxy = new WebProxy($"http://{host}:{port}");
                handler.UseProxy = true;
            }
            else handler.UseProxy = false;
        }

        private static (bool Enabled, string Host, int Port) Proxy()
        {
            try
            {
                var settings = BridgeService.GetSettings();
                return (
                    settings["proxyEnabled"]?.GetValue<bool>() == true,
                    settings["proxyHost"]?.GetValue<string>() ?? "",
                    settings["proxyPort"]?.GetValue<int>() ?? 0);
            }
            catch { return (false, "", 0); }
        }
    }
}
