using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
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
            var account = new JsonObject
            {
                ["id"] = "authlib-" + Guid.NewGuid().ToString("N")[..12],
                ["type"] = "authlib",
                ["username"] = name,
                ["uuid"] = uuid,
                ["accessToken"] = data["accessToken"]?.GetValue<string>() ?? "",
                ["authServer"] = server,
                ["authServerName"] = new Uri(server).Host,
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["lastUsedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            AccountService.AddAuthlib(account);
            GameEvents.Emit("accounts", AccountService.List());
            return account;
        }

        private static string Normalize(string raw)
        {
            var text = (raw ?? "").Trim().TrimEnd('/');
            if (!text.StartsWith("http://") && !text.StartsWith("https://")) text = "https://" + text;
            return text;
        }
    }
}
