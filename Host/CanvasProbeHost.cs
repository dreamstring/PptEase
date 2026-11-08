using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Reflection;

[assembly: ComVisible(false)]
[assembly: AssemblyVersion("0.1.*")]
namespace PptEase.CanvasInputV3
{
    [ComVisible(true), Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface Lifecycle
    {
        [DispId(1)] void OnConnection([In, MarshalAs(UnmanagedType.IDispatch)] object application, int mode, [In, MarshalAs(UnmanagedType.IDispatch)] object addin, [In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType=VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(2)] void OnDisconnection(int mode, [In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType=VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(3)] void OnAddInsUpdate([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType=VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(4)] void OnStartupComplete([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType=VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(5)] void OnBeginShutdown([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType=VarEnum.VT_VARIANT)] ref Array custom);
    }
    [ComVisible(true), Guid("A2D4B783-8D73-4BDC-91F9-E1C622824AD3"), ProgId("PptEase.CanvasInputV3"), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class Connect : Lifecycle
    {
        private dynamic app;
        private Control dispatcher;
        private volatile bool stopped;
        private NamedPipeServerStream current;
        private readonly object gate = new object();
        private CanvasInput input;
        public void OnConnection(object application, int mode, object addin, ref Array custom)
        {
            app = application; stopped = false; dispatcher = new Control(); IntPtr handle = dispatcher.Handle;
            input = new CanvasInput(application);
            new Thread(Serve) { IsBackground=true, Name="PptEase canvas preview" }.Start();
        }
        public void OnDisconnection(int mode, ref Array custom) { Stop(); }
        public void OnBeginShutdown(ref Array custom) { Stop(); }
        public void OnStartupComplete(ref Array custom) { }
        public void OnAddInsUpdate(ref Array custom) { }
        private void Stop()
        {
            if(stopped)return; stopped=true;
            if(input!=null){input.Dispose();input=null;}
            lock(gate) { if(current!=null) { try {current.Dispose();}catch{} } }
            if(dispatcher!=null) dispatcher.Dispose(); dispatcher=null; app=null;
        }
        private void Serve()
        {
            while(!stopped) try
            {
                var sid=WindowsIdentity.GetCurrent().User; var security=new PipeSecurity(); security.SetOwner(sid);security.SetAccessRuleProtection(true,false);security.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
                using(var pipe=new NamedPipeServerStream("PptEaseCanvasV3-"+Process.GetCurrentProcess().Id,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,16384,16384,security))
                {
                    lock(gate) {if(stopped)return; current=pipe;} pipe.WaitForConnection();
                    using(var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true))
                    using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true})
                    {
                        string line; while(!stopped&&(line=ReadLine(reader))!=null)
                        {
                            var json=new JavaScriptSerializer(); object id=null;
                            try
                            {
                                var request=json.Deserialize<Dictionary<string,object>>(line); request.TryGetValue("id",out id);
                                string op=Convert.ToString(request["op"]);
                                object result=null; Exception failure=null; int canceled=0;
                                using(var done=new ManualResetEventSlim(false))
                                {
                                    dispatcher.BeginInvoke((Action)delegate
                                    {
                                        if(Interlocked.CompareExchange(ref canceled,0,0)!=0)return;
                                        try
                                        {
                                            if(op=="status") result=new {IntegrationLoaded=true,HostBuild=Assembly.GetExecutingAssembly().GetName().Version.ToString(),Backend="native-canvas-input-v3",Input=input.Status()};
                                            else if(op=="enableCanvasInput") result=input.Enable();
                                            else if(op=="disableCanvasInput") result=input.Disable();
                                            else if(op=="inspectCanvas") result=PptEase.Host.NativeCanvasBridge.PanCanvas(app,0,0,true);
                                            else if(op=="panCanvas") {var payload=(Dictionary<string,object>)request["payload"];result=PptEase.Host.NativeCanvasBridge.PanCanvas(app,Convert.ToInt32(payload["dx"]),Convert.ToInt32(payload["dy"]));}
                                            else throw new InvalidOperationException("未知画布命令。");
                                        }
                                        catch(Exception ex){failure=ex;}
                                        finally {try{done.Set();}catch(ObjectDisposedException){}}
                                    });
                                    if(!done.Wait(10000)){Interlocked.Exchange(ref canceled,1);throw new TimeoutException("PPT未及时响应；请回到编辑视图后再试。");}
                                }
                                if(failure!=null)throw failure;
                                writer.WriteLine(json.Serialize(new{id,ok=true,result,error=(string)null}));
                            }
                            catch(Exception ex){writer.WriteLine(json.Serialize(new{id,ok=false,result=(object)null,error=ex.Message}));}
                        }
                    }
                }
            }
            catch {if(!stopped)Thread.Sleep(200);}
            finally {lock(gate){current=null;}}
        }
        private static string ReadLine(TextReader reader)
        {
            var b=new StringBuilder();int c;while((c=reader.Read())!=-1){if(c=='\n')return b.ToString();if(c!='\r')b.Append((char)c);if(b.Length>16384)throw new InvalidOperationException("画布请求过长。");}return b.Length==0?null:b.ToString();
        }
    }
}
