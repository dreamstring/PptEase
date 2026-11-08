using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PptEase.Shader;
class Probe {
 [STAThread] static int Main(string[] args){
  string shader=File.ReadAllText(args[0]);Directory.CreateDirectory(args[1]);
  using var renderer=new D3DShaderRenderer(960,600);
  var parameters=new Dictionary<string,float>{{"axisX",0},{"axisY",0}};
  foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(shader,@"// @param (\w+) [\d.]+ [\d.]+ ([\d.]+)"))parameters[m.Groups[1].Value]=float.Parse(m.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture);
  using var stream=File.OpenRead(args[2]);var bitmap=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);byte[] rgba=new byte[converted.PixelWidth*converted.PixelHeight*4];converted.CopyPixels(rgba,converted.PixelWidth*4,0);for(int i=0;i<rgba.Length;i+=4)(rgba[i],rgba[i+2])=(rgba[i+2],rgba[i]);
  var inputs=shader.Contains("brushMask.Sample")?new[]{new D3DShaderRenderer.InputTexture{Name="brushMask",Width=converted.PixelWidth,Height=converted.PixelHeight,Rgba=rgba}}:Array.Empty<D3DShaderRenderer.InputTexture>();
  var timer=System.Diagnostics.Stopwatch.StartNew();renderer.CompileComponent(shader,parameters,inputs);double coldMs=timer.Elapsed.TotalMilliseconds;
  timer.Restart();renderer.CompileComponent(shader,parameters,inputs);double warmMs=timer.Elapsed.TotalMilliseconds;
  File.WriteAllText(Path.Combine(args[1],"compile-timing.json"),JsonSerializer.Serialize(new{coldMs,warmMs,scope="component compilation and texture upload; not PowerPoint end-to-end latency"}));
  var results=new Dictionary<string,object>();var images=new Dictionary<string,byte[]>();
  foreach(var state in new[]{("marker",.5f,.5f),("spray",.6545085f,.9755283f),("drip",.0954915f,.2061074f),("inner",1f,.5f),("mix",.75f,.5f),("bottom",.6545085f,.0244717f),("top",.0954915f,.7938926f),("left",.25f,.5f),("right",.702254f,.646946f)}){
   parameters["axisX"]=state.Item2;parameters["axisY"]=state.Item3;renderer.UpdateParameters(parameters);
   byte[] first=renderer.Render(0,.3f),second=renderer.Render(2,.3f);images[state.Item1]=first;
   var image=BitmapSource.Create(960,600,96,96,PixelFormats.Bgra32,null,first,960*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(Path.Combine(args[1],state.Item1+".png")))encoder.Save(output);
   if(state.Item1=="inner"||state.Item1=="top"||state.Item1=="bottom"){
    var evolving=new GifBitmapEncoder();
    for(int frame=0;frame<40;frame++){
     var pixels=renderer.Render(frame*.3f,.3f);var view=BitmapSource.Create(960,600,96,96,PixelFormats.Bgra32,null,pixels,960*4);
     var md=new BitmapMetadata("gif");md.SetQuery("/grctlext/Delay",(ushort)30);evolving.Frames.Add(BitmapFrame.Create(view,null,md,null));
    }
    using var evolutionFile=File.Create(Path.Combine(args[1],state.Item1+"-evolution.gif"));evolving.Save(evolutionFile);
   }
   int changed=0,topChanged=0;for(int i=0;i<first.Length;i+=4){bool different=first[i]!=second[i]||first[i+1]!=second[i+1]||first[i+2]!=second[i+2];if(different){changed++;if(i/4/960<50)topChanged++;}}
   results[state.Item1]=new{timeChangedPixels=changed,topBackgroundChanged=topChanged};
  }
  bool markerStatic=images["marker"].Length>0 && ((JsonElement)JsonSerializer.SerializeToElement(results["marker"])).GetProperty("timeChangedPixels").GetInt32()==0;
  bool innerMoves=JsonSerializer.SerializeToElement(results["inner"]).GetProperty("timeChangedPixels").GetInt32()>1000;
  bool dripsMove=JsonSerializer.SerializeToElement(results["drip"]).GetProperty("timeChangedPixels").GetInt32()>1000;
  bool variantsDifferent=!images["marker"].SequenceEqual(images["spray"]);
  bool passed=markerStatic&&innerMoves&&dripsMove&&variantsDifferent;
  parameters["axisX"]=.0954915f;parameters["axisY"]=.2061074f;renderer.UpdateParameters(parameters);
  var gif=new GifBitmapEncoder();
  for(int frame=0;frame<72;frame++){
   byte[] pixels=renderer.Render(frame*.2f,.3f);
   var bitmapFrame=BitmapSource.Create(960,600,96,96,PixelFormats.Bgra32,null,pixels,960*4);
   if(frame==10||frame==30||frame==50){var still=new PngBitmapEncoder();still.Frames.Add(BitmapFrame.Create(bitmapFrame));using var file=File.Create(Path.Combine(args[1],"drip-time-"+(frame*.2f)+".png"));still.Save(file);}
   var metadata=new BitmapMetadata("gif");metadata.SetQuery("/grctlext/Delay",(ushort)20);
   gif.Frames.Add(BitmapFrame.Create(bitmapFrame,null,metadata,null));
  }
  using(var output=File.Create(Path.Combine(args[1],"drip-motion.gif")))gif.Save(output);
  var sheet=new DrawingVisual();using(var dc=sheet.RenderOpen()){
   string[] order={"inner","top","drip","left","mix","right","marker","bottom","spray"};
   for(int n=0;n<9;n++){var frame=BitmapSource.Create(960,600,96,96,PixelFormats.Bgra32,null,images[order[n]],960*4);dc.DrawImage(frame,new System.Windows.Rect(n%3*480,n/3*300,480,300));}}
  var board=new RenderTargetBitmap(1440,900,96,96,PixelFormats.Pbgra32);board.Render(sheet);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(board));using(var output=File.Create(Path.Combine(args[1],"nine-states.png")))png.Save(output);
  var sweep=new List<object>();
  foreach(float radius in new[]{.05f,.5f,1f}){
   byte[]? previous=null;double maxMean=0;int finiteFrames=0;
   for(int degree=0;degree<=360;degree+=5){
    double angle=degree*Math.PI/180;parameters["axisX"]=(float)(.5+.5*radius*Math.Cos(angle));parameters["axisY"]=(float)(.5+.5*radius*Math.Sin(angle));renderer.UpdateParameters(parameters);
    var pixels=renderer.Render(3,.3f);finiteFrames++;
    if(previous!=null){long delta=0;for(int n=0;n<pixels.Length;n+=4)for(int c=0;c<3;c++)delta+=Math.Abs(pixels[n+c]-previous[n+c]);maxMean=Math.Max(maxMean,(double)delta/(960*600*3));}previous=pixels;
   }
   sweep.Add(new{radius,frames=finiteFrames,maxMeanChannelDeltaPerFiveDegrees=maxMean});
  }
  File.WriteAllText(Path.Combine(args[1],"disk-sweep.json"),JsonSerializer.Serialize(sweep,new JsonSerializerOptions{WriteIndented=true}));
  File.WriteAllText(Path.Combine(args[1],"gpu-check.json"),JsonSerializer.Serialize(new{passed,renderer.DeviceKind,markerStatic,innerMoves,dripsMove,variantsDifferent,states=results},new JsonSerializerOptions{WriteIndented=true}));return passed?0:1;
 }
}
