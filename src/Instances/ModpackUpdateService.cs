using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class ModpackUpdateService
    {
        /// <summary>
        /// 用指定项目/版本更新一个已存在的实例。安装新包时先落到临时实例，
        /// 再合并到目标实例，保证所有下载/校验逻辑复用现有安装器。
        /// </summary>
        public static async Task<JsonObject> Update(string instanceId, string source, string projectId, string versionId)
        {
            var instance = BridgeService.GetInstance(instanceId) as JsonObject
                ?? throw new Exception("实例不存在");
            var gameDir = instance["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            if (!Directory.Exists(gameDir)) throw new Exception("实例目录不存在：" + gameDir);

            var oldMeta = PackMetaService.Load(instanceId);
            var oldPackMods = (oldMeta?["packMods"] as JsonArray)?.OfType<JsonNode>()
                .Select(item => item.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ProgressService.Begin("更新整合包");
            var tempName = "更新临时-" + Guid.NewGuid().ToString("N")[..6];
            string? tempId = null;
            string? backupDir = null;
            try
            {
                ProgressService.Step("安装新版本", 1, tempName);
                JsonObject? installResult = null;
                if (source == "modrinth")
                {
                    installResult = await ModrinthService.InstallPack(projectId, tempName, versionId, true, Path.Combine(gameDir, "mods"));
                    tempId = installResult["instanceId"]?.GetValue<string>();
                }
                else
                {
                    var input = JsonSerializer.SerializeToElement(new
                    {
                        modId = int.TryParse(projectId, out var modId) ? modId : 0,
                        fileId = int.TryParse(versionId, out var fileId) ? fileId : 0,
                        instanceName = tempName
                    });
                    installResult = await CurseForgeService.InstallPack(input, true, Path.Combine(gameDir, "mods"));
                    tempId = installResult["instanceId"]?.GetValue<string>();
                }
                if (string.IsNullOrWhiteSpace(tempId)) throw new Exception("新版本安装失败");
                var temp = BridgeService.GetInstance(tempId!) as JsonObject ?? throw new Exception("临时实例不存在");
                var tempGameDir = temp["gameDir"]?.GetValue<string>()
                    ?? Path.Combine(AppPaths.RootDir, "versions", temp["versionId"]?.GetValue<string>() ?? "");

                // 整合包新版本可能更换了 Minecraft / 加载器版本：把临时实例的
                // 自包含版本（json + jar）复制到目标实例名下，并更新实例指向。
                var targetName = instance["name"]?.GetValue<string>() ?? "";
                var targetVersion = SafeName(targetName);
                var tempVersionId = temp["versionId"]?.GetValue<string>() ?? "";
                var tempVersionDir = Path.Combine(AppPaths.RootDir, "versions", tempVersionId);
                var targetVersionDir = Path.Combine(AppPaths.RootDir, "versions", targetVersion);
                if (Directory.Exists(tempVersionDir) && !string.IsNullOrWhiteSpace(targetVersion))
                {
                    Directory.CreateDirectory(targetVersionDir);
                    foreach (var versionFile in Directory.GetFiles(tempVersionDir))
                    {
                        var ext = Path.GetExtension(versionFile);
                        var dest = Path.Combine(targetVersionDir, targetVersion + ext);
                        File.Copy(versionFile, dest, true);
                    }
                }
                try
                {
                    var instanceFile = Path.Combine(AppPaths.InstancesDir, instanceId, "instance.json");
                    var json = JsonNode.Parse(File.ReadAllText(instanceFile)) as JsonObject;
                    if (json != null)
                    {
                        json["versionId"] = targetVersion;
                        if (temp["mcVersion"] != null) json["mcVersion"] = temp["mcVersion"]!.DeepCloneCompat();
                        if (temp["loader"] != null) json["loader"] = temp["loader"]!.DeepCloneCompat();
                        if (temp["loaderVersion"] != null) json["loaderVersion"] = temp["loaderVersion"]!.DeepCloneCompat();
                        File.WriteAllText(instanceFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch { }

                ProgressService.Step("备份用户数据", 1, "saves / config / 设置");
                backupDir = Backup(instanceId, gameDir);

                var tempMods = Directory.Exists(Path.Combine(tempGameDir, "mods"))
                    ? Directory.GetFiles(Path.Combine(tempGameDir, "mods"), "*", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName).Where(name => name != null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var targetModsDir = Path.Combine(gameDir, "mods");
                Directory.CreateDirectory(targetModsDir);

                var failedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (installResult?["missing"] is JsonArray missingArray)
                {
                    foreach (var item in missingArray)
                    {
                        var name = item is JsonObject obj ? obj["fileName"]?.GetValue<string>() : item?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(name)) failedNames.Add(Path.GetFileName(name!));
                    }
                }
                var newPackSet = new HashSet<string>(tempMods, StringComparer.OrdinalIgnoreCase);
                if (installResult?["packFiles"] is JsonArray returnedFiles)
                    foreach (var item in returnedFiles)
                        if (item != null)
                        {
                            var name = Path.GetFileName(item.GetValue<string>());
                            if (!string.IsNullOrWhiteSpace(name)) newPackSet.Add(name);
                        }
                var removed = 0;
                {
                    // 首次从 PCL/旧版接管时没有旧清单：按文件名主体去重，
                    // 避免同一模组的旧版本和新版本同时留在 mods 里。
                    var newKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in newPackSet)
                    {
                        var key = ModKey(name);
                        if (key.Length >= 5) newKeys.Add(key);
                    }
                    foreach (var file in Directory.GetFiles(targetModsDir, "*.jar", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(file);
                        if (newPackSet.Contains(name) || failedNames.Contains(name)) continue;
                        if (!newKeys.Contains(ModKey(name))) continue;
                        try { File.Delete(file); removed++; } catch { }
                    }
                }
                foreach (var mod in oldPackMods)
                {
                    if (newPackSet.Contains(mod)) continue;
                    if (failedNames.Contains(mod)) continue;
                    var path = Path.Combine(targetModsDir, mod);
                    if (!File.Exists(path)) continue;
                    try { File.Delete(path); removed++; } catch { }
                }

                ProgressService.Step("合并模组与配置", 1, "保留用户文件");
                var added = 0;
                var updated = 0;
                foreach (var file in Directory.GetFiles(tempGameDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(tempGameDir, file);
                    var top = relative.Split(Path.DirectorySeparatorChar)[0];
                    if (top is "versions" or "libraries" or "assets" or "logs" or "crash-reports" or "saves") continue;
                    if (PackMetaService.IsProtected(relative)) continue;
                    var target = Path.Combine(gameDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var exists = File.Exists(target);
                    File.Copy(file, target, true);
                    if (exists) updated++; else added++;
                }

                var userMods = Directory.GetFiles(targetModsDir, "*.jar")
                    .Select(Path.GetFileName).Where(name => name != null && !tempMods.Contains(name!)).Count();
                PackMetaService.Save(instanceId, source, projectId, versionId, newPackSet);
                ProgressService.UpdateAt(ProgressService.Step("更新完成", 1, ""), 1, "完成");
                ProgressService.Complete();
                return new JsonObject
                {
                    ["added"] = added,
                    ["updated"] = updated,
                    ["removed"] = removed,
                    ["userMods"] = userMods,
                    ["failed"] = failedNames.Count,
                    ["backup"] = backupDir ?? ""
                };
            }
            catch
            {
                ProgressService.Fail("更新失败，已保留备份");
                throw;
            }
            finally
            {
                if (tempId != null)
                {
                    try { InstallCleanup.Remove(tempId); } catch { }
                    try { var dir = Path.Combine(AppPaths.InstancesDir, tempId); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        try
                        {
                            var vdir = Path.Combine(AppPaths.RootDir, "versions", tempName);
                            if (Directory.Exists(vdir)) Directory.Delete(vdir, true);
                            break;
                        }
                        catch { if (attempt < 3) System.Threading.Thread.Sleep(300); }
                    }
                    try { foreach (var ext in new[] { ".json", ".jar" }) { var file = Path.Combine(AppPaths.RootDir, "versions", tempName + ext); if (File.Exists(file)) File.Delete(file); } } catch { }
                    try { foreach (var file in Directory.GetFiles(Path.Combine(AppPaths.RootDir, "versions"), tempName + "*", SearchOption.AllDirectories)) File.Delete(file); } catch { }
                }
                try { GameEvents.Emit("instances", BridgeService.ListInstances()); } catch { }
            }
        }

        private static string ModKey(string name)
        {
            var text = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^\[[^\]]*\]", "");
            // 去掉数字、标点和加载器标记，只留模组名主体。
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[^a-z]+", "");
            return text;
        }

        private static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = string.Concat((name ?? "").Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            return result.Length == 0 ? "instance" : result.Length > 48 ? result[..48].TrimEnd() : result;
        }

        private static string Backup(string instanceId, string gameDir)
        {
            var root = Path.Combine(AppPaths.SettingsDir, "backups", instanceId + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            void Copy(string relative)
            {
                var source = Path.Combine(gameDir, relative);
                if (!File.Exists(source)) return;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(source, target, true); } catch { }
            }
            void CopyDir(string relative)
            {
                var source = Path.Combine(gameDir, relative);
                if (!Directory.Exists(source)) return;
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(gameDir, file);
                    var target = Path.Combine(root, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try { File.Copy(file, target, true); } catch { }
                }
            }
            foreach (var file in new[] { "options.txt", "optionsof.txt", "optionsshaders.txt" }) Copy(file);
            foreach (var dir in new[] { "saves", "config", "local", "xaero", "XaeroWaypoints", "journeymap" }) CopyDir(dir);
            return root;
        }
    }
}
