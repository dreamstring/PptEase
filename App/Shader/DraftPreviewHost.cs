using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PptEase.Shader;

// Editor-local GPU child window. Never calls the Office host or writes a document.
internal sealed class DraftPreviewHost : HwndHost
{
    private IntPtr root;
    private Session? current;
    private readonly SemaphoreSlim compileGate = new(1, 1);
    private readonly DispatcherTimer frames = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long generation;
    private bool ended, paused, suspended;
    private bool Running=>!ended&&!paused&&!suspended;
    public event Action<string>? Error;
    private sealed class Session : IDisposable
    {
        public IntPtr Window;
        public int OutputWidth,OutputHeight;
        public Dictionary<string,float> Parameters=new();
        public ComponentRenderer? D3D;
        public WebGlFrameRenderer? Web;
        public void Dispose() { D3D?.Dispose(); Web?.Dispose(); if(Window!=IntPtr.Zero)DestroyWindow(Window); Window=IntPtr.Zero; }
    }
    public DraftPreviewHost()
    {
        frames.Tick += (_, _) => { if(current?.D3D==null||!IsVisible||!Running)return;try{current.D3D.Present(clock.Elapsed.TotalSeconds);}catch(Exception ex){frames.Stop();Error?.Invoke(ex.Message);} };
        SizeChanged += (_, _) => Resize();
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        root=CreateWindowEx(0,"STATIC","",0x50000000,0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(root==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        return new HandleRef(this,root);
    }
    protected override void DestroyWindowCore(HandleRef window)
    {
        ended=true;generation++;frames.Stop();current?.Dispose();current=null;DestroyWindow(window.Handle);root=IntPtr.Zero;
    }
    public void InvalidateCompilation()=>generation++;
    public async Task<bool> CompileAsync(ShaderComponent draft)
    {
        long revision=++generation;
        var snapshot=JsonSerializer.Deserialize<ShaderComponent>(JsonSerializer.Serialize(draft))!;
        snapshot.Validate();
        await compileGate.WaitAsync();
        Session? candidate=null;
        try{
            if(ended||revision!=generation||root==IntPtr.Zero)return false;
            candidate=new Session{OutputWidth=snapshot.Width,OutputHeight=snapshot.Height,Parameters=new(snapshot.Parameters),Window=CreateWindowEx(0,"STATIC","Shader candidate",0x40000000,0,0,1,1,root,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero)};
            if(candidate.Window==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            if(snapshot.Backend=="HLSL"){
                candidate.D3D=new ComponentRenderer(snapshot,new Grid());
                await candidate.D3D.InitializeAsync();
                if(ended||revision!=generation)return false;
                candidate.D3D.AttachGpuWindow(candidate.Window);
            }else{
                candidate.Web=new WebGlFrameRenderer(candidate.Window);
                await candidate.Web.CompileAsync(snapshot.Source);await candidate.Web.ConfigureAsync(snapshot);
                if(ended||revision!=generation)return false;
            }
            GetClientRect(root,out var rect);Size(candidate,rect.Right,rect.Bottom);
            if(candidate.D3D!=null)candidate.D3D.Present(clock.Elapsed.TotalSeconds);
            else await candidate.Web!.SetLiveStateAsync(snapshot.Parameters,clock.Elapsed.TotalSeconds,Running);
            if(ended||revision!=generation)return false;
            var previous=current;current=candidate;candidate=null;
            ShowWindow(current.Window,5);previous?.Dispose();if(Running)frames.Start();return true;
        }finally{candidate?.Dispose();compileGate.Release();}
    }
    public async Task UpdateParametersAsync(Dictionary<string,float> values)
    {
        if(current==null||ended)return;
        current.Parameters=new(values);
        current.D3D?.UpdateParameters(values);
        if(!Running&&!suspended)current.D3D?.Present(clock.Elapsed.TotalSeconds);
        if(current?.Web!=null)await current.Web.SetLiveStateAsync(values,clock.Elapsed.TotalSeconds,Running);
    }
    public async Task SetPlaybackAsync(bool pause, bool suspend)
    {
        paused=pause;suspended=suspend;
        if(Running){clock.Start();frames.Start();}else{clock.Stop();frames.Stop();}
        if(current?.Web!=null)await current.Web.SetLiveStateAsync(current.Parameters,clock.Elapsed.TotalSeconds,Running);
    }
    private void Resize(){if(root!=IntPtr.Zero&&current!=null){GetClientRect(root,out var r);Size(current,r.Right,r.Bottom);}}
    internal static (int X,int Y,int Width,int Height) FitPreview(int w,int h,int outputWidth,int outputHeight)
    {
        w=Math.Max(1,w);h=Math.Max(1,h);
        double scale=Math.Min((double)w/outputWidth,(double)h/outputHeight);
        int width=Math.Clamp((int)Math.Round(outputWidth*scale),1,w),height=Math.Clamp((int)Math.Round(outputHeight*scale),1,h);
        return ((w-width)/2,(h-height)/2,width,height);
    }
    private static void Size(Session s,int w,int h)
    {
        var fit=FitPreview(w,h,s.OutputWidth,s.OutputHeight);
        SetWindowPos(s.Window,IntPtr.Zero,fit.X,fit.Y,fit.Width,fit.Height,0x14);
        s.Web?.ResizeNative(fit.Width,fit.Height);
    }
    internal static async Task<object> RunTestAsync()
    {
        var view=new DraftPreviewHost();var owner=new System.Windows.Window{Content=view,Width=320,Height=240,Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false};
        var checks=new Dictionary<string,bool>();owner.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        try{
            var config=new ShaderComponent{Source=D3DShaderRenderer.DefaultSource,Width=64,Height=64};
            checks["validCompile"]=await view.CompileAsync(config);var first=view.current;
            var wide=FitPreview(1000,200,960,600);var tall=FitPreview(200,1000,960,600);
            checks["previewAspectFitsWideAndTall"]=wide==(340,0,320,200)&&tall==(0,437,200,125);
            Size(first!,1000,200);GetClientRect(first!.Window,out var fitted);
            checks["nativePreviewUsesFittedSize"]=fitted.Right==200&&fitted.Bottom==200;
            view.Resize();

            config.Source="invalid shader source";bool rejected=false;
            try{await view.CompileAsync(config);}catch{rejected=true;}
            checks["invalidPreservesProgram"]=rejected&&ReferenceEquals(first,view.current);
            await view.UpdateParametersAsync(new(){["roughness"]=.7f});
            checks["parametersPreserveProgram"]=ReferenceEquals(first,view.current);
            await view.SetPlaybackAsync(true,false);long stoppedFrames=first!.D3D!.PresentedFrames;double stoppedTime=view.clock.Elapsed.TotalSeconds;
            await Task.Delay(60);
            checks["pauseStopsFramesAndTime"]=first.D3D.PresentedFrames==stoppedFrames&&view.clock.Elapsed.TotalSeconds==stoppedTime;
            await view.UpdateParametersAsync(new(){["roughness"]=.2f});
            checks["pausedParameterRefresh"]=first.D3D.PresentedFrames==stoppedFrames+1&&!view.clock.IsRunning;
            await view.SetPlaybackAsync(false,true);await Task.Delay(40);
            checks["suspendStopsTimer"]=!view.frames.IsEnabled&&!view.clock.IsRunning;
            await view.SetPlaybackAsync(false,false);await Task.Delay(60);
            checks["resumeKeepsProgram"]=ReferenceEquals(first,view.current)&&first.D3D.PresentedFrames>stoppedFrames+1;
            config.Source=D3DShaderRenderer.DefaultSource;
            var firstRequest=view.CompileAsync(config);var newerRequest=view.CompileAsync(config);
            bool older=await firstRequest,newer=await newerRequest;
            checks["latestCompileWins"]=!older&&newer;
            config.Backend="GLSL";config.Source="#version 300 es\nprecision highp float;out vec4 color;void main(){color=vec4(0.3,0.1,0.2,1.);}";
            checks["glslDraftCompile"]=await view.CompileAsync(config);var gl=view.current;
            await view.SetPlaybackAsync(true,false);
            var stopped=await gl!.Web!.ReadPlaybackDiagnosticsAsync();await Task.Delay(80);
            var still=await gl.Web.ReadPlaybackDiagnosticsAsync();
            checks["glslPauseStopsFramesAndScheduling"]=!still.GetProperty("scheduled").GetBoolean()&&!still.GetProperty("playing").GetBoolean()&&stopped.GetProperty("renderCount").GetInt64()==still.GetProperty("renderCount").GetInt64()&&stopped.GetProperty("time").GetDouble()==still.GetProperty("time").GetDouble();
            await view.UpdateParametersAsync(new(){["roughness"]=.2f});
            var edited=await gl.Web.ReadPlaybackDiagnosticsAsync();
            checks["glslPausedParameterRefresh"]=edited.GetProperty("renderCount").GetInt64()==still.GetProperty("renderCount").GetInt64()+1&&!edited.GetProperty("scheduled").GetBoolean();
            await view.SetPlaybackAsync(false,false);
            var resumed=await gl.Web.ReadPlaybackDiagnosticsAsync();
            checks["glslResumeSchedulesFrames"]=resumed.GetProperty("playing").GetBoolean()&&resumed.GetProperty("scheduled").GetBoolean();
            config.Source="invalid glsl";rejected=false;try{await view.CompileAsync(config);}catch{rejected=true;}
            checks["glslInvalidPreservesProgram"]=rejected&&ReferenceEquals(gl,view.current);
            string TextureBytes(byte red)
            {
                var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(1,1,96,96,System.Windows.Media.PixelFormats.Bgra32,null,new byte[]{0,0,red,255},4);
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var memory=new System.IO.MemoryStream();encoder.Save(memory);return Convert.ToBase64String(memory.ToArray());
            }
            foreach(string backend in new[]{"HLSL","GLSL"}) {
                config.Backend=backend;
                config.Source=backend=="HLSL"?"float4 main(float4 pos:SV_POSITION):SV_TARGET{return testTexture.Sample(peSampler,pos.xy/64);}":"#version 300 es\nprecision highp float;uniform sampler2D testTexture;out vec4 color;void main(){color=texture(testTexture,gl_FragCoord.xy/64.);}";
                config.Textures=new(){new ShaderTexture{Name="testTexture",Mime="image/png",Data=TextureBytes(200)}};
                bool imported=await view.CompileAsync(config);var textured=view.current;
                config.Textures[0].Data=Convert.ToBase64String(new byte[]{1,2,3,4});rejected=false;
                try{await view.CompileAsync(config);}catch{rejected=true;}
                checks[backend+"CorruptTexturePreservesPreview"]=imported&&rejected&&ReferenceEquals(textured,view.current);
                config.Textures[0].Data=TextureBytes(100);
                checks[backend+"TextureReplacementRecovers"]=await view.CompileAsync(config)&&!ReferenceEquals(textured,view.current);
                var lastValid=view.current;config.Textures.Clear();
                // Missing custom textures must not silently sample the built-in checker.
                {rejected=false;try{await view.CompileAsync(config);}catch{rejected=true;}checks[backend+"MissingTextureRetainsPreview"]=rejected&&ReferenceEquals(lastValid,view.current);}
            }
            config.Textures.Clear();config.Backend="HLSL";config.Source=D3DShaderRenderer.DefaultSource;
            var closing=view.CompileAsync(config);view.Dispose();owner.Close();
            checks["closeCancelsPending"]=!await closing&&view.current==null;
            return new{passed=checks.Values.All(v=>v),checks,scope="real GPU child preview; no Office host is used"};
        }finally{owner.Close();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr h,out Rect r);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr h,int cmd);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
}
