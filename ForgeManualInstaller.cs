using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class ForgeManualInstaller
    {
        private static string Root => AppPaths.RootDir;
        private static string Libraries => Path.Combine(Root, "libraries");

        public static async Task<string> Install(string installerPath, string mcVersion, string kind, CancellationToken token)
        {
            using var zip = ZipFile.OpenRead(installerPath);
            var profile = ReadJson(zip, "install_profile.json");
            var version = ReadJson(zip, "version.json");
            var versionId = version["id"]?.GetValue<string>()
                ?? profile["version"]?.GetValue<string>()
                ?? $"{mcVersion}-{kind}-manual";
            var versionDir = Path.Combine(Root, "versions", versionId);
            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(versionDir, versionId + ".json"), version.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var clientJar = Path.Combine(Root, "versions", mcVersion, mcVersion + ".jar");
            if (!File.Exists(clientJar)) throw new Exception("缺少原版 client jar：" + clientJar);

            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ROOT"] = Root,
                ["INSTALLER"] = installerPath,
                ["MINECRAFT_JAR"] = clientJar,
                ["MINECRAFT_VERSION"] = mcVersion,
                ["SIDE"] = "client",
                ["LIBRARY_DIR"] = Libraries
            };
            if (profile["data"] is JsonObject profileData)
            {
                foreach (var pair in profileData)
                {
                    if (pair.Value is not JsonObject side) continue;
                    var value = side["client"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value)) data[pair.Key] = value!;
                }
            }

            var mojmaps = Path.Combine(Libraries, "net", "minecraft", "client", mcVersion, $"client-{mcVersion}-mappings.tsrg");
            await EnsureMappings(mcVersion, mojmaps, token);
            data["MOJMAPS"] = mojmaps;

            var clientArtifact = (version["libraries"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(item => (item["name"]?.GetValue<string>() ?? "").Contains(":forge:", StringComparison.Ordinal)
                    && (item["name"]?.GetValue<string>() ?? "").EndsWith(":client", StringComparison.Ordinal));
            var patched = clientArtifact?["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(patched))
                throw new Exception("Forge 版本缺少 client 产物路径");
            var patchedPath = Path.Combine(Libraries, patched!.Replace('/', Path.DirectorySeparatorChar));
            data["PATCHED"] = patchedPath;
            data["MC_OFF"] = Path.Combine(Libraries, "net", "minecraft", "client", mcVersion, $"client-{mcVersion}-official.jar");
            data["MC_UNPACKED"] = clientJar;
            data["MC_UNPACKED_SHA"] = Sha1(clientJar);
            data["PATCHED_SHA"] = "";
            data["MOJMAPS_SHA"] = Sha1(mojmaps);

            var binpatchEntry = zip.GetEntry("data/client.lzma");
            if (binpatchEntry == null) throw new Exception("Forge 安装器缺少 data/client.lzma");
            var binpatch = Path.Combine(Root, "data", "client.lzma");
            Directory.CreateDirectory(Path.GetDirectoryName(binpatch)!);
            binpatchEntry.ExtractToFile(binpatch, true);
            data["BINPATCH"] = binpatch;

            Directory.CreateDirectory(Path.GetDirectoryName(patchedPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(data["MC_OFF"])!);
            Directory.CreateDirectory(Path.GetDirectoryName(mojmaps)!);

            var processors = profile["processors"] as JsonArray;
            if (processors != null)
            {
                foreach (var item in processors)
                {
                    if (item is not JsonObject processor) continue;
                    if (processor["sides"] is JsonArray sides && !sides.Any(side => side?.GetValue<string>() == "client")) continue;
                    var jarName = processor["jar"]?.GetValue<string>() ?? "";
                    if (jarName.Contains("installertools", StringComparison.OrdinalIgnoreCase)) continue; // mappings/archive tasks are done here
                    var jarPath = ResolveMaven(jarName);
                    if (jarPath == null || !File.Exists(jarPath)) continue;
                    var classpath = new List<string> { jarPath };
                    if (processor["classpath"] is JsonArray cp)
                    {
                        foreach (var entry in cp)
                        {
                            var path = ResolveMaven(entry?.GetValue<string>() ?? "");
                            if (path != null && File.Exists(path)) classpath.Add(path);
                        }
                    }
                    var mainClass = ReadMainClass(jarPath);
                    if (string.IsNullOrWhiteSpace(mainClass)) continue;
                    var args = new List<string>();
                    if (processor["args"] is JsonArray rawArgs)
                    {
                        foreach (var raw in rawArgs)
                        {
                            var text = raw?.GetValue<string>() ?? "";
                            foreach (var pair in data) text = text.Replace("{" + pair.Key + "}", pair.Value);
                            args.Add(text);
                        }
                    }
                    var task = args.FirstOrDefault(a => a == "ForgeAutoRenamingTool" || a.Contains("fart", StringComparison.OrdinalIgnoreCase));
                    ProgressService.Detail(Path.GetFileName(jarPath));
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = SelectJava(),
                        WorkingDirectory = Root,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    };
                    startInfo.ArgumentList.Add("-cp");
                    startInfo.ArgumentList.Add(string.Join(Path.PathSeparator, classpath));
                    startInfo.ArgumentList.Add(mainClass);
                    foreach (var arg in args) startInfo.ArgumentList.Add(arg);
                    using var process = Process.Start(startInfo) ?? throw new Exception("无法启动 Forge processor");
                    await process.WaitForExitAsync(token);
                    if (process.ExitCode != 0) throw new Exception($"Forge processor 失败：{Path.GetFileName(jarPath)} 退出码 {process.ExitCode}");
                }
            }

            if (!File.Exists(patchedPath)) throw new Exception("Forge processor 没有生成 client 产物");
            return versionId;
        }

        private static async Task EnsureMappings(string mcVersion, string destination, CancellationToken token)
        {
            if (File.Exists(destination)) return;
            var manifest = await VersionService.Manifest(false);
            var summary = (manifest["versions"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(item => item["id"]?.GetValue<string>() == mcVersion);
            var url = summary?["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url)) throw new Exception("找不到版本 JSON：" + mcVersion);
            var version = JsonNode.Parse(await DownloadService.GetTextOfficial(url!, token)) as JsonObject;
            var mappingsUrl = version?["downloads"]?["client_mappings"]?["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(mappingsUrl)) throw new Exception("版本没有 client mappings");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await DownloadService.Download(mappingsUrl!, destination, null, 0, token);
        }

        private static JsonObject ReadJson(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name) ?? throw new Exception("安装器缺少 " + name);
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new Exception(name + " 格式不正确");
        }

        private static string? ResolveMaven(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var at = name.IndexOf('@');
            var ext = "jar";
            if (at >= 0) { ext = name.Substring(at + 1); name = name.Substring(0, at); }
            var parts = name.Split(':');
            if (parts.Length < 3) return null;
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : "";
            return Path.Combine(Libraries, group, artifact, version, $"{artifact}-{version}{classifier}.{ext}");
        }

        private static string ReadMainClass(string jarPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                var entry = zip.GetEntry("META-INF/MANIFEST.MF");
                if (entry == null) return "";
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                foreach (var line in reader.ReadToEnd().Split('\n'))
                    if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(11).Trim();
            }
            catch { }
            return "";
        }

        private static string SelectJava()
        {
            return JavaService.Scan().OfType<JsonObject>()
                .Select(item => new { Path = item["path"]?.GetValue<string>() ?? "", Major = item["major"]?.GetValue<int>() ?? 0 })
                .Where(item => item.Path.Length > 0 && File.Exists(item.Path))
                .OrderByDescending(item => item.Major)
                .Select(item => item.Path)
                .FirstOrDefault() ?? throw new Exception("未找到可用的 Java");
        }

        private static string Sha1(string file)
        {
            using var stream = File.OpenRead(file);
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
    }
}
