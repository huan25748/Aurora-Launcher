using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class ServerPingService
    {
        public static async Task<JsonObject> Ping(string address)
        {
            var host = address?.Trim() ?? "";
            var port = 25565;
            if (host.StartsWith("["))
            {
                var end = host.IndexOf(']');
                if (end > 0) host = host.Substring(1, end - 1);
            }
            var colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out var parsedPort))
            {
                port = parsedPort;
                host = host.Substring(0, colon);
            }
            if (string.IsNullOrWhiteSpace(host)) return new JsonObject { ["online"] = false, ["error"] = "服务器地址为空" };

            var watch = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, port);
                if (await Task.WhenAny(connect, Task.Delay(6000)) != connect)
                    return new JsonObject { ["online"] = false, ["error"] = "连接超时" };
                await connect;
                using var stream = client.GetStream();
                stream.ReadTimeout = 6000;
                stream.WriteTimeout = 6000;

                using (var handshake = new MemoryStream())
                {
                    WriteVarInt(handshake, 0x00);
                    WriteVarInt(handshake, 0x00);
                    WriteString(handshake, host);
                    handshake.WriteByte((byte)(port >> 8));
                    handshake.WriteByte((byte)port);
                    WriteVarInt(handshake, 0x01);
                    var body = handshake.ToArray();
                    using var packet = new MemoryStream();
                    WriteVarInt(packet, body.Length);
                    packet.Write(body);
                    await stream.WriteAsync(packet.ToArray());
                }
                using (var request = new MemoryStream())
                {
                    WriteVarInt(request, 0x00);
                    var body = request.ToArray();
                    using var packet = new MemoryStream();
                    WriteVarInt(packet, body.Length);
                    packet.Write(body);
                    await stream.WriteAsync(packet.ToArray());
                }

                var length = ReadVarInt(stream);
                if (length <= 0 || length > 4 * 1024 * 1024) throw new Exception("服务器响应长度异常");
                var payload = new byte[length];
                var offset = 0;
                while (offset < length)
                {
                    var read = await stream.ReadAsync(payload, offset, length - offset);
                    if (read <= 0) throw new Exception("服务器提前关闭连接");
                    offset += read;
                }
                using var reader = new MemoryStream(payload);
                ReadVarInt(reader);
                var jsonText = ReadString(reader);
                var json = JsonNode.Parse(jsonText) as JsonObject ?? new JsonObject();
                watch.Stop();
                var result = new JsonObject
                {
                    ["online"] = true,
                    ["ping"] = watch.ElapsedMilliseconds,
                    ["version"] = new JsonObject
                    {
                        ["name"] = json["version"]?["name"]?.GetValue<string>() ?? "",
                        ["protocol"] = json["version"]?["protocol"]?.GetValue<int>() ?? 0
                    },
                    ["players"] = new JsonObject
                    {
                        ["online"] = json["players"]?["online"]?.GetValue<int>() ?? 0,
                        ["max"] = json["players"]?["max"]?.GetValue<int>() ?? 0
                    },
                    ["motd"] = new JsonObject
                    {
                        ["text"] = MotdText(json["description"])
                    }
                };
                var favicon = json["favicon"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(favicon)) result["favicon"] = favicon;
                return result;
            }
            catch (Exception ex)
            {
                return new JsonObject { ["online"] = false, ["error"] = ex.Message };
            }
        }

        private static string MotdText(JsonNode? node)
        {
            if (node == null) return "";
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
            if (node is JsonObject obj)
            {
                var builder = new StringBuilder(obj["text"]?.GetValue<string>() ?? "");
                if (obj["extra"] is JsonArray extra)
                    foreach (var item in extra) builder.Append(MotdText(item));
                return builder.ToString();
            }
            return "";
        }

        private static void WriteVarInt(Stream stream, int value)
        {
            var unsigned = (uint)value;
            while (true)
            {
                var temp = (byte)(unsigned & 0x7f);
                unsigned >>= 7;
                if (unsigned != 0) temp |= 0x80;
                stream.WriteByte(temp);
                if (unsigned == 0) break;
            }
        }

        private static int ReadVarInt(Stream stream)
        {
            var result = 0;
            var shift = 0;
            while (true)
            {
                var read = stream.ReadByte();
                if (read < 0) throw new EndOfStreamException();
                result |= (read & 0x7f) << shift;
                if ((read & 0x80) == 0) return result;
                shift += 7;
                if (shift > 28) throw new Exception("VarInt 过大");
            }
        }

        private static void WriteString(Stream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            WriteVarInt(stream, bytes.Length);
            stream.Write(bytes);
        }

        private static string ReadString(Stream stream)
        {
            var length = ReadVarInt(stream);
            if (length < 0 || length > 4 * 1024 * 1024) throw new Exception("字符串长度异常");
            var bytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(bytes, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
