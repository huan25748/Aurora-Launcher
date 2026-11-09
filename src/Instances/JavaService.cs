using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    internal static class JavaService
    {
        public static JsonArray Scan()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new JsonArray();

            void Add(string? home, string source)
            {
                if (string.IsNullOrWhiteSpace(home)) return;
                try
                {
                    var exe = Path.Combine(home, "bin", "java.exe");
                    if (!File.Exists(exe)) return;
                    if (source == "managed")
                    {
                        // A half-downloaded Mojang runtime must never be selected.
                        if (!File.Exists(Path.Combine(home, "lib", "rt.jar")) &&
                            !File.Exists(Path.Combine(home, "lib", "modules"))) return;
                    }
                    if (!seen.Add(Path.GetFullPath(exe))) return;
                    result.Add(Describe(exe, source));
                }
                catch { }
            }

            var runtime = Path.Combine(AppPaths.RootDir, "runtime");
            if (Directory.Exists(runtime))
            {
                foreach (var directory in SafeDirectories(runtime))
                    Add(directory, "managed");
            }

            Add(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME");

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var segment in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(segment)) continue;
                var exe = Path.Combine(segment, "java.exe");
                if (File.Exists(exe)) Add(Path.GetDirectoryName(segment), "PATH");
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var roots = new (string Path, int Depth)[]
            {
                (Path.Combine(programFiles, "Java"), 1),
                (Path.Combine(programFiles, "Eclipse Adoptium"), 1),
                (Path.Combine(programFiles, "Eclipse Foundation"), 1),
                (Path.Combine(programFiles, "Microsoft"), 1),
                (Path.Combine(programFiles, "Zulu"), 1),
                (Path.Combine(programFiles, "BellSoft"), 1),
                (Path.Combine(programFiles, "Amazon Corretto"), 1),
                (Path.Combine(programFiles, "Semeru"), 1),
                (Path.Combine(programFiles, "JetBrains"), 2),
                (Path.Combine(programFilesX86, "Java"), 1),
                (Path.Combine(localAppData, "Programs", "Eclipse Adoptium"), 1),
                (Path.Combine(home, ".jdks"), 1),
                (Path.Combine(home, "scoop", "apps"), 2),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".hmcl", "java"), 3)
            };

            foreach (var root in roots)
                AddFromRoot(root.Path, root.Depth, Add);

            var output = new JsonArray();
            foreach (var item in result.OfType<JsonObject>()
                .OrderByDescending(item => item["major"]?.GetValue<int>() ?? 0)
                .ThenBy(item => item["path"]?.GetValue<string>() ?? ""))
            {
                output.Add(JsonNode.Parse(item.ToJsonString()));
            }
            return output;
        }

        private static void AddFromRoot(string root, int depth, Action<string?, string> add)
        {
            if (!Directory.Exists(root)) return;
            AddFromDirectory(root, depth, add);
        }

        private static void AddFromDirectory(string directory, int depth, Action<string?, string> add)
        {
            var exe = Path.Combine(directory, "bin", "java.exe");
            if (File.Exists(exe))
            {
                add(directory, "scan");
                return;
            }
            if (depth <= 0) return;
            foreach (var child in SafeDirectories(directory))
                AddFromDirectory(child, depth - 1, add);
        }

        private static IEnumerable<string> SafeDirectories(string directory)
        {
            try { return Directory.GetDirectories(directory); }
            catch { return Array.Empty<string>(); }
        }

        private static JsonObject Describe(string exe, string source)
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(exe)) ?? "";
            var release = ParseRelease(home);
            var folder = Path.GetFileName(home).ToLowerInvariant();
            var componentMajor = new Dictionary<string, int>
            {
                ["jre-legacy"] = 8,
                ["java-runtime-alpha"] = 16,
                ["java-runtime-beta"] = 17,
                ["java-runtime-gamma"] = 17,
                ["java-runtime-gamma-snapshot"] = 17,
                ["java-runtime-delta"] = 21,
                ["java-runtime-epsilon"] = 25
            };
            var version = release.Version ?? "";
            var major = MajorOf(version);
            if (major == 0 && componentMajor.TryGetValue(folder, out var component)) major = component;
            var archText = (release.Arch ?? "").ToLowerInvariant();
            var arch = archText.Contains("64") || archText.Contains("amd64") || archText.Contains("x86_64")
                ? "x64"
                : archText.Contains("arm64") || archText.Contains("aarch64")
                    ? "arm64"
                    : archText.Contains("86") || archText.Contains("i386")
                        ? "x86"
                        : "unknown";
            return new JsonObject
            {
                ["path"] = exe,
                ["version"] = string.IsNullOrEmpty(version) ? (major > 0 ? major.ToString() : "未知") : version,
                ["major"] = major,
                ["build"] = BuildOf(version),
                ["arch"] = arch,
                ["vendor"] = InferVendor(home, release.Vendor),
                ["is64Bit"] = arch == "x64" || arch == "arm64" || Environment.Is64BitOperatingSystem,
                ["source"] = source,
                ["managed"] = source == "managed"
            };
        }

        private static (string? Version, string? Arch, string? Vendor) ParseRelease(string home)
        {
            var file = Path.Combine(home, "release");
            if (!File.Exists(file)) return (null, null, null);
            try
            {
                string? version = null, arch = null, vendor = null;
                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.StartsWith("JAVA_VERSION=")) version = line.Substring(13).Trim().Trim('"');
                    else if (line.StartsWith("OS_ARCH=")) arch = line.Substring(8).Trim().Trim('"');
                    else if (line.StartsWith("IMPLEMENTOR=")) vendor = line.Substring(12).Trim().Trim('"');
                    else if (vendor == null && line.StartsWith("JAVA_VENDOR=")) vendor = line.Substring(12).Trim().Trim('"');
                }
                return (version, arch, vendor);
            }
            catch { return (null, null, null); }
        }

        private static int BuildOf(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            var text = version.Trim();
            var underscore = text.LastIndexOf('_');
            if (underscore >= 0 && int.TryParse(text[(underscore + 1)..], out var build)) return build;
            var parts = text.Split('.', '-', '_');
            return parts.Length >= 3 && int.TryParse(parts[2], out var third) ? third : 0;
        }

        private static int MajorOf(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            var text = version.Trim();
            var parts = text.Split('.', '-', '_');
            if (parts.Length == 0) return 0;
            if (!int.TryParse(parts[0], out var first)) return 0;
            if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
            return first;
        }

        private static string InferVendor(string home, string? releaseVendor)
        {
            if (!string.IsNullOrWhiteSpace(releaseVendor)) return releaseVendor!;
            var p = home.ToLowerInvariant();
            if (p.Contains("temurin") || p.Contains("adoptium")) return "Eclipse Temurin";
            if (p.Contains("corretto") || p.Contains("amazon")) return "Amazon Corretto";
            if (p.Contains("zulu") || p.Contains("azul")) return "Azul Zulu";
            if (p.Contains("microsoft")) return "Microsoft OpenJDK";
            if (p.Contains("bellsoft") || p.Contains("liberica")) return "BellSoft Liberica";
            if (p.Contains("jbr") || p.Contains("jetbrains")) return "JetBrains Runtime";
            if (p.Contains("semeru") || p.Contains("ibm")) return "IBM Semeru";
            return "Java";
        }
    }
}

