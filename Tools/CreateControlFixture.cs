using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using PptEase.Shader;
class CreateControlFixture {
 static JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=ShaderPayload.MaximumBytes};
 const string Source=@"// @param gain 0 2 1
// @param axisX 0 1 0.5
// @param axisY 0 1 0.5
// @param enabled 0 1 1
cbuffer PreviewParameters:register(b0){float time;float roughness;float2 resolution;};
float4 main(float4 p:SV_POSITION):SV_TARGET {
 float2 uv=p.xy/resolution;
 float spot=1-smoothstep(.06,.16,length(uv-float2(axisX,1-axisY)));
 float3 bg=float3(.04,.08,.15);
 return float4(lerp(bg,float3(.9,.25,.15)*gain,spot*enabled),1);
}";
 [STAThread] static int Main(string[] args){
  dynamic app=null,pres=null;
  try{
   string path=Path.GetFullPath(args[0]);if(File.Exists(path))throw new IOException("Refusing overwrite");
   app=Marshal.GetActiveObject("PowerPoint.Application");
   pres=app.Presentations.Add(0);pres.PageSetup.SlideWidth=960f;pres.PageSetup.SlideHeight=540f;
   dynamic slide=CreateShaderFixture.Page(pres,"交互控件验收 · 三种输入，一个材质");
   CreateShaderFixture.Text(slide,"滑块控制亮度，圆盘移动光点，复选框切换显示。",48,88,860,30,18,0x604A3D);
   dynamic shader=slide.Shapes.AddShape(1,48f,140f,500f,300f);shader.Name="Shader · 控件验收";shader.Fill.ForeColor.RGB=0x26140A;shader.Line.Visible=0;
   var config=new ShaderComponent{Source=Source,Width=800,Height=480,FramesPerSecond=60,AutoPlay=true};
   config.Parameters["gain"]=1;config.Parameters["axisX"]=.5f;config.Parameters["axisY"]=.5f;config.Parameters["enabled"]=1;
   for(int i=0;i<3;i++){
    bool disk=i==1,check=i==2;float x=600,y=i==0?170:i==1?245:450,w=disk?160:check?28:250,h=disk?160:28;
    dynamic track=slide.Shapes.AddShape(disk?9:1,x,y,w,h);track.Fill.ForeColor.RGB=0x3B291E;track.Line.Visible=0;
    dynamic knob=slide.Shapes.AddShape(disk?9:1,x+(w-16)/2,y+(h-16)/2,16f,16f);knob.Fill.ForeColor.RGB=check?0x3B291E:0xE5464F;knob.Line.Visible=0;
    string kind=disk?"disk":check?"checkbox":"slider",pair=Guid.NewGuid().ToString("N");track.Name="验收 · "+kind;knob.Name="游标 · "+kind;
    track.Tags.Add("PPTEASE_CONTROL_PAIR",pair);knob.Tags.Add("PPTEASE_CONTROL_PAIR",pair);track.Tags.Add("PPTEASE_CONTROL_KIND",kind);knob.Tags.Add("PPTEASE_CONTROL_KIND",kind);
    config.Bindings.Add(new ShaderBinding{Kind=disk?"ShapePadX":"ShapeSlider",Checkbox=check,Circular=disk,Parameter=disk?"axisX":check?"enabled":"gain",ShapeId=(int)track.Id,ThumbShapeId=(int)knob.Id,Minimum=0,Maximum=i==0?2:1});
    if(disk)config.Bindings.Add(new ShaderBinding{Kind="ShapePadY",Circular=true,Parameter="axisY",ShapeId=(int)track.Id,ThumbShapeId=(int)knob.Id,Minimum=0,Maximum=1});
    CreateShaderFixture.Text(slide,i==0?"亮度 0—2":disk?"位置 X / Y":"显示光点",check?642:600,check?448:y-30,260,28,18,0x604A3D);
   }
   config.Validate();ShaderPayload.Write(json.Serialize(config),k=>(string)shader.Tags.Item(k),(k,v)=>shader.Tags.Add(k,v),k=>shader.Tags.Delete(k));
   dynamic blank=CreateShaderFixture.Page(pres,"离页与恢复验收");
   CreateShaderFixture.Text(blank,"此页不应显示材质或交互控件。返回上一页检查恢复。\n保存并重开后，再检查三种控件。",48,150,860,180,26,0x604A3D);
   pres.SaveAs(path,24,0);
   for(int i=1;i<=2;i++)pres.Slides.Item(i).Export(path+"."+i+".png","PNG",1280,720);
   pres.Close();pres=null;
   pres=app.Presentations.Open(path,-1,0,0);
   dynamic restored=pres.Slides.Item(1).Shapes.Item(3);
   int count=0;
   foreach(dynamic sh in pres.Slides.Item(1).Shapes){if(String.IsNullOrEmpty((string)sh.Tags.Item(ShaderPayload.ManifestKey)))continue;var c=json.Deserialize<ShaderComponent>(ShaderPayload.Read(k=>(string)sh.Tags.Item(k)));c.Validate();if(c.Bindings.Count!=4||!c.Bindings[3].Checkbox)throw new Exception("Binding roundtrip failed");count++;}
   if(count!=1)throw new Exception("Component missing");
   File.WriteAllText(path+".report.json",json.Serialize(new{structurePassed=true,bindings=4,interactionVerified=false}));return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{if(pres!=null)try{pres.Close();}catch{}}
 }
}
