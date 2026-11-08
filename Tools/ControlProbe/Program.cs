using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PptEase.Shader;
class Probe {
 [STAThread] static int Main(string[] args){
  using var zip=ZipFile.OpenRead(args[0]);JsonElement config=default;
  foreach(var entry in zip.Entries.Where(e=>e.FullName.StartsWith("ppt/tags/")&&e.FullName.EndsWith(".xml"))){
   using var stream=entry.Open();var xml=XDocument.Load(stream);
   var tags=xml.Descendants().Where(e=>e.Name.LocalName=="tag").ToDictionary(e=>(string)e.Attribute("name")!,e=>(string)e.Attribute("val")!);
   if(!tags.ContainsKey(ShaderPayload.ManifestKey))continue;
   config=JsonDocument.Parse(ShaderPayload.Read(k=>tags.GetValueOrDefault(k,""))).RootElement.Clone();break;
  }
  var values=config.GetProperty("Parameters").Deserialize<Dictionary<string,float>>()!;
  using var renderer=new D3DShaderRenderer(800,480);renderer.CompileComponent(config.GetProperty("Source").GetString()!,values,Array.Empty<D3DShaderRenderer.InputTexture>());
  byte[] Render(float gain,float x,float y,float enabled){values["gain"]=gain;values["axisX"]=x;values["axisY"]=y;values["enabled"]=enabled;renderer.UpdateParameters(values);return renderer.Render(0,0);}
  var on=Render(1,.5f,.5f,1);var off=Render(1,.5f,.5f,0);var bright=Render(2,.5f,.5f,1);var moved=Render(1,.8f,.2f,1);
  int Pixel(byte[] p,int x,int y)=>p[(y*800+x)*4+2];
  bool passed=Pixel(on,400,240)>Pixel(off,400,240)+80&&Pixel(bright,400,240)>Pixel(on,400,240)&&Pixel(moved,640,384)>Pixel(moved,400,240)+80;
  Directory.CreateDirectory(args[1]);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(800,480,96,96,PixelFormats.Bgra32,null,on,3200)));using(var file=File.Create(Path.Combine(args[1],"control-cover.png")))encoder.Save(file);
  File.WriteAllText(Path.Combine(args[1],"control-gpu.json"),JsonSerializer.Serialize(new{passed,renderer.DeviceKind,scope="Shader read from actual saved PPTX; GPU output changes for slider/disk/checkbox parameters, not real input test"}));return passed?0:1;
 }
}