namespace AuroraLauncher
{
    internal static class JavaRuntimeService
    {
        private const string AllUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
        private static string CacheFile => Path.Combine(AppPaths.SettingsDir, "cache", "java_runtime_all.json");
        private static string RuntimeDir => Path.Combine(AppPaths.RootDir, "runtime");

        public static async Task<System.Text.Json.Nodes.JsonObject> Download(int major)
        {
            ProgressService.Begin($"下载 Java {major}");
            try
            {
                var all = await GetAll();
                var platformName = Environment.Is64BitOperatingSystem ? "windows-x64" : "windows-x86";
                var platform = all[platformName] as System.Text.Json.Nodes.JsonObject
                    ?? throw new Exception("Mojang 没有提供当前平台的 Java 运行时");
                var preferred = new Dictionary<int, string>
                {
                    [8] = "jre-legacy",
                    [16] = "java-runtime-alpha",
                    [17] = "java-runtime-gamma",
                    [21] = "java-runtime-delta",
                    [25] = "java-runtime-epsilon"
                };
                System.Text.Json.Nodes.JsonObject? chosen = null;
                string? component = null;
                var wanted = preferred.TryGetValue(major, out var preferredName) ? preferredName : "";
                if (!string.IsNullOrEmpty(wanted) && platform[wanted] is System.Text.Json.Nodes.JsonArray preferredList && preferredList.Count > 0)
                {
                    component = wanted;
                    chosen = preferredList[0] as System.Text.Json.Nodes.JsonObject;
                }
                if (chosen == null)
                {
                    foreach (var pair in platform)
                    {
                        if (pair.Value is not System.Text.Json.Nodes.JsonArray list || list.Count == 0) continue;
                        var entry = list[0] as System.Text.Json.Nodes.JsonObject;
                        var version = entry?["version"]?["name"]?.GetValue<string>() ?? "";
                        if (MajorOf(version) == major) { component = pair.Key; chosen = entry; break; }
                    }
                }
                if (chosen == null) throw new Exception($"未找到适用于 Java {major} 的运行时");
                var versionName = chosen["version"]?["name"]?.GetValue<string>() ?? major.ToString();
                var manifestUrl = chosen["manifest"]?["url"]?.GetValue<string>() ?? throw new Exception("运行时清单地址为空");
                // BMCLAPI mirrors Java runtime package manifests even though normal
                // version JSON packages stay on the official host.
                if (manifestUrl.StartsWith("https://piston-meta.mojang.com/", StringComparison.OrdinalIgnoreCase))
                    manifestUrl = manifestUrl.Replace("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/", StringComparison.OrdinalIgnoreCase);
                var target = Path.Combine(RuntimeDir, component!);
                // A previously interrupted Java download leaves a half-populated
                // directory. Remove it before reinstalling instead of reusing it.
                try
                {
                    if (Directory.Exists(target))
                    {
                        var complete = File.Exists(Path.Combine(target, "bin", "java.exe")) &&
                            (major <= 8
                                ? File.Exists(Path.Combine(target, "lib", "rt.jar"))
                                : File.Exists(Path.Combine(target, "lib", "modules")));
                        if (!complete) Directory.Delete(target, true);
                    }
                }
                catch { }
                ProgressService.Step("获取运行时清单", 1, component);
                var manifest = System.Text.Json.Nodes.JsonNode.Parse(await DownloadService.GetText(manifestUrl)) as System.Text.Json.Nodes.JsonObject
                    ?? throw new Exception("运行时清单格式不正确");
                var files = manifest["files"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                var items = new List<(string Url, string Path, string? Sha1, long Size)>();
                foreach (var pair in files)
                {
                    if (pair.Value is not System.Text.Json.Nodes.JsonObject info) continue;
                    var type = info["type"]?.GetValue<string>() ?? "";
                    var raw = info["downloads"]?["raw"];
                    if (type == "directory") { Directory.CreateDirectory(Path.Combine(target, pair.Key)); continue; }
                    if (type != "file" || raw == null) continue;
                    var url = raw["url"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    items.Add((url!, Path.Combine(target, pair.Key), raw["sha1"]?.GetValue<string>(), raw["size"]?.GetValue<long>() ?? 0));
                }
                ProgressService.Update(1, "清单已就绪");
                ProgressService.Step($"下载 Java 运行时 ({versionName})", items.Count, component);
                var done = 0;
                var javaConcurrency = 32;
                try { javaConcurrency = Math.Clamp(BridgeService.GetSettings()["maxConcurrency"]?.GetValue<int>() ?? 32, 1, 32); } catch { }
                using var gate = new SemaphoreSlim(javaConcurrency);
                await Task.WhenAll(items.Select(async item =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        await DownloadService.Download(item.Url, item.Path, item.Sha1, item.Size);
                    }
                    finally
                    {
                        gate.Release();
                        var current = Interlocked.Increment(ref done);
                        ProgressService.Update(current, Path.GetFileName(item.Path));
                    }
                }));
                var javaExe = Path.Combine(target, "bin", "java.exe");
                if (!File.Exists(javaExe)) throw new Exception("Java 运行时缺少 bin/java.exe");
                if (major <= 8 && !File.Exists(Path.Combine(target, "lib", "rt.jar")))
                    throw new Exception("Java 8 运行时下载不完整（缺少 lib/rt.jar），请重试");
                if (major > 8 && !File.Exists(Path.Combine(target, "lib", "modules")))
                    throw new Exception("Java 运行时下载不完整（缺少 lib/modules），请重试");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "aurora.json"), new System.Text.Json.Nodes.JsonObject
                {
                    ["component"] = component,
                    ["major"] = major,
                    ["version"] = versionName,
                    ["installedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }.ToJsonString());
                ProgressService.Complete();
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["path"] = javaExe,
                    ["version"] = versionName,
                    ["major"] = major,
                    ["arch"] = "x64",
                    ["vendor"] = "Mojang",
                    ["is64Bit"] = true,
                    ["source"] = "managed",
                    ["managed"] = true
                };
            }
            catch (Exception ex)
            {
                ProgressService.Fail(ex.Message);
                throw;
            }
        }

