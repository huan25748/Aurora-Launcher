using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuroraLauncher
{
    internal static class NbtServersService
    {
        private static string FileFor(string instanceId)
        {
            var instance = BridgeService.GetInstance(instanceId) as JsonObject;
            var gameDir = instance?["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance?["versionId"]?.GetValue<string>() ?? "");
            return Path.Combine(gameDir, "servers.dat");
        }

        public static JsonArray List(string instanceId)
        {
            var result = new JsonArray();
            var path = FileFor(instanceId);
            if (!File.Exists(path)) return result;
            try
            {
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                var type = ReadByte(gzip);
                if (type != 10) return result;
                ReadString(gzip);
                while (true)
                {
                    var childType = ReadByte(gzip);
                    if (childType == 0) break;
                    var name = ReadString(gzip);
                    if (name == "servers" && childType == 9)
                    {
                        var elementType = ReadByte(gzip);
                        var length = ReadInt(gzip);
                        for (var i = 0; i < length; i++)
                        {
                            if (elementType != 10) { Skip(gzip, elementType); continue; }
                            var entry = new JsonObject();
                            while (true)
                            {
                                var fieldType = ReadByte(gzip);
                                if (fieldType == 0) break;
                                var field = ReadString(gzip);
                                switch (fieldType)
                                {
                                    case 8: entry[field] = ReadString(gzip); break;
                                    case 1: entry[field] = ReadByte(gzip); break;
                                    default: Skip(gzip, fieldType); break;
                                }
                            }
                            var ip = entry["ip"]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(ip)) continue;
                            result.Add(new JsonObject
                            {
                                ["name"] = entry["name"]?.GetValue<string>() ?? ip,
                                ["ip"] = ip,
                                ["icon"] = entry["icon"]?.GetValue<string>() is string icon ? "data:image/png;base64," + icon : null,
                                ["hidden"] = entry["hidden"]?.GetValue<byte>() == 1
                            });
                        }
                    }
                    else Skip(gzip, childType);
                }
            }
            catch { }
            return result;
        }

        public static JsonArray Add(string instanceId, JsonElement input)
        {
            var list = (JsonArray)JsonNode.Parse(List(instanceId).ToJsonString())!;
            var ip = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("ip", out var ipValue) ? ipValue.GetString()?.Trim() ?? "" : "";
            if (ip.Length == 0) throw new Exception("服务器地址不能为空");
            var name = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? ip : ip;
            var icon = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("icon", out var iconValue) ? iconValue.GetString() : null;
            for (var i = 0; i < list.Count; i++)
                if (list[i]?["ip"]?.GetValue<string>() == ip) { list.RemoveAt(i); break; }
            list.Add(new JsonObject { ["name"] = name, ["ip"] = ip, ["icon"] = icon, ["hidden"] = false });
            Write(instanceId, list);
            return list;
        }

        public static JsonArray Remove(string instanceId, string ip)
        {
            var list = (JsonArray)JsonNode.Parse(List(instanceId).ToJsonString())!;
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i]?["ip"]?.GetValue<string>() == ip) list.RemoveAt(i);
            Write(instanceId, list);
            return list;
        }

        private static void Write(string instanceId, JsonArray list)
        {
            var path = FileFor(instanceId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionMode.Compress);
            WriteByte(gzip, 10);
            WriteString(gzip, "servers");
            WriteByte(gzip, 9);
            WriteString(gzip, "servers");
            WriteByte(gzip, 10);
            WriteInt(gzip, list.Count);
            foreach (var item in list)
            {
                if (item is not JsonObject server) continue;
                WriteByte(gzip, 8); WriteString(gzip, "name"); WriteString(gzip, server["name"]?.GetValue<string>() ?? server["ip"]?.GetValue<string>() ?? "");
                WriteByte(gzip, 8); WriteString(gzip, "ip"); WriteString(gzip, server["ip"]?.GetValue<string>() ?? "");
                var icon = server["icon"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(icon))
                {
                    WriteByte(gzip, 8); WriteString(gzip, "icon"); WriteString(gzip, icon!.Replace("data:image/png;base64,", ""));
                }
                if (server["hidden"]?.GetValue<bool>() == true)
                {
                    WriteByte(gzip, 1); WriteString(gzip, "hidden"); WriteByte(gzip, 1);
                }
                WriteByte(gzip, 0);
            }
            WriteByte(gzip, 0);
        }

        private static byte ReadByte(Stream stream)
        {
            var value = stream.ReadByte();
            return value < 0 ? (byte)0 : (byte)value;
        }

        private static int ReadInt(Stream stream)
        {
            var buffer = new byte[4];
            ReadExact(stream, buffer, 0, 4);
            return (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
        }

        private static string ReadString(Stream stream)
        {
            var length = (ReadByte(stream) << 8) | ReadByte(stream);
            var buffer = new byte[length];
            ReadExact(stream, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }

        private static void WriteByte(Stream stream, int value) => stream.WriteByte((byte)value);

        private static void WriteInt(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteString(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        private static void Skip(Stream stream, byte type)
        {
            switch (type)
            {
                case 1: stream.ReadByte(); break;
                case 2: { var buffer = new byte[2]; ReadExact(stream, buffer, 0, buffer.Length); break; }
                case 3: { var buffer = new byte[4]; ReadExact(stream, buffer, 0, buffer.Length); break; }
                case 4: { var buffer = new byte[8]; ReadExact(stream, buffer, 0, buffer.Length); break; }
                case 5: { var buffer = new byte[4]; ReadExact(stream, buffer, 0, buffer.Length); break; }
                case 6: { var buffer = new byte[8]; ReadExact(stream, buffer, 0, buffer.Length); break; }
                case 7: { var length = ReadInt(stream); var buffer = new byte[Math.Max(0, length)]; ReadExact(stream, buffer, 0, buffer.Length); break; }
                case 8: ReadString(stream); break;
                case 9:
                {
                    var element = ReadByte(stream);
                    var length = ReadInt(stream);
                    for (var i = 0; i < length; i++) Skip(stream, element);
                    break;
                }
                case 10:
                {
                    while (true)
                    {
                        var child = ReadByte(stream);
                        if (child == 0) break;
                        ReadString(stream);
                        Skip(stream, child);
                    }
                    break;
                }
                case 11: { var length = ReadInt(stream); var buffer = new byte[4 * Math.Max(0, length)]; ReadExact(stream, buffer, 0, buffer.Length); break; }
                case 12: { var length = ReadInt(stream); var buffer = new byte[8 * Math.Max(0, length)]; ReadExact(stream, buffer, 0, buffer.Length); break; }
            }
        }
    }
}
