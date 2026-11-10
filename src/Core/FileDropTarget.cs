using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace AuroraLauncher
{
    /// <summary>
    /// 在 WebView2 子窗口上注册 OLE 拖放接收器，用于接收拖入的整合包文件。
    /// （WPF Window 的 Drop 会被 WebView2 的子 HWND 挡住，所以直接把目标注册到子窗口上。）
    /// </summary>
    internal static class FileDropTarget
    {
        private const int WM_DROPFILES_CF = 15; // CF_HDROP

        private static DropTarget? _target;
        private static readonly object Gate = new();
        private static Action<string>? _handler;
        private static Action<bool>? _dragState;

        public static void Attach(Window window, Action<string> handler, Action<bool>? dragState = null)
        {
            _handler = handler;
            _dragState = dragState;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            lock (Gate)
            {
                _target ??= new DropTarget(path => _handler?.Invoke(path), active => _dragState?.Invoke(active));
                foreach (var child in EnumerateDescendants(hwnd))
                {
                    try
                    {
                        RevokeDragDrop(child);
                    }
                    catch { }
                    try
                    {
                        RegisterDragDrop(child, _target);
                    }
                    catch { }
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<IntPtr> EnumerateDescendants(IntPtr parent)
        {
            var result = new System.Collections.Generic.List<IntPtr>();
            EnumChildWindows(parent, (hwnd, _) => { result.Add(hwnd); return true; }, IntPtr.Zero);
            return result;
        }

        [DllImport("ole32.dll")]
        private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);

        [DllImport("ole32.dll")]
        private static extern int RevokeDragDrop(IntPtr hwnd);

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? file, uint cch);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTL { public int x; public int y; }

        [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropTarget
        {
            [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object data, uint keyState, POINTL point, ref uint effect);
            [PreserveSig] int DragOver(uint keyState, POINTL point, ref uint effect);
            [PreserveSig] int DragLeave();
            [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object data, uint keyState, POINTL point, ref uint effect);
        }

        private sealed class DropTarget : IDropTarget
        {
            private readonly Action<string> _onDrop;
            private readonly Action<bool> _onDragState;
            public DropTarget(Action<string> onDrop, Action<bool> onDragState)
            {
                _onDrop = onDrop;
                _onDragState = onDragState;
            }

            public int DragEnter(object data, uint keyState, POINTL point, ref uint effect)
            {
                var supported = Supported(data);
                _onDragState(supported);
                effect = supported ? 1u : 0u; // DROPEFFECT_COPY
                return 0;
            }

            public int DragOver(uint keyState, POINTL point, ref uint effect)
            {
                effect = 1u;
                return 0;
            }

            public int DragLeave()
            {
                _onDragState(false);
                return 0;
            }

            public int Drop(object data, uint keyState, POINTL point, ref uint effect)
            {
                effect = 0;
                _onDragState(false);
                var path = GetPath(data);
                if (path == null) return 0;
                effect = 1u;
                try { _onDrop(path); } catch { }
                return 0;
            }

            private static bool Supported(object data) => GetPath(data) != null;

            private static string? GetPath(object data)
            {
                try
                {
                    var com = (System.Runtime.InteropServices.ComTypes.IDataObject)data;
                    var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
                    {
                        cfFormat = WM_DROPFILES_CF,
                        dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                        lindex = -1,
                        tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                    };
                    com.GetData(ref format, out var medium);
                    try
                    {
                        var hDrop = medium.unionmember;
                        var count = DragQueryFile(hDrop, uint.MaxValue, null, 0);
                        for (uint i = 0; i < count; i++)
                        {
                            var length = DragQueryFile(hDrop, i, null, 0);
                            var buffer = new StringBuilder((int)length + 1);
                            DragQueryFile(hDrop, i, buffer, (uint)buffer.Capacity);
                            var path = buffer.ToString();
                            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                || path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                                || path.EndsWith(".curseforge", StringComparison.OrdinalIgnoreCase)
                                || path.EndsWith(".modpack", StringComparison.OrdinalIgnoreCase))
                                return path;
                        }
                    }
                    finally
                    {
                        ReleaseStgMedium(ref medium);
                    }
                }
                catch { }
                return null;
            }
        }
    }
}
