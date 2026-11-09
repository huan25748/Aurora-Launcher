using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class AccountSkinService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        private const string Steve = "8667ba71-b85a-4004-af54-457a9734eed7";
        private const string Alex = "ec561538-f3fd-461d-aff5-086b22154bce";
        private static readonly string[] RandomUuids =
        {
            Steve, Alex,
            "069a79f4-44e9-4726-a5be-fca90e38aaf5",
            "853c80ef-3c37-49fd-aa49-938b674adae6",
            "61699b2e-d327-4a01-9f1e-0ea8c3f06bc6"
        };

        public static string SkinDir
        {
            get
            {
                var dir = Path.Combine(AppPaths.SettingsDir, "skins");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string SkinPath(string accountId) => Path.Combine(SkinDir, accountId + ".png");

        public static async Task<JsonObject> Apply(string accountId, string mode, string variant, string? data, string playerName, CancellationToken token = default)
        {
            var account = AccountService.Find(accountId) ?? throw new Exception("账户不存在");
            var type = account["type"]?.GetValue<string>() ?? "offline";
            if (type == "microsoft") throw new Exception("正版账户只支持更换披风");
            mode = (mode ?? "").Trim().ToLowerInvariant();
            if (mode != "random" && mode != "steve" && mode != "alex" && mode != "premium" && mode != "custom")
                throw new Exception("未知的皮肤类型");

            byte[] skin;
            var skinName = "";
            if (mode == "custom")
            {
                skin = DecodePng(data);
            }
            else if (mode is "random" or "steve" or "alex")
            {
                var uuid = mode switch
                {
                    "steve" => Steve,
                    "alex" => Alex,
                    _ => RandomUuids[Random.Shared.Next(RandomUuids.Length)]
                };
                skin = await DownloadSkin("https://crafatar.com/skins/" + uuid, token);
            }
            else
            {
                skinName = (string.IsNullOrWhiteSpace(playerName)
                    ? account["username"]?.GetValue<string>() ?? ""
                    : playerName.Trim());
                if (string.IsNullOrWhiteSpace(skinName)) throw new Exception("请输入正版玩家名");
                skin = await DownloadPremiumSkin(skinName, token);
            }
            File.WriteAllBytes(SkinPath(accountId), skin);

            var head = AvatarService.CropHead(skin);
            var avatar = head == null ? null : "data:image/png;base64," + Convert.ToBase64String(head);
            AccountService.Update(accountId, item =>
            {
                item["skinMode"] = mode;
                item["skinName"] = skinName;
                item["skinVariant"] = variant == "slim" ? "slim" : "classic";
                item["skinFile"] = Path.GetFileName(SkinPath(accountId));
                if (avatar != null)
                {
                    item["avatarUrl"] = avatar;
                    item["avatarMode"] = "head3d";
                    item["avatarVersion"] = 4;
                }
            });
            return (JsonObject)JsonNode.Parse(AccountService.Find(accountId)!.ToJsonString())!;
        }

        public static async Task<JsonObject> UploadMicrosoft(string accountId, string variant, string? data, CancellationToken token = default)
        {
            var account = AccountService.Find(accountId) ?? throw new Exception("账户不存在");
            if (account["type"]?.GetValue<string>() != "microsoft") throw new Exception("只有微软账户可以上传正版皮肤");
            var png = DecodePng(data);
            var (width, height) = PngSize(png);
            if (width != 64 || (height != 32 && height != 64)) throw new Exception("皮肤尺寸必须是 64×32 或 64×64");
            if (png.Length > 24 * 1024) throw new Exception("皮肤文件不能超过 24 KB");

            var access = account["accessToken"]?.GetValue<string>() ?? "";
            if (!await UploadSkin(access, png, variant, token))
            {
                account = await Refresh(account);
                access = account["accessToken"]?.GetValue<string>() ?? "";
                if (!await UploadSkin(access, png, variant, token)) throw new Exception("上传皮肤失败，请重新登录微软账户");
            }
            var head = AvatarService.CropHead(png);
            AccountService.Update(accountId, item =>
            {
                item["skinMode"] = "custom";
                item["skinVariant"] = variant == "slim" ? "slim" : "classic";
                if (head != null)
                {
                    item["avatarUrl"] = "data:image/png;base64," + Convert.ToBase64String(head);
                    item["avatarMode"] = "head3d";
                    item["avatarVersion"] = 4;
                }
            });
            return (JsonObject)JsonNode.Parse(AccountService.Find(accountId)!.ToJsonString())!;
        }

        private static async Task<bool> UploadSkin(string access, byte[] png, string variant, CancellationToken token)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(variant == "slim" ? "slim" : "classic"), "variant");
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(file, "file", "skin.png");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await Http.SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }

        private static (int Width, int Height) PngSize(byte[] png)
        {
            if (png.Length < 24) return (0, 0);
            var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return (width, height);
        }

        public static async Task<JsonArray> GetCapes(string accountId, CancellationToken token = default)
        {
            var account = await EnsureMicrosoft(accountId, token);
            var access = account["accessToken"]?.GetValue<string>() ?? "";
            var result = await RequestCapes(access, token);
            if (result == null)
            {
                var refreshed = await Refresh(account);
                result = await RequestCapes(refreshed["accessToken"]?.GetValue<string>() ?? "", token);
            }
            if (result == null) throw new Exception("获取披风列表失败，请重新登录微软账户");
            return result;
        }

        public static async Task SetCape(string accountId, string capeId, CancellationToken token = default)
        {
            var account = await EnsureMicrosoft(accountId, token);
            if (await ChangeCape(account["accessToken"]?.GetValue<string>() ?? "", capeId, token)) return;
            var refreshed = await Refresh(account);
            if (!await ChangeCape(refreshed["accessToken"]?.GetValue<string>() ?? "", capeId, token))
                throw new Exception("更换披风失败，请重新登录微软账户");
        }

        private static async Task<JsonObject> EnsureMicrosoft(string accountId, CancellationToken token)
        {
            var account = AccountService.Find(accountId) ?? throw new Exception("账户不存在");
            if (account["type"]?.GetValue<string>() != "microsoft") throw new Exception("只有微软账户可以更换官方披风");
            return account;
        }

        private static async Task<JsonObject> Refresh(JsonObject account)
        {
            var refreshed = await MicrosoftAuthService.Refresh(account);
            AccountService.Update(account["id"]?.GetValue<string>() ?? "", item =>
            {
                item["accessToken"] = refreshed["accessToken"]?.GetValue<string>() ?? item["accessToken"]?.GetValue<string>();
                item["refreshToken"] = refreshed["refreshToken"]?.GetValue<string>() ?? item["refreshToken"]?.GetValue<string>();
            });
            return AccountService.Find(account["id"]?.GetValue<string>() ?? "") ?? refreshed;
        }

        private static async Task<JsonArray?> RequestCapes(string access, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await Http.SendAsync(request, token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return null;
            if (!response.IsSuccessStatusCode) throw new Exception("获取披风失败 HTTP " + (int)response.StatusCode);
            var profile = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonObject ?? new JsonObject();
            var json = profile["capes"] as JsonArray ?? new JsonArray();
            var result = new JsonArray();
            foreach (var item in json.OfType<JsonObject>())
            {
                result.Add(new JsonObject
                {
                    ["id"] = item["id"]?.GetValue<string>() ?? "",
                    ["alias"] = item["alias"]?.GetValue<string>() ?? "",
                    ["state"] = item["state"]?.GetValue<string>() ?? "",
                    ["url"] = item["url"]?.GetValue<string>() ?? ""
                });
            }
            return result;
        }

        private static async Task<bool> ChangeCape(string access, string capeId, CancellationToken token)
        {
            using var request = new HttpRequestMessage(
                string.IsNullOrWhiteSpace(capeId) ? HttpMethod.Delete : HttpMethod.Put,
                "https://api.minecraftservices.com/minecraft/profile/capes/active");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (!string.IsNullOrWhiteSpace(capeId))
                request.Content = new StringContent(new JsonObject { ["capeId"] = capeId }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }

        private static async Task<byte[]> DownloadPremiumSkin(string name, CancellationToken token)
        {
            var lookup = await DownloadService.GetText("https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(name), token);
            var id = (JsonNode.Parse(lookup) as JsonObject)?["id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("找不到正版玩家：" + name);
            var profile = await DownloadService.GetText("https://sessionserver.mojang.com/session/minecraft/profile/" + id, token);
            var json = JsonNode.Parse(profile) as JsonObject ?? throw new Exception("正版皮肤信息解析失败");
            var properties = json["properties"] as JsonArray;
            var property = properties?.OfType<JsonObject>().FirstOrDefault(item => item["name"]?.GetValue<string>() == "textures");
            var value = property?["value"]?.GetValue<string>() ?? throw new Exception("该正版玩家没有设置皮肤");
            var decoded = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))) as JsonObject;
            var url = decoded?["textures"]?["SKIN"]?["url"]?.GetValue<string>()
                ?? decoded?["textures"]?["skin"]?["url"]?.GetValue<string>()
                ?? throw new Exception("该正版玩家没有设置皮肤");
            return await DownloadSkin(url, token);
        }

        private static async Task<byte[]> DownloadSkin(string url, CancellationToken token)
        {
            var target = Path.Combine(Path.GetTempPath(), "aurora-skin-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await DownloadService.Download(url, target, null, 0, token);
                return File.ReadAllBytes(target);
            }
            finally
            {
                try { if (File.Exists(target)) File.Delete(target); } catch { }
            }
        }

        private static byte[] DecodePng(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) throw new Exception("没有选择皮肤文件");
            var text = data;
            var comma = text.IndexOf(',');
            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) text = text[(comma + 1)..];
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                throw new Exception("皮肤必须是 PNG 文件");
            return bytes;
        }
    }
}
