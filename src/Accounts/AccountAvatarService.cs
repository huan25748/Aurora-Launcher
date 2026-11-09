using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class AccountAvatarService
    {
        private static bool _refreshed;

        public static async Task RefreshAll()
        {
            if (_refreshed) return;
            _refreshed = true;
            try
            {
                foreach (var account in AccountService.List().OfType<JsonObject>())
                {
                    var current = account["avatarUrl"]?.GetValue<string>() ?? "";
                    var type = account["type"]?.GetValue<string>() ?? "";
                    if (type != "authlib"
                        && current.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        && (account["avatarVersion"]?.GetValue<int>() ?? 0) == 4) continue;
                    string? avatar = null;
                    if (type == "microsoft")
                    {
                        avatar = await AvatarService.Fetch(account["uuid"]?.GetValue<string>() ?? "");
                    }
                    else if (type == "offline")
                    {
                        var skin = AccountSkinService.SkinPath(account["id"]?.GetValue<string>() ?? "");
                        if (File.Exists(skin))
                        {
                            var head = AvatarService.CropHead(File.ReadAllBytes(skin));
                            if (head != null) avatar = "data:image/png;base64," + Convert.ToBase64String(head);
                        }
                        if (avatar == null)
                        {
                            var name = account["skinName"]?.GetValue<string>() ?? "";
                            avatar = await AvatarService.Fetch(string.IsNullOrWhiteSpace(name) ? account["username"]?.GetValue<string>() ?? "" : name);
                        }
                        if (avatar == null)
                        {
                            var slim = account["skinVariant"]?.GetValue<string>() == "slim";
                            avatar = await AvatarService.Fetch(slim
                                ? "ec561538-f3fd-461d-aff5-086b22154bce"
                                : "8667ba71-b85a-4004-af54-457a9734eed7");
                        }
                    }
                    else if (type == "authlib")
                    {
                        avatar = await AuthlibService.RefreshAccountAvatar(account["id"]?.GetValue<string>() ?? "");
                    }
                    else continue;
                    if (avatar == null) continue;
                    AccountService.Update(account["id"]?.GetValue<string>() ?? "", item =>
                    {
                        item["avatarUrl"] = avatar;
                        item["avatarMode"] = "head3d";
                        item["avatarVersion"] = 4;
                    });
                }
            }
            catch { }
        }
    }
}
