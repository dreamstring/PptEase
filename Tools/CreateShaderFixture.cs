using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using PptEase.Shader;

// Creates and validates only a new, owned test presentation. No UI automation.
class CreateShaderFixture
{
    static JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=ShaderPayload.MaximumBytes};
    static List<string> notes=new List<string>();
    internal static int Rgb(int r,int g,int b){return r|(g<<8)|(b<<16);}
    internal static void Text(dynamic slide,string text,float x,float y,float w,float h,int size,int color)
    {
        dynamic box=slide.Shapes.AddTextbox(1,x,y,w,h);box.TextFrame.TextRange.Text=text;
        box.TextFrame.TextRange.Font.Name="Microsoft YaHei";box.TextFrame.TextRange.Font.Size=size;box.TextFrame.TextRange.Font.Color.RGB=color;
        box.TextFrame.MarginLeft=0;box.TextFrame.MarginRight=0;box.TextFrame.MarginTop=0;box.TextFrame.MarginBottom=0;
    }
    internal static dynamic Page(dynamic pres,string title)
    {
        dynamic slide=pres.Slides.Add((int)pres.Slides.Count+1,12);slide.FollowMasterBackground=0;slide.Background.Fill.Solid();slide.Background.Fill.ForeColor.RGB=Rgb(246,248,252);
        slide.SlideShowTransition.EntryEffect=0;slide.SlideShowTransition.Duration=0;slide.SlideShowTransition.AdvanceOnTime=0;
        Text(slide,title,48,34,864,55,30,Rgb(24,36,58));return slide;
    }
    static void Component(dynamic slide,ShaderComponent config)
    {
        dynamic shape=slide.Shapes.AddShape(1,48f,112f,500f,281.25f);shape.Name="Shader · "+config.Backend+" · gain";
        shape.Fill.Solid();shape.Fill.ForeColor.RGB=Rgb(35,45,68);shape.Line.Visible=0;
        shape.TextFrame.TextRange.Text="Shader 静态封面\n启用 PPT 增强后自动播放";shape.TextFrame.TextRange.Font.Size=22;shape.TextFrame.TextRange.Font.Color.RGB=Rgb(230,236,248);
        bool bound=false;
        dynamic controlShape=null;
        string stage="ShapeSlider";
        try{
            controlShape=slide.Shapes.AddShape(1,72f,432f,450f,26f);controlShape.Fill.Solid();controlShape.Fill.ForeColor.RGB=Rgb(216,222,232);controlShape.Line.Visible=0;controlShape.Name="gain · 形状轨道";
            dynamic knob=slide.Shapes.AddShape(1,286f,432f,22f,26f);knob.Fill.Solid();knob.Fill.ForeColor.RGB=Rgb(79,70,229);knob.Line.Visible=0;knob.Name="gain · 形状滑块";
            config.Bindings.Add(new ShaderBinding{Kind="ShapeSlider",Parameter="gain",ShapeId=(int)controlShape.Id,ThumbShapeId=(int)knob.Id,Minimum=0,Maximum=2});bound=true;
        }catch(Exception ex){if(controlShape!=null)try{controlShape.Delete();}catch{}notes.Add(config.Backend+" ActiveX "+stage+"："+ex.Message);}
        Text(slide,bound?"gain  0                                      1                                      2":"形状滑块未创建。可使用 Shader Studio 左侧 gain 滑块。",72,474,470,45,16,Rgb(74,88,110));
        config.Validate();ShaderPayload.Write(json.Serialize(config),k=>(string)shape.Tags.Item(k),(k,v)=>shape.Tags.Add(k,v),k=>shape.Tags.Delete(k));
        var roundtrip=json.Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)shape.Tags.Item(k)));roundtrip.Validate();
        if(roundtrip.Source!=config.Source||roundtrip.Bindings.Count!=config.Bindings.Count)throw new Exception("组件Tags回读不一致");
        notes.Add(config.Backend+" 源码/参数Tags回读通过；滑块绑定="+bound);
    }
    const string Hlsl=@"// @param gain 0 2 1
