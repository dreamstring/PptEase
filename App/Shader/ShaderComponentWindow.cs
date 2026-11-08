using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PptEase.Shader;

// Stores a native cover once. Live playback is handled by the GPU runtime.
public sealed partial class ShaderComponentWindow : Window
{
    private readonly HostClient host;
    private readonly TextBox code = new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Slider roughness = new() { Minimum = 0, Maximum = 1, Value = .3, Width = 120 };
    private readonly TextBox parameters = new() { Text = "{}", MinHeight = 48, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox textureName = new() { Text = "previewTexture", Width = 110, Margin = new Thickness(4) };
    private readonly TextBox dimensions = new() { Text = "320x240", Width=90, Margin=new Thickness(4) };
    private readonly TextBox frameRate = new() { Text="60", Width=40, Margin=new Thickness(4) };
    private readonly DispatcherTimer parameterTimer = new() { Interval=TimeSpan.FromMilliseconds(100) };
    private readonly ComboBox reentry = new() { Width=140, Margin=new Thickness(4) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly System.Diagnostics.Stopwatch clock = new();
    private ShaderComponent component = new() { Source = D3DShaderRenderer.DefaultSource, FramesPerSecond=60 };
    private D3DShaderRenderer? renderer;
    private readonly WebGlFrameRenderer webGl = new();
    private bool compiled;
    private string? key;
    private bool busy, closed;

    public ShaderComponentWindow(int pid, string? shapeKey)
    {
        host = new HostClient(targetPid: pid); key = shapeKey;
        Title = "Shader 组件 · GPU 播放"; Width = 820; Height = 650;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var bar = new WrapPanel { Margin = new Thickness(0,0,0,12) }; DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        void Button(string label, Func<Task> action) { var b = new Button { Content = label, Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,0,8,0) }; b.Click += async (_,_) => await Run(action); bar.Children.Add(b); }
        Button("编译并写入幻灯片", CompileAndApply);
        Button("PPT 播放", () => SetPlayback(true));
        Button("PPT 暂停", () => SetPlayback(false));
        Button("播放诊断", ReadPlaybackStatus);
        Button("应用参数到 PPT", ApplyLiveParameters);
        Button("写入组件参数", SaveParameters);
        Button("切换后端并重置源码", async () => {
            if (MessageBox.Show("切换会替换当前编辑器中的源码为另一后端示例，文稿中的组件在编译写入前不变。继续？", "切换 Shader 后端", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            SaveLocalDraft(true);
            draftSession=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
            timer.Stop(); clock.Stop(); compiled = false;
            string backend = component.Backend == "HLSL" ? "GLSL" : "HLSL";
            string source = backend == "GLSL" ? await webGl.DefaultSourceAsync() : D3DShaderRenderer.DefaultSource;
            component.Backend = backend; code.Text = source; status.Text = "当前后端：" + backend;
        });
        bar.Children.Add(new TextBlock { Text = "粗糙度", VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(roughness);
        bar.Children.Add(textureName);
        Button("导入纹理", () => {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "纹理|*.png;*.jpg;*.jpeg" };
            if (dialog.ShowDialog(this) == true) {
                var info = new FileInfo(dialog.FileName); if (info.Length > 2 * 1024 * 1024) throw new InvalidDataException("纹理最大 2 MiB。");
                string name = textureName.Text.Trim();
                var texture = new ShaderTexture { Name = name, Mime = info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg", Data = Convert.ToBase64String(File.ReadAllBytes(dialog.FileName)) };
                var before = component.Textures.ToList();
                component.Textures.RemoveAll(t => t.Name == name); component.Textures.Add(texture);
                try { component.Validate(); } catch { component.Textures = before; throw; }
                RefreshTextures();ScheduleDraftPreview();status.Text = "已载入纹理 " + name + "；正在更新草稿预览，尚未写入文稿。";
            }
            return Task.CompletedTask;
        });
        Button("移除该纹理", () => { component.Textures.RemoveAll(t => t.Name == textureName.Text.Trim()); RefreshTextures();ScheduleDraftPreview();status.Text = "已移除指定纹理；正在更新草稿预览，尚未写入文稿。"; return Task.CompletedTask; });
        var settings = new StackPanel(); settings.Children.Add(new TextBlock { Text = "float 参数 JSON，例如 {\"gain\":0.8}；GLSL 声明同名 uniform。HLSL 自动声明参数/纹理，采样器名 peSampler。", TextWrapping = TextWrapping.Wrap }); settings.Children.Add(parameters);
        var playbackSettings=new WrapPanel();
        playbackSettings.Children.Add(new TextBlock{Text="输出像素",VerticalAlignment=VerticalAlignment.Center});playbackSettings.Children.Add(dimensions);
        playbackSettings.Children.Add(new TextBlock{Text="目标 FPS",VerticalAlignment=VerticalAlignment.Center});playbackSettings.Children.Add(frameRate);
        reentry.Items.Add("重入：重新开始");reentry.Items.Add("重入：继续");reentry.SelectedIndex=0;playbackSettings.Children.Add(reentry);settings.Children.Add(playbackSettings);
        DockPanel.SetDock(settings, Dock.Bottom); root.Children.Add(settings);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status); root.Children.Add(code);
        bar.Children.Add(webGl.View);
        code.Text = component.Source;
        status.Text = "下方为本地草稿预览。编译并写入后才更新幻灯片；PPT 播放使用 GPU，离页停止。重叠或对象动画等不支持场景保留封面。";
        roughness.ValueChanged+=(_,_)=>{if(!busy){parameterTimer.Stop();parameterTimer.Start();}};
        parameterTimer.Tick+=async(_,_)=>{parameterTimer.Stop();try{await draftPreview.UpdateParametersAsync(ReadParameters());draftState.Text="草稿参数已更新 · 未写入 PPT";}catch(Exception ex){draftState.Text=ex.Message;}};
        timer.Tick += async (_,_) => await Run(SendFrame);
        Loaded += async (_,_) => { if (key != null) await Run(LoadComponent); };
        Closed += (_,_) => { closed = true; parameterTimer.Stop(); timer.Stop(); clock.Stop(); renderer?.Dispose(); renderer = null; webGl.Dispose(); };
        BuildWorkspace(root,bar,settings);
    }
    private Dictionary<string,float> ReadParameters()
    {
        var values=JsonSerializer.Deserialize<Dictionary<string,float>>(parameters.Text)??throw new InvalidDataException("参数必须是 JSON 对象。");
        values["roughness"]=(float)roughness.Value;return values;
    }
    private async Task ApplyLiveParameters()
    {
        if(key==null)throw new InvalidOperationException("请先编译并写入组件。");
        await host.RequestAsync("shaderSetParameters",new{key,parameters=ReadParameters()});
        status.Text="参数已实时更新，无需重新编译；点击“写入组件参数”并保存 PPT 文稿后，重开才会保留。";
    }
    private async Task SaveParameters()
    {
        if(key==null)throw new InvalidOperationException("请先编译并写入组件。");
        var reply=await host.RequestAsync("shaderGet",new{key});
        var saved=reply.GetProperty("Component").Deserialize<ShaderComponent>()!;
        saved.Parameters=ReadParameters();saved.Validate();
        await host.RequestAsync("shaderUpdate",new{key,component=saved});
        component.Parameters=saved.Parameters;
        status.Text="参数已写入组件。保存 PPT 文稿后保留。源码未修改。";
    }

    private async Task Run(Func<Task> action)
    {
        if (busy || closed) return; busy = true;
        try { await action(); }
        catch (Exception ex) { timer.Stop(); clock.Stop(); status.Text = ex.Message; }
        finally { busy = false; }
    }

    private async Task SetPlayback(bool play)
    {
        if (key == null) throw new InvalidOperationException("请先编译并写入组件。");
        timer.Stop(); clock.Stop();
        var reply = await host.RequestAsync("shaderGet", new { key });
        var saved = reply.GetProperty("Component").Deserialize<ShaderComponent>() ?? throw new InvalidDataException("组件数据无效。");
        saved.AutoPlay = play;
        await host.RequestAsync("shaderUpdate",new { key, component=saved });
        component.AutoPlay=play;
        if(play)await host.RequestAsync("shaderEnsureRuntime");
        status.Text=play?"GPU 后台播放已启动；离开本页停止，切页期间显示封面。实际状态见安装与维护中的诊断。":"已停止 GPU 播放，显示原生封面。";
        if(play)await ReadPlaybackStatus();
    }
    private async Task ReadPlaybackStatus()
    {
        var state=await host.RequestAsync("shaderScene",new{});
        if(!state.TryGetProperty("InteractionVersion",out var interaction)||interaction.GetInt32()<2){status.Text="当前PPT仍是旧宿主，不支持二维十字轴。安装修复后请保存文稿、关闭所有PPT进程（含后台）再重新打开。";return;}
        var item=state.GetProperty("Components").EnumerateArray().FirstOrDefault(i=>i.GetProperty("Key").GetString()==key);
        if(item.ValueKind==JsonValueKind.Undefined){status.Text="当前页面没有匹配的播放组件。请选中组件后重新打开“编辑 Shader”，再点播放。";return;}
        if(!item.TryGetProperty("Placement",out var placement)){status.Text="PPT 仍加载旧版宿主，请安装修复并重启 PPT。";return;}
        status.Text=placement.GetProperty("Visible").GetBoolean()?"已定位 PPT 播放位置；若画面仍静止，请反馈“安装与维护”中的 Shader 最近状态。":"未进入实时播放："+placement.GetProperty("Reason").GetString();
    }

    private async Task LoadComponent()
    {
        var reply = await host.RequestAsync("shaderGet", new { key });
        if (closed) return;
        component = reply.GetProperty("Component").Deserialize<ShaderComponent>() ?? throw new InvalidDataException("缺少组件数据。");
        component.Validate(); code.Text = component.Source;
        dimensions.Text=component.Width+"x"+component.Height;frameRate.Text=component.FramesPerSecond.ToString();reentry.SelectedIndex=component.Reentry=="resume"?1:0;
        if (component.Parameters.TryGetValue("roughness", out float value)) roughness.Value = value;
        parameters.Text = JsonSerializer.Serialize(component.Parameters.Where(p => p.Key != "roughness").ToDictionary(p => p.Key, p => p.Value));
        RefreshParameterPanel();RefreshTextures();baselineDraft=CaptureDraft();
        status.Text = "已从文稿读取组件；修改源码后编译写入。后端：" + component.Backend;
    }

    private async Task CompileAndApply()
    {
        timer.Stop();clock.Stop();
        RefreshParameterPanel();
        var snapshot=ReadDraft();
        string applyingDraft=CaptureDraft();
        if(!int.TryParse(frameRate.Text,out int fps))throw new InvalidDataException("帧率须为整数。");
        snapshot.FramesPerSecond=fps;snapshot.Reentry=reentry.SelectedIndex==1?"resume":"restart";
        int originalBindingCount=component.Bindings.Count;
        snapshot.Bindings=component.Bindings.Where(b=>snapshot.Parameters.ContainsKey(b.Parameter)).ToList();snapshot.Validate();
        // Work on an immutable snapshot; failed compilation never mutates the saved component.
        using var candidate=new ComponentRenderer(snapshot,new StackPanel());
        await candidate.InitializeAsync();if(closed)return;
        string cover=await candidate.RenderAsync(0);if(closed)return;
        var reply=await host.RequestAsync(key==null?"shaderCreate":"shaderUpdate",new{key,component=snapshot,preserveBindings=true});
        key=reply.GetProperty("Key").GetString();snapshot.Bindings=reply.GetProperty("Component").Deserialize<ShaderComponent>()!.Bindings;component=snapshot;
        if(closed)return;
        refreshBindingAvailability?.Invoke();
        try{await host.RequestAsync("shaderFrame",new{key,png=cover});}
        catch(Exception ex){status.Text="组件源码已写入，但封面更新失败。请重新编译并写入以刷新封面："+ex.Message;return;}
        bool editedDuringApply=CaptureDraft()!=applyingDraft;
        if(!editedDuringApply)baselineDraft=CaptureDraft();
        status.Text="已应用此版本到组件。保存 PowerPoint 文稿后持久保留；后续草稿修改不会自动写入。";
        if(editedDuringApply)status.Text+=" 编译期间的新修改仍是未应用草稿。";
        if(snapshot.Bindings.Count<originalBindingCount)status.Text+=" 已解除 "+(originalBindingCount-snapshot.Bindings.Count)+" 个已移除参数的绑定，原控件形状仍保留。";
    }

    private async Task SendFrame()
    {
        if (!compiled || key == null || closed) return;
        string image;
        if (component.Backend == "GLSL") image = await webGl.RenderAsync(component.Width, component.Height, clock.Elapsed.TotalSeconds, (float)roughness.Value);
        else {
            if (renderer == null) return;
            byte[] pixels = renderer.Render((float)clock.Elapsed.TotalSeconds, (float)roughness.Value);
            var bitmap = BitmapSource.Create(renderer.Width, renderer.Height, 96, 96, PixelFormats.Bgra32, null, pixels, renderer.Width * 4);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var memory = new MemoryStream(); png.Save(memory); image = Convert.ToBase64String(memory.ToArray());
        }
        if (closed) return;
        var reply = await host.RequestAsync("shaderFrame", new { key, png = image });
        if (!closed) status.Text = "静态封面已写入。点击“播放”启动 GPU 预览；播放不会逐帧改写 PPT 图片。";
    }
}
