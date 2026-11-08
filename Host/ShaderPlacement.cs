using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PptEase.Host
{
    public sealed partial class Connect
    {
        // Public slide coordinates + the already version-guarded editing viewport.
        // No document mutation and no calls into Office's private rendering pipeline.
        private object ShaderPlacement(ShapeHandle handle, List<ShapeHandle> shapes, bool presenting,bool control=false)
        {
            try
            {
                dynamic shape=handle.Shape;
                if((int)shape.Visible==0)return new{Visible=false,Reason=global::PptEase.Localization.L.T("组件已隐藏，保留原生隐藏状态")};
                if((handle.Depth!=0&&!control) || Math.Abs((double)shape.Rotation)>.01 || (int)shape.HorizontalFlip!=0 || (int)shape.VerticalFlip!=0)
                    return new { Visible=false, Reason=global::PptEase.Localization.L.T("组合、旋转或翻转组件使用静态封面") };
                if(control&&handle.Depth>0){
                    foreach(var ancestor in shapes){
                        if(ancestor.Depth>=handle.Depth||!handle.Key.StartsWith(ancestor.Key+":"))continue;
                        dynamic group=ancestor.Shape;
                        if((int)group.Visible==0||Math.Abs((double)group.Rotation)>.01||(int)group.HorizontalFlip!=0||(int)group.VerticalFlip!=0)return new{Visible=false,Reason=global::PptEase.Localization.L.T("控件组隐藏、旋转或翻转，保留静态外观")};
                    }
                }
                dynamic window=presenting ? app.SlideShowWindows.Item(1) : app.ActiveWindow;
                dynamic slide=window.View.Slide;
                // Office exposes no per-frame animation transform. Never paint over an
                // entrance/exit/motion effect with a stationary live window.
                if(presenting && ((int)slide.TimeLine.MainSequence.Count>0 || (int)slide.TimeLine.InteractiveSequences.Count>0))
                    return new { Visible=false, Reason=global::PptEase.Localization.L.T("含对象动画的放映页使用原生静态封面") };
                double left=(double)shape.Left,top=(double)shape.Top,width=(double)shape.Width,height=(double)shape.Height;
                int z=(int)shape.ZOrderPosition;
                foreach(var other in shapes)
                {
                    if(control)break;
                    if(other.Depth!=0 || other.Key==handle.Key)continue;
                    dynamic upper=other.Shape;
                    if((int)upper.Visible==0 || (int)upper.ZOrderPosition<=z)continue;
                    // Conservative bounds avoid incorrectly covering translucent, text,
                    // rotated or irregular foreground shapes. Native cover remains visible.
                    double ul=(double)upper.Left,ut=(double)upper.Top,uw=(double)upper.Width,uh=(double)upper.Height;
                    double radius=Math.Sqrt(uw*uw+uh*uh)/2;
                    if(Math.Abs((double)upper.Rotation)>.01){ul+=uw/2-radius;ut+=uh/2-radius;uw=uh=2*radius;}
                    if(ul<left+width && ul+uw>left && ut<top+height && ut+uh>top)
                        return new { Visible=false, Reason=global::PptEase.Localization.L.T("上层对象与组件重叠，保留原生层级并显示静态封面") };
                }
                long owner=0;
                double x,y,sx,sy;int cl,ct,cr,cb;
                if(presenting)
                {
                    owner=ResolveShaderWindow(window,true,0,0).ToInt64();
                    ShaderRect rect;ShaderPoint origin=new ShaderPoint();
                    if(!ShaderGetClientRect(new IntPtr(owner),out rect)||!ShaderClientToScreen(new IntPtr(owner),ref origin))throw new InvalidOperationException(global::PptEase.Localization.L.T("无法读取放映窗口"));
                    double sw=(double)window.Presentation.PageSetup.SlideWidth,sh=(double)window.Presentation.PageSetup.SlideHeight;
                    sx=sy=Math.Min((rect.Right-rect.Left)/sw,(rect.Bottom-rect.Top)/sh);
                    x=origin.X+((rect.Right-rect.Left)-sw*sx)/2;y=origin.Y+((rect.Bottom-rect.Top)-sh*sy)/2;
                    cl=(int)Math.Ceiling(x);ct=(int)Math.Ceiling(y);cr=(int)Math.Floor(x+sw*sx);cb=(int)Math.Floor(y+sh*sy);
                }
                else
                {
                    var viewport=(NativeCanvasBridge.ViewportInfo)NativeCanvasBridge.PanCanvas(app,0,0,true);
                    x=(double)window.PointsToScreenPixelsX(0);y=(double)window.PointsToScreenPixelsY(0);
                    sx=viewport.ScaleX;sy=viewport.ScaleY;cl=viewport.Left;ct=viewport.Top;cr=viewport.Right;cb=viewport.Bottom;
                    owner=ResolveShaderWindow(window,false,cl+(cr-cl)/2,ct+(cb-ct)/2).ToInt64();
                }
                int px=(int)Math.Round(x+left*sx),py=(int)Math.Round(y+top*sy),pw=(int)Math.Round(width*sx),ph=(int)Math.Round(height*sy);
                if(pw<1||ph<1||pw>32768||ph>32768)return new {Visible=false,Reason=global::PptEase.Localization.L.T("组件屏幕尺寸无效")};
                if(px>=cr||py>=cb||px+pw<=cl||py+ph<=ct)return new{Visible=false,Reason=global::PptEase.Localization.L.T("组件位于当前视口之外")};
                IntPtr root=ShaderGetAncestor(new IntPtr(owner),2);
                return new { Visible=true, Reason=global::PptEase.Localization.L.T("GPU 窗口原位呈现"), Owner=root==IntPtr.Zero?owner:root.ToInt64(), X=px,Y=py,Width=pw,Height=ph,
                    ClipLeft=Math.Max(0,cl-px),ClipTop=Math.Max(0,ct-py),ClipRight=Math.Min(pw,cr-px),ClipBottom=Math.Min(ph,cb-py) };
            }
            catch(Exception ex){return new {Visible=false,Reason=global::PptEase.Localization.L.T("原位预览暂不可用：")+ex.Message};}
        }
        private static IntPtr ResolveShaderWindow(dynamic window,bool presenting,int centerX,int centerY)
        {
            // Some Office builds expose HWND in the type library but throw when
            // dispatching it. Validate it if available, then use native windows.
            uint pid=(uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                IntPtr candidate=new IntPtr(unchecked((long)(uint)(int)window.HWND));
                uint actual;ShaderWindowProcess(candidate,out actual);
                if(actual==pid&&ShaderIsWindow(candidate))return candidate;
            }
            catch{}
            return FindShaderNativeWindow(pid,presenting,centerX,centerY);
        }
        internal static IntPtr FindShaderNativeWindow(uint pid,bool presenting,int centerX,int centerY)
        {
            var candidates=new List<IntPtr>();
            ShaderEnumWindows(delegate(IntPtr hwnd,IntPtr unused){
                uint actual;ShaderWindowProcess(hwnd,out actual);
                if(actual!=pid||!ShaderWindowVisible(hwnd))return true;
                var name=new System.Text.StringBuilder(256);ShaderClassName(hwnd,name,name.Capacity);
                bool match=presenting?String.Equals(name.ToString(),"screenClass",StringComparison.OrdinalIgnoreCase):name.ToString()=="PPTFrameClass";
                if(match)candidates.Add(hwnd);return true;
            },IntPtr.Zero);
            if(candidates.Count==1)return candidates[0];
            if(!presenting)
            {
                var matching=new List<IntPtr>();
                foreach(IntPtr candidate in candidates){ShaderRect rect;if(ShaderGetWindowRect(candidate,out rect)&&centerX>=rect.Left&&centerX<rect.Right&&centerY>=rect.Top&&centerY<rect.Bottom)matching.Add(candidate);}
                if(matching.Count==1)return matching[0];
                IntPtr active=ShaderGetAncestor(ShaderActiveWindow(),2);
                if(matching.Contains(active))return active;
            }
            throw new InvalidOperationException(global::PptEase.Localization.L.T("无法唯一定位 PowerPoint ")+(presenting?global::PptEase.Localization.L.T("放映"):global::PptEase.Localization.L.T("编辑"))+global::PptEase.Localization.L.T("窗口"));
        }
        private delegate bool ShaderEnumCallback(IntPtr hwnd,IntPtr parameter);
        [DllImport("user32.dll",EntryPoint="EnumWindows")]private static extern bool ShaderEnumWindows(ShaderEnumCallback callback,IntPtr parameter);
        [DllImport("user32.dll",EntryPoint="GetWindowThreadProcessId")]private static extern uint ShaderWindowProcess(IntPtr hwnd,out uint pid);
        [DllImport("user32.dll",EntryPoint="GetClassNameW",CharSet=CharSet.Unicode)]private static extern int ShaderClassName(IntPtr hwnd,System.Text.StringBuilder name,int count);
        [DllImport("user32.dll",EntryPoint="IsWindow")]private static extern bool ShaderIsWindow(IntPtr hwnd);
        [DllImport("user32.dll",EntryPoint="IsWindowVisible")]private static extern bool ShaderWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll",EntryPoint="GetWindowRect")]private static extern bool ShaderGetWindowRect(IntPtr hwnd,out ShaderRect rect);
        [DllImport("user32.dll",EntryPoint="GetActiveWindow")]private static extern IntPtr ShaderActiveWindow();
        [StructLayout(LayoutKind.Sequential)]private struct ShaderRect{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)]private struct ShaderPoint{public int X,Y;}
        [DllImport("user32.dll",EntryPoint="GetClientRect")]private static extern bool ShaderGetClientRect(IntPtr hwnd,out ShaderRect rect);
        [DllImport("user32.dll",EntryPoint="ClientToScreen")]private static extern bool ShaderClientToScreen(IntPtr hwnd,ref ShaderPoint point);
        [DllImport("user32.dll",EntryPoint="GetAncestor")]private static extern IntPtr ShaderGetAncestor(IntPtr hwnd,uint flags);
    }
}