cbuffer PreviewParameters : register(b0) { float time; float roughness; float2 resolution; };
float4 main(float4 pos:SV_POSITION):SV_TARGET {
 float2 uv=pos.xy/resolution;
 float wave=.5+.5*sin(time*1.8+uv.x*12+uv.y*4);
 float3 color=lerp(float3(.08,.18,.65),float3(.65,.25,.95),wave);
 return float4(color*gain*(1-roughness*.3),1);
}";
    const string Glsl=@"#version 300 es
precision highp float;
// @param gain 0 2 1
uniform float gain; uniform float time; uniform float roughness; uniform vec2 resolution;
out vec4 color;
void main(){vec2 uv=gl_FragCoord.xy/resolution;
float wave=.5+.5*sin(time*1.8+uv.x*12.+uv.y*4.);
color=vec4(mix(vec3(.05,.4,.45),vec3(.15,.9,.65),wave)*gain*(1.-roughness*.3),1.);}";
    [STAThread]static int Main(string[] args)
    {
        dynamic app=null,pres=null;bool started=false;
        try{
            string path=Path.GetFullPath(args[0]);if(File.Exists(path))throw new IOException("拒绝覆盖已有测试文稿");
            try{app=Marshal.GetActiveObject("PowerPoint.Application");}catch{app=Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application"));started=true;}
            pres=app.Presentations.Add(-1);pres.PageSetup.SlideWidth=960f;pres.PageSetup.SlideHeight=540f;
            if(Path.GetExtension(path).Equals(".pptm",StringComparison.OrdinalIgnoreCase))pres.SaveAs(path,25,0);
            foreach(string backend in new[]{"HLSL","GLSL"}){
                dynamic slide=Page(pres,backend+" 参数与放映滑块测试");
                Text(slide,"预期结果",590,115,310,35,23,Rgb(31,43,65));
                Text(slide,"材质波纹持续移动。\n\n放映时拖动下方形状滑块：\n0 时接近黑色，\n2 时明显变亮。\n\n编辑时两个矩形可改样式。\n切页不应残留实时画面。\n不需要ActiveX或VBA宏。",590,168,310,294,19,Rgb(61,74,96));
                var config=new ShaderComponent{Backend=backend,Source=backend=="HLSL"?Hlsl:Glsl,Width=640,Height=360,FramesPerSecond=60,AutoPlay=true,Reentry="restart"};config.Parameters["gain"]=1;config.Parameters["roughness"]=.3f;Component(slide,config);
            }
            dynamic pause=Page(pres,"切页与编辑兼容测试");
            Text(pause,"这一页没有 Shader。",48,130,820,60,30,Rgb(52,64,86));
            Text(pause,"从前两页切到这里：不应残留 GPU 画面。\n返回前页：材质重新开始播放。\n退出放映：PPT 应能正常选择对象、输入文字和滚动。\n保存、关闭并重开这个测试文件，检查组件与绑定能否恢复。",48,230,850,200,23,Rgb(70,84,108));
            // Preserve the selected file format after adding the test components.
            pres.SaveAs(path,Path.GetExtension(path).Equals(".pptm",StringComparison.OrdinalIgnoreCase)?25:24,0);
            for(int i=1;i<=3;i++)pres.Slides.Item(i).Export(Path.Combine(Path.GetDirectoryName(path),"Shader-Test-"+i+".png"),"PNG",1280,720);
            pres.Close();pres=null;
            // Reopen only our generated file and verify actual persisted tags.
            pres=app.Presentations.Open(path, -1,0,0);int restored=0;
            for(int i=1;i<=2;i++){dynamic slide=pres.Slides.Item(i);for(int s=1;s<=(int)slide.Shapes.Count;s++){dynamic shape=slide.Shapes.Item(s);if(String.IsNullOrEmpty((string)shape.Tags.Item(ShaderPayload.ManifestKey)))continue;var config=json.Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)shape.Tags.Item(k)));config.Validate();if(config.Parameters["gain"]!=1)throw new Exception("保存后参数不一致");restored++;}}
            if(restored!=2)throw new Exception("保存重开后Shader数量不一致");notes.Add("保存并只读重开：2个Shader组件数据验证通过");
            File.WriteAllText(path+".report.json",json.Serialize(new{structurePassed=true,componentsRestored=restored,notes=notes.ToArray(),interactionVerified=false}));
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{if(pres!=null)try{pres.Close();}catch{}if(started&&app!=null)try{app.Quit();}catch{}}
    }
}
