using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Drawing;
using System.Web.Script.Serialization;
using PptEase.Shader;

namespace PptEase.Host
{
    public sealed partial class Connect
    {
        private string shaderScene = "";
        private long shaderEventSequence;
        private string shaderEventName="none";
        private DateTime shaderEventUtc;
        private readonly Dictionary<int,SelectionChangeHandler> shaderEventHandlers=new Dictionary<int,SelectionChangeHandler>();
        private void RecordShaderEvent(string name){shaderEventName=name;shaderEventUtc=DateTime.UtcNow;shaderEventSequence++;}
        private void SetupShaderEventTiming()
        {
            // DispIds verified against installed PowerPoint 15.0 interop metadata.
            foreach(var pair in new Dictionary<int,string>{{2011,"show-begin"},{2013,"show-next-slide"},{2014,"show-end"},{2016,"edit-slide-selection"}}){
                string name=pair.Value;SelectionChangeHandler handler=delegate(object ignored){RecordShaderEvent(name);};
                try{System.Runtime.InteropServices.ComEventsHelper.Combine((object)app,EApplication,pair.Key,handler);shaderEventHandlers[pair.Key]=handler;}catch(Exception ex){Log("shader-event-timing "+ex.Message);}
            }
        }
        private void StopShaderEventTiming(){foreach(var pair in shaderEventHandlers)try{System.Runtime.InteropServices.ComEventsHelper.Remove((object)app,EApplication,pair.Key,pair.Value);}catch{}shaderEventHandlers.Clear();}
        private object shaderPresentationAnchor;
        private readonly Dictionary<string, Dictionary<string,float>> shaderLiveParameters = new Dictionary<string, Dictionary<string,float>>();
        // Bound both Studio and slideshow writes without discarding every active value.
        private readonly Dictionary<string,long> shaderParameterWrites = new Dictionary<string,long>();
        private long shaderParameterSequence;
        private void StoreShaderLiveParameters(string key,Dictionary<string,float> values)
        {
            if(!shaderLiveParameters.ContainsKey(key)&&shaderLiveParameters.Count>=128){
                string oldest=shaderParameterWrites.OrderBy(pair=>pair.Value).First().Key;
                shaderLiveParameters.Remove(oldest);shaderParameterWrites.Remove(oldest);
            }
            shaderLiveParameters[key]=values;shaderParameterWrites[key]=++shaderParameterSequence;
        }
        private readonly Dictionary<string, ShaderComponent> shaderComponentCache = new Dictionary<string, ShaderComponent>();
        private long shaderEpoch;
        private DateTime shaderSceneChanged = DateTime.UtcNow;
        private System.Windows.Forms.Timer shaderRuntimeTimer;
        private System.Diagnostics.Process shaderRuntimeProcess;
        private void SetupShaderRuntime()
        {
            SetupShaderEventTiming();
            shaderRuntimeTimer = new System.Windows.Forms.Timer { Interval = 100 };
            shaderRuntimeTimer.Tick += delegate { shaderRuntimeTimer.Interval=30000;try { EnsureShaderRuntime(); } catch(Exception ex) { Log("shader-runtime-start " + ex.Message); } };
            shaderRuntimeTimer.Start();
        }
        private void StopShaderRuntime() { StopShaderEventTiming(); if(shaderRuntimeTimer!=null){shaderRuntimeTimer.Stop();shaderRuntimeTimer.Dispose();shaderRuntimeTimer=null;} if(shaderRuntimeProcess!=null){shaderRuntimeProcess.Dispose();shaderRuntimeProcess=null;} }
        private object EnsureShaderRuntime()
        {
            if(shaderRuntimeProcess!=null&&!shaderRuntimeProcess.HasExited)return new { Running=true };
            string exe=Convert.ToString(Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\Software\\PptEase","ManagerPath",""));
            if(String.IsNullOrEmpty(exe)||!File.Exists(exe))throw new FileNotFoundException(global::PptEase.Localization.L.T("找不到 Shader 后台，请安装/修复新版。"));
            if(shaderRuntimeProcess!=null)shaderRuntimeProcess.Dispose();
            shaderRuntimeProcess=System.Diagnostics.Process.Start(LocalizedStartInfo(exe,"--shader-runtime "+System.Diagnostics.Process.GetCurrentProcess().Id));
            return new { Running=true };
        }
        private List<ShapeHandle> ShaderSceneShapes(out string scene, out bool presenting, out double settleSeconds)
        {
            scene = ""; presenting = false; settleSeconds = 0;
            if (app == null || (int)app.Presentations.Count == 0) {shaderPresentationAnchor=null;return new List<ShapeHandle>();}
            dynamic slide, presentation;
            if ((int)app.SlideShowWindows.Count > 0)
            {
                // Multiple concurrent slide shows must not select an arbitrary audience window.
                if ((int)app.SlideShowWindows.Count != 1) return new List<ShapeHandle>();
                dynamic window = app.SlideShowWindows.Item(1);
                // Paused slides remain visible and interactive. Black/white/end states do not.
                int showState = (int)window.View.State;
                if (showState != 1 && showState != 2) return new List<ShapeHandle>();
                slide = window.View.Slide; presentation = window.Presentation; presenting = true;
                try { settleSeconds = (int)slide.SlideShowTransition.EntryEffect==0?0:Math.Max(0, Math.Min(30, (double)slide.SlideShowTransition.Duration)); } catch { settleSeconds = 0; }
            }
            else
            {
                int view = (int)app.ActiveWindow.ViewType;
                if (view != 1 && view != 9) return new List<ShapeHandle>();
                slide = app.ActiveWindow.View.Slide; presentation = app.ActivePresentation;
            }
            int slideId = (int)slide.SlideID;
            shaderPresentationAnchor=(object)presentation;
            string prefix = nonce + ":" + Identity((object)presentation) + ":" + slideId;
            scene = (presenting ? "show:" : "edit:") + prefix;
            var result = new List<ShapeHandle>(); Walk(slide.Shapes, prefix, slideId, 0, result); return result;
        }
        private List<ShapeHandle> CurrentShaderScene(out bool presenting, out double wait)
        {
            string scene; double duration;
            var shapes = ShaderSceneShapes(out scene, out presenting, out duration);
            if (scene != shaderScene) { shaderScene = scene; shaderEpoch++; shaderSceneChanged = DateTime.UtcNow; shaderComponentCache.Clear(); }
            // Conservative static-frame hold, not a claim that transition completion is observable.
            wait = presenting && duration>0 ? Math.Max(0, duration - (DateTime.UtcNow - shaderSceneChanged).TotalSeconds) : 0;
            return shapes;
        }
        private object ShaderSceneSnapshot(Dictionary<string,object> payload)
        {
            object knownValue; Dictionary<string,object> known = null;
            if(payload.TryGetValue("known",out knownValue))known=knownValue as Dictionary<string,object>;
            bool presenting; double wait;
            var shapes = CurrentShaderScene(out presenting, out wait);
            var components = new List<object>();
            foreach (var handle in shapes)
            {
                dynamic shape = handle.Shape;
                string revision = (string)shape.Tags.Item(ShaderPayload.ManifestKey);
                if (String.IsNullOrEmpty(revision)) continue;
                try {
                    ShaderComponent component;
                    if(!shaderComponentCache.TryGetValue(revision,out component)){
                        component = ShaderSerializer().Deserialize<ShaderComponent>(ShaderPayload.Read(k => (string)shape.Tags.Item(k)));
                        component.Validate();
                        if(shaderComponentCache.Count>=32)shaderComponentCache.Clear();
                        shaderComponentCache[revision]=component;
                    }
                    object knownRevision;
                    bool unchanged=known!=null&&known.TryGetValue(handle.Key,out knownRevision)&&Convert.ToString(knownRevision)==revision;
                    Dictionary<string,float> live;
                    var values=new Dictionary<string,float>(shaderLiveParameters.TryGetValue(handle.Key,out live)?live:component.Parameters);
                    var bindingErrors=new List<string>();
                    var sliders=new List<object>();
                    foreach(var binding in component.Bindings){try{
                        var control=shapes.SingleOrDefault(h=>h.Id==binding.ShapeId);
                        if(control==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("控件已删除"));
                        if(binding.Kind=="ShapePadY")continue;
                        if(binding.Kind=="ShapeSlider"||binding.Kind=="ShapePadX"){
                            var thumb=shapes.SingleOrDefault(h=>h.Id==binding.ThumbShapeId);
                            if(thumb==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("滑块矩形已删除"));
                            if((int)control.Shape.Visible==0||(int)thumb.Shape.Visible==0)throw new InvalidOperationException(global::PptEase.Localization.L.T("控件或游标已隐藏"));
                            var vertical=configPair(component,binding);
                            sliders.Add(new{SurfaceColor=ShaderControlBackground(control.Shape),StylePreset=binding.StylePreset,ShowValue=binding.ShowValue,Checkbox=binding.Checkbox,Circular=binding.Circular,Parameter=binding.Parameter,ParameterY=vertical==null?null:vertical.Parameter,
                                MinimumY=vertical==null?0:vertical.Minimum,MaximumY=vertical==null?1:vertical.Maximum,ValueY=vertical==null?0:values[vertical.Parameter],
                                ThumbRatioY=Math.Max(.02,Math.Min(.5,(double)thumb.Shape.Height/(double)control.Shape.Height)),Minimum=binding.Minimum,Maximum=binding.Maximum,
                                Value=values[binding.Parameter],TrackId=binding.ShapeId,ThumbId=binding.ThumbShapeId,
                                TrackColor=(int)control.Shape.Fill.ForeColor.RGB,ThumbColor=(int)thumb.Shape.Fill.ForeColor.RGB,
                                ThumbRatio=Math.Max(.02,Math.Min(.5,(double)thumb.Shape.Width/(double)control.Shape.Width)),
                                Placement=presenting?ShaderPlacement(control,shapes,true,true):new{Visible=false}});
                            continue;
                        }
                        dynamic ole=control.Shape.OLEFormat.Object;
                        double min=Convert.ToDouble(ole.Min),max=Convert.ToDouble(ole.Max),value=Convert.ToDouble(ole.Value);
                        if(max<=min)throw new InvalidOperationException(global::PptEase.Localization.L.T("控件范围无效"));
                        values[binding.Parameter]=binding.Minimum+(binding.Maximum-binding.Minimum)*(float)Math.Max(0,Math.Min(1,(value-min)/(max-min)));
                    }catch(Exception ex){bindingErrors.Add(binding.Parameter+"："+ex.Message);}}
                    if (component.AutoPlay && (int)shape.Visible != 0) components.Add(new { Key = handle.Key, Revision = revision, Component = unchanged ? null : component,
                        Parameters = values, BindingErrors=bindingErrors,Sliders=sliders,
                        Placement = ShaderPlacement(handle, shapes, presenting) });
                } catch (Exception ex) { Log("shader-component-invalid " + ex.Message); }
            }
            return new { Scene = shaderScene, Epoch = shaderEpoch, HostEvent=new{Sequence=shaderEventSequence,Name=shaderEventName,Utc=shaderEventUtc.ToString("o")}, Presenting = presenting, HoldSeconds = wait, Components = components,InteractionVersion=2 };
        }
        private static ShaderBinding configPair(ShaderComponent component,ShaderBinding binding){return binding.Kind=="ShapePadX"?component.Bindings.FirstOrDefault(b=>b.Kind=="ShapePadY"&&b.ShapeId==binding.ShapeId):null;}
        private object ShaderBindSlider(Dictionary<string,object> payload)
        {
            string key=Convert.ToString(payload["key"]),parameter=Convert.ToString(payload["parameter"]);
            var handle=GetShapes().SingleOrDefault(h=>h.Key==key);if(handle==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("请回到组件所在编辑页。"));
            dynamic shape=handle.Shape;
            var component=ShaderSerializer().Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)shape.Tags.Item(k)));
            if(!component.Parameters.ContainsKey(parameter))throw new InvalidOperationException(global::PptEase.Localization.L.T("请先编译写入此参数。"));
            if(payload.ContainsKey("remove")&&Convert.ToBoolean(payload["remove"])){
                var pairedIds=component.Bindings.Where(b=>b.Parameter==parameter&&(b.Kind=="ShapePadX"||b.Kind=="ShapePadY")).Select(b=>b.ShapeId).ToList();
                component.Bindings.RemoveAll(b=>b.Parameter==parameter||pairedIds.Contains(b.ShapeId));app.StartNewUndoEntry();
                ShaderPayload.Write(ShaderSerializer().Serialize(component),k=>(string)shape.Tags.Item(k),(k,v)=>shape.Tags.Add(k,v),k=>shape.Tags.Delete(k));
                return new{Component=component,ShapeId=0};
            }
            float minimum=Convert.ToSingle(payload["minimum"]),maximum=Convert.ToSingle(payload["maximum"]);
            var binding=new ShaderBinding{Parameter=parameter,ShapeId=1,Minimum=minimum,Maximum=maximum};
            component.Bindings.RemoveAll(b=>b.Parameter==parameter);component.Bindings.Add(binding);component.Validate();
            dynamic slider=null;bool created=false;
            try{
                bool selected=payload.ContainsKey("selected")&&Convert.ToBoolean(payload["selected"]);
                app.StartNewUndoEntry();
                if(selected){var selectedKeys=SelectedKeys();if(selectedKeys.Count!=1)throw new InvalidOperationException(global::PptEase.Localization.L.T("先在PPT中选中一个ActiveX滚动条。"));slider=GetShapes().Single(h=>h.Key==selectedKeys[0]).Shape;}
                else{slider=app.ActiveWindow.View.Slide.Shapes.AddOLEObject(72f,Math.Min((float)app.ActivePresentation.PageSetup.SlideHeight-30f,(float)shape.Top+(float)shape.Height+16f),240f,22f,"Forms.ScrollBar.1","",0,"",0,"",0);created=true;}
                dynamic control=slider.OLEFormat.Object;
                // Read the actual control contract before persisting a binding.
                double oldMin=Convert.ToDouble(control.Min),oldMax=Convert.ToDouble(control.Max);double oldValue=Convert.ToDouble(control.Value);
                if(oldMax<=oldMin)throw new InvalidOperationException(global::PptEase.Localization.L.T("不是可用的滚动条/滑块控件。"));
                if(created){control.Min=0;control.Max=1000;control.SmallChange=10;control.LargeChange=100;control.Orientation=1;control.Value=(int)Math.Round(1000*Math.Max(0,Math.Min(1,(component.Parameters[parameter]-minimum)/(maximum-minimum))));slider.Name="Shader · "+parameter+global::PptEase.Localization.L.T(" 滑块");}
                binding.ShapeId=(int)slider.Id;
                ShaderPayload.Write(ShaderSerializer().Serialize(component),k=>(string)shape.Tags.Item(k),(k,v)=>shape.Tags.Add(k,v),k=>shape.Tags.Delete(k));
                return new{Component=component,ShapeId=binding.ShapeId};
            }catch(Exception ex){if(created&&slider!=null)try{slider.Delete();}catch{}throw new InvalidOperationException(global::PptEase.Localization.L.T("滑块绑定失败（未更改Office信任设置）：")+ex.Message);}
        }
        private object ShaderSetParameters(Dictionary<string,object> payload)
        {
            string key=Convert.ToString(payload["key"]);
            var handle=GetShapes().SingleOrDefault(h=>h.Key==key);
            if(handle==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("组件已删除或页面已切换。"));
            dynamic shape=handle.Shape;
            var component=ShaderSerializer().Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)shape.Tags.Item(k)));
            var values=ShaderSerializer().Deserialize<Dictionary<string,float>>(ShaderSerializer().Serialize(payload["parameters"]));
            if(!values.Keys.OrderBy(k=>k).SequenceEqual(component.Parameters.Keys.OrderBy(k=>k)))throw new InvalidOperationException(global::PptEase.Localization.L.T("新增参数需要先编译写入；实时调整只修改现有参数值。"));
            component.Parameters=values;component.Validate();
            StoreShaderLiveParameters(key,values);
            return new { Applied=true, Persisted=false };
        }
        public void InsertShaderComponent(object control) { OpenShaderEditor(false); }
        public void InsertShapeSlider(object control){RibbonAction(delegate{InsertUnboundShaderControl("slider");});}
        public void InsertShapePad(object control){RibbonAction(delegate{InsertUnboundShaderControl("pad");});}
        public void InsertShapeDisk(object control){RibbonAction(delegate{InsertUnboundShaderControl("disk");});}
        public void InsertShapeCheckbox(object control){RibbonAction(delegate{InsertUnboundShaderControl("checkbox");});}
        private void InsertUnboundShaderControl(string kind)
        {
            // Ordinary editable shapes; no ActiveX, macros or Shader selection required.
            dynamic slide=app.ActiveWindow.View.Slide;
            bool disk=kind=="disk",check=kind=="checkbox",pad=disk||kind=="pad";
            dynamic track=null,thumb=null;
            app.StartNewUndoEntry();
            try{
                float width=pad?180f:check?28f:240f,height=pad?180f:28f;
                track=slide.Shapes.AddShape(disk?9:1,72f,72f,width,height);
                track.Fill.Solid();track.Fill.ForeColor.RGB=0x3B291E;track.Line.Visible=0;
                track.Name=disk?global::PptEase.Localization.L.T("交互圆盘"):check?global::PptEase.Localization.L.T("交互复选框"):global::PptEase.Localization.L.T("交互滑块");
                thumb=slide.Shapes.AddShape(disk?9:1,72f+(width-16f)/2,72f+(height-16f)/2,16f,16f);
                thumb.Fill.Solid();thumb.Fill.ForeColor.RGB=check?0x3B291E:0xE5464F;thumb.Line.Visible=0;
                thumb.Name=check?global::PptEase.Localization.L.T("复选框标记"):global::PptEase.Localization.L.T("交互游标");
                string pair=Guid.NewGuid().ToString("N");
                track.Tags.Add("PPTEASE_CONTROL_PAIR",pair);thumb.Tags.Add("PPTEASE_CONTROL_PAIR",pair);
                track.Tags.Add("PPTEASE_CONTROL_KIND",kind);thumb.Tags.Add("PPTEASE_CONTROL_KIND",kind);
                StyleShaderControl(track,thumb,pad,disk,check,0);if(check)thumb.TextFrame.TextRange.Text="";
                GroupShaderControl(slide,track,thumb).Select();
            }catch{
                if(thumb!=null)try{thumb.Delete();}catch{}
                if(track!=null)try{track.Delete();}catch{}
                throw;
            }
        }
        private static dynamic GroupShaderControl(dynamic slide,dynamic track,dynamic thumb){
            dynamic parent=null;try{parent=track.ParentGroup;}catch{}
            if(parent!=null)return parent;
            object[] names=new object[]{(string)track.Name,(string)thumb.Name};
            dynamic group=slide.Shapes.Range(names).Group();group.Name=global::PptEase.Localization.L.T("交互控件 · ")+(string)track.Tags.Item("PPTEASE_CONTROL_KIND");
            group.Tags.Add("PPTEASE_CONTROL_GROUP","1");group.LockAspectRatio=-1;return group;
        }
        private static int? ShaderControlBackground(dynamic shape){
            try{dynamic fill=shape.Parent.Background.Fill;if((int)fill.Type==1)return (int)fill.ForeColor.RGB;}catch{}
            return null; // Non-solid slides use the control's own opaque surface.
        }
        private static void StyleShaderControl(dynamic track,dynamic thumb,bool pad,bool disk,bool check,int preset)
        {
            // Transparent high-resolution track artwork stays embedded in the ordinary PPT shape.
            int width=960,height=pad?960:check?112:112;
            string file=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"pptease-control-"+Guid.NewGuid().ToString("N")+".png");
            var background=preset==1?System.Drawing.Color.FromArgb(244,244,245):System.Drawing.Color.FromArgb(39,39,42);
            var line=preset==1?System.Drawing.Color.FromArgb(212,212,216):System.Drawing.Color.FromArgb(82,82,91);
            var accent=preset==2?System.Drawing.Color.Silver:preset==3?System.Drawing.Color.Goldenrod:System.Drawing.Color.FromArgb(59,130,246);
            if(check)width=112;
            try{
                using(var bitmap=new System.Drawing.Bitmap(width,height))using(var g=System.Drawing.Graphics.FromImage(bitmap))using(var fill=new System.Drawing.SolidBrush(background))using(var pen=new System.Drawing.Pen(line,pad?5:2)){
                    g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    if(disk){g.FillEllipse(fill,4,4,width-8,height-8);g.DrawEllipse(pen,4,4,width-8,height-8);}
                    else if(pad||check){g.FillRectangle(fill,2,2,width-4,height-4);g.DrawRectangle(pen,2,2,width-4,height-4);}
                    else using(var rail=new System.Drawing.Pen(line,12)){rail.StartCap=rail.EndCap=System.Drawing.Drawing2D.LineCap.Round;g.DrawLine(rail,8,height/2,width-8,height/2);}
                    if(pad){g.DrawLine(pen,width*.15f,height/2,width*.85f,height/2);g.DrawLine(pen,width/2,height*.15f,width/2,height*.85f);}
                    bitmap.Save(file,System.Drawing.Imaging.ImageFormat.Png);
                }
                track.Fill.UserPicture(file);track.Line.Visible=0;
                thumb.Line.Visible=-1;thumb.Line.ForeColor.RGB=0xD8D4D4;thumb.Line.Weight=1f;
                thumb.Fill.Solid();thumb.Fill.ForeColor.RGB=accent.R|(accent.G<<8)|(accent.B<<16);
                thumb.Shadow.Visible=-1;thumb.Shadow.Blur=3f;thumb.Shadow.OffsetX=0f;thumb.Shadow.OffsetY=1f;thumb.Shadow.Transparency=.65f;
                if(check){thumb.Fill.Transparency=1f;thumb.Line.Visible=0;thumb.Shadow.Visible=0;thumb.TextFrame.MarginLeft=0;thumb.TextFrame.MarginRight=0;thumb.TextFrame.MarginTop=0;thumb.TextFrame.MarginBottom=0;thumb.TextFrame.TextRange.Text="✓";thumb.TextFrame.TextRange.Font.Size=12f;thumb.TextFrame.TextRange.Font.Color.RGB=preset==1?0:0xFFFFFF;}
                if(!check){thumb.AutoShapeType=9;thumb.Height=thumb.Width;thumb.Top=(float)track.Top+((float)track.Height-(float)thumb.Height)/2;}
            }finally{if(System.IO.File.Exists(file))System.IO.File.Delete(file);}
        }
        private object ShaderShapeSlider(Dictionary<string,object> payload)
        {
            string key=Convert.ToString(payload["key"]),parameter=Convert.ToString(payload["parameter"]);
            var shapes=GetShapes();var handle=shapes.SingleOrDefault(h=>h.Key==key);if(handle==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("请回到Shader所在编辑页。"));
            dynamic shape=handle.Shape;
            var config=ShaderSerializer().Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)shape.Tags.Item(k)));
            string parameterY=payload.ContainsKey("parameterY")?Convert.ToString(payload["parameterY"]):null;
            bool pad=!String.IsNullOrEmpty(parameterY);
            bool circular=pad&&!(payload.ContainsKey("square")&&Convert.ToBoolean(payload["square"]));
            bool checkbox=payload.ContainsKey("checkbox")&&Convert.ToBoolean(payload["checkbox"]);
            if(checkbox&&pad)throw new InvalidOperationException(global::PptEase.Localization.L.T("复选框只能绑定一个参数。"));
            if(pad&&(parameterY==parameter||!config.Parameters.ContainsKey(parameterY)))throw new InvalidOperationException(global::PptEase.Localization.L.T("圆盘需要两个不同参数。"));
            var binding=new ShaderBinding{StylePreset=payload.ContainsKey("stylePreset")?Math.Max(0,Math.Min(3,Convert.ToInt32(payload["stylePreset"]))):0,ShowValue=payload.ContainsKey("showValue")&&Convert.ToBoolean(payload["showValue"]),Checkbox=checkbox,Circular=circular,Kind=pad?"ShapePadX":"ShapeSlider",Parameter=parameter,ShapeId=1,Minimum=Convert.ToSingle(payload["minimum"]),Maximum=Convert.ToSingle(payload["maximum"])};
            var previousBindings=config.Bindings.ToList();
            var oldPairIds=config.Bindings.Where(b=>(b.Parameter==parameter||(pad&&b.Parameter==parameterY))&&(b.Kind=="ShapePadX"||b.Kind=="ShapePadY")).Select(b=>b.ShapeId).ToList();
            config.Bindings.RemoveAll(b=>b.Parameter==parameter||(pad&&b.Parameter==parameterY)||oldPairIds.Contains(b.ShapeId));config.Bindings.Add(binding);config.Validate();
            bool selected=payload.ContainsKey("selected")&&Convert.ToBoolean(payload["selected"]);dynamic track=null,thumb=null;
            app.StartNewUndoEntry();
            try{
                if(selected){var keys=SelectedKeys();var rectangles=shapes.Where(h=>keys.Contains(h.Key)&&h.Depth==0).OrderByDescending(h=>(float)h.Shape.Width).ToList();
                    if(rectangles.Count==1&&(int)rectangles[0].Shape.Type==6){
                        string prefix=rectangles[0].Key+":";
                        var members=shapes.Where(h=>h.Key.StartsWith(prefix)&&!String.IsNullOrEmpty((string)h.Shape.Tags.Item("PPTEASE_CONTROL_PAIR"))).ToList();
                        if(members.Count!=2)throw new InvalidOperationException(global::PptEase.Localization.L.T("请选择一个完整控件组，不能同时绑定多个控件。"));
                        rectangles=members.OrderByDescending(h=>(float)h.Shape.Width).ToList();
                    }
                    if(rectangles.Count==1){
                        string pair=(string)rectangles[0].Shape.Tags.Item("PPTEASE_CONTROL_PAIR");
                        if(!String.IsNullOrEmpty(pair)){
                            var matches=shapes.Where(h=>(string)h.Shape.Tags.Item("PPTEASE_CONTROL_PAIR")==pair).OrderByDescending(h=>(float)h.Shape.Width).ToList();
                            if(matches.Count!=2)throw new InvalidOperationException(global::PptEase.Localization.L.T("控件配对不完整或存在复制副本，请明确选中轨道和游标后重新绑定。"));
                            rectangles=matches;
                        }else{
                            int selectedId=rectangles[0].Id;
                            var existing=previousBindings.FirstOrDefault(b=>b.ShapeId==selectedId||b.ThumbShapeId==selectedId);
                            if(existing!=null)rectangles=shapes.Where(h=>(h.Id==existing.ShapeId||h.Id==existing.ThumbShapeId)).OrderByDescending(h=>(float)h.Shape.Width).ToList();
                        }
                    }
                    if(rectangles.Count!=2)throw new InvalidOperationException(circular?global::PptEase.Localization.L.T("请选择两个未组合的圆形：大圆为圆盘，小圆为游标。"):global::PptEase.Localization.L.T("请在PPT中选中两个未组合的矩形：宽的是轨道，窄的是滑块。"));
                    track=rectangles[0].Shape;thumb=rectangles[1].Shape;
                    string taggedKind=(string)track.Tags.Item("PPTEASE_CONTROL_KIND");
                    string expectedKind=circular?"disk":pad?"pad":checkbox?"checkbox":"slider";
                    if(!String.IsNullOrEmpty(taggedKind)&&taggedKind!=expectedKind)throw new InvalidOperationException(global::PptEase.Localization.L.T("所选控件类型与编辑器不一致，请选择对应的圆盘、滑块或复选框类型。"));
                    foreach(var item in rectangles)if((int)item.Shape.Type!=1||((int)item.Shape.AutoShapeType!=(circular?9:1)&&!((int)item.Shape.Id==(int)thumb.Id&&(int)item.Shape.AutoShapeType==9))||!String.IsNullOrEmpty((string)item.Shape.Tags.Item(ShaderPayload.ManifestKey))||Math.Abs((double)item.Shape.Rotation)>.01)throw new InvalidOperationException(circular?global::PptEase.Localization.L.T("只能绑定未旋转的普通圆形。"):global::PptEase.Localization.L.T("只能绑定未旋转的普通矩形。"));
                    if(circular&&rectangles.Any(item=>Math.Abs((float)item.Shape.Width-(float)item.Shape.Height)>.5f))throw new InvalidOperationException(global::PptEase.Localization.L.T("圆盘和游标必须是正圆，请先调整宽高相等。"));
                }else{
                    float top=Math.Min((float)app.ActivePresentation.PageSetup.SlideHeight-34f,(float)shape.Top+(float)shape.Height+18f);
                    if(pad)top=Math.Max(24f,Math.Min(top,(float)app.ActivePresentation.PageSetup.SlideHeight-210f));
                    track=app.ActiveWindow.View.Slide.Shapes.AddShape(circular?9:1,72f,top,pad?180f:280f,pad?180f:24f);track.Fill.Solid();track.Fill.ForeColor.RGB=0xE4DED8;track.Line.Visible=0;track.Name=global::PptEase.Localization.L.T("参数轨道 · ")+parameter;
                    thumb=app.ActiveWindow.View.Slide.Shapes.AddShape(circular?9:1,pad?152f:202f,top+(pad?80f:0f),20f,pad?20f:24f);thumb.Fill.Solid();thumb.Fill.ForeColor.RGB=0xE5464F;thumb.Line.Visible=0;thumb.Name=global::PptEase.Localization.L.T("参数滑块 · ")+parameter;
                }
                if(checkbox&&!selected){
                    track.Width=28f;track.Height=28f;track.Name=global::PptEase.Localization.L.T("参数复选框 · ")+parameter;
                    thumb.Left=(float)track.Left+7f;thumb.Top=(float)track.Top+7f;thumb.Width=14f;thumb.Height=14f;thumb.Name=global::PptEase.Localization.L.T("复选框标记 · ")+parameter;
                    thumb.Fill.ForeColor.RGB=(int)track.Fill.ForeColor.RGB;
                    thumb.TextFrame.TextRange.Text=config.Parameters[parameter]>=(binding.Minimum+binding.Maximum)/2?"✓":"";
                    thumb.TextFrame.MarginLeft=0;thumb.TextFrame.MarginRight=0;thumb.TextFrame.MarginTop=0;thumb.TextFrame.MarginBottom=0;
                    thumb.TextFrame.TextRange.Font.Size=12f;thumb.TextFrame.TextRange.Font.Color.RGB=0xFFFFFF;
                }
                if((float)thumb.Width>=(float)track.Width||(float)track.Width<=0||(float)track.Height<=0)throw new InvalidOperationException(global::PptEase.Localization.L.T("轨道必须比滑块宽。"));
                binding.ShapeId=(int)track.Id;binding.ThumbShapeId=(int)thumb.Id;
                foreach(var other in shapes.Where(h=>h.Key!=key)){
                    string manifest=(string)other.Shape.Tags.Item(ShaderPayload.ManifestKey);
                    if(String.IsNullOrEmpty(manifest))continue;
                    dynamic otherShape=other.Shape;
                    var otherConfig=ShaderSerializer().Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)otherShape.Tags.Item(k)));
                    if(otherConfig.Bindings.Any(b=>b.ShapeId==binding.ShapeId||b.ShapeId==binding.ThumbShapeId||b.ThumbShapeId==binding.ShapeId||b.ThumbShapeId==binding.ThumbShapeId))
                        throw new InvalidOperationException(global::PptEase.Localization.L.T("所选控件已绑定到另一 Shader，请先在原组件解除绑定，或插入新控件。"));
                }
                if(pad)config.Bindings.Add(new ShaderBinding{Kind="ShapePadY",Circular=circular,Parameter=parameterY,ShapeId=binding.ShapeId,ThumbShapeId=binding.ThumbShapeId,Minimum=Convert.ToSingle(payload["minimumY"]),Maximum=Convert.ToSingle(payload["maximumY"])});
                // Rebinding the same control replaces its old target parameters, including both disk axes.
                config.Bindings.RemoveAll(b=>b!=binding&&b.ShapeId==binding.ShapeId&&!(pad&&b.Kind=="ShapePadY"&&b.Parameter==parameterY));
                config.Validate();
                StyleShaderControl(track,thumb,pad,circular,checkbox,binding.StylePreset);if(checkbox)thumb.TextFrame.TextRange.Text=config.Parameters[parameter]>=(binding.Minimum+binding.Maximum)/2?"✓":"";
                string controlPair=Guid.NewGuid().ToString("N");
                track.Tags.Add("PPTEASE_CONTROL_PAIR",controlPair);thumb.Tags.Add("PPTEASE_CONTROL_PAIR",controlPair);
                string controlKind=circular?"disk":pad?"pad":checkbox?"checkbox":"slider";
                track.Tags.Add("PPTEASE_CONTROL_KIND",controlKind);thumb.Tags.Add("PPTEASE_CONTROL_KIND",controlKind);
                ShaderPayload.Write(ShaderSerializer().Serialize(config),k=>(string)shape.Tags.Item(k),(k,v)=>shape.Tags.Add(k,v),k=>shape.Tags.Delete(k));
                GroupShaderControl(app.ActiveWindow.View.Slide,track,thumb);
                return new{Component=config,TrackId=binding.ShapeId,ThumbId=binding.ThumbShapeId};
            }catch{if(!selected){if(thumb!=null)try{thumb.Delete();}catch{}if(track!=null)try{track.Delete();}catch{}}throw;}
        }
        private object ShaderSliderValue(Dictionary<string,object> payload)
        {
            bool presenting;double wait;var shapes=CurrentShaderScene(out presenting,out wait);
            if(!presenting||wait>0||Convert.ToString(payload["scene"])!=shaderScene)throw new InvalidOperationException(global::PptEase.Localization.L.T("放映页已变化。"));
            var handle=shapes.SingleOrDefault(h=>h.Key==Convert.ToString(payload["key"]));if(handle==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("Shader已不在当前页。"));
            dynamic shape=handle.Shape;string revision=(string)shape.Tags.Item(ShaderPayload.ManifestKey);
            if(revision!=Convert.ToString(payload["revision"]))throw new InvalidOperationException(global::PptEase.Localization.L.T("组件配置已变化。"));
            ShaderComponent config;if(!shaderComponentCache.TryGetValue(revision,out config))config=ShaderSerializer().Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)shape.Tags.Item(k)));
            string parameter=Convert.ToString(payload["parameter"]);int track=Convert.ToInt32(payload["trackId"]);
            var binding=config.Bindings.SingleOrDefault(b=>(b.Kind=="ShapeSlider"||b.Kind=="ShapePadX")&&b.Parameter==parameter&&b.ShapeId==track);
            if(binding==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("滑块绑定已失效。"));
            float value=Convert.ToSingle(payload["value"]);if(Single.IsNaN(value)||Single.IsInfinity(value))throw new InvalidOperationException(global::PptEase.Localization.L.T("参数无效。"));
            Dictionary<string,float> live;var values=new Dictionary<string,float>(shaderLiveParameters.TryGetValue(handle.Key,out live)?live:config.Parameters);
            values[parameter]=Math.Max(binding.Minimum,Math.Min(binding.Maximum,value));
            if(payload.ContainsKey("parameterY")&&payload["parameterY"]!=null){var vertical=configPair(config,binding);if(vertical==null||vertical.Parameter!=Convert.ToString(payload["parameterY"]))throw new InvalidOperationException(global::PptEase.Localization.L.T("二维绑定已变化。"));float v=Convert.ToSingle(payload["valueY"]);if(Single.IsNaN(v)||Single.IsInfinity(v))throw new InvalidOperationException(global::PptEase.Localization.L.T("纵向参数无效。"));values[vertical.Parameter]=Math.Max(vertical.Minimum,Math.Min(vertical.Maximum,v));}
            StoreShaderLiveParameters(handle.Key,values);
            return new{Applied=true,Value=values[parameter]};
        }
        public void EditShaderComponent(object control) { OpenShaderEditor(true); }
        private void OpenShaderEditor(bool selected)
        {
            RibbonAction(delegate
            {
                string key = null;
                if (selected)
                {
                    var keys = SelectedKeys();
                    if (keys.Count != 1) throw new InvalidOperationException(global::PptEase.Localization.L.T("请选中一个 Shader 组件。"));
                    key = keys[0];
                    ShaderGet(new Dictionary<string, object> { { "key", key } });
                }
                string exe = Convert.ToString(Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\Software\\PptEase", "ManagerPath", ""));
                if (String.IsNullOrEmpty(exe) || !File.Exists(exe)) throw new FileNotFoundException(global::PptEase.Localization.L.T("请先安装或修复新版 PPT 增强。"));
                string arguments = "--shader-component " + System.Diagnostics.Process.GetCurrentProcess().Id + (key == null ? "" : " " + key);
                System.Diagnostics.Process.Start(LocalizedStartInfo(exe, arguments));
            });
        }
        private static JavaScriptSerializer ShaderSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = ShaderPayload.MaximumBytes, RecursionLimit = 32 };
        }

        private object ShaderGet(Dictionary<string, object> payload)
        {
            string key = Convert.ToString(payload["key"]);
            ShapeHandle handle = GetShapes().SingleOrDefault(h => h.Key == key);
            if (handle == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("组件已删除或页面已切换，请重新选择。"));
            dynamic shape = handle.Shape;
            string json = ShaderPayload.Read(k => (string)shape.Tags.Item(k));
            if (json == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("所选对象不是 Shader 组件。"));
            ShaderComponent component = ShaderSerializer().Deserialize<ShaderComponent>(json);
            component.Validate();
            return new { Key = handle.Key, Component = component, Left = (float)shape.Left, Top = (float)shape.Top,
                Width = (float)shape.Width, Height = (float)shape.Height, Rotation = (float)shape.Rotation };
        }

        private object ShaderFrame(Dictionary<string, object> payload)
        {
            string key = Convert.ToString(payload["key"]);
            bool presenting; double wait;
            ShapeHandle handle = CurrentShaderScene(out presenting, out wait).SingleOrDefault(h => h.Key == key);
            object epoch;
            if (payload.TryGetValue("epoch", out epoch) && (Convert.ToInt64(epoch) != shaderEpoch || wait > 0)) return new { Applied = false, Reason = "scene-changed-or-transition", Milliseconds = 0 };
            if (handle == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("组件已删除或页面已切换，已拒绝过期帧。"));
            dynamic shape = handle.Shape;
            object revision;
            if (payload.TryGetValue("revision", out revision) && Convert.ToString(revision) != (string)shape.Tags.Item(ShaderPayload.ManifestKey)) return new { Applied = false, Reason = "component-changed", Milliseconds = 0 };
            if (String.IsNullOrEmpty((string)shape.Tags.Item(ShaderPayload.ManifestKey))) throw new InvalidOperationException(global::PptEase.Localization.L.T("目标不是 Shader 组件。"));
            string encoded = Convert.ToString(payload["png"]);
            if (encoded.Length > 8 * 1024 * 1024) throw new InvalidDataException(global::PptEase.Localization.L.T("帧数据超出限制。"));
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71)
                throw new InvalidDataException(global::PptEase.Localization.L.T("帧必须为 PNG。"));
            // Validate IHDR dimensions before invoking the image decoder.
            int width = BigEndianInt(bytes, 16), height = BigEndianInt(bytes, 20);
            if (width < 1 || height < 1 || width > 2048 || height > 2048) throw new InvalidDataException(global::PptEase.Localization.L.T("帧尺寸超出限制。"));
            string path = Path.Combine(Path.GetTempPath(), "PptEase-shader-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (var memory = new MemoryStream(bytes))
                using (var image = Image.FromStream(memory, false, true))
                {
                    if (image.Width != width || image.Height != height) throw new InvalidDataException(global::PptEase.Localization.L.T("帧尺寸校验失败。"));
                    image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                shape.Fill.UserPicture(path);
                // Only remove the initial placeholder text, never overwrite user text later.
                if ((string)shape.TextFrame.TextRange.Text == global::PptEase.Localization.L.T("Shader · 等待首次渲染")) shape.TextFrame.TextRange.Text = "";
                watch.Stop();
                return new { Key = key, Applied = true, Milliseconds = watch.Elapsed.TotalMilliseconds };
            }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        private static int BigEndianInt(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private object ShaderStore(Dictionary<string, object> payload, bool create)
        {
            var serializer = ShaderSerializer();
            string json = serializer.Serialize(payload["component"]);
            ShaderComponent component = serializer.Deserialize<ShaderComponent>(json);
            component.Validate();
            // Canonicalize the contract; do not persist arbitrary unknown request fields.
            json = serializer.Serialize(component);
            dynamic shape;
            string existingKey = null;
            if (create)
            {
                int view = (int)app.ActiveWindow.ViewType;
                if (view != 9 && view != 1) throw new InvalidOperationException(global::PptEase.Localization.L.T("请在普通幻灯片编辑视图中插入组件。"));
                app.StartNewUndoEntry();
                shape = app.ActiveWindow.View.Slide.Shapes.AddShape(1, 72f, 72f, 320f, 240f);
            }
            else
            {
                string key = Convert.ToString(payload["key"]);
                ShapeHandle handle = GetShapes().SingleOrDefault(h => h.Key == key);
                if (handle == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("组件已删除或页面已切换，请重新选择。"));
                shape = handle.Shape;
                existingKey = handle.Key;
                if (String.IsNullOrEmpty((string)shape.Tags.Item(ShaderPayload.ManifestKey))) throw new InvalidOperationException(global::PptEase.Localization.L.T("所选对象不是 Shader 组件。"));
                if(payload.ContainsKey("preserveBindings")&&Convert.ToBoolean(payload["preserveBindings"])){
                    var current=serializer.Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)shape.Tags.Item(k)));
                    var incompletePads=current.Bindings.Where(b=>(b.Kind=="ShapePadX"||b.Kind=="ShapePadY")&&!component.Parameters.ContainsKey(b.Parameter)).Select(b=>b.ShapeId).ToList();
                    component.Bindings=current.Bindings.Where(b=>component.Parameters.ContainsKey(b.Parameter)&&!incompletePads.Contains(b.ShapeId)).ToList();
                    component.Validate();json=serializer.Serialize(component);
                }
                app.StartNewUndoEntry();
            }
            try
            {
                ShaderPayload.Write(json, k => (string)shape.Tags.Item(k), (k,v) => shape.Tags.Add(k,v), k => shape.Tags.Delete(k));
                if (create)
                {
                    shape.Name = "Shader · " + component.Backend;
                    shape.Fill.Solid(); shape.Fill.ForeColor.RGB = 0x302820;
                    shape.TextFrame.TextRange.Text = global::PptEase.Localization.L.T("Shader · 等待首次渲染");
                    shape.Select();
                }
                int id = (int)shape.Id;
                string resultKey = existingKey ?? GetShapes().Single(h => h.Id == id && h.Depth == 0).Key;
                shaderLiveParameters.Remove(resultKey);shaderParameterWrites.Remove(resultKey);
                return new { Key = resultKey, Component = component };
            }
            catch { if (create) try { shape.Delete(); } catch { } throw; }
        }
    }
}
