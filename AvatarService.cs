using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AuroraLauncher
{
    internal static class AvatarService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        public static async Task<string?> Fetch(string nameOrUuid)
        {
            if (string.IsNullOrWhiteSpace(nameOrUuid)) return null;
            try
            {
                var uuid = nameOrUuid.Replace("-", "").Trim();
                if (uuid.Length != 32)
                {
                    var lookup = await DownloadService.GetText(
                        "https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(nameOrUuid));
                    uuid = (JsonNode.Parse(lookup) as JsonObject)?["id"]?.GetValue<string>() ?? "";
                }
                if (uuid.Length != 32) return null;

                var profileText = await DownloadService.GetText(
                    "https://sessionserver.mojang.com/session/minecraft/profile/" + uuid);
                var profile = JsonNode.Parse(profileText) as JsonObject;
                var property = (profile?["properties"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(item => item["name"]?.GetValue<string>() == "textures");
                var value = property?["value"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(value)) return null;
                var decoded = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))) as JsonObject;
                var skinUrl = decoded?["textures"]?["SKIN"]?["url"]?.GetValue<string>()
                    ?? decoded?["textures"]?["skin"]?["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(skinUrl)) return null;

                var temp = Path.Combine(Path.GetTempPath(), "aurora-head-" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    await DownloadService.Download(skinUrl!, temp);
                    var png = CropHead(File.ReadAllBytes(temp));
                    return png == null ? null : "data:image/png;base64," + Convert.ToBase64String(png);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch { return null; }
        }

        private static byte[] ReadPixels(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var data = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(data, converted.PixelWidth * 4, 0);
            return data;
        }

        public static byte[]? CropHead(byte[] skin)
        {
            try
            {
                using var input = new MemoryStream(skin);
                var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth < 64 || frame.PixelHeight < 32) return null;
                // Mirrors PCL's MySkin head composition: 56px canvas, face
                // centered at (4,4) 48x48, hat layer stretched over 56x56.
                var scale = Math.Max(1, frame.PixelWidth / 64);
                var face = ReadPixels(new CroppedBitmap(frame, new Int32Rect(8 * scale, 8 * scale, 8 * scale, 8 * scale)));
                var hat = frame.PixelHeight >= 64 * scale
                    ? ReadPixels(new CroppedBitmap(frame, new Int32Rect(40 * scale, 8 * scale, 8 * scale, 8 * scale)))
                    : null;
                // PCL's MySkin: 56px canvas, face 48px at (4,4), hat stretched
                // over the whole canvas, all with nearest-neighbor sampling.
                const int canvas = 56;
                var pixels = new byte[canvas * canvas * 4];
                for (var y = 0; y < 48; y++)
                {
                    for (var x = 0; x < 48; x++)
                    {
                        var s = (y * 8 / 48 * 8 + x * 8 / 48) * 4;
                        var d = ((y + 4) * canvas + x + 4) * 4;
                        pixels[d] = face[s];
                        pixels[d + 1] = face[s + 1];
                        pixels[d + 2] = face[s + 2];
                        pixels[d + 3] = face[s + 3];
                    }
                }
                if (hat != null)
                {
                    for (var y = 0; y < canvas; y++)
                    {
                        for (var x = 0; x < canvas; x++)
                        {
                            var s = (y * 8 / canvas * 8 + x * 8 / canvas) * 4;
                            var a = hat[s + 3];
                            if (a == 0) continue;
                            var d = (y * canvas + x) * 4;
                            pixels[d] = (byte)((hat[s] * a + pixels[d] * (255 - a)) / 255);
                            pixels[d + 1] = (byte)((hat[s + 1] * a + pixels[d + 1] * (255 - a)) / 255);
                            pixels[d + 2] = (byte)((hat[s + 2] * a + pixels[d + 2] * (255 - a)) / 255);
                            pixels[d + 3] = (byte)Math.Min(255, a + pixels[d + 3] * (255 - a) / 255);
                        }
                    }
                }
                var result = BitmapSource.Create(canvas, canvas, 96, 96, PixelFormats.Bgra32, null, pixels, canvas * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(result));
                using var output = new MemoryStream();
                encoder.Save(output);
                return output.ToArray();
            }
            catch { return null; }
        }
    }
}
