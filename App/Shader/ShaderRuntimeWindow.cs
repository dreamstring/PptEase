using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PptEase.Shader;
// Hidden rendering host, never a canvas overlay. PowerPoint owns the visible shapes.
public sealed class ShaderRuntimeWindow : Window
{
    private readonly Func<string,object?,Task<JsonElement>> request;
    private readonly Func<bool> hostExited;
    private readonly Process? process;
    private readonly Mutex? mutex;
    private readonly StackPanel surfaces = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly Dictionary<string, Entry> active = new();
    private readonly Dictionary<string,double> savedTimes = new();
    private readonly HashSet<string> failed = new();
    private bool busy, closed;
    private bool suppressDiagnosticLog;
    private string scene = "";
    private int failures;
    private sealed class Entry : IDisposable {
        public required ComponentRenderer Renderer; public required ShaderComponent Component;
        public required string Revision; public readonly Stopwatch Clock = new();
        public double Offset, LastFrame = -1;
        public double Seconds => Offset + Clock.Elapsed.TotalSeconds;
        public void Dispose(){Clock.Stop();Renderer.Dispose();}
    }
    public ShaderRuntimeWindow(int pid)
    {
        mutex = new Mutex(true,"Local\\PptEase-ShaderRuntime-"+pid,out bool created);
        if(!created){mutex.Dispose();throw new InvalidOperationException("Shader runtime already running.");}
        process=Process.GetProcessById(pid);var host=new HostClient(targetPid:pid);
        request=(op,payload)=>host.RequestAsync(op,payload,3000);hostExited=()=>process.HasExited;
        SetupWindow(false);
    }
    private ShaderRuntimeWindow(Func<string,object?,Task<JsonElement>> request,Func<bool> exited)
    {
        this.request=request;hostExited=exited;SetupWindow(true);
    }
    private void SetupWindow(bool manual)
    {
        suppressDiagnosticLog=manual;
        Width=32;Height=32;Left=-20000;Top=-20000;ShowActivated=false;ShowInTaskbar=false;Content=surfaces;
        Loaded+=(_,_)=>{if(!manual)timer.Start();};timer.Tick+=async(_,_)=>await Tick();
        Closed+=(_,_)=>{closed=true;timer.Stop();Clear();process?.Dispose();if(mutex!=null){mutex.ReleaseMutex();mutex.Dispose();}};
    }
    private void Clear(){foreach(var pair in active){if(pair.Value.Component.Reentry=="resume")savedTimes[pair.Key+"|"+pair.Value.Revision]=pair.Value.Seconds;pair.Value.Dispose();}active.Clear();}
    private async Task Tick()
    {
        if(busy||closed)return;busy=true;
        try{
            if(hostExited()){Close();return;}
            var known=active.ToDictionary(p=>p.Key,p=>p.Value.Revision);
            var state=await request("shaderScene",new{known});
            if(closed)return;failures=0;
            string next=state.GetProperty("Scene").GetString()??"";
            if(next!=scene){Clear();scene=next;failed.Clear();if(next.Length==0)savedTimes.Clear();}
            timer.Interval=TimeSpan.FromMilliseconds(next.Length==0?500:66);
            if(state.GetProperty("HoldSeconds").GetDouble()>0)return;
            long epoch=state.GetProperty("Epoch").GetInt64();
            var items=state.GetProperty("Components").EnumerateArray().ToArray();
            var keys=items.Select(i=>i.GetProperty("Key").GetString()!).ToHashSet();
            foreach(string key in active.Keys.Where(k=>!keys.Contains(k)).ToArray()){
                var entry=active[key];if(entry.Component.Reentry=="resume")savedTimes[key+"|"+entry.Revision]=entry.Seconds;
                entry.Dispose();active.Remove(key);
            }
            foreach(var item in items){
                if(closed)return;
                string key=item.GetProperty("Key").GetString()!,revision=item.GetProperty("Revision").GetString()!;
                if(failed.Contains(key+revision))continue;
                try{
                    if(active.TryGetValue(key,out var old)&&old.Revision!=revision){old.Dispose();active.Remove(key);}
                    if(!active.TryGetValue(key,out var entry)){
                        // A mode switch can retain shape keys while clearing local renderers.
                        // Ask for the payload again next tick if the host elided it as known.
                        if(item.GetProperty("Component").ValueKind==JsonValueKind.Null)continue;
                        var component=item.GetProperty("Component").Deserialize<ShaderComponent>()!;
                        var renderer=new ComponentRenderer(component,surfaces);
                        try{await renderer.InitializeAsync();}catch{renderer.Dispose();throw;}
                        if(closed){renderer.Dispose();return;}
                        entry=new Entry{Renderer=renderer,Component=component,Revision=revision};
                        if(component.Reentry=="resume"&&savedTimes.TryGetValue(key+"|"+revision,out double offset))entry.Offset=offset;
                        entry.Clock.Start();active.Add(key,entry);
                    }
                    double seconds=entry.Seconds;
                    if(entry.LastFrame>=0&&seconds-entry.LastFrame<1d/entry.Component.FramesPerSecond)continue;
                    string png=await entry.Renderer.RenderAsync(seconds);
                    if(closed)return;
                    var reply=await request("shaderFrame",new{key,revision,epoch,png});
                    if(reply.GetProperty("Applied").GetBoolean())entry.LastFrame=seconds;
                }catch(Exception ex){failed.Add(key+revision);if(active.Remove(key,out var entry))entry.Dispose();Log(ex.Message);}
            }
            if(savedTimes.Count>512)savedTimes.Clear();
        }catch(Exception ex){Clear();Log(ex.Message);if(++failures>=5)Close();}
        finally{busy=false;}
    }
    public static async Task<object> RunLifecycleTestAsync()
    {
        string scene="edit:one",revision="R1";bool include=true,exited=false,transportBroken=false;double hold=0;int frames=0;
        var config=new ShaderComponent{Source=D3DShaderRenderer.DefaultSource,Width=32,Height=32,AutoPlay=true,Reentry="resume"};
        var checks=new Dictionary<string,bool>();
        Task<JsonElement> Request(string op,object? payload){
            if(transportBroken)throw new IOException("simulated disconnect");
            if(op=="shaderFrame"){
                var frame=JsonSerializer.SerializeToElement(payload);
                if(Convert.FromBase64String(frame.GetProperty("png").GetString()!)[0]!=137)throw new InvalidDataException("Not a PNG frame");
                frames++;return Task.FromResult(JsonSerializer.SerializeToElement(new{Applied=true}));
            }
            object[] items=include?new object[]{new{Key="shape1",Revision=revision,Component=config}}:Array.Empty<object>();
            return Task.FromResult(JsonSerializer.SerializeToElement(new{Scene=scene,Epoch=1,HoldSeconds=hold,Components=items}));
        }
        var window=new ShaderRuntimeWindow(Request,()=>exited);window.Show();
        try{
            await window.Tick();checks["initialFrame"]=frames==1&&window.active.Count==1;
            var first=window.active["shape1"];first.Offset=3;
            scene="edit:two";include=false;await window.Tick();
            checks["leaveReleases"]=window.active.Count==0&&!first.Clock.IsRunning;
            scene="edit:one";include=true;await window.Tick();checks["resumeClock"]=window.active["shape1"].Seconds>=3;
            int before=frames;scene="show:one";hold=1;await window.Tick();checks["transitionHolds"]=frames==before&&window.active.Count==0;
            hold=0;await window.Tick();checks["showStartsAfterHold"]=frames==before+1;
            var old=window.active["shape1"];revision="R2";config.Reentry="restart";await window.Tick();
            checks["revisionReplaces"]=window.active["shape1"]!=old&&!old.Clock.IsRunning&&window.active["shape1"].Seconds<1;
            include=false;await window.Tick();checks["deleteOrPauseReleases"]=window.active.Count==0;
            include=true;await window.Tick();exited=true;await window.Tick();checks["hostExitCloses"]=window.closed&&window.active.Count==0;
        }finally{if(!window.closed)window.Close();}
        exited=false;transportBroken=true;
        var disconnected=new ShaderRuntimeWindow(Request,()=>false);disconnected.Show();
        try{for(int i=0;i<5;i++)await disconnected.Tick();checks["disconnectCloses"]=disconnected.closed;}finally{if(!disconnected.closed)disconnected.Close();}
        return new {passed=checks.Values.All(x=>x),checks,frames,scope="simulated host; real D3D renderer; no PowerPoint automation"};
    }
    private void Log(string message){if(suppressDiagnosticLog)return;try{string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PptEase");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"shader-runtime-status.txt"),DateTime.UtcNow.ToString("o")+" "+message);}catch{}}
}
