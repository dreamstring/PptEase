using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PptEase.Shader;
public sealed class ComponentRenderer : IDisposable
{
    private readonly ShaderComponent component;
    private readonly Panel owner;
    private D3DShaderRenderer? d3d;
    private WebGlFrameRenderer? web;
    private bool disposed;
    private readonly CancellationTokenSource lifetime=new();
    private static readonly SemaphoreSlim compilationSlots=new(2,2);
    internal static Func<Task>? CompileBarrierForTest;
    internal static int Compiling;
    public ComponentRenderer(ShaderComponent component, Panel owner) { this.component = component; this.owner = owner; }
    public async Task InitializeAsync()
    {
        component.Validate();
        if (component.Backend == "GLSL") {
            web = new WebGlFrameRenderer(); owner.Children.Add(web.View);
            await web.CompileAsync(component.Source); await web.ConfigureAsync(component);
        } else {
            var textures = new List<D3DShaderRenderer.InputTexture>();
            foreach (var texture in component.Textures) {
                using var memory = new MemoryStream(Convert.FromBase64String(texture.Data));
                var frame = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                if (frame.PixelWidth > 2048 || frame.PixelHeight > 2048) throw new InvalidDataException(global::PptEase.Localization.L.T("纹理尺寸超限。"));
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
                for (int i=0;i<pixels.Length;i+=4) (pixels[i],pixels[i+2])=(pixels[i+2],pixels[i]);
                textures.Add(new D3DShaderRenderer.InputTexture { Name=texture.Name, Width=converted.PixelWidth, Height=converted.PixelHeight, Rgba=pixels });
            }
            // Bound native compiler/device work. Queued pages cancel without allocating a device.
            await compilationSlots.WaitAsync(lifetime.Token);
            try{
                lifetime.Token.ThrowIfCancellationRequested();
                var token=lifetime.Token;
                var ready=await Task.Run(async()=>{
                    Interlocked.Increment(ref Compiling);
                    D3DShaderRenderer? renderer=null;
                    try{
                        token.ThrowIfCancellationRequested();
                        renderer=new D3DShaderRenderer(component.Width,component.Height);
                        if(CompileBarrierForTest!=null)await CompileBarrierForTest();
                        token.ThrowIfCancellationRequested();
                        renderer.CompileComponent(component.Source,component.Parameters,textures);
                        return renderer;
                    }catch{renderer?.Dispose();throw;}
                    finally{Interlocked.Decrement(ref Compiling);}
                });
                if(disposed){ready.Dispose();throw new ObjectDisposedException(nameof(ComponentRenderer));}
                d3d=ready;
            }finally{compilationSlots.Release();}
        }
    }
    public async Task<string> RenderAsync(double seconds)
    {
        float roughness = component.Parameters.TryGetValue("roughness", out float value) ? value : .3f;
        if (web != null) return await web.RenderAsync(component.Width,component.Height,seconds,roughness);
        if (d3d == null) throw new InvalidOperationException("Renderer not initialized.");
        byte[] pixels = d3d.Render((float)seconds,roughness);
        var image = BitmapSource.Create(d3d.Width,d3d.Height,96,96,PixelFormats.Bgra32,null,pixels,d3d.Width*4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream(); encoder.Save(memory); return Convert.ToBase64String(memory.ToArray());
    }
    public void AttachGpuWindow(IntPtr hwnd)
    {
        if(d3d==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("此入口仅用于 HLSL。"));
        d3d.AttachPresentationWindow(hwnd);
    }
    public void UpdateParameters(Dictionary<string,float> values) { component.Parameters=values; d3d?.UpdateParameters(values); }
    public void Present(double seconds)
    {
        if(d3d==null)throw new InvalidOperationException(global::PptEase.Localization.L.T("GPU 未初始化。"));
        d3d.PresentFrame((float)seconds,component.Parameters.TryGetValue("roughness",out var r)?r:.3f,false);
    }
    public long PresentedFrames => d3d?.PresentedFrames ?? 0;
    public long ReadbackFrames => d3d?.ReadbackFrames ?? 0;
    public void Dispose() { if(disposed)return;disposed=true;lifetime.Cancel();lifetime.Dispose();d3d?.Dispose(); d3d=null; if(web!=null){owner.Children.Remove(web.View);web.Dispose();web=null;} }
}
