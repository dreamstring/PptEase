using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PptEase.Shader;

// An owned, non-activating, input-transparent presentation surface. This is NOT
// an Office visual-tree injection; unsupported composition stays on the native cover.
internal sealed class GpuSurfaceWindow : IDisposable
{
    private readonly ShaderComponent component;
    private ComponentRenderer? renderer;
    private WebGlFrameRenderer? web;
    private readonly WindowProcedure procedure;
    private IntPtr previousProcedure;
    private IntPtr hwnd,owner;
    private bool disposed,webBusy,webPlaying;
    private string parameters="",placement="";
    public bool Presented {get;private set;}
    public long Frames=>renderer?.PresentedFrames??0;
    public long Readbacks=>renderer?.ReadbackFrames??0;
    public object Diagnostics()=>new{Presented,Frames,Readbacks,Hwnd=hwnd.ToInt64(),Owner=owner.ToInt64(),Visible=IsWindowVisible(hwnd),ExtendedStyle=GetWindowLongPtr(hwnd,-20).ToInt64().ToString("X"),Foreground=GetForegroundWindow().ToInt64()};
    public GpuSurfaceWindow(ShaderComponent component)
    {
        this.component=component;
        procedure=InputHook;
    }
    public async Task InitializeAsync()
    {
        hwnd=CreateWindowEx(0x080800A0,"STATIC","PptEase GPU surface",0x80000000,-20000,-20000,component.Width,component.Height,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(hwnd==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        previousProcedure=SetWindowLongPtr(hwnd,-4,Marshal.GetFunctionPointerForDelegate(procedure));
        ConfigureInputTransparency();
        if(component.Backend=="GLSL")
        {
            web=new WebGlFrameRenderer(hwnd);
            await web.CompileAsync(component.Source);await web.ConfigureAsync(component);
            await web.RequireHardwareAsync();
        }
        else
        {
            renderer=new ComponentRenderer(component,new StackPanel());await renderer.InitializeAsync();
            if(disposed)throw new ObjectDisposedException(nameof(GpuSurfaceWindow));
            renderer.AttachGpuWindow(hwnd);
        }
    }
    private IntPtr InputHook(IntPtr window,uint message,IntPtr wp,IntPtr lp)
    {
        if(message==0x84)return new IntPtr(-1); // HTTRANSPARENT
        if(message==0x21)return new IntPtr(3); // MA_NOACTIVATE
        return CallWindowProc(previousProcedure,window,message,wp,lp);
    }
    private void ConfigureInputTransparency()
    {
        // This is a Win32 window: WPF cannot strip WS_EX_LAYERED on style changes.
        SetWindowLongPtr(hwnd,-20,new IntPtr(GetWindowLongPtr(hwnd,-20).ToInt64()|0x080800A0));
        if(!SetLayeredWindowAttributes(hwnd,0,255,2))throw new System.ComponentModel.Win32Exception();
    }
    public async Task SetParametersAsync(Dictionary<string,float> values,double seconds)
    {
        string next=JsonSerializer.Serialize(values);
        if(parameters==next)return;
        parameters=next;component.Parameters=values;renderer?.UpdateParameters(values);
        if(web!=null)await web.SetLiveStateAsync(values,seconds,Presented);
    }
    public void Place(JsonElement state)
    {
        if(!state.GetProperty("Visible").GetBoolean()){Suspend();return;}
        owner=new IntPtr(state.GetProperty("Owner").GetInt64());
        if(!IsWindow(owner)||IsIconic(owner)||!IsWindowVisible(owner)){Suspend();return;}
        int l=state.GetProperty("ClipLeft").GetInt32(),t=state.GetProperty("ClipTop").GetInt32(),r=state.GetProperty("ClipRight").GetInt32(),b=state.GetProperty("ClipBottom").GetInt32();
        if(r<=l||b<=t){Suspend();return;}
        string next=state.GetRawText();
        if(next!=placement)
        {
            SetWindowLongPtr(hwnd,-8,owner);
            bool topmost=(GetWindowLongPtr(owner,-20).ToInt64()&8)!=0;
            SetWindowPos(hwnd,new IntPtr(topmost?-1:-2),state.GetProperty("X").GetInt32(),state.GetProperty("Y").GetInt32(),state.GetProperty("Width").GetInt32(),state.GetProperty("Height").GetInt32(),0x10);
            web?.ResizeNative(state.GetProperty("Width").GetInt32(),state.GetProperty("Height").GetInt32());
            IntPtr region=CreateRectRgn(l,t,r,b);
            if(SetWindowRgn(hwnd,region,true)==0)DeleteObject(region);
            placement=next;
        }
        // Do not leave a live window over an unrelated app or a PPT dialog/menu.
        var foreground=GetForegroundWindow();GetWindowThreadProcessId(foreground,out uint pid);
        bool eligible=foreground==owner || pid==(uint)Environment.ProcessId || IsOurEditor(pid);
        if(!eligible){Suspend();return;}
        if(!Presented){ConfigureInputTransparency();ShowWindow(hwnd,8);Presented=true;}
    }
    private uint cachedForegroundPid;
    private bool cachedEditor;
    private bool IsOurEditor(uint pid)
    {
        if(pid==cachedForegroundPid)return cachedEditor;
        cachedForegroundPid=pid;cachedEditor=false;
        try{using var process=System.Diagnostics.Process.GetProcessById((int)pid);cachedEditor=string.Equals(process.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase);}catch{}
        return cachedEditor;
    }
    public void Suspend(){if(Presented){ShowWindow(hwnd,0);Presented=false;}}
    public async Task PresentAsync(double seconds)
    {
        if(disposed)return;
        // Input is passed through by the native window. A held mouse button
        // (including a parameter slider drag) must not interrupt presentation.
        if(Presented)
        {
            IntPtr foreground=GetForegroundWindow();GetWindowThreadProcessId(foreground,out uint pid);
            if(!IsWindow(owner)||IsIconic(owner)||(foreground!=owner&&pid!=(uint)Environment.ProcessId&&!IsOurEditor(pid)))Suspend();
        }
        if(renderer!=null){if(Presented)renderer.Present(seconds);return;}
        if(web!=null&&!webBusy&&webPlaying!=Presented)
        {
            webBusy=true;try{await web.SetLiveStateAsync(component.Parameters,seconds,Presented);webPlaying=Presented;}finally{webBusy=false;}
        }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;Suspend();renderer?.Dispose();web?.Dispose();
        if(hwnd!=IntPtr.Zero){DestroyWindow(hwnd);hwnd=IntPtr.Zero;}GC.KeepAlive(procedure);
    }
    public static async Task<object> RunSurfaceTestAsync()
    {
        IntPtr foregroundBefore=GetForegroundWindow();
        var config=new ShaderComponent{Source="float4 main(float4 p:SV_POSITION):SV_TARGET{return float4(gain,0,0,1);}",Width=64,Height=64};
        config.Parameters["gain"]=1;
        using var surface=new GpuSurfaceWindow(config);
        await surface.InitializeAsync();
        for(int i=0;i<120;i++)
        {
            await surface.SetParametersAsync(new Dictionary<string,float>{{"gain",i/120f}},i/60d);
            surface.renderer!.Present(i/60d);
        }
        var glConfig=new ShaderComponent{Backend="GLSL",Width=64,Height=64,Source="#version 300 es\nprecision highp float;uniform float gain;out vec4 color;void main(){color=vec4(gain,0.,0.,1.);}"};
        glConfig.Parameters["gain"]=1;
        using var glSurface=new GpuSurfaceWindow(glConfig);
        await glSurface.InitializeAsync();
        await glSurface.SetParametersAsync(new Dictionary<string,float>{{"gain",.25f}},1);
        string webDevice=await glSurface.web!.RequireHardwareAsync();
        bool inputSafe=(GetWindowLongPtr(surface.hwnd,-20).ToInt64()&0x08080020)==0x08080020 && (GetWindowLongPtr(glSurface.hwnd,-20).ToInt64()&0x08080020)==0x08080020;
        bool foregroundPreserved=foregroundBefore==GetForegroundWindow();
        // Read-only hit-test message to our own HWND; no synthetic user input.
        bool hitTransparent=SendMessage(surface.hwnd,0x84,IntPtr.Zero,IntPtr.Zero).ToInt64()==-1;
        return new{passed=surface.Frames==120&&surface.Readbacks==0&&inputSafe&&hitTransparent&&foregroundPreserved,frames=surface.Frames,readbacks=surface.Readbacks,
            hitTransparent,foregroundPreserved,
            extendedStyle=GetWindowLongPtr(surface.hwnd,-20).ToInt64().ToString("X"), inputTransparent=(GetWindowLongPtr(surface.hwnd,-20).ToInt64()&0x08080020)==0x08080020,
            webGlHardware=webDevice,
            scope="native input-transparent Win32 GPU surfaces; no PowerPoint visual acceptance"};
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate IntPtr WindowProcedure(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll",EntryPoint="CallWindowProcW")]private static extern IntPtr CallWindowProc(IntPtr previous,IntPtr hwnd,uint message,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern IntPtr SendMessage(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr h,int i);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr h,int i,IntPtr value);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(IntPtr h,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(IntPtr h,IntPtr region,bool redraw);
}
