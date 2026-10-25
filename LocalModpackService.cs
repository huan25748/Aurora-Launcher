using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class LocalModpackService
    {
        public static async Task<JsonObject> Import(string zipPath, string? instanceName)
        {
            ProgressService.Begin("导入 Modrinth 整合包");
            var temp = Path.Combine(AppPaths.SettingsDir, "cache", "packs", "import-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temp);
            try
            {
                ProgressService.Step("解压整合包", 1, Path.GetFileName(zipPath));
                ZipFile.ExtractToDirectory(zipPath, temp, true);
                var indexPath = Path.Combine(temp, "modrinth.index.json");
                if (!File.Exists(indexPath)) throw new Exception("无效的整合包：缺少 modrinth.index.json");
                var index = JsonNode.Parse(File.ReadAllText(indexPath)) as JsonObject ?? throw new Exception("整合包索引格式错误");
                var mcVersion = index["dependencies"]?["minecraft"]?.GetValue<string>() ?? throw new Exception("整合包没有声明 Minecraft 版本");
                var fabricLoader = index["dependencies"]?["fabric-loader"]?.GetValue<string>();
                var quiltLoader = index["dependencies"]?["quilt-loader"]?.GetValue<string>();
                var forge = index["dependencies"]?["forge"]?.GetValue<string>();
                var neoforge = index["dependencies"]?["neoforge"]?.GetValue<string>();
                ProgressService.Update(1, "整合包已解压");

                var name = string.IsNullOrWhiteSpace(instanceName)
                    ? index["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(zipPath)
                    : instanceName!;
                ProgressService.Step("安装游戏版本", 1, mcVersion);
                string instanceId;
                if (!string.IsNullOrWhiteSpace(fabricLoader) || !string.IsNullOrWhiteSpace(quiltLoader))
                {
                    var kind = string.IsNullOrWhiteSpace(fabricLoader) ? "quilt" : "fabric";
                    var loaderVersion = kind == "fabric" ? fabricLoader : quiltLoader;
                    var result = await LoaderService.Install(kind, mcVersion, loaderVersion, name, true);
                    instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
                }
                else if (!string.IsNullOrWhiteSpace(forge) || !string.IsNullOrWhiteSpace(neoforge))
                {
                    var kind = string.IsNullOrWhiteSpace(forge) ? "neoforge" : "forge";
                    var loaderVersion = kind == "forge" ? forge : neoforge;
                    var result = await ForgeService.Install(kind, mcVersion, loaderVersion, name, true);
                    instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
                }
                else
                {
                    var result = await MinecraftInstallService.InstallVanilla(mcVersion, true, name);
                    instanceId = result["instanceId"]?.GetValue<string>() ?? throw new Exception("创建整合包实例失败");
                }
                ProgressService.Update(1, "游戏版本已安装");

                var instance = BridgeService.GetInstance(instanceId) as JsonObject ?? throw new Exception("实例不存在");
                var gameDir = instance["gameDir"]?.GetValue<string>() ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
                if (index["files"] is JsonArray files)
                {
                    var downloads = files.OfType<JsonObject>().Where(file => file["env"]?["client"]?.GetValue<string>() != "unsupported").ToList();
                    ProgressService.Step("下载整合包文件", downloads.Count, downloads.Count + " 个文件");
                    var done = 0;
                    using var gate = new System.Threading.SemaphoreSlim(8);
                    await Task.WhenAll(downloads.Select(async file =>
                    {
                        var path = file["path"]?.GetValue<string>() ?? "";
                        var url = (file["downloads"] as JsonArray)?.OfType<JsonNode>().Select(item => item.GetValue<string>()).FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url)) return;
                        await gate.WaitAsync();
                        try
                        {
                            var target = Path.GetFullPath(Path.Combine(gameDir, path));
                            if (!target.StartsWith(Path.GetFullPath(gameDir), StringComparison.OrdinalIgnoreCase)) return;
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            var sha1 = file["hashes"]?["sha1"]?.GetValue<string>();
                            var size = file["fileSize"]?.GetValue<long>() ?? 0;
                            await DownloadService.Download(url!, target, sha1, size);
                        }
                        catch { }
                        finally
                        {
                            gate.Release();
                            ProgressService.Progress(System.Threading.Interlocked.Increment(ref done), done + " / " + downloads.Count);
                        }
                    }));
                    ProgressService.Update(downloads.Count, "整合包文件已下载");
                }

                var overrides = Path.Combine(temp, "overrides");
                if (Directory.Exists(overrides))
                {
                    ProgressService.Step("应用整合包覆盖文件", 1, "overrides");
                    foreach (var source in Directory.EnumerateFiles(overrides, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(overrides, source);
                        var target = Path.Combine(gameDir, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target, true);
                    }
                    ProgressService.Update(1, "覆盖文件已应用");
                }
                ProgressService.Complete();
                return instance;
            }
            catch (Exception ex)
            {
                ProgressService.Fail(ex.Message);
                throw;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
