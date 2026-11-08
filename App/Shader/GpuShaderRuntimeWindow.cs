using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PptEase.Shader;

// Geometry/metadata polling never transports pixels. The presentation clock is
// separate so a slow Office COM reply cannot reduce drawing to the polling rate.
public sealed class GpuShaderRuntimeWindow:Window
{
    private readonly Process? process;
    private readonly Func<string,object?,Task<JsonElement>> request;
    private readonly Func<bool> exited;
    private readonly Mutex? mutex;
    private readonly DispatcherTimer poll=new(){Interval=TimeSpan.FromMilliseconds(50)};
    private readonly DispatcherTimer render=new(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(8)};
    private readonly Dictionary<string,Entry> active=new();
    private readonly Dictionary<string,double> resume=new();
    private readonly Dictionary<string,string> failed=new();
    private string scene="";
    private readonly Stopwatch runtimeTime=Stopwatch.StartNew();
    private readonly Queue<object> timingEvents=new();
    private JsonElement? latestHostEvent;
    private void TraceTiming(string stage,double durationMs=0,string? key=null)
    {
        if(timingEvents.Count>=256)timingEvents.Dequeue();
        timingEvents.Enqueue(new{stage,elapsedMs=runtimeTime.Elapsed.TotalMilliseconds,durationMs,key,scene,hostEvent=latestHostEvent,utc=DateTime.UtcNow});
    }
    private bool busy,drawing,closed;
    private int failures;
    private long lastReply=Stopwatch.GetTimestamp();
    private long lastHealth=Stopwatch.GetTimestamp();
    private sealed class Entry:IDisposable
    {
        public required GpuSurfaceWindow Surface;
        public required ShaderComponent Component;
        public required string Revision;
        public readonly Stopwatch Clock=Stopwatch.StartNew();
        public double Offset,LastDraw=-1;
        public bool Ready,FirstSubmitted,ControlsReported;
        public Task? Initialization;
        public readonly Dictionary<int,ShapeSliderWindow> Sliders=new();
        public readonly Dictionary<string,(float Value,DateTime At)> Overrides=new();
        public double Seconds=>Offset+Clock.Elapsed.TotalSeconds;
        public void Dispose(){Clock.Stop();foreach(var slider in Sliders.Values)slider.Dispose();Sliders.Clear();Surface.Dispose();}
    }
    public GpuShaderRuntimeWindow(int pid)
    {
        mutex=new Mutex(true,"Local\\PptEase-ShaderRuntime-"+pid,out bool created);
        if(!created){mutex.Dispose();throw new InvalidOperationException("Shader runtime already running.");}
        process=Process.GetProcessById(pid);var host=new HostClient(targetPid:pid);
        request=(op,payload)=>host.RequestAsync(op,payload,1500);exited=()=>process.HasExited;
        Setup(false);
    }
    private GpuShaderRuntimeWindow(Func<string,object?,Task<JsonElement>> request,Func<bool> exited)
    {
        this.request=request;this.exited=exited;Setup(true);
    }
    private bool testing;
    private Func<Task>? interactionBarrierForTest;
    private void Setup(bool manual)
    {
        testing=manual;
        Width=1;Height=1;Left=-20000;Top=-20000;ShowActivated=false;ShowInTaskbar=false;
        Loaded+=async(_,_)=>{if(!manual){poll.Start();render.Start();await Poll();}};
        poll.Tick+=async(_,_)=>await Poll();render.Tick+=async(_,_)=>await Draw();
        Closed+=(_,_)=>{closed=true;poll.Stop();render.Stop();Clear();process?.Dispose();if(mutex!=null){mutex.ReleaseMutex();mutex.Dispose();}};
    }
    private void Remove(string key)
    {
        if(!active.Remove(key,out var entry))return;
        if(entry.Component.Reentry=="resume")resume[key]=entry.Seconds;
        entry.Dispose();
    }
    private void Clear(){foreach(string key in active.Keys.ToArray())Remove(key);}
    private async Task Poll()
    {
        if(busy||closed)return;busy=true;
        try
        {
            if(exited()){Close();return;}
            var requestWatch=Stopwatch.StartNew();
            var state=await request("shaderScene",new{known=active.ToDictionary(p=>p.Key,p=>p.Value.Revision)});
            if(closed)return;
            lastReply=Stopwatch.GetTimestamp();failures=0;
            if(state.TryGetProperty("HostEvent",out var hostEvent))latestHostEvent=hostEvent.Clone();
            string next=state.GetProperty("Scene").GetString()??"";
            if(next!=scene){
                // Entering/leaving show on the same slide can reuse the live GPU
                // surface. A genuine page/document change still releases it.
                bool samePage=scene.Contains(':')&&next.Contains(':')&&scene.Substring(scene.IndexOf(':')+1)==next.Substring(next.IndexOf(':')+1);
                if(!samePage)Clear();
                else foreach(var entry in active.Values){entry.FirstSubmitted=false;entry.ControlsReported=false;entry.Surface.Suspend();foreach(var slider in entry.Sliders.Values)slider.HideSurface();if(entry.Component.Reentry=="restart"){entry.Offset=0;entry.Clock.Restart();}}
                failed.Clear();scene=next;TraceTiming("scene-reply",requestWatch.Elapsed.TotalMilliseconds);
            }
            poll.Interval=TimeSpan.FromMilliseconds(scene.Length==0?150:50);
            bool transitionHold=state.GetProperty("HoldSeconds").GetDouble()>0;
            var items=state.GetProperty("Components").EnumerateArray().ToArray();
            var keys=items.Select(i=>i.GetProperty("Key").GetString()!).ToHashSet();
            foreach(string key in active.Keys.Where(k=>!keys.Contains(k)).ToArray())Remove(key);
            foreach(var item in items)
            {
                string key=item.GetProperty("Key").GetString()!,revision=item.GetProperty("Revision").GetString()!;
                if(failed.TryGetValue(key,out var bad)&&bad==revision)continue;
                try
                {
                    if(item.TryGetProperty("BindingErrors",out var bindingErrors)&&bindingErrors.GetArrayLength()>0)Log(global::PptEase.Localization.L.T("参数控件不可用：")+string.Join("；",bindingErrors.EnumerateArray().Select(e=>e.GetString())));
                    var placement=item.GetProperty("Placement");
                    if(!placement.GetProperty("Visible").GetBoolean())
                    {
                        if(active.TryGetValue(key,out var suspended)){suspended.Surface.Suspend();foreach(var slider in suspended.Sliders.Values)slider.HideSurface();}
                        Log(placement.GetProperty("Reason").GetString()??global::PptEase.Localization.L.T("静态封面"));continue;
                    }
                    if(active.TryGetValue(key,out var current)&&current.Revision!=revision){
                        if(item.GetProperty("Component").ValueKind==JsonValueKind.Null)continue;
                        var updated=item.GetProperty("Component").Deserialize<ShaderComponent>()!;
                        if(SameProgram(current.Component,updated)){
                            if(JsonSerializer.Serialize(current.Component.Bindings)!=JsonSerializer.Serialize(updated.Bindings)){foreach(var slider in current.Sliders.Values)slider.Dispose();current.Sliders.Clear();}
                            current.Component=updated;current.Revision=revision;
                        }
                        else Remove(key);
                    }
                    if(!active.TryGetValue(key,out var entry))
                    {
                        if(item.GetProperty("Component").ValueKind==JsonValueKind.Null)continue;
                        var component=item.GetProperty("Component").Deserialize<ShaderComponent>()!;
                        var surface=new GpuSurfaceWindow(component);
                        entry=new Entry{Surface=surface,Component=component,Revision=revision};
                        if(component.Reentry=="resume"&&resume.TryGetValue(key,out double time))entry.Offset=time;
                        active.Add(key,entry);TraceTiming("component-observed",0,key);
                    }
                    var values=item.GetProperty("Parameters").Deserialize<Dictionary<string,float>>()!;
                    foreach(var edit in entry.Overrides)if((DateTime.UtcNow-edit.Value.At).TotalMilliseconds<500)values[edit.Key]=edit.Value.Value;
                    if(item.TryGetProperty("Sliders",out var sliders)){
                        var ids=sliders.EnumerateArray().Select(s=>s.GetProperty("TrackId").GetInt32()).ToHashSet();
                        foreach(int id in entry.Sliders.Keys.Where(id=>!ids.Contains(id)).ToArray()){entry.Sliders[id].Dispose();entry.Sliders.Remove(id);}
                        foreach(var spec in sliders.EnumerateArray()){
                            int id=spec.GetProperty("TrackId").GetInt32();string parameter=spec.GetProperty("Parameter").GetString()!;var target=entry;
                            if(!entry.Sliders.TryGetValue(id,out var slider)){
                                string? parameterY=spec.TryGetProperty("ParameterY",out var py)&&py.ValueKind==JsonValueKind.String?py.GetString():null;
                                slider=new ShapeSliderWindow(async (value,valueY)=>{
                                    if(closed||!active.TryGetValue(key,out var live)||!ReferenceEquals(live,target))return;
                                    string interactionScene=scene,interactionRevision=target.Revision;
                                    target.Overrides[parameter]=(value,DateTime.UtcNow);
                                    if(parameterY!=null&&valueY.HasValue)target.Overrides[parameterY]=(valueY.Value,DateTime.UtcNow);
                                    var nextValues=new Dictionary<string,float>(target.Component.Parameters){[parameter]=value};
                                    foreach(var edit in target.Overrides)nextValues[edit.Key]=edit.Value.Value;
                                    if(target.Ready)await target.Surface.SetParametersAsync(nextValues,target.Seconds);
                                    if(interactionBarrierForTest!=null)await interactionBarrierForTest();
                                    if(closed||scene!=interactionScene||target.Revision!=interactionRevision||!active.TryGetValue(key,out live)||!ReferenceEquals(live,target))return;
                                    await request("shaderSliderValue",new{key,revision=interactionRevision,scene=interactionScene,parameter,parameterY,trackId=id,value,valueY});
                                });entry.Sliders[id]=slider;
                            }
                            if(transitionHold)slider.HideSurface();else slider.Update(spec);
                        }
                    }
                    if(!transitionHold&&entry.Sliders.Count>0&&!entry.ControlsReported){entry.ControlsReported=true;TraceTiming("controls-updated",0,key);}
                    if(!entry.Ready){
                        if(transitionHold)entry.Clock.Stop();
                        entry.Initialization??=InitializeEntry(key,entry);
                        continue;
                    }
                    foreach(var edit in entry.Overrides)if((DateTime.UtcNow-edit.Value.At).TotalMilliseconds<500)values[edit.Key]=edit.Value.Value;
                    await entry.Surface.SetParametersAsync(values,entry.Seconds);
                    if(transitionHold){entry.Clock.Stop();entry.Surface.Suspend();}
                    else{entry.Clock.Start();entry.Surface.Place(placement);}
                }
                catch(Exception ex){Remove(key);failed[key]=revision;Log(ex.ToString());}
            }
            if(resume.Count>512)resume.Clear();
        }
        catch(Exception ex){Log(ex.Message);if(++failures>=5)Close();}
        finally{busy=false;}
    }
    // Initialization does not own the polling loop. Only a later fresh scene reply
    // may place the surface; a completed stale compile must never revive an old page.
    private async Task InitializeEntry(string key,Entry entry)
    {
        var watch=Stopwatch.StartNew();
        try{
            await Dispatcher.Yield(DispatcherPriority.Background);
            if(closed||!active.TryGetValue(key,out var current)||!ReferenceEquals(current,entry))return;
            await entry.Surface.InitializeAsync();
            if(closed||!active.TryGetValue(key,out current)||!ReferenceEquals(current,entry)){entry.Dispose();return;}
            entry.Ready=true;TraceTiming("gpu-initialized",watch.Elapsed.TotalMilliseconds,key);
            Log(global::PptEase.Localization.L.T("GPU 已预备 · ")+entry.Component.Backend+global::PptEase.Localization.L.T(" · 初始化 ")+watch.ElapsedMilliseconds+" ms");
        }catch(Exception ex){
            if(!closed&&active.TryGetValue(key,out var current)&&ReferenceEquals(current,entry)){
                Remove(key);failed[key]=entry.Revision;Log(ex.ToString());
            }
        }
    }
    private async Task Draw()
    {
        if(drawing||closed)return;drawing=true;
        try
        {
            bool stale=Stopwatch.GetElapsedTime(lastReply).TotalMilliseconds>500;
            foreach(var pair in active.ToArray())
            {
                var entry=pair.Value;
                if(!entry.Ready)continue;
                // A slow metadata reply must not flash the static cover. Confirmed
                // scene changes, pause, owner loss and disconnect close still hide.
                double seconds=entry.Seconds;
                if(seconds<entry.LastDraw)continue;
                try{await entry.Surface.PresentAsync(seconds);if(entry.Surface.Presented&&!entry.FirstSubmitted){entry.FirstSubmitted=true;TraceTiming("first-present-call",0,pair.Key);}entry.LastDraw=Math.Max(entry.LastDraw+1d/entry.Component.FramesPerSecond,seconds);}
                catch(Exception ex){Remove(pair.Key);failed[pair.Key]=entry.Revision;Log(ex.ToString());}
            }
            if(!testing&&Stopwatch.GetElapsedTime(lastHealth).TotalSeconds>=2)
            {
                lastHealth=Stopwatch.GetTimestamp();
                try{string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PptEase");Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder,"shader-runtime-timings.json"),JsonSerializer.Serialize(new{Scope="Runtime observations; first-present-call is submission, not measured display visibility; controls-updated is window update, not user hit-test confirmation",ProcessId=Environment.ProcessId,Events=timingEvents.ToArray()}));
                    File.WriteAllText(Path.Combine(folder,"shader-gpu-health.json"),CaptureHealth(stale));}catch(Exception ex){TraceTiming("diagnostics-write-failed: "+ex.GetType().Name+": "+ex.Message);}
            }
        }
        finally{drawing=false;}
    }
    private string CaptureHealth(bool stale)=>JsonSerializer.Serialize(new{Utc=DateTime.UtcNow,ProcessId=Environment.ProcessId,Scene=scene,Stale=stale,Surfaces=active.Values.Select(e=>e.Surface.Diagnostics()).ToArray(),Controls=active.Values.SelectMany(e=>e.Sliders.Values).Select(s=>s.Diagnostics()).ToArray()});
    private string lastLog="";
    private readonly Dictionary<string,long> recentLogs=new();
    private bool ShouldRecordLog(string message,long now)
    {
        if(message==lastLog)return false;
        if(recentLogs.TryGetValue(message,out long previous)&&Stopwatch.GetElapsedTime(previous,now).TotalSeconds<2)return false;
        if(!recentLogs.ContainsKey(message)&&recentLogs.Count>=128)
            recentLogs.Remove(recentLogs.MinBy(pair=>pair.Value).Key);
        recentLogs[message]=now;lastLog=message;return true;
    }
    private static bool SameProgram(ShaderComponent before,ShaderComponent after)=>
        before.Backend==after.Backend&&before.Source==after.Source&&before.Width==after.Width&&before.Height==after.Height&&
        before.Parameters.Keys.OrderBy(k=>k).SequenceEqual(after.Parameters.Keys.OrderBy(k=>k))&&
        JsonSerializer.Serialize(before.Textures)==JsonSerializer.Serialize(after.Textures);
    private void Log(string message)
    {
        if(testing||!ShouldRecordLog(message,Stopwatch.GetTimestamp()))return;
        TraceTiming("runtime-status: "+message);
        try{string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PptEase");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"shader-runtime-status.txt"),DateTime.UtcNow.ToString("o")+" GPU-v3 "+message);}catch{}
    }
    public static async Task<object> RunLifecycleTestAsync()
    {
        string current="edit:one",revision="R1";bool include=true,done=false,broken=false,placementVisible=true,secondComponent=false;double hold=0;
        var checks=new Dictionary<string,bool>();var operations=new List<string>();var stress=new List<object>();
        var config=new ShaderComponent{Source=D3DShaderRenderer.DefaultSource,Width=32,Height=32,Reentry="resume",AutoPlay=true};
        config.Parameters["roughness"]=.3f;
        var ownerWindow=new Window{Left=-20000,Top=-20000,Width=32,Height=32,ShowInTaskbar=false,ShowActivated=false};
        IntPtr owner=new System.Windows.Interop.WindowInteropHelper(ownerWindow).EnsureHandle();
        var placement=new{Visible=true,Owner=owner.ToInt64(),X=-20000,Y=-20000,Width=32,Height=32,ClipLeft=0,ClipTop=0,ClipRight=32,ClipBottom=32};
        Task<JsonElement> Request(string op,object? payload)
        {
            operations.Add(op);if(broken)throw new IOException("test disconnect");
            if(op!="shaderScene")throw new InvalidOperationException("Unexpected pixel transport: "+op);
            var sliders=new[]{new{TrackId=20,ThumbId=21,Parameter="roughness",Placement=new{Visible=false}}};
            object[] items=include?new object[]{new{Key="shape1",Revision=revision,Component=config,Parameters=config.Parameters,Placement=placementVisible?(object)placement:new{Visible=false,Reason="test hidden"},Sliders=sliders}}:Array.Empty<object>();
            if(secondComponent)items=items.Concat(new object[]{new{Key="shape2",Revision=revision,Component=config,Parameters=config.Parameters,Placement=placement,Sliders=Array.Empty<object>()}}).ToArray();
            return Task.FromResult(JsonSerializer.SerializeToElement(new{Scene=current,HoldSeconds=hold,Components=items}));
        }
        var runtime=new GpuShaderRuntimeWindow(Request,()=>done);runtime.Show();
        try
        {
            await runtime.Poll();checks["gpuSurfaceCreated"]=runtime.active.Count==1;
            var first=runtime.active["shape1"];if(first.Initialization!=null)await first.Initialization;await runtime.Poll();first.Offset=3;
            var firstSlider=first.Sliders[20];checks["shapeSliderCreated"]=firstSlider.IsAlive;
            var indent=ShaderComponentWindow.IndentSelection("a\r\nb\r\nc",0,6,false);
            var unindent=ShaderComponentWindow.IndentSelection("    a\n\tb",0,8,true);
            checks["editorMultilineIndentPreservesLineEndings"]=indent.Replacement=="    a\r\n    b\r"&&indent.Length==5;
            checks["editorUnindentHandlesSpacesAndTabs"]=unindent.Replacement=="a\nb";
            checks["dragAfterNumericFocus"]=ShapeSliderWindow.TestDragAfterNumericFocus();
            checks["numericFiniteClamp"]=ShapeSliderWindow.TryNumeric("3.7",0,2,out double clamped)&&clamped==2&&!ShapeSliderWindow.TryNumeric("NaN",0,2,out _)&&!ShapeSliderWindow.TryNumeric("bad",0,2,out _)&&ShapeSliderWindow.TryNumeric("-1.2",-2,2,out double negative)&&negative==-1.2;
            checks["checkboxToggleAndHideCancelsPending"]=ShapeSliderWindow.TestCheckboxTransitions();
            var center=ShapeSliderWindow.ClampDisk(.5,.5);var corner=ShapeSliderWindow.ClampDisk(1,1);
            checks["diskCenterAndBoundary"]=center.X==.5&&center.Y==.5&&Math.Abs(Math.Pow(corner.X*2-1,2)+Math.Pow(corner.Y*2-1,2)-1)<.000001;
            checks["shapeSliderMapsAndClamps"]=ShapeSliderWindow.PositionFraction(-30,280,.1)==0&&Math.Abs(ShapeSliderWindow.PositionFraction(140,280,.1)-.5)<.0001&&ShapeSliderWindow.PositionFraction(310,280,.1)==1;
            placementVisible=false;await runtime.Poll();
            long beforeHiddenFrames=first.Surface.Frames;await runtime.Draw();
            checks["hiddenPlacementStopsSubmission"]=!first.Surface.Presented&&first.Surface.Frames==beforeHiddenFrames;
            checks["hiddenPlacementKeepsProgram"]=ReferenceEquals(first,runtime.active["shape1"]);
            placementVisible=true;await runtime.Poll();
            checks["visibilityRestoreReusesProgram"]=ReferenceEquals(first,runtime.active["shape1"])&&first.Ready;
            config.Parameters["roughness"]=.8f;await runtime.Poll();checks["parametersDoNotRebuild"]=ReferenceEquals(first,runtime.active["shape1"]);
            revision="R2";await runtime.Poll();checks["savedParametersKeepSurface"]=ReferenceEquals(first,runtime.active["shape1"])&&runtime.active["shape1"].Revision=="R2";
            broken=true;await runtime.Poll();broken=false;checks["transientHostFailureKeepsSurface"]=ReferenceEquals(first,runtime.active["shape1"]);await runtime.Poll();
            var inputStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inputRelease=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.interactionBarrierForTest=()=>{inputStarted.TrySetResult(true);return inputRelease.Task;};
            var pendingInput=firstSlider.InvokeChangeForTest(.6f);
            await inputStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            current="edit:two";include=false;await runtime.Poll();
            inputRelease.TrySetResult(true);await pendingInput;runtime.interactionBarrierForTest=null;
            checks["leaveCancelsPendingControlWrite"]=!operations.Contains("shaderSliderValue");
            await firstSlider.InvokeChangeForTest(.7f);
            checks["disposedEntryRejectsLateControlCallback"]=!operations.Contains("shaderSliderValue");
            checks["leaveReleases"]=runtime.active.Count==0&&!first.Clock.IsRunning;
            checks["leaveClosesSliderWindow"]=!firstSlider.IsAlive;
            current="edit:one";include=true;await runtime.Poll();checks["resumeClock"]=runtime.active["shape1"].Seconds>=3;
            var editSurface=runtime.active["shape1"];
            editSurface.FirstSubmitted=true;editSurface.ControlsReported=true;
            current="show:one";hold=1;await runtime.Poll();checks["transitionPrewarmsBehindCover"]=runtime.active.Count==1&&!runtime.active["shape1"].Clock.IsRunning;
            checks["editToShowReusesSurface"]=ReferenceEquals(editSurface,runtime.active["shape1"]);
            using(var health=JsonDocument.Parse(runtime.CaptureHealth(false))){checks["healthSnapshotSerializesLiveWindows"]=health.RootElement.GetProperty("ProcessId").GetInt32()==Environment.ProcessId&&health.RootElement.GetProperty("Surfaces").GetArrayLength()==1;}

            long logNow=Stopwatch.GetTimestamp();
            bool firstLog=runtime.ShouldRecordLog("hidden-a",logNow),secondLog=runtime.ShouldRecordLog("hidden-b",logNow);
            checks["alternatingStatusLogsThrottled"]=firstLog&&secondLog&&!runtime.ShouldRecordLog("hidden-a",logNow+1);
            for(int logIndex=0;logIndex<200;logIndex++)runtime.ShouldRecordLog("status-"+logIndex,logNow+logIndex+2);
            checks["statusLogCacheBounded"]=runtime.recentLogs.Count<=128;

            checks["modeSwitchResetsTimingMarkers"]=!editSurface.FirstSubmitted&&!editSurface.ControlsReported;
            hold=0;await runtime.Poll();checks["showStarts"]=runtime.active.Count==1;
            include=false;await runtime.Poll();checks["pauseOrDeleteReleases"]=runtime.active.Count==0;
            include=true;await runtime.Poll();var pendingEntry=runtime.active["shape1"];
            include=false;await runtime.Poll();if(pendingEntry.Initialization!=null)await pendingEntry.Initialization;
            checks["leaveDuringInitializationDoesNotRevive"]=runtime.active.Count==0&&!pendingEntry.Ready;
            var started=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ComponentRenderer.CompileBarrierForTest=()=>{started.TrySetResult(true);return release.Task;};
            include=true;await runtime.Poll();var compilingEntry=runtime.active["shape1"];
            try{
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                include=false;var leaveWatch=Stopwatch.StartNew();await runtime.Poll();
                checks["inFlightLeaveDoesNotWaitForCompiler"]=runtime.active.Count==0&&leaveWatch.ElapsedMilliseconds<250;
                release.TrySetResult(true);if(compilingEntry.Initialization!=null)await compilingEntry.Initialization;
                checks["inFlightCompileReleased"]=ComponentRenderer.Compiling==0&&!compilingEntry.Ready;
            }finally{release.TrySetResult(true);ComponentRenderer.CompileBarrierForTest=null;}
            include=true;secondComponent=true;current="show:multi";await runtime.Poll();
            await Task.WhenAll(runtime.active.Values.Select(e=>e.Initialization??Task.CompletedTask));await runtime.Poll();
            checks["multipleComponentsInitialize"]=runtime.active.Count==2&&runtime.active.Values.All(e=>e.Ready);
            var surviving=runtime.active["shape2"];
            include=false;await runtime.Poll();
            checks["deleteOnePreservesOther"]=runtime.active.Count==1&&ReferenceEquals(surviving,runtime.active["shape2"])&&surviving.Ready;
            secondComponent=false;await runtime.Poll();
            checks["multipleComponentsRelease"]=runtime.active.Count==0&&ComponentRenderer.Compiling==0;
            int baselineDevices=D3DShaderRenderer.LiveInstances;
            using var self=Process.GetCurrentProcess();var stressWatch=Stopwatch.StartNew();var cpuStart=self.TotalProcessorTime;
            for(int cycle=0;cycle<30;cycle++){
                current="show:stress"+cycle;include=true;var initializationWatch=Stopwatch.StartNew();
                await runtime.Poll();var activeEntry=runtime.active["shape1"];
                if(activeEntry.Initialization!=null)await activeEntry.Initialization;
                double initializeMs=initializationWatch.Elapsed.TotalMilliseconds;
                include=false;var cleanupWatch=Stopwatch.StartNew();await runtime.Poll();double cleanupMs=cleanupWatch.Elapsed.TotalMilliseconds;
                self.Refresh();stress.Add(new{cycle,initializeMs,cleanupMs,privateBytes=self.PrivateMemorySize64,handles=self.HandleCount,liveRenderers=D3DShaderRenderer.LiveInstances,compiling=ComponentRenderer.Compiling});
            }
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            self.Refresh();stress.Add(new{settledAfterExplicitGc=true,privateBytes=self.PrivateMemorySize64,handles=self.HandleCount,liveRenderers=D3DShaderRenderer.LiveInstances});
            checks["repeatedPagesReleaseRenderers"]=D3DShaderRenderer.LiveInstances==baselineDevices&&runtime.active.Count==0&&ComponentRenderer.Compiling==0;
            for(int probe=0;probe<10;probe++){using(var renderer=new D3DShaderRenderer(32,32)){}self.Refresh();if(probe==0||probe==9)stress.Add(new{deviceOnlyProbe=probe,deviceRefs=D3DShaderRenderer.LastDeviceReleaseCount,contextRefs=D3DShaderRenderer.LastContextReleaseCount,handles=self.HandleCount,privateBytes=self.PrivateMemorySize64});}
            await Task.Delay(1500);self.Refresh();stress.Add(new{settledAfterDriverWait=true,handles=self.HandleCount,privateBytes=self.PrivateMemorySize64});
            D3DShaderRenderer.ForceWarpForTest=true;
            try { for(int probe=0;probe<10;probe++){using(var renderer=new D3DShaderRenderer(32,32)){}self.Refresh();if(probe==0||probe==9)stress.Add(new{warpOnlyProbe=probe,deviceRefs=D3DShaderRenderer.LastDeviceReleaseCount,contextRefs=D3DShaderRenderer.LastContextReleaseCount,handles=self.HandleCount,privateBytes=self.PrivateMemorySize64});} }
            finally { D3DShaderRenderer.ForceWarpForTest=false; }

            stress.Add(new{summary=true,wallMs=stressWatch.Elapsed.TotalMilliseconds,cpuMs=(self.TotalProcessorTime-cpuStart).TotalMilliseconds});
            include=true;await runtime.Poll();done=true;await runtime.Poll();checks["hostExitReleases"]=runtime.closed&&runtime.active.Count==0;
        }
        finally{if(!runtime.closed)runtime.Close();ownerWindow.Close();}
        done=false;broken=true;var disconnected=new GpuShaderRuntimeWindow(Request,()=>false);disconnected.Show();
        try{for(int i=0;i<5;i++)await disconnected.Poll();checks["disconnectCloses"]=disconnected.closed;}finally{if(!disconnected.closed)disconnected.Close();}
        using(var a=new D3DShaderRenderer(32,32))
        using(var b=new D3DShaderRenderer(32,32))
        using(var c=new D3DShaderRenderer(32,32)) {
            checks["liveDevicesHaveExclusiveLeases"]=D3DShaderRenderer.IdleDeviceCount==0;
            a.Render(0,.3f);b.Render(1,.4f);c.Render(2,.5f);
        }
        checks["idlePoolBounded"]=D3DShaderRenderer.IdleDeviceCount==2;
        D3DShaderRenderer.TrimDevicePool(false,0);
        checks["idlePoolReclaimed"]=D3DShaderRenderer.IdleDeviceCount==0&&D3DShaderRenderer.LastDeviceReleaseCount==0&&D3DShaderRenderer.LastContextReleaseCount==0;
        using(var fresh=new D3DShaderRenderer(32,32)){fresh.Render(0,.3f);}
        checks["deviceWorksAfterIdleReclaim"]=D3DShaderRenderer.IdleDeviceCount==1;
        checks["noPngFrameRequests"]=operations.All(op=>op=="shaderScene");
        return new{passed=checks.Values.All(v=>v),checks,stress,scope="simulated host + real GPU surfaces; init/cleanup timings are NOT PowerPoint first-visible-frame latency"};
    }
}
