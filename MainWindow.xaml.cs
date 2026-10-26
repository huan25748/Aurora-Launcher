using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace AuroraLauncher
{
    public partial class MainWindow : Window
    {
        private int _resizeHit;
        private int _resizeStartX;
        private int _resizeStartY;
        private int _resizeLeft;
        private int _resizeTop;
        private int _resizeWidth;
        private int _resizeHeight;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            // Do not kill a running game when the launcher window is closed:
            // Minecraft may still be saving the world. The game keeps running and
            // exits normally when the player quits it.
            try { ForgeService.StopInstaller(); } catch { }
            try { Browser.Dispose(); } catch { }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureLayout();
                try
                {
                    var storedRoot = BridgeService.GetSettings()["rootDir"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(storedRoot) && Directory.Exists(storedRoot) && File.Exists(Path.Combine(storedRoot!, ".aurora-root"))) AppPaths.UseRoot(storedRoot!);
                }
                catch { }
                var assets = AssetBundle.Ensure();
                var dataDir = Path.Combine(AppContext.BaseDirectory, ".webview2", assets.VersionHash);
                Directory.CreateDirectory(dataDir);
                var environment = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await Browser.EnsureCoreWebView2Async(environment);

                SizeChanged += (_, _) => ApplyRoundedRegion();
                StateChanged += (_, _) => ApplyRoundedRegion();
                SourceInitialized += (_, _) => ApplyRoundedRegion();
                ApplyRoundedRegion();
                var handle = new WindowInteropHelper(this).Handle;
                var corner = 2;
                try { DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int)); } catch { }
                var core = Browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;

                var renderer = assets.RendererDir;
                core.SetVirtualHostNameToFolderMapping("aurora.local", renderer, CoreWebView2HostResourceAccessKind.Allow);

                var instances = new List<JsonElement>();
                try
                {
                    foreach (var file in Directory.GetFiles(AppPaths.InstancesDir, "instance.json", SearchOption.AllDirectories))
                        instances.Add(JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone());
                }
                catch { }

                var boot = new
                {
                    version = "1.1.0",
                    baseDir = AppPaths.BaseDir,
                    rootDir = AppPaths.RootDir,
                    settingsDir = AppPaths.SettingsDir,
                    versionsDir = Path.Combine(AppPaths.RootDir, "versions"),
                    assetsDir = Path.Combine(AppPaths.RootDir, "assets"),
                    librariesDir = Path.Combine(AppPaths.RootDir, "libraries"),
                    instancesDir = AppPaths.InstancesDir,
                    instances
                };
                await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__AURORA_BOOT__ = " + JsonSerializer.Serialize(boot) + ";");

                var shimFile = assets.ShimFile;
                if (File.Exists(shimFile))
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(File.ReadAllText(shimFile));

                core.WebMessageReceived += OnWebMessage;
                GameEvents.Raised += (name, payload) =>
                {
                    try
                    {
                        var message = JsonSerializer.Serialize(new { type = "event", name, payload });
                        Dispatcher.BeginInvoke(new Action(() => core.PostWebMessageAsJson(message)));
                    }
                    catch { }
                };
                core.Navigate("https://aurora.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "WebView2 初始化失败：\n" + ex.Message, "Aurora Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        private void ApplyRoundedRegion()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                if (WindowState == WindowState.Maximized)
                {
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                    return;
                }
                var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                var radius = (int)Math.Max(8, 16 * scale);
                var region = CreateRoundRectRgn(0, 0, (int)(ActualWidth * scale) + 1, (int)(ActualHeight * scale) + 1, radius, radius);
                SetWindowRgn(hwnd, region, true);
            }
            catch { }
        }

        private void PostEvent(string name, JsonNode? payload)
        {
            try { Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "event", name, payload })); } catch { }
        }

        private async Task HandleInvokeAsync(string id, string channel, JsonElement args)
        {
            try
            {
                var result = await BridgeService.Dispatch(channel, args);
                var payload = JsonSerializer.Serialize(new { type = "resolve", id, result });
                Browser.CoreWebView2?.PostWebMessageAsJson(payload);
            }
            catch (Exception ex)
            {
                try { if (ProgressService.IsActive) ProgressService.Fail(ex.Message); } catch { }
                var payload = JsonSerializer.Serialize(new { type = "reject", id, error = ex.Message });
                Browser.CoreWebView2?.PostWebMessageAsJson(payload);
            }
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement)) return;
                var type = typeElement.GetString();

                if (type == "invoke")
                {
                    var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    var channel = root.TryGetProperty("channel", out var channelElement) ? channelElement.GetString() : null;
                    var args = root.TryGetProperty("args", out var argsElement) ? argsElement.Clone() : default;
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(channel))
                        _ = HandleInvokeAsync(id!, channel!, args);
                    return;
                }

                if (type == "window")
                {
                    var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
                    switch (action)
                    {
                        case "minimize":
                            WindowState = WindowState.Minimized;
                            break;
                        case "maximize":
                            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                            PostEvent("maximized", WindowState == WindowState.Maximized);
                            break;
                        case "drag":
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    ReleaseCapture();
                                    SendMessage(new WindowInteropHelper(this).Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                                }
                                catch { }
                            }));
                            break;
                        case "resize-start":
                            if (root.TryGetProperty("hit", out var hitElement) && hitElement.TryGetInt32(out var hit)
                                && root.TryGetProperty("x", out var rx) && rx.TryGetDouble(out var startX)
                                && root.TryGetProperty("y", out var ry) && ry.TryGetDouble(out var startY))
                                Dispatcher.BeginInvoke(new Action(() => StartResize(hit, startX, startY)));
                            break;
                        case "resize-move":
                            if (root.TryGetProperty("x", out var mx) && mx.TryGetDouble(out var moveX)
                                && root.TryGetProperty("y", out var my) && my.TryGetDouble(out var moveY))
                                Dispatcher.BeginInvoke(new Action(() => MoveResize(moveX, moveY)));
                            break;
                        case "resize-end":
                            _resizeHit = 0;
                            break;
                        case "close":
                            Close();
                            break;
                        case "title":
                            if (root.TryGetProperty("title", out var title)) Title = title.GetString() ?? "Aurora Launcher";
                            break;
                        case "relaunch":
                            Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? "") { UseShellExecute = true });
                            Close();
                            break;
                    }
                    return;
                }

                if (type == "openExternal")
                {
                    var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    return;
                }

                if (type == "openPath" || type == "showItem")
                {
                    var path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        if (type == "showItem" && File.Exists(path))
                            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                        else if (Directory.Exists(path))
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                    }
                }
            }
            catch
            {
                // Browser-side malformed messages must never crash the shell.
            }
        }

        private void ResizeLeft_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(10);
        private void ResizeRight_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(11);
        private void ResizeTop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(12);
        private void ResizeTopLeft_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(13);
        private void ResizeTopRight_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(14);
        private void ResizeBottom_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(15);
        private void ResizeBottomLeft_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(16);
        private void ResizeBottomRight_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BeginResize(17);

        private void BeginResize(int hitTest)
        {
            if (WindowState == WindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(new WindowInteropHelper(this).Handle, 0xA1, (IntPtr)hitTest, IntPtr.Zero);
        }

        private void StartResize(int hitTest, double x, double y)
        {
            if (WindowState == WindowState.Maximized) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!GetWindowRect(hwnd, out var rect)) return;
            _resizeHit = hitTest;
            _resizeStartX = (int)x;
            _resizeStartY = (int)y;
            _resizeLeft = rect.Left;
            _resizeTop = rect.Top;
            _resizeWidth = rect.Right - rect.Left;
            _resizeHeight = rect.Bottom - rect.Top;
        }

        private void MoveResize(double x, double y)
        {
            if (_resizeHit == 0 || WindowState == WindowState.Maximized) return;
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            var dx = (int)((x - _resizeStartX) * scale);
            var dy = (int)((y - _resizeStartY) * scale);
            var left = _resizeLeft;
            var top = _resizeTop;
            var width = _resizeWidth;
            var height = _resizeHeight;
            var minWidth = Math.Max(320, (int)(MinWidth * scale));
            var minHeight = Math.Max(200, (int)(MinHeight * scale));

            if (_resizeHit is 10 or 13 or 16)
            {
                width = _resizeWidth - dx;
                left = _resizeLeft + dx;
                if (width < minWidth) { width = minWidth; left = _resizeLeft + _resizeWidth - minWidth; }
            }
            if (_resizeHit is 11 or 14 or 17)
            {
                width = _resizeWidth + dx;
                if (width < minWidth) width = minWidth;
            }
            if (_resizeHit is 12 or 13 or 14)
            {
                height = _resizeHeight - dy;
                top = _resizeTop + dy;
                if (height < minHeight) { height = minHeight; top = _resizeTop + _resizeHeight - minHeight; }
            }
            if (_resizeHit is 15 or 16 or 17)
            {
                height = _resizeHeight + dy;
                if (height < minHeight) height = minHeight;
            }
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int flags);

    }
}
