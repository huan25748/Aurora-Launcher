using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class AuthlibService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        public static async Task<JsonObject> Add(string serverRaw, string username, string password)
        {
            var server = Normalize(serverRaw);
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) throw new Exception("请输入用户名与密码");
            var clientToken = Guid.NewGuid().ToString("N");
            var body = new JsonObject
            {
                ["username"] = username,
                ["password"] = password,
                ["clientToken"] = clientToken,
                ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
                ["requestUser"] = false
            };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, server + "/authserver/authenticate") { Content = content };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception($"验证失败 HTTP {(int)response.StatusCode}: {text}");
            var data = JsonNode.Parse(text) as JsonObject ?? throw new Exception("验证响应格式错误");
            var profile = data["selectedProfile"] as JsonObject ?? data["availableProfiles"]?[0] as JsonObject;
            if (profile == null) throw new Exception("该账户没有可用的角色");
            var uuid = profile["id"]?.GetValue<string>()?.Replace("-", "") ?? "";
            var name = profile["name"]?.GetValue<string>() ?? username;
            var profiles = new JsonArray();
            if (data["availableProfiles"] is JsonArray available)
            {
                foreach (var item in available.OfType<JsonObject>())
                {
                    var id = item["id"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    profiles.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["uuid"] = id.Replace("-", ""),
                        ["name"] = item["name"]?.GetValue<string>() ?? ""
                    });
                }
            }
            if (profiles.Count == 0)
                profiles.Add(new JsonObject { ["id"] = profile["id"]?.GetValue<string>() ?? "", ["uuid"] = uuid, ["name"] = name });
            foreach (var item in profiles.OfType<JsonObject>())
            {
                var roleAvatar = await FetchAvatar(server, item["uuid"]?.GetValue<string>() ?? "");
                if (roleAvatar != null) item["avatarUrl"] = roleAvatar;
            }
            var account = new JsonObject
            {
                ["id"] = "authlib-" + Guid.NewGuid().ToString("N")[..12],
                ["type"] = "authlib",
                ["username"] = name,
                ["uuid"] = uuid,
                ["accessToken"] = data["accessToken"]?.GetValue<string>() ?? "",
                ["clientToken"] = data["clientToken"]?.GetValue<string>() ?? clientToken,
                ["refreshToken"] = data["refreshToken"]?.GetValue<string>() ?? "",
                ["selectedProfile"] = profile["id"]?.GetValue<string>() ?? "",
                ["profiles"] = profiles,
                ["authServer"] = server,
                ["authServerName"] = new Uri(server).Host,
                ["loginName"] = username,
                ["password"] = password,
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["lastUsedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            try
            {
                var avatar = await FetchAvatar(server, uuid);
                if (avatar != null)
                {
                    account["avatarUrl"] = avatar;
                    account["avatarMode"] = "head3d";
                    account["avatarVersion"] = 4;
                }
            }
            catch { }
            AccountService.AddAuthlib(account);
            GameEvents.Emit("accounts", AccountService.List());
            return account;
        }

        public static async Task<JsonObject> SwitchProfile(string accountId, string profileId, CancellationToken token = default)
        {
            var account = AccountService.Find(accountId) ?? throw new Exception("账户不存在");
            if (account["type"]?.GetValue<string>() != "authlib") throw new Exception("只有第三方账户可以切换角色");
            var server = account["authServer"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(server)) throw new Exception("账户缺少验证服务器地址");
            var profile = (account["profiles"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(item => item["id"]?.GetValue<string>() == profileId);
            if (profile == null) throw new Exception("角色不存在");
            var accessToken = account["accessToken"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new Exception("登录已过期，请重新登录该账户");
            var clientToken = account["clientToken"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(clientToken)) clientToken = Guid.NewGuid().ToString("N");

            JsonObject? data = null;
            try
            {
                var body = new JsonObject
                {
                    ["accessToken"] = accessToken,
                    ["clientToken"] = clientToken,
                    ["selectedProfile"] = new JsonObject
                    {
                        ["id"] = profile["id"]?.GetValue<string>() ?? "",
                        ["name"] = profile["name"]?.GetValue<string>() ?? ""
                    }
                };
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, server + "/authserver/refresh") { Content = content };
                using var response = await Http.SendAsync(request, token);
                var text = await response.Content.ReadAsStringAsync(token);
                if (response.IsSuccessStatusCode) data = JsonNode.Parse(text) as JsonObject;
            }
            catch { }

            if (data == null)
            {
                // LittleSkin binds the token to a profile, so selectedProfile cannot
                // be changed by refresh. Logging in with the profile name binds the
                // requested profile directly (feature.non_email_login).
                var password = account["password"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(password))
                    throw new Exception("登录已过期，请重新登录该账户后再切换角色");
                var body = new JsonObject
                {
                    ["username"] = profile["name"]?.GetValue<string>() ?? "",
                    ["password"] = password,
                    ["clientToken"] = clientToken,
                    ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
                    ["requestUser"] = false
                };
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, server + "/authserver/authenticate") { Content = content };
                using var response = await Http.SendAsync(request, token);
                var text = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"切换角色失败 HTTP {(int)response.StatusCode}: {text}");
                data = JsonNode.Parse(text) as JsonObject ?? throw new Exception("切换角色响应格式错误");
            }
            var selected = data["selectedProfile"] as JsonObject;
            var uuid = (selected?["id"]?.GetValue<string>() ?? profile["id"]?.GetValue<string>() ?? "").Replace("-", "");
            var name = selected?["name"]?.GetValue<string>() ?? profile["name"]?.GetValue<string>() ?? "";
            var avatar = await FetchAvatar(server, uuid);
            AccountService.Update(accountId, item =>
            {
                item["accessToken"] = data["accessToken"]?.GetValue<string>() ?? item["accessToken"]?.GetValue<string>();
                item["refreshToken"] = data["refreshToken"]?.GetValue<string>() ?? item["refreshToken"]?.GetValue<string>();
                item["selectedProfile"] = profile["id"]?.GetValue<string>() ?? "";
                item["username"] = name;
                item["uuid"] = uuid;
                if (avatar != null)
                {
                    item["avatarUrl"] = avatar;
                    item["avatarMode"] = "head3d";
                    item["avatarVersion"] = 4;
                }
            });
            return (JsonObject)JsonNode.Parse(AccountService.Find(accountId)!.ToJsonString())!;
        }

        public static async Task<string?> RefreshAccountAvatar(string accountId)
        {
            var account = AccountService.Find(accountId);
            if (account == null || account["type"]?.GetValue<string>() != "authlib") return null;
            var avatar = await FetchAvatar(
                account["authServer"]?.GetValue<string>() ?? "",
                account["uuid"]?.GetValue<string>() ?? "");
            if (avatar == null) return null;
            AccountService.Update(accountId, item =>
            {
                item["avatarUrl"] = avatar;
                item["avatarMode"] = "head3d";
                item["avatarVersion"] = 4;
            });
            return avatar;
        }

        public static async Task<JsonArray> Profiles(string accountId)
        {
            var account = AccountService.Find(accountId) ?? throw new Exception("账户不存在");
            var server = account["authServer"]?.GetValue<string>() ?? "";
            var profiles = account["profiles"] as JsonArray ?? new JsonArray();
            var avatars = new Dictionary<string, string>();
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                var avatar = await FetchAvatar(server, profile["uuid"]?.GetValue<string>() ?? "");
                if (avatar != null) avatars[profile["id"]?.GetValue<string>() ?? ""] = avatar;
            }
            if (avatars.Count > 0)
            {
                AccountService.Update(accountId, item =>
                {
                    foreach (var profile in (item["profiles"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    {
                        var id = profile["id"]?.GetValue<string>() ?? "";
                        if (avatars.TryGetValue(id, out var avatar)) profile["avatarUrl"] = avatar;
                    }
                    var selectedId = item["selectedProfile"]?.GetValue<string>() ?? "";
                    if (avatars.TryGetValue(selectedId, out var selectedAvatar))
                    {
                        item["avatarUrl"] = selectedAvatar;
                        item["avatarMode"] = "head3d";
                        item["avatarVersion"] = 4;
                    }
                });
            }
            var latest = AccountService.Find(accountId)?["profiles"] as JsonArray ?? profiles;
            return (JsonArray)JsonNode.Parse(latest.ToJsonString())!;
        }

        private static async Task<string?> FetchAvatar(string server, string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return null;
            try
            {
                var profileText = await DownloadService.GetText(
                    server.TrimEnd('/') + "/sessionserver/session/minecraft/profile/" + uuid);
                var profile = JsonNode.Parse(profileText) as JsonObject;
                var property = (profile?["properties"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(item => item["name"]?.GetValue<string>() == "textures");
                var value = property?["value"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var decoded = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))) as JsonObject;
                    var skinUrl = decoded?["textures"]?["SKIN"]?["url"]?.GetValue<string>()
                        ?? decoded?["textures"]?["skin"]?["url"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(skinUrl))
                    {
                        var temp = Path.Combine(Path.GetTempPath(), "aurora-role-" + Guid.NewGuid().ToString("N") + ".png");
                        try
                        {
                            await DownloadService.Download(skinUrl!, temp);
                            var head = AvatarService.CropHead(File.ReadAllBytes(temp));
                            if (head != null) return "data:image/png;base64," + Convert.ToBase64String(head);
                        }
                        finally
                        {
                            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                        }
                    }
                }
            }
            catch { }
            try
            {
                var origin = new Uri(server).GetLeftPart(UriPartial.Authority);
                using var response = await Http.GetAsync($"{origin}/avatar/{uuid}");
                if (!response.IsSuccessStatusCode) return null;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) return null;
                var type = response.Content.Headers.ContentType?.MediaType ?? "image/png";
                return "data:" + type + ";base64," + Convert.ToBase64String(bytes);
            }
            catch { return null; }
        }

        private static string Normalize(string raw)
        {
            var text = (raw ?? "").Trim().TrimEnd('/');
            if (!text.StartsWith("http://") && !text.StartsWith("https://")) text = "https://" + text;
            if (text.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
                text = text[..^"/authserver".Length].TrimEnd('/');
            // LittleSkin accepts auth calls at the site root, but authlib-injector
            // (and in-game skin loading) needs the Yggdrasil API root.
            if (text.Contains("littleskin.cn", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
                text += "/api/yggdrasil";
            return text;
        }
    }
}
