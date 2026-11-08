using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using PptEase.Shader;

class CreateGraffitiFixture
{
    [ComImport,Guid("00000016-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IMessageFilter{[PreserveSig]int HandleInComingCall(int type,IntPtr caller,int ticks,IntPtr info);[PreserveSig]int RetryRejectedCall(IntPtr callee,int ticks,int reject);[PreserveSig]int MessagePending(IntPtr callee,int ticks,int type);}
    [ComVisible(true)]class BusyFilter:IMessageFilter{public int HandleInComingCall(int a,IntPtr b,int c,IntPtr d){return 0;}public int RetryRejectedCall(IntPtr a,int ticks,int reject){return ticks<10000?80:-1;}public int MessagePending(IntPtr a,int b,int c){return 2;}}
    [DllImport("ole32.dll")]static extern int CoRegisterMessageFilter(IMessageFilter filter,out IMessageFilter previous);
    static void Text(dynamic s,string value,float x,float y,float w,float h,int size=19){CreateShaderFixture.Text(s,value,x,y,w,h,size,CreateShaderFixture.Rgb(37,45,62));}
    static void Image(dynamic s,string file,float x,float y,float w,float h){dynamic picture=s.Shapes.AddPicture(file,0,-1,x,y,-1f,-1f);float ratio=Math.Min(w/(float)picture.Width,h/(float)picture.Height);picture.LockAspectRatio=-1;picture.Width=(float)picture.Width*ratio;picture.Left=x+(w-(float)picture.Width)/2;picture.Top=y+(h-(float)picture.Height)/2;}
    static JavaScriptSerializer serializer=new JavaScriptSerializer{MaxJsonLength=ShaderPayload.MaximumBytes};
    [STAThread]static int Main(string[] args)
    {
        dynamic app=null,pres=null;bool owned=false;
        IMessageFilter old;CoRegisterMessageFilter(new BusyFilter(),out old);
        try{
            string dir=Path.GetFullPath(args[0]),source=Path.GetFullPath(args[1]),target=Path.Combine(dir,"Graffiti-Style-Disk.pptx");
            if(File.Exists(target))throw new IOException("Refusing to overwrite test file");
            try{app=Marshal.GetActiveObject("PowerPoint.Application");}catch{app=Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application"));owned=true;}
            pres=app.Presentations.Add(-1);pres.PageSetup.SlideWidth=960f;pres.PageSetup.SlideHeight=640f;
            dynamic mood=CreateShaderFixture.Page(pres,"笔刷材质参考与方向");
            string[] names={"主形参考：用户提供的喷漆叉","喷漆：整个边缘颗粒化","流挂参考：连续漆流与泪滴","干边流动：固定轮廓，内部运动"};
            string[] files={"reference-x.png","reference-spray.jpg","reference-drips-user.png","inner.png"};
            for(int i=0;i<4;i++){float x=i%2==0?48:506,y=i<2?118:365;Text(mood,names[i],x,y-29,410,28,20);Image(mood,Path.Combine(dir,files[i]),x,y,402,180);}
            Text(mood,"主形与流挂以用户参考为准，照片仅作材质研究。第四图为本工程 GPU 输出。",48,592,865,28,15);
            try{mood.NotesPage.Shapes.Placeholders.Item(2).TextFrame.TextRange.Text="Marker: BLICK Sharpie oil-based paint marker. Spray: FreeStockTextures abstract-art-spray-paint. Drip: Betterrun Molotow Dripstick. Links in README.";}catch{}
            dynamic slide=CreateShaderFixture.Page(pres,"涂鸦材质盘 · 在圆内探索");
            dynamic paint=slide.Shapes.AddShape(1,40f,130f,600f,375f);paint.Name="Shader · 红色喷漆叉";paint.Line.Visible=0;paint.Fill.UserPicture(Path.Combine(dir,"marker.png"));
            dynamic pad=slide.Shapes.AddShape(9,710f,170f,180f,180f);pad.Name="材质圆盘";pad.Fill.Solid();pad.Fill.ForeColor.RGB=CreateShaderFixture.Rgb(44,54,76);pad.Line.Visible=0;
            dynamic knob=slide.Shapes.AddShape(9,792f,252f,16f,16f);knob.Name="圆盘游标";knob.Fill.Solid();knob.Fill.ForeColor.RGB=CreateShaderFixture.Rgb(246,79,82);knob.Line.Visible=0;
            Text(slide,"飞白内流",892,249,68,28,13);
            Text(slide,"颗粒喷漆",790,136,120,28,14);
            Text(slide,"水墨晕染",651,180,100,28,14);
            Text(slide,"湿漆流挂",652,327,115,28,14);
            Text(slide,"油彩内流",800,355,120,28,14);
            Text(slide,"中心：静态基础笔触\n越向外，风格与流动越强。\n角度：相邻材质自然混合。\n可在整个圆内拖动，不限圆周。",683,420,260,130,17);
            Text(slide,"同一 SDF 基形 · 覆盖 / 颜料 / 厚度分层 · 双相 UV 流动 · 不使用额外流动滑块",40,566,865,45,16);
            var config=new ShaderComponent{Backend="HLSL",Source=File.ReadAllText(source),Width=960,Height=600,FramesPerSecond=60,AutoPlay=true,Reentry="restart"};
            config.Parameters["axisX"]=.5f;config.Parameters["axisY"]=.5f;config.Parameters["roughness"]=.3f;
            config.Bindings.Add(new ShaderBinding{Kind="ShapePadX",Circular=true,Parameter="axisX",ShapeId=(int)pad.Id,ThumbShapeId=(int)knob.Id,Minimum=0,Maximum=1});
            config.Bindings.Add(new ShaderBinding{Kind="ShapePadY",Circular=true,Parameter="axisY",ShapeId=(int)pad.Id,ThumbShapeId=(int)knob.Id,Minimum=0,Maximum=1});
            if(File.Exists(Path.Combine(dir,"brush-mask.png")))config.Textures.Add(new ShaderTexture{Name="brushMask",Mime="image/png",Data=Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir,"brush-mask.png")))});config.Validate();ShaderPayload.Write(serializer.Serialize(config),k=>(string)paint.Tags.Item(k),(k,v)=>paint.Tags.Add(k,v),k=>paint.Tags.Delete(k));
            dynamic comparison=CreateShaderFixture.Page(pres,"五种材质与中心基形 · GPU 实际输出");
            string[] states={"marker","inner","spray","top","drip","bottom"};string[] labels={"中心 · 静态基形","飞白内流","颗粒喷漆","水墨晕染","湿漆流挂","油彩内流"};
            for(int i=0;i<6;i++){float x=28+(i%3)*310,y=i<3?143:385;Text(comparison,labels[i],x,y-28,295,28,19);Image(comparison,Path.Combine(dir,states[i]+".png"),x,y,295,185);}
            dynamic end=CreateShaderFixture.Page(pres,"测试方法与实现范围");
            Text(end,"1. 安装新版 PptEase 后重开 PowerPoint。\n2. 第 2 页放映，在实心圆内拖动游标。\n3. 中心应静止；向外逐渐出现不同材质及流动。\n4. 绕圆或经过中心，检查过渡是否连续、无跳帧闪白。\n5. 切页后，不应残留 Shader 或控制面。\n6. Shader 参数页可选择两个参数创建圆盘，也保留普通滑块绑定。",48,130,865,280,22);
            Text(end,"流动为艺术化表达，不是流体物理模拟。\n本页原生椭圆与游标，放映交互由 PptEase 接管。\n保存文档保留绑定；未安装增强时显示静态封面。",48,443,865,125,19);
            pres.SaveAs(target,24,0);for(int i=1;i<=4;i++)pres.Slides.Item(i).Export(Path.Combine(dir,"slide-"+i+".png"),"PNG",1200,800);
            pres.Close();pres=null;pres=app.Presentations.Open(target,-1,0,0);
            dynamic saved=pres.Slides.Item(2);ShaderComponent restored=null;
            for(int i=1;i<=(int)saved.Shapes.Count;i++){dynamic item=saved.Shapes.Item(i);if(String.IsNullOrEmpty((string)item.Tags.Item(ShaderPayload.ManifestKey)))continue;restored=serializer.Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)item.Tags.Item(k)));}
            if(restored==null||restored.Bindings.Count!=2||restored.Textures.Count!=(File.Exists(Path.Combine(dir,"brush-mask.png"))?1:0))throw new Exception("Saved component validation failed");restored.Validate();
            File.WriteAllText(Path.Combine(dir,"ppt-check.json"),serializer.Serialize(new{structurePassed=true,slides=4,xyBindings=2,embeddedTextures=restored.Textures.Count,interactionVerified=false}));return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{if(pres!=null)try{pres.Close();}catch{}if(owned&&app!=null)try{app.Quit();}catch{}IMessageFilter ignored;CoRegisterMessageFilter(old,out ignored);}
    }
}
