using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PptEase.Shader;
class Probe {
 [STAThread] static int Main(string[] args){
  string shader=File.ReadAllText(args[0]);if(args.Length>3&&args[3]=="--bom")shader="\uFEFF"+shader;Directory.CreateDirectory(args[1]);
  using var renderer=new D3DShaderRenderer(960,600);
  var parameters=new Dictionary<string,float>{{"axisX",0},{"axisY",0}};
  foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(shader,@"// @param (\w+) [\d.]+ [\d.]+ ([\d.]+)"))parameters[m.Groups[1].Value]=float.Parse(m.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture);
  using var stream=File.OpenRead(args[2]);var bitmap=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);byte[] rgba=new byte[converted.PixelWidth*converted.PixelHeight*4];converted.CopyPixels(rgba,converted.PixelWidth*4,0);for(int i=0;i<rgba.Length;i+=4)(rgba[i],rgba[i+2])=(rgba[i+2],rgba[i]);
  var inputs=shader.Contains("brushMask.Sample")?new[]{new D3DShaderRenderer.InputTexture{Name="brushMask",Width=converted.PixelWidth,Height=converted.PixelHeight,Rgba=rgba}}:Array.Empty<D3DShaderRenderer.InputTexture>();
  var timer=System.Diagnostics.Stopwatch.StartNew();renderer.CompileComponent(shader,parameters,inputs);double coldMs=timer.Elapsed.TotalMilliseconds;
  timer.Restart();renderer.CompileComponent(shader,parameters,inputs);double warmMs=timer.Elapsed.TotalMilliseconds;
  File.WriteAllText(Path.Combine(args[1],"compile-timing.json"),JsonSerializer.Serialize(new{coldMs,warmMs,scope="component compilation and texture upload; not PowerPoint end-to-end latency"}));

  var results=new List<object>();bool passed=true;
  foreach(var style in new[]{("brush",1f,.5f),("ink",.0954915f,.7938926f),("oil",.6545085f,.0244717f),("spray",.6545085f,.9755283f),("paint",.0954915f,.2061074f),("ink-paint-mix",0f,.5f)})
  foreach(float speed in new[]{0f,1f,2f}) foreach(float drip in new[]{0f,.15f,.3f,.5f,1f}){
   parameters["axisX"]=style.Item2;parameters["axisY"]=style.Item3;parameters["motionSpeed"]=speed;parameters["dripAmount"]=drip;renderer.UpdateParameters(parameters);
   var a=renderer.Render(1,.3f);var b=renderer.Render(3,.3f);int changed=0;for(int i=0;i<a.Length;i+=4)if(a[i]!=b[i]||a[i+1]!=b[i+1]||a[i+2]!=b[i+2])changed++;
   if(speed==0&&changed!=0)passed=false;
   if(speed>0&&drip>0&&changed==0)passed=false;
   if(speed==1){var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(960,600,96,96,PixelFormats.Bgra32,null,a,960*4)));using var file=File.Create(Path.Combine(args[1],style.Item1+"-"+drip.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));png.Save(file);}
   if(speed==1&&drip==1){
    foreach(float stamp in new[]{0f,2f,4f,6f}){var pixels=renderer.Render(stamp,.3f);var still=new PngBitmapEncoder();still.Frames.Add(BitmapFrame.Create(BitmapSource.Create(960,600,96,96,PixelFormats.Bgra32,null,pixels,960*4)));using var output=File.Create(Path.Combine(args[1],style.Item1+"-time-"+stamp+".png"));still.Save(output);}
   }
   results.Add(new{style=style.Item1,speed,drip,changed});
  }
  // Sweep style with the right-hand controller fixed; never rewrite its values.
  var sweep=new List<object>();
  foreach(float radius in new[]{.25f,.6f,1f}){
   byte[]? prior=null;double peak=0;
   parameters["motionSpeed"]=1;parameters["dripAmount"]=.7f;
   for(int degree=0;degree<=360;degree+=5){
    double angle=degree*Math.PI/180;parameters["axisX"]=(float)(.5+radius*.5*Math.Cos(angle));parameters["axisY"]=(float)(.5+radius*.5*Math.Sin(angle));renderer.UpdateParameters(parameters);var frame=renderer.Render(2,.3f);
    if(prior!=null){long delta=0;for(int j=0;j<frame.Length;j+=4)for(int c=0;c<3;c++)delta+=Math.Abs(frame[j+c]-prior[j+c]);peak=Math.Max(peak,(double)delta/(960*600*3));}prior=frame;
   }
   sweep.Add(new{radius,frames=73,peakMeanChannelChange=peak});
  }
  File.WriteAllText(Path.Combine(args[1],"style-sweep.json"),JsonSerializer.Serialize(sweep));
  File.WriteAllText(Path.Combine(args[1],"checks.json"),JsonSerializer.Serialize(new{passed,renderer.DeviceKind,results},new JsonSerializerOptions{WriteIndented=true}));return passed?0:1;
 }
}
