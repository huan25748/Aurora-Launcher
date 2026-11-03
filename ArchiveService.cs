using System;
using System.IO;
using System.IO.Compression;

namespace AuroraLauncher
{
    internal static class ArchiveService
    {
        public static void ExtractWithProgress(string zipPath, string targetDir, Action<int, int>? progress)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var total = archive.Entries.Count;
            var done = 0;
            Directory.CreateDirectory(targetDir);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) { progress?.Invoke(++done, total); continue; }
                var target = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
                progress?.Invoke(++done, total);
            }
        }
    }
}