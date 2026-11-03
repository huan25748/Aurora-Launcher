using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuroraLauncher
{
    /// <summary>
    /// Resolves the dependency tree of a mod so the UI can ask the user which
    /// prerequisites to install together with it. Installed detection works on
    /// mod identity (jar metadata id / slug / normalized name), not exact file
    /// name, so another version of the same mod still counts as installed.
    /// </summary>
    internal static class DependencyService
    {
        private const int MaxEntries = 30;
        private const int MaxDepth = 4;

        public static async Task<JsonArray> Plan(JsonElement input)
        {
            Log("v1.5.5.4 rpc " + (input.ValueKind == JsonValueKind.Undefined ? "undefined" : input.GetRawText()));
            var source = "curseforge";
            var instanceId = "";
            var projectId = "";
            var versionId = "";
            var fileId = 0;
            try
            {
                source = (GetString(input, "source") ?? "curseforge").ToLowerInvariant();
                instanceId = GetString(input, "instanceId") ?? "";
                projectId = GetString(input, "projectId") ?? "";
                versionId = GetString(input, "versionId") ?? "";
                fileId = GetInt(input, "fileId", 0);
            }
            catch (Exception ex)
            {
                Log("parse failed: " + ex);
                return new JsonArray();
            }
            Log($"args source={source} instance={instanceId} project={projectId} version={versionId}");
            if (string.IsNullOrWhiteSpace(instanceId)) { Log("instance id empty"); return new JsonArray(); }
            JsonObject? instance = null;
            try { instance = BridgeService.GetInstance(instanceId) as JsonObject; }
            catch (Exception ex) { Log("get instance failed: " + ex.Message); }
            if (instance == null) { Log("instance missing: " + instanceId); return new JsonArray(); }
            var mcVersion = instance["mcVersion"]?.GetValue<string>() ?? "";
            var loader = instance["loader"]?.GetValue<string>() ?? "";
            var gameDir = instance["gameDir"]?.GetValue<string>()
                ?? Path.Combine(AppPaths.RootDir, "versions", instance["versionId"]?.GetValue<string>() ?? "");
            var modsDir = Path.Combine(gameDir, "mods");
            var installed = ScanInstalledMods(modsDir);
            Log($"plan source={source} project={projectId} file={fileId} version={versionId} mc={mcVersion} loader={loader}");

            var result = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Source, string ProjectId, string VersionId, bool Optional)>();
            var pending = new List<(string Source, string ProjectId, string VersionId, bool Optional)>();

            try
            {
                if (source == "curseforge")
                {
                    var root = await CurseForgeService.FileDetails(ParseInt(projectId), fileId);
                    AddCurseForgeDependencies(root, pending);
                }
                else
                {
                    JsonObject root;
                    try
                    {
                        root = string.IsNullOrWhiteSpace(versionId)
                            ? await ModrinthService.VersionDetails(projectId, versionId)
                            : await ModrinthService.VersionById(versionId);
                    }
                    catch
                    {
                        root = await ModrinthService.VersionDetails(projectId, versionId);
                    }
                    if (string.IsNullOrWhiteSpace(projectId)) projectId = root["project_id"]?.GetValue<string>() ?? "";
                    AddModrinthDependencies(root, pending);
                }
            }
            catch (Exception ex)
            {
                Log("root failed: " + ex.Message);
                return result;
            }

            foreach (var item in pending) queue.Enqueue(item);
            var depth = 0;
            while (queue.Count > 0 && result.Count < MaxEntries && depth < MaxDepth)
            {
                depth++;
                var count = queue.Count;
                for (var i = 0; i < count && result.Count < MaxEntries; i++)
                {
                    var item = queue.Dequeue();
                    var key = item.Source + ":" + item.ProjectId;
                    if (!seen.Add(key)) continue;
                    try
                    {
                    if (item.Source == "curseforge")
                    {
                        var dependency = await ResolveCurseForge(item.ProjectId, mcVersion, loader);
                        if (dependency == null) continue;
                        var file = (JsonObject)dependency["file"]!;
                        var project = (JsonObject)dependency["project"]!;
                        var fileName = file["fileName"]?.GetValue<string>() ?? "";
                        result.Add(new JsonObject
                        {
                            ["key"] = key,
                            ["source"] = "curseforge",
                            ["projectId"] = item.ProjectId,
                            ["modId"] = ParseInt(item.ProjectId),
                            ["fileId"] = file["id"]?.GetValue<int>() ?? 0,
                            ["versionId"] = "",
                            ["name"] = project["name"]?.GetValue<string>() ?? fileName,
                            ["icon"] = project["logo"]?.GetValue<string>(),
                            ["fileName"] = fileName,
                            ["size"] = file["fileLength"]?.GetValue<long>() ?? 0,
                            ["installed"] = IsInstalled(installed, project, fileName),
                            ["required"] = !item.Optional
                        });
                        if (!item.Optional)
                        {
                            foreach (var child in (file["dependencies"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                var relation = child["relationType"]?.GetValue<int>() ?? 3;
                                if (relation != 3) continue;
                                var id = child["modId"]?.GetValue<int>() ?? 0;
                                if (id <= 0) continue;
                                queue.Enqueue(("curseforge", id.ToString(), "", false));
                            }
                        }
                    }
                    else
                    {
                        var dependency = await ResolveModrinth(item.ProjectId, item.VersionId, mcVersion, loader);
                        if (dependency == null) continue;
                        var version = (JsonObject)dependency["version"]!;
                        var file = (JsonObject)dependency["file"]!;
                        var project = (JsonObject)dependency["project"]!;
                        var fileName = file["filename"]?.GetValue<string>() ?? "";
                        result.Add(new JsonObject
                        {
                            ["key"] = key,
                            ["source"] = "modrinth",
                            ["projectId"] = item.ProjectId,
                            ["modId"] = 0,
                            ["fileId"] = 0,
                            ["versionId"] = version["id"]?.GetValue<string>() ?? "",
                            ["name"] = project["title"]?.GetValue<string>() ?? fileName,
                            ["icon"] = project["icon_url"]?.GetValue<string>(),
                            ["fileName"] = fileName,
                            ["size"] = file["size"]?.GetValue<long>() ?? 0,
                            ["installed"] = IsInstalled(installed, project, fileName),
                            ["required"] = !item.Optional
                        });
                        if (!item.Optional)
                        {
                            foreach (var child in (version["dependencies"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                var type = child["dependency_type"]?.GetValue<string>() ?? "required";
                                if (type != "required") continue;
                                var id = child["project_id"]?.GetValue<string>() ?? "";
                                var childVersion = child["version_id"]?.GetValue<string>() ?? "";
                                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(childVersion)) continue;
                                queue.Enqueue(("modrinth", id, childVersion, false));
                            }
                        }
                    }
                    }
                    catch { continue; }
                }
            }
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("requiredOnly", out var requiredOnly) && requiredOnly.ValueKind == JsonValueKind.True)
            {
                var required = new JsonArray();
                foreach (var item in result.OfType<JsonObject>())
                    if (item["required"]?.GetValue<bool>() == true) required.Add(item.DeepCloneCompat());
                result = required;
            }
            Log("plan done: " + result.Count);
            return result;
        }

        private static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(AppPaths.SettingsDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "deps.log"), DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static void AddCurseForgeDependencies(JsonObject file, List<(string, string, string, bool)> pending)
        {
            foreach (var child in (file["dependencies"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var relation = child["relationType"]?.GetValue<int>() ?? 3;
                if (relation != 3 && relation != 2) continue;
                var id = child["modId"]?.GetValue<int>() ?? 0;
                if (id <= 0) continue;
                pending.Add(("curseforge", id.ToString(), "", relation == 2));
            }
        }

        private static void AddModrinthDependencies(JsonObject version, List<(string, string, string, bool)> pending)
        {
            foreach (var child in (version["dependencies"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var type = child["dependency_type"]?.GetValue<string>() ?? "required";
                if (type != "required" && type != "optional") continue;
                var id = child["project_id"]?.GetValue<string>() ?? "";
                var childVersion = child["version_id"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(childVersion)) continue;
                pending.Add(("modrinth", id, childVersion, type == "optional"));
            }
        }

        private static async Task<JsonObject?> ResolveCurseForge(string projectId, string mcVersion, string loader)
        {
            var modId = ParseInt(projectId);
            if (modId <= 0) return null;
            var files = await CurseForgeService.Files(modId, mcVersion, loader);
            var candidates = files.OfType<JsonObject>()
                .Where(file => file["downloadUrl"]?.GetValue<string>() is { Length: > 0 } || file["fileName"]?.GetValue<string>() is { Length: > 0 })
                .ToList();
            if (candidates.Count == 0) return null;
            var file = candidates.FirstOrDefault(item => item["releaseType"]?.GetValue<int>() == 1) ?? candidates[0];
            JsonObject project;
            try { project = await CurseForgeService.Project(modId); }
            catch { project = new JsonObject { ["name"] = file["fileName"]?.GetValue<string>() ?? ("mod " + modId), ["slug"] = "" }; }
            return new JsonObject { ["file"] = file.DeepCloneCompat(), ["project"] = project };
        }

        private static async Task<JsonObject?> ResolveModrinth(string projectId, string versionId, string mcVersion, string loader)
        {
            JsonObject? pinned = null;
            if (!string.IsNullOrWhiteSpace(versionId))
            {
                try { pinned = await ModrinthService.VersionById(versionId); } catch { }
            }
            var versions = pinned != null ? new JsonArray(pinned) : await ModrinthService.Versions(projectId, mcVersion, loader);
            if (string.IsNullOrWhiteSpace(projectId) && pinned != null)
                projectId = pinned["project_id"]?.GetValue<string>() ?? "";
            JsonObject? version = null;
            JsonObject? file = null;
            foreach (var item in versions.OfType<JsonObject>())
            {
                var files = item["files"] as JsonArray;
                var primary = files?.OfType<JsonObject>().FirstOrDefault(entry => entry["primary"]?.GetValue<bool>() == true)
                    ?? files?.OfType<JsonObject>().FirstOrDefault();
                if (primary == null) continue;
                version = item;
                file = primary;
                break;
            }
            if (version == null || file == null) return null;
            if (string.IsNullOrWhiteSpace(projectId)) projectId = version["project_id"]?.GetValue<string>() ?? "";
            JsonObject project;
            try { project = await ModrinthService.Project(projectId); }
            catch { project = new JsonObject { ["title"] = file["filename"]?.GetValue<string>() ?? projectId, ["slug"] = "" }; }
            return new JsonObject { ["version"] = version.DeepCloneCompat(), ["file"] = file.DeepCloneCompat(), ["project"] = project };
        }

        private sealed class InstalledMods
        {
            public readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> FileNames = new(StringComparer.OrdinalIgnoreCase);

            public bool Matches(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                if (Ids.Contains(value) || FileNames.Contains(value)) return true;
                var normalized = Normalize(value);
                return normalized.Length > 0 && (Ids.Contains(normalized) || FileNames.Contains(normalized));
            }
        }

        private static InstalledMods ScanInstalledMods(string modsDir)
        {
            var installed = new InstalledMods();
            try
            {
                if (!Directory.Exists(modsDir)) return installed;
                foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar"))
                {
                    var name = Path.GetFileName(path);
                    installed.FileNames.Add(name);
                    installed.FileNames.Add(Normalize(name));
                    foreach (var id in ReadModIds(path))
                    {
                        installed.Ids.Add(id);
                        installed.Ids.Add(Normalize(id));
                    }
                }
            }
            catch { }
            return installed;
        }

        private static IEnumerable<string> ReadModIds(string path)
        {
            var ids = new List<string>();
            try
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var name in new[] { "fabric.mod.json", "quilt.mod.json" })
                {
                    var entry = zip.GetEntry(name);
                    if (entry == null) continue;
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var json = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
                    if (json?["id"]?.GetValue<string>() is { Length: > 0 } id) ids.Add(id);
                }
                foreach (var name in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
                {
                    var entry = zip.GetEntry(name);
                    if (entry == null) continue;
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    foreach (Match match in Regex.Matches(text, "modId\\s*=\\s*[\"']([^\"']+)[\"']"))
                    {
                        var id = match.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(id) && id != "minecraft" && id != "forge" && id != "neoforge" && id != "fabricloader")
                            ids.Add(id);
                    }
                }
            }
            catch { }
            return ids;
        }

        private static bool IsInstalled(InstalledMods installed, JsonObject project, string fileName)
        {
            if (installed.Matches(fileName)) return true;
            var slug = project["slug"]?.GetValue<string>() ?? "";
            if (installed.Matches(slug)) return true;
            var name = project["name"]?.GetValue<string>() ?? project["title"]?.GetValue<string>() ?? "";
            if (installed.Matches(name)) return true;
            var normalizedName = Normalize(name);
            if (normalizedName.Length >= 4 && installed.Matches(normalizedName)) return true;
            // File names often look like "fabric-api-0.92.12+1.20.1.jar"; compare the
            // leading identity part so another version still counts as installed.
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var dash = stem.IndexOf('-');
            if (dash > 2 && installed.Matches(stem[..dash])) return true;
            return false;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static string? GetString(JsonElement input, string name)
        {
            try
            {
                if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var value)) return null;
                return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch { return null; }
        }

        private static int GetInt(JsonElement input, string name, int fallback)
        {
            try
            {
                if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var value)) return fallback;
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
            }
            catch { return fallback; }
        }

        private static int ParseInt(string value) => int.TryParse(value, out var number) ? number : 0;
    }
}
