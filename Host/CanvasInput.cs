using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PptEase.Host;

namespace PptEase.CanvasInputV3
{
    // Hooks are confined to the Office UI thread. They process real user input only.
    internal sealed class CanvasInput : IDisposable
    {
        private readonly dynamic app;
        private readonly Timer timer;
        private readonly Hook mouseProc, keyboardProc;
        private IntPtr mouseHook, keyboardHook, captured;
        private bool enabled, spaceHeld, dragging, handling;
        private int dragButton, lastX, lastY, pendingX, pendingY;
        private string dragContext, zoomContext;
        private DateTime dragStarted, zoomUntil;
        private double anchorWorldX, anchorWorldY;
        private int anchorX, anchorY;
        private double remainderX, remainderY;
        private long mouseMessages, wheelEvents, zoomEvents, panFrames;
        private long wheelSeen;
        private string lastWheelReason = "";
        private int[] lastWheelPoint;
        private int lastWheelDelta;
        private string lastError = "";
        private NativeCanvasBridge.ViewportInfo lastBounds;
        internal bool IsEnabled { get { return enabled; } }
        internal void ResetTransient() { zoomUntil=DateTime.MinValue;spaceHeld=false;EndDrag(true); }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Hook(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInfo { public Point Point; public IntPtr Window; public uint HitTest; public IntPtr Extra; public uint Data; }
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern short GetKeyState(int key);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
        private static bool Down(int key) { return GetKeyState(key) < 0; }
        internal CanvasInput(object application)
        {
            app = application; mouseProc = Mouse; keyboardProc = Keyboard;
            timer = new Timer { Interval = 16 }; timer.Tick += Tick;
        }
        internal object Status() { return new { Enabled=enabled, Dragging=dragging, MouseMessages=mouseMessages, WheelSeen=wheelSeen, WheelEvents=wheelEvents, ZoomEvents=zoomEvents, PanFrames=panFrames, LastWheelReason=lastWheelReason, LastWheelPoint=lastWheelPoint, LastWheelDelta=lastWheelDelta, LastError=lastError, Bounds=lastBounds }; }
        internal object Enable()
        {
            if (enabled) return Status();
            lastBounds = Inspect(); // Admit the exact supported native view before installing hooks.
            uint thread = GetCurrentThreadId();
            mouseHook = SetWindowsHookEx(7, mouseProc, IntPtr.Zero, thread);
            if (mouseHook == IntPtr.Zero) throw new InvalidOperationException(global::PptEase.Localization.L.T("画布鼠标接管失败：") + Marshal.GetLastWin32Error());
            keyboardHook = SetWindowsHookEx(2, keyboardProc, IntPtr.Zero, thread);
            if (keyboardHook == IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook=IntPtr.Zero; throw new InvalidOperationException(global::PptEase.Localization.L.T("空格拖动接管失败：") + Marshal.GetLastWin32Error()); }
            enabled=true; lastError=""; timer.Start(); return Status();
        }
        internal object Disable()
        {
            enabled=false; timer.Stop(); zoomUntil=DateTime.MinValue;
            EndDrag(false); spaceHeld=false;
            if(mouseHook!=IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if(keyboardHook!=IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            mouseHook=keyboardHook=IntPtr.Zero; return Status();
        }
        public void Dispose() { Disable(); timer.Dispose(); GC.KeepAlive(mouseProc); GC.KeepAlive(keyboardProc); }
        private NativeCanvasBridge.ViewportInfo Inspect() { return (NativeCanvasBridge.ViewportInfo)NativeCanvasBridge.PanCanvas(app,0,0,true); }
        private static bool Surface(IntPtr window)
        {
            if(window==IntPtr.Zero)return false;
            uint process; GetWindowThreadProcessId(window,out process);
            if(process!=(uint)Process.GetCurrentProcess().Id)return false;
            var name=new StringBuilder(128); GetClassName(window,name,name.Capacity);
            string type=name.ToString();
            if(type!="mdiClass"&&type!="paneClassDC"&&type!="paneClass")return false;
            name.Clear(); GetClassName(GetAncestor(window,2),name,name.Capacity);
            return name.ToString()=="PPTFrameClass";
        }
        private bool TextEditing() { try { return (int)app.ActiveWindow.Selection.Type==3; } catch { return true; } }
        private IntPtr Keyboard(int code, IntPtr wp, IntPtr lp)
        {
            if(code!=0||!enabled)return CallNextHookEx(keyboardHook,code,wp,lp);
            try
            {
                int key=wp.ToInt32(); bool up=(lp.ToInt64()&0x80000000L)!=0;
                if(key==27&&dragging){EndDrag(true);spaceHeld=false;return new IntPtr(1);}
                if(key==32)
                {
                    if(up&&spaceHeld){spaceHeld=false;EndDrag(true);return new IntPtr(1);}
                    if(!up&&spaceHeld)return new IntPtr(1);
                    Point p;
                    if(!up&&!Down(17)&&!Down(18)&&Surface(GetFocus())&&GetCapture()==IntPtr.Zero&&GetCursorPos(out p)&&!TextEditing())
                    {
                        var v=Inspect(); if(v.Contains(p.X,p.Y)){spaceHeld=true;return new IntPtr(1);}
                    }
                }
            }
            catch(Exception ex){lastError=ex.Message;}
            return CallNextHookEx(keyboardHook,code,wp,lp);
        }
        private IntPtr Mouse(int code, IntPtr wp, IntPtr lp)
        {
            if(code!=0||!enabled||handling)return CallNextHookEx(mouseHook,code,wp,lp);
            bool intercepted=false;
            try
            {
                mouseMessages++; int message=wp.ToInt32(); var m=(MouseInfo)Marshal.PtrToStructure(lp,typeof(MouseInfo));
                bool wheel=message==0x20a||message==0x20e;
                if(wheel){wheelSeen++;lastWheelPoint=new[]{m.Point.X,m.Point.Y};lastWheelReason="received";}
                if(dragging)
                {
                    if(message==0x200){pendingX+=m.Point.X-lastX;pendingY+=m.Point.Y-lastY;lastX=m.Point.X;lastY=m.Point.Y;SetCursor(Cursors.SizeAll.Handle);return new IntPtr(1);}
                    if(message==(dragButton==4?0x208:0x202)){EndDrag(true);return new IntPtr(1);}
                }
                if(message!=0x20a&&message!=0x20e&&message!=0x207&&!(message==0x201&&spaceHeld))return CallNextHookEx(mouseHook,code,wp,lp);
                // Capture belongs to native object dragging, menus or the native eyedropper unless we own it.
                if(GetCapture()!=IntPtr.Zero){if(wheel)lastWheelReason="native-capture";return CallNextHookEx(mouseHook,code,wp,lp);}
                if(!Surface(WindowFromPoint(m.Point))){if(wheel)lastWheelReason="outside-editor-window";return CallNextHookEx(mouseHook,code,wp,lp);}
                var view=Inspect(); lastBounds=view;
                if(!view.Contains(m.Point.X,m.Point.Y)){if(wheel)lastWheelReason="outside-canvas-bounds";return CallNextHookEx(mouseHook,code,wp,lp);}
                intercepted=true; handling=true;
                if(message==0x207||message==0x201)
                {
                    if(TextEditing()&&message==0x201){intercepted=false;return CallNextHookEx(mouseHook,code,wp,lp);}
                    zoomUntil=DateTime.MinValue; dragging=true; dragContext=view.Context;dragButton=message==0x207?4:1;
                    pendingX=pendingY=0;lastX=m.Point.X;lastY=m.Point.Y;captured=m.Window;dragStarted=DateTime.UtcNow;SetCapture(captured);SetCursor(Cursors.SizeAll.Handle);
                }
                else
                {
                    wheelEvents++; int delta=(short)(m.Data>>16);lastWheelDelta=delta;lastWheelReason="handled";
                    if(Down(17)) ZoomAt(m.Point.X,m.Point.Y,delta,view);
                    else
                    {
                        zoomUntil=DateTime.MinValue;
                        if(message==0x20e)remainderX-=delta*0.8;
                        else if(Down(16))remainderX+=delta*0.8;
                        else remainderY+=delta*0.8;
                        int dx=(int)remainderX,dy=(int)remainderY;remainderX-=dx;remainderY-=dy;
                        Pan(dx,dy);
                    }
                }
                return new IntPtr(1);
            }
            catch(Exception ex){lastError=ex.Message;return intercepted?new IntPtr(1):CallNextHookEx(mouseHook,code,wp,lp);}
            finally{handling=false;}
        }
        private void Pan(int dx,int dy)
        {
            if(dx==0&&dy==0)return;
            NativeCanvasBridge.PanCanvas(app,Math.Max(-2000,Math.Min(2000,dx)),Math.Max(-2000,Math.Min(2000,dy)));panFrames++;
        }
        private void ZoomAt(int x,int y,int delta,NativeCanvasBridge.ViewportInfo view)
        {
            if(view.ScaleX<=0||view.ScaleY<=0)throw new InvalidOperationException(global::PptEase.Localization.L.T("画布缩放坐标无效。"));
            dynamic w=app.ActiveWindow;
            anchorWorldX=(x-(double)w.PointsToScreenPixelsX(0))/view.ScaleX;
            anchorWorldY=(y-(double)w.PointsToScreenPixelsY(0))/view.ScaleY;
            anchorX=x;anchorY=y;zoomContext=view.Context;
            int zoom=Math.Max(10,Math.Min(400,(int)Math.Round(view.Zoom*Math.Pow(1.1,delta/120.0))));
            if(zoom==view.Zoom)return;
            w.View.Zoom=zoom;zoomEvents++;zoomUntil=DateTime.UtcNow.AddMilliseconds(100);RestoreAnchor();
        }
        private void RestoreAnchor()
        {
            var v=Inspect();if(v.Context!=zoomContext){zoomUntil=DateTime.MinValue;return;}
            dynamic w=app.ActiveWindow;
            int dx=(int)Math.Round(anchorX-anchorWorldX*v.ScaleX-(double)w.PointsToScreenPixelsX(0));
            int dy=(int)Math.Round(anchorY-anchorWorldY*v.ScaleY-(double)w.PointsToScreenPixelsY(0));Pan(dx,dy);
        }
        private void FlushDrag()
        {
            if(pendingX==0&&pendingY==0)return;
            if(Inspect().Context!=dragContext){pendingX=pendingY=0;EndDrag(false);return;}
            int dx=Math.Max(-2000,Math.Min(2000,pendingX)),dy=Math.Max(-2000,Math.Min(2000,pendingY));pendingX-=dx;pendingY-=dy;Pan(dx,dy);
        }
        private void EndDrag(bool flush)
        {
            if(flush&&dragging)try{FlushDrag();}catch(Exception ex){lastError=ex.Message;}
            dragging=false;pendingX=pendingY=0;
            if(captured!=IntPtr.Zero&&GetCapture()==captured)ReleaseCapture();captured=IntPtr.Zero;
        }
        private void Tick(object sender,EventArgs args)
        {
            if(handling||!enabled)return;handling=true;
            try
            {
                if(spaceHeld&&GetAsyncKeyState(32)>=0){spaceHeld=false;EndDrag(true);}
                if(dragging)
                {
                    uint process;GetWindowThreadProcessId(GetForegroundWindow(),out process);
                    if(GetAsyncKeyState(dragButton)>=0||GetCapture()!=captured||!IsWindowVisible(captured)||(process!=(uint)Process.GetCurrentProcess().Id&&DateTime.UtcNow-dragStarted>TimeSpan.FromMilliseconds(200)))EndDrag(true);
                    else FlushDrag();
                }
                if(DateTime.UtcNow<zoomUntil)RestoreAnchor();
            }
            catch(Exception ex){lastError=ex.Message;EndDrag(false);zoomUntil=DateTime.MinValue;}
            finally{handling=false;}
        }
    }
}
