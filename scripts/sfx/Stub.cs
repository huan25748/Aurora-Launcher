// Aurora Launcher single-file bootstrapper.
// The build script appends a ZIP payload plus a 16-byte footer to this executable:
//   [stub.exe][zip bytes][int64 zip length][8-byte magic "AURORASF"]
// On first launch the payload is extracted to %LOCALAPPDATA%\AuroraLauncher\runtime\<id>
// and the real AuroraLauncher.exe is started. Later launches reuse the extracted copy.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private static readonly byte[] Magic = new byte[] { 0x41, 0x55, 0x52, 0x4F, 0x52, 0x41, 0x53, 0x46 }; // AURORASF
    private const string Marker = ".aurora-sfx-ok";
    private const string InnerExe = "AuroraLauncher.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        bool extractOnly = HasArg(args, "--extract-only");
        try
        {
            string self = Assembly.GetExecutingAssembly().Location;
            PayloadInfo payload = ReadFooter(self);
            string target = ResolveTarget(payload);
            Log("build " + BuildStamp.Id + " exe=" + self);
            if (!IsReady(target))
            {
                if (extractOnly)
                {
                    ExtractPayload(self, payload, target);
                }
                else
                {
                    ExtractWithSplash(self, payload, target);
                }
            }
            if (extractOnly)
            {
                Console.WriteLine("extracted: " + target);
                return 0;
            }
            Launch(target, self);
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            if (!extractOnly)
            {
                MessageBox.Show(
                    "Aurora Launcher 启动失败：\n\n" + ex.Message + "\n\n日志：\n" + LogPath(),
                    "Aurora Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private sealed class PayloadInfo
    {
        public long Offset;
        public long Length;
    }

    private static PayloadInfo ReadFooter(string self)
    {
        using (FileStream fs = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Length < 16)
            {
                throw new InvalidDataException("文件不完整（缺少内置资源）。");
            }
            fs.Seek(-16, SeekOrigin.End);
            byte[] footer = new byte[16];
            ReadExact(fs, footer, 16);
            for (int i = 0; i < 8; i++)
            {
                if (footer[8 + i] != Magic[i])
                {
                    throw new InvalidDataException("未找到内置资源。请重新下载完整的单文件版本。");
                }
            }
            long length = BitConverter.ToInt64(footer, 0);
            long offset = fs.Length - 16 - length;
            if (length <= 0 || offset <= 0)
            {
                throw new InvalidDataException("内置资源数据损坏。");
            }
            PayloadInfo info = new PayloadInfo();
            info.Length = length;
            info.Offset = offset;
            return info;
        }
    }

    private static string ResolveTarget(PayloadInfo payload)
    {
        string overrideTarget = Environment.GetEnvironmentVariable("AURORA_SFX_TARGET");
        if (!string.IsNullOrEmpty(overrideTarget))
        {
            return overrideTarget;
        }
        // BuildStamp is generated for every package build. The old length-based name could
        // collide between builds, silently reusing a stale runtime and never extracting.
        string localBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AuroraLauncher",
            "runtime");
        string preferred = Path.Combine(localBase, BuildStamp.Id);
        if (CanWrite(localBase))
        {
            return preferred;
        }

        // Some antivirus/ACL policies deny writes below %LOCALAPPDATA%. Fall back to a
        // folder next to the exe, then to the user profile, instead of failing to start.
        string selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string portableBase = Path.Combine(selfDir, "AuroraLauncher-runtime");
        if (CanWrite(portableBase))
        {
            Log("LocalAppData not writable, using " + portableBase);
            return Path.Combine(portableBase, BuildStamp.Id);
        }
        string profileBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AuroraLauncher",
            "runtime");
        if (CanWrite(profileBase))
        {
            Log("LocalAppData/exe folder not writable, using " + profileBase);
            return Path.Combine(profileBase, BuildStamp.Id);
        }
        throw new IOException(
            "无法写入运行目录。请检查杀毒软件权限，或把启动器放到可写目录后重试。\n" +
            "已尝试：\n" + preferred + "\n" + Path.Combine(portableBase, BuildStamp.Id) + "\n" + Path.Combine(profileBase, BuildStamp.Id));
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReady(string target)
    {
        return Directory.Exists(target)
            && File.Exists(Path.Combine(target, Marker))
            && File.Exists(Path.Combine(target, InnerExe));
    }

    private static void ExtractWithSplash(string self, PayloadInfo payload, string target)
    {
        Exception error = null;
        SplashForm form = new SplashForm();
        Thread worker = new Thread(delegate()
        {
            try
            {
                ExtractPayload(self, payload, target);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try
                {
                    form.BeginInvoke(new Action(form.Close));
                }
                catch
                {
                }
            }
        });
        worker.IsBackground = true;
        form.Shown += delegate { worker.Start(); };
        Application.Run(form);
        if (error != null)
        {
            throw error;
        }
    }

    private static void ExtractPayload(string self, PayloadInfo payload, string target)
    {
        using (Mutex mutex = new Mutex(false, "AuroraLauncherSfxExtract-" + BuildStamp.Id))
        {
            if (!mutex.WaitOne(TimeSpan.FromMinutes(3)))
            {
                throw new TimeoutException("等待其他启动实例超时。");
            }
            try
            {
                if (IsReady(target))
                {
                    return;
                }
                string parent = Path.GetDirectoryName(target);
                Directory.CreateDirectory(parent);
                string tempDir = target + ".tmp-" + Process.GetCurrentProcess().Id;
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                    Directory.CreateDirectory(tempDir);
                    // Read the ZIP directly from the appended payload. This avoids writing a
                    // temporary .zip to %TEMP%, which antivirus/ACL policies sometimes deny.
                    using (FileStream selfStream = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (SubStream payloadStream = new SubStream(selfStream, payload.Offset, payload.Length))
                    using (ZipArchive archive = new ZipArchive(payloadStream, ZipArchiveMode.Read))
                    {
                        string tempRoot = Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar;
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string name = (entry.FullName ?? string.Empty).Replace('\\', '/');
                            if (name.Length == 0)
                            {
                                continue;
                            }
                            string destination = Path.GetFullPath(Path.Combine(tempDir, name));
                            if (!destination.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("压缩包包含非法路径。");
                            }
                            if (name.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0)
                            {
                                Directory.CreateDirectory(destination);
                                continue;
                            }
                            string directory = Path.GetDirectoryName(destination);
                            if (!string.IsNullOrEmpty(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }
                            using (Stream input = entry.Open())
                            using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write))
                            {
                                input.CopyTo(output);
                            }
                        }
                    }
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, true);
                    }
                    Directory.Move(tempDir, target);
                    File.WriteAllText(Path.Combine(target, Marker), DateTime.UtcNow.ToString("o"));
                    Log("extracted payload to " + target);
                }
                finally
                {
                    TryDeleteDirectory(tempDir);
                }
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>Read-only window over the payload bytes inside the SFX executable.</summary>
    private sealed class SubStream : Stream
    {
        private readonly Stream inner;
        private readonly long start;
        private readonly long length;
        private long position;

        public SubStream(Stream inner, long start, long length)
        {
            this.inner = inner;
            this.start = start;
            this.length = length;
            this.position = 0;
            inner.Seek(start, SeekOrigin.Begin);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }

        public override long Position
        {
            get { return position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length)
            {
                return 0;
            }
            int want = (int)Math.Min(count, length - position);
            int read = inner.Read(buffer, offset, want);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next;
            if (origin == SeekOrigin.Begin)
            {
                next = offset;
            }
            else if (origin == SeekOrigin.Current)
            {
                next = position + offset;
            }
            else
            {
                next = length + offset;
            }
            if (next < 0)
            {
                next = 0;
            }
            if (next > length)
            {
                next = length;
            }
            position = next;
            inner.Seek(start + position, SeekOrigin.Begin);
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static void Launch(string target, string self)
    {
        string inner = Path.Combine(target, InnerExe);
        if (!File.Exists(inner))
        {
            throw new FileNotFoundException("未找到 AuroraLauncher.exe。", inner);
        }
        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = inner;
        info.WorkingDirectory = target;
        info.UseShellExecute = false;
        try
        {
            info.EnvironmentVariables["AURORA_EXE_DIR"] = Path.GetDirectoryName(self);
            info.EnvironmentVariables["AURORA_SINGLE_FILE"] = "1";
        }
        catch
        {
        }
        Process process = Process.Start(info);
        try
        {
            Log("launched inner pid=" + process.Id + " path=" + inner);
        }
        catch
        {
        }
    }

    private static bool HasArg(string[] args, string value)
    {
        if (args == null)
        {
            return false;
        }
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void ReadExact(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }

    private static string LogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AuroraLauncher",
            "sfx.log");
    }

    private static void Log(string message)
    {
        try
        {
            string path = LogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, "[" + DateTime.Now.ToString("s") + "] " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}

internal sealed class SplashForm : Form
{
    public SplashForm()
    {
        Text = "Aurora Launcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(440, 132);
        BackColor = Color.FromArgb(10, 14, 22);
        ForeColor = Color.FromArgb(235, 240, 250);
        Font = new Font("Microsoft YaHei UI", 9f);

        Label title = new Label();
        title.Text = "Aurora Launcher";
        title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(235, 240, 250);
        title.AutoSize = true;
        title.Location = new Point(24, 20);
        Controls.Add(title);

        Label hint = new Label();
        hint.Text = "首次启动正在解压运行文件，请稍候…";
        hint.ForeColor = Color.FromArgb(150, 165, 190);
        hint.AutoSize = true;
        hint.Location = new Point(26, 54);
        Controls.Add(hint);

        ProgressBar bar = new ProgressBar();
        bar.Style = ProgressBarStyle.Marquee;
        bar.MarqueeAnimationSpeed = 24;
        bar.Location = new Point(26, 86);
        bar.Size = new Size(388, 8);
        Controls.Add(bar);
    }
}
