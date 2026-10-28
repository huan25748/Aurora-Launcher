using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class MicrosoftAuthService
    {
        private sealed class Flow
        {
            public string DeviceCode = "";
            public string ClientId = "";
            public int Interval = 5;
            public long ExpiresAt;
        }

        private static readonly Dictionary<string, Flow> Flows = new();
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private static string ClientId()
        {
            var settings = BridgeService.GetSettings();
            var id = settings["msClientId"]?.GetValue<string>()?.Trim();
            return string.IsNullOrEmpty(id) ? "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb" : id!;
        }

        public static async Task<JsonObject> Start()
        {
            var clientId = ClientId();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "XboxLive.signin offline_access"
            });
            using var response = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", content);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception("微软设备代码请求失败：" + text);
            var json = JsonNode.Parse(text) as JsonObject ?? throw new Exception("微软登录响应格式错误");
            var id = "ms-" + Guid.NewGuid().ToString("N")[..12];
            var interval = json["interval"]?.GetValue<int>() ?? 5;
            var expires = json["expires_in"]?.GetValue<int>() ?? 900;
            Flows[id] = new Flow
            {
                DeviceCode = json["device_code"]?.GetValue<string>() ?? throw new Exception("缺少 device_code"),
                ClientId = clientId,
                Interval = Math.Max(2, interval),
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expires * 1000L
            };
            return new JsonObject
            {
                ["flowId"] = id,
                ["userCode"] = json["user_code"]?.GetValue<string>() ?? "",
                ["verificationUri"] = json["verification_uri"]?.GetValue<string>() ?? "",
                ["expiresIn"] = expires,
                ["interval"] = interval,
                ["message"] = json["message"]?.GetValue<string>() ?? ""
            };
        }

        public static void Cancel(string flowId)
        {
            lock (Flows) Flows.Remove(flowId);
        }

        public static async Task<JsonObject> Poll(string flowId)
        {
            Flow? flow;
            lock (Flows) Flows.TryGetValue(flowId, out flow);
            if (flow == null) return new JsonObject { ["status"] = "error", ["message"] = "登录会话已失效，请重新开始" };
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > flow.ExpiresAt)
            {
                Cancel(flowId);
                return new JsonObject { ["status"] = "expired", ["message"] = "设备代码已过期，请重新开始登录" };
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = flow.ClientId,
                ["device_code"] = flow.DeviceCode
            });
            using var response = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", content);
            var text = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            if (!response.IsSuccessStatusCode)
            {
                var error = json["error"]?.GetValue<string>() ?? "";
                if (error == "authorization_pending") return new JsonObject { ["status"] = "pending" };
                if (error == "slow_down") return new JsonObject { ["status"] = "pending" };
                if (error == "expired_token") { Cancel(flowId); return new JsonObject { ["status"] = "expired", ["message"] = "设备代码已过期" }; }
                if (error == "authorization_declined") { Cancel(flowId); return new JsonObject { ["status"] = "error", ["message"] = "登录已被取消" }; }
                return new JsonObject { ["status"] = "error", ["message"] = json["error_description"]?.GetValue<string>() ?? error };
            }
            Cancel(flowId);
            var accessToken = json["access_token"]?.GetValue<string>() ?? throw new Exception("缺少微软 access_token");
            var refreshToken = json["refresh_token"]?.GetValue<string>() ?? "";
            return await LoginMinecraft(accessToken, refreshToken);
        }

        public static async Task<JsonObject> Refresh(JsonObject account)
        {
            var refreshToken = account["refreshToken"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(refreshToken)) throw new Exception("账户没有 refresh_token，请重新登录");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId(),
                ["refresh_token"] = refreshToken
            });
            using var response = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", content);
            var text = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            if (!response.IsSuccessStatusCode) throw new Exception(json["error_description"]?.GetValue<string>() ?? "微软令牌刷新失败");
            return await LoginMinecraft(json["access_token"]?.GetValue<string>() ?? throw new Exception("缺少 access_token"), json["refresh_token"]?.GetValue<string>() ?? refreshToken);
        }

        private static async Task<JsonObject> LoginMinecraft(string microsoftToken, string refreshToken)
        {
            var xbox = await PostJson("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["AuthMethod"] = "RPS",
                    ["SiteName"] = "user.auth.xboxlive.com",
                    ["RpsTicket"] = "d=" + microsoftToken
                },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT"
            });
            var xboxToken = xbox["Token"]?.GetValue<string>() ?? throw new Exception("Xbox 认证失败");
            var uhs = xbox["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>() ?? throw new Exception("Xbox 认证缺少 uhs");

            var xsts = await PostJson("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
            {
                ["Properties"] = new JsonObject { ["SandboxId"] = "RETAIL", ["UserTokens"] = new JsonArray(xboxToken) },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT"
            });
            var xstsToken = xsts["Token"]?.GetValue<string>() ?? throw new Exception("XSTS 认证失败");

            var minecraft = await PostJson("https://api.minecraftservices.com/authentication/login_with_xbox", new JsonObject
            {
                ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}"
            });
            var mcToken = minecraft["access_token"]?.GetValue<string>() ?? throw new Exception("Minecraft 登录失败");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception("无法获取 Minecraft 档案，可能没有购买游戏");
            var profile = JsonNode.Parse(text) as JsonObject ?? throw new Exception("Minecraft 档案格式错误");
            var uuid = profile["id"]?.GetValue<string>() ?? throw new Exception("Minecraft 档案缺少 UUID");
            var name = profile["name"]?.GetValue<string>() ?? "Player";

            var account = new JsonObject
            {
                ["id"] = "ms-" + Guid.NewGuid().ToString("N")[..12],
                ["type"] = "microsoft",
                ["username"] = name,
                ["uuid"] = uuid,
                ["accessToken"] = mcToken,
                ["refreshToken"] = refreshToken,
                ["avatarUrl"] = "https://mc-heads.net/avatar/" + uuid + "/64",
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["lastUsedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var avatar = await AvatarService.Fetch(uuid);
            if (avatar != null)
            {
                account["avatarUrl"] = avatar;
                account["avatarMode"] = "head3d";
                account["avatarVersion"] = 4;
            }
            AccountService.AddMicrosoft(account);
            // AddMicrosoft attaches the object to the account store; returning the
            // same JsonNode in another JsonObject throws "The node already has a parent".
            return new JsonObject
            {
                ["status"] = "success",
                ["account"] = JsonNode.Parse(account.ToJsonString())
            };
        }

        private static async Task<JsonObject> PostJson(string url, JsonObject body)
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception($"认证失败 HTTP {(int)response.StatusCode}: {text}");
            return JsonNode.Parse(text) as JsonObject ?? throw new Exception("认证响应格式错误");
        }
    }
}