        public static void Delete(string javaPath)
        {
            var directory = Path.GetDirectoryName(Path.GetDirectoryName(javaPath)) ?? "";
            var runtime = Path.GetFullPath(RuntimeDir);
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(runtime, StringComparison.OrdinalIgnoreCase))
                throw new Exception("只能删除由启动器下载的 Java");
            if (!File.Exists(Path.Combine(full, "aurora.json")))
                throw new Exception("只能删除由启动器下载的 Java");
            Directory.Delete(full, true);
        }

        private static async Task<System.Text.Json.Nodes.JsonObject> GetAll()
        {
            try
            {
                if (File.Exists(CacheFile))
                {
                    var cached = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CacheFile)) as System.Text.Json.Nodes.JsonObject;
                    var at = cached?["at"]?.GetValue<long>() ?? 0;
                    if (cached?["data"] is System.Text.Json.Nodes.JsonObject data && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at < 3600_000)
                        return data;
                }
            }
            catch { }
            var all = System.Text.Json.Nodes.JsonNode.Parse(await DownloadService.GetText(AllUrl)) as System.Text.Json.Nodes.JsonObject
                ?? throw new Exception("Java 运行时列表格式不正确");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                File.WriteAllText(CacheFile, new System.Text.Json.Nodes.JsonObject
                {
                    ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["data"] = System.Text.Json.Nodes.JsonNode.Parse(all.ToJsonString())
                }.ToJsonString());
            }
            catch { }
            return all;
        }

        private static int MajorOf(string version)
        {
            var parts = version.Split('.', '-', '_');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var first)) return 0;
            if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
            return first;
        }
    }
}
