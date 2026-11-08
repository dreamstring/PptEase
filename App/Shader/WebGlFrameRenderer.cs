using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PptEase.Shader;

public sealed class WebGlFrameRenderer : IDisposable
{
    public WebView2 View { get; } = new() { Width = 1, Height = 1, IsHitTestVisible = false };
    private Task? initialization;
    private bool disposed;
    private readonly IntPtr nativeParent;
    private CoreWebView2Controller? controller;
    private CoreWebView2? coreView;
    public WebGlFrameRenderer(){}
    public WebGlFrameRenderer(IntPtr parent){nativeParent=parent;}
    public void ResizeNative(int width,int height){if(controller!=null){controller.Bounds=new System.Drawing.Rectangle(0,0,width,height);controller.NotifyParentWindowPositionChanged();}}
    private Task<string> ExecuteAsync(string script)=>coreView!.ExecuteScriptAsync(script);
    public Task InitializeAsync() => initialization ??= InitializeCore();
    private async Task InitializeCore()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptEase", "ShaderFrames"));
        if(nativeParent!=IntPtr.Zero){controller=await environment.CreateCoreWebView2ControllerAsync(nativeParent);if(disposed){controller.Close();controller=null;throw new ObjectDisposedException(nameof(WebGlFrameRenderer));}controller.Bounds=new System.Drawing.Rectangle(0,0,320,240);controller.IsVisible=true;coreView=controller.CoreWebView2;}
        else{await View.EnsureCoreWebView2Async(environment);coreView=View.CoreWebView2;}
        if (disposed) throw new ObjectDisposedException(nameof(WebGlFrameRenderer));
        var core = coreView;
        core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
        core.NewWindowRequested += (_,e) => e.Handled = true;
        core.NavigationStarting += (_,e) => { if (e.Uri != "about:blank" && !e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.PermissionRequested += (_,e) => e.State = CoreWebView2PermissionState.Deny;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_,e) => { if (!e.Request.Uri.StartsWith("data:") && !e.Request.Uri.StartsWith("about:")) e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Offline", ""); };
        var loaded = new TaskCompletionSource<bool>();
        void Complete(object? sender, CoreWebView2NavigationCompletedEventArgs args) { core.NavigationCompleted -= Complete; if (args.IsSuccess) loaded.TrySetResult(true); else loaded.TrySetException(new IOException("WebGL 页面加载失败：" + args.WebErrorStatus)); }
        core.NavigationCompleted += Complete;
        core.NavigateToString(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Shader", "preview.html")));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public async Task<string> DefaultSourceAsync()
    {
        await InitializeAsync();
        return JsonSerializer.Deserialize<string>(await ExecuteAsync("window.preview.defaultSource")) ?? throw new InvalidOperationException("WebGL2 不可用。");
    }
    public async Task CompileAsync(string source)
    {
        await InitializeAsync();
        using var result = JsonDocument.Parse(await ExecuteAsync("window.preview.compile(" + JsonSerializer.Serialize(source) + ")"));
        if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.RootElement.GetProperty("error").GetString());
    }
    public async Task<string> RenderAsync(int width, int height, double time, float roughness)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WebGlFrameRenderer));
        await InitializeAsync();
        string result = await ExecuteAsync("window.preview.exportFrame(" + JsonSerializer.Serialize(new { width, height, time, roughness }) + ")");
        return JsonSerializer.Deserialize<string>(result) ?? throw new InvalidOperationException("WebGL 未返回图像帧。");
    }
    public async Task ConfigureAsync(ShaderComponent component)
    {
        component.Validate(); await InitializeAsync();
        var textures = new List<object>();
        foreach (var texture in component.Textures)
        {
            using var memory = new MemoryStream(Convert.FromBase64String(texture.Data));
            var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth > 2048 || frame.PixelHeight > 2048) throw new InvalidDataException("纹理尺寸不能超过 2048×2048。");
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            textures.Add(new { name = texture.Name, width = bitmap.PixelWidth, height = bitmap.PixelHeight, rgba = Convert.ToBase64String(pixels) });
        }
        // roughness is supplied per frame by the editor, not shadowed by a stale custom binding.
        var parameters = component.Parameters.Where(p => p.Key != "roughness").ToDictionary(p => p.Key, p => p.Value);
        using var result = JsonDocument.Parse(await ExecuteAsync("window.preview.configureBindings(" + JsonSerializer.Serialize(parameters) + "," + JsonSerializer.Serialize(textures) + ")"));
        if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.RootElement.GetProperty("error").GetString());
    }
    public async Task SetLiveStateAsync(Dictionary<string,float> values,double time,bool playing)
    {
        await InitializeAsync();
        await ExecuteAsync("window.preview.setLive("+JsonSerializer.Serialize(new{parameters=values,time,playing})+")");
    }
    internal async Task<JsonElement> ReadPlaybackDiagnosticsAsync()
    {
        await InitializeAsync();
        using var data=JsonDocument.Parse(await ExecuteAsync("window.preview.diagnostics()"));
        return data.RootElement.Clone();
    }
    public async Task<string> RequireHardwareAsync()
    {
        await InitializeAsync();
        string device=JsonSerializer.Deserialize<string>(await ExecuteAsync("(()=>{const g=document.querySelector('canvas').getContext('webgl2');const d=g&&g.getExtension('WEBGL_debug_renderer_info');return d?g.getParameter(d.UNMASKED_RENDERER_WEBGL):''})()"))??"";
        if(string.IsNullOrWhiteSpace(device)||new[]{"swiftshader","software","llvmpipe","warp","basic render"}.Any(s=>device.Contains(s,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("WebGL 未确认硬件 GPU，已保留封面："+device);
        return device;
    }
    public void Dispose() { if (disposed) return; disposed = true; controller?.Close();View.Dispose(); }
}
