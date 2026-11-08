using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PptEase.Host
{
    internal static class NativeCanvasBridge
    {
        internal sealed class ViewportInfo
        {
            public bool Supported = true;
            public string Backend = "native-pan-restored-v4";
            public string BoundaryPolicy = "direct-origin-original-renderer-limits-remain";
            public string CoreSha256 = CanvasCoreHash;
            public int[] Origin;
            public int Left, Top, Right, Bottom, Zoom;
            public double ScaleX, ScaleY;
            public string Context;
            public bool Contains(int x, int y) { return x >= Left && x < Right && y >= Top && y < Bottom; }
        }
        // Exact installed artifact admitted by passive COM/vtable observations and disassembly.
        // Other Office builds must be investigated before enabling this adapter.
        private const string CanvasCoreHash = "04F92C99013870F4DFB3EE9AD032A4ADD47D20DD9AEB64C53562425CEB6C989F";
        private static IntPtr canvasCore;
        [StructLayout(LayoutKind.Sequential)] private struct CanvasPoint { public int X, Y; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr UnwrapCanvasView(IntPtr window);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr ReadCanvasOrigin(IntPtr view, out CanvasPoint point);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WriteCanvasOrigin(IntPtr view, double x, double y);

        private static IntPtr CanvasAddress(int rva) { return IntPtr.Add(canvasCore, rva); }
        private static void VerifyCanvasCore()
        {
            if (canvasCore != IntPtr.Zero) return;
            ProcessModule core = null;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (String.Equals(module.ModuleName, "ppcore.dll", StringComparison.OrdinalIgnoreCase)) { core = module; break; }
            if (core == null) throw new InvalidOperationException("未找到 PowerPoint 画布模块。");
            string hash;
            using (FileStream stream = File.OpenRead(core.FileName))
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (hash != CanvasCoreHash) throw new InvalidOperationException("当前 Office 版本尚未适配越界平移，已保持原生行为。");
            byte[] signature = { 0x48, 0x8b, 0xc4, 0xf2, 0x0f, 0x11, 0x50, 0x18, 0xf2, 0x0f, 0x11, 0x48, 0x10 };
            for (int i = 0; i < signature.Length; i++)
                if (Marshal.ReadByte(IntPtr.Add(core.BaseAddress, 0x25af30 + i)) != signature[i]) throw new InvalidOperationException("画布入口校验失败，未启用平移。");
            canvasCore = core.BaseAddress;
        }

        internal static object PanCanvas(dynamic app, int dx, int dy, bool inspectOnly = false)
        {
            if (Math.Abs((long)dx) > 2000 || Math.Abs((long)dy) > 2000) throw new InvalidOperationException("单次平移距离超出范围。");
            if (!inspectOnly && (int)app.WindowState == 2) throw new InvalidOperationException("请先恢复 PowerPoint 编辑窗口。");
            dynamic window = app.ActiveWindow;
            if ((int)window.ViewType != 9 && (int)window.ViewType != 1) throw new InvalidOperationException("越界平移仅用于普通幻灯片编辑视图。");
            VerifyCanvasCore();
            int slide = (int)window.View.Slide.SlideID;
            int screenX = (int)window.PointsToScreenPixelsX(0), screenY = (int)window.PointsToScreenPixelsY(0);
            object viewObject = (object)window.View;
            IntPtr unknown = Marshal.GetIUnknownForObject(viewObject), viewInterface = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("91493458-5A91-11CF-8700-00AA0060263B");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out viewInterface));
                IntPtr table = Marshal.ReadIntPtr(viewInterface);
                if (Marshal.ReadIntPtr(table, 80) != CanvasAddress(0x149f450)) throw new InvalidOperationException("当前视图接口与已验证版本不匹配。");
                UnwrapCanvasView unwrap = (UnwrapCanvasView)Marshal.GetDelegateForFunctionPointer(CanvasAddress(0x50ef50), typeof(UnwrapCanvasView));
                IntPtr view = unwrap(Marshal.ReadIntPtr(viewInterface, 8));
                if (view == IntPtr.Zero || Marshal.ReadIntPtr(view) != CanvasAddress(0x18eeb50) || Marshal.ReadInt32(view, 0x18) != 1)
                    throw new InvalidOperationException("当前画布类型尚未适配越界平移。");
                IntPtr controller = Marshal.ReadIntPtr(view, 0x120);
                if (controller == IntPtr.Zero || Marshal.ReadIntPtr(controller, 8) != view) throw new InvalidOperationException("画布滚动控制器校验失败。");
                ReadCanvasOrigin read = (ReadCanvasOrigin)Marshal.GetDelegateForFunctionPointer(CanvasAddress(0x95570), typeof(ReadCanvasOrigin));
                WriteCanvasOrigin write = (WriteCanvasOrigin)Marshal.GetDelegateForFunctionPointer(CanvasAddress(0x25af30), typeof(WriteCanvasOrigin));
                CanvasPoint before;
                read(view, out before);
                if (inspectOnly)
                {
                    IntPtr pane = Marshal.ReadIntPtr(view, 0x28);
                    if (pane == IntPtr.Zero) throw new InvalidOperationException("当前画布不可见。");
                    int width = Marshal.ReadInt32(pane, 0x38) - Marshal.ReadInt32(pane, 0x30);
                    int height = Marshal.ReadInt32(pane, 0x3c) - Marshal.ReadInt32(pane, 0x34);
                    if (width <= 0 || height <= 0 || width > 100000 || height > 100000) throw new InvalidOperationException("画布范围无效。");
                    return new ViewportInfo { Origin = new[] { before.X, before.Y }, Left = screenX + before.X, Top = screenY + before.Y, Right = screenX + before.X + width, Bottom = screenY + before.Y + height, Zoom = (int)window.View.Zoom, ScaleX = ((double)window.PointsToScreenPixelsX(72) - screenX) / 72.0, ScaleY = ((double)window.PointsToScreenPixelsY(72) - screenY) / 72.0, Context = view.ToString("X") + ":" + slide };
                }
                CanvasPoint next = new CanvasPoint { X = checked(before.X - dx), Y = checked(before.Y - dy) };
                if (Math.Abs((long)next.X) > 1000000 || Math.Abs((long)next.Y) > 1000000) throw new InvalidOperationException("已到增强画布的平移边界。");
                // Restore the previously working direct-origin pan. Office's
                // normalizer pins fitted slides to the center and defeats this
                // feature. Renderer limits at extreme offsets remain unresolved;
                // do not call this an unbounded renderer fix.
                double targetX = -next.X, targetY = -next.Y;
                if (Double.IsNaN(targetX) || Double.IsInfinity(targetX) || Double.IsNaN(targetY) || Double.IsInfinity(targetY) || Math.Abs(targetX) > 1000000 || Math.Abs(targetY) > 1000000)
                    throw new InvalidOperationException("画布目标坐标无效，未提交平移。");
                bool clamped = false;
                write(view, targetX, targetY);
                CanvasPoint after;
                read(view, out after);
                int afterX = (int)window.PointsToScreenPixelsX(0), afterY = (int)window.PointsToScreenPixelsY(0);
                return new { Applied = afterX != screenX || afterY != screenY, BoundaryClamped = clamped, Before = new[] { before.X, before.Y }, After = new[] { after.X, after.Y }, ScreenBefore = new[] { screenX, screenY }, ScreenAfter = new[] { afterX, afterY }, SlideBefore = slide, SlideAfter = (int)window.View.Slide.SlideID, Backend = "native-pan-restored-v4", Utc = DateTime.UtcNow.ToString("o") };
            }
            finally
            {
                if (viewInterface != IntPtr.Zero) Marshal.Release(viewInterface);
                Marshal.Release(unknown);
            }
        }
    }
}
