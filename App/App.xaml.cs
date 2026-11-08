using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PptEase.Models;
using PptEase.Shader;

namespace PptEase;
public partial class App : Application
{
    private readonly HostClient _host = new();
    private readonly IntegrationInstaller _installer = new();
    private MainWindow? _window;
    private ShaderPreviewControl? _shader;
    private DispatcherTimer? _refreshTimer;
    private bool _busy;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => { _window?.AppendLog(args.Exception.Message); args.Handled = true; };
        try
        {
            await UiLanguage.InitializeAsync(e.Args);
            if(e.Args.Length==2&&e.Args[0]=="--shader-controls-test"){
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                try{File.WriteAllText(e.Args[1]+".json",JsonSerializer.Serialize(ShapeSliderWindow.RenderControlGallery(e.Args[1])));Shutdown(0);}catch(Exception ex){File.WriteAllText(e.Args[1]+".json",JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Shutdown(1);}return;
            }
            if(e.Args.Length==2&&e.Args[0]=="--shader-workspace-test")
            {
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                try{var editor=new ShaderComponentWindow(0,null){Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false};editor.PrepareWorkspaceFixture();editor.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);RenderWindow(editor,e.Args[1]);editor.Width=editor.MinWidth;editor.Height=editor.MinHeight;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);RenderWindow(editor,Path.ChangeExtension(e.Args[1],"minimum.png"));editor.Close();Shutdown(0);}
                catch(Exception ex){File.WriteAllText(e.Args[1]+".error.txt",ex.ToString());Shutdown(1);}return;
            }
            if(e.Args.Length==2&&e.Args[0]=="--shader-gpu-surface-test")
            {
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                try{File.WriteAllText(e.Args[1],JsonSerializer.Serialize(await GpuSurfaceWindow.RunSurfaceTestAsync()));Shutdown(0);}
                catch(Exception ex){File.WriteAllText(e.Args[1],JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Shutdown(1);}
                return;
            }
            if(e.Args.Length==2&&e.Args[0]=="--shader-draft-test"){
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                try{File.WriteAllText(e.Args[1],JsonSerializer.Serialize(await DraftPreviewHost.RunTestAsync()));Shutdown(0);}
                catch(Exception ex){File.WriteAllText(e.Args[1],JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Shutdown(1);}
                return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--shader-lifecycle-test")
            {
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                try { File.WriteAllText(e.Args[1],JsonSerializer.Serialize(await GpuShaderRuntimeWindow.RunLifecycleTestAsync())); Shutdown(0); }
                catch(Exception ex){File.WriteAllText(e.Args[1],JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Shutdown(1);}
                return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--shader-runtime")
            {
                MainWindow = new GpuShaderRuntimeWindow(int.Parse(e.Args[1]));
                MainWindow.Show();
                return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--shader-frame-test")
            {
                var renderer = new WebGlFrameRenderer();
                var probe = new Window { Width = 40, Height = 40, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, Content = renderer.View };
                MainWindow = probe; probe.Show();
                try {
                    string source = await renderer.DefaultSourceAsync();
                    await renderer.CompileAsync(source);
                    string first = await renderer.RenderAsync(64,64,0,.3f), second = await renderer.RenderAsync(64,64,1,.3f);
                    bool rejected = false;
                    try { await renderer.CompileAsync("invalid GLSL"); } catch (InvalidOperationException) { rejected = true; }
                    string afterError = await renderer.RenderAsync(64,64,0,.3f);
                    string Texture(byte red) {
                        var bitmap = BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,red,255},4);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var memory = new MemoryStream(); encoder.Save(memory); return Convert.ToBase64String(memory.ToArray());
                    }
                    var configuration = new ShaderComponent { Backend = "GLSL", Source = "#version 300 es\nprecision highp float;uniform sampler2D albedo;uniform float gain;out vec4 color;void main(){color=texture(albedo,vec2(0.5))*vec4(gain,gain,gain,1.0);}" };
                    configuration.Parameters.Add("gain",1);
                    configuration.Textures.Add(new ShaderTexture { Name="albedo", Mime="image/png", Data=Texture(255) });
                    await renderer.CompileAsync(configuration.Source); await renderer.ConfigureAsync(configuration);
                    string redFrame = await renderer.RenderAsync(64,64,0,.3f);
                    configuration.Parameters["gain"] = .5f; await renderer.ConfigureAsync(configuration);
                    string dimFrame = await renderer.RenderAsync(64,64,0,.3f);
                    configuration.Textures[0].Data = Texture(0); await renderer.ConfigureAsync(configuration);
                    string blackFrame = await renderer.RenderAsync(64,64,0,.3f);
                    bool parametersWork = redFrame != dimFrame, texturesWork = dimFrame != blackFrame;
                    bool passed = first != second && first == afterError && rejected && Convert.FromBase64String(first)[0] == 137 && parametersWork && texturesWork;
                    File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { passed, timeChangesPixels = first != second, oldShaderSurvives = first == afterError, invalidRejected = rejected, parametersWork, texturesWork, pngBytes = Convert.FromBase64String(first).Length }));
                    Shutdown(passed ? 0 : 1);
                } catch (Exception ex) { File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); Shutdown(1); }
                finally { renderer.Dispose(); }
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--shader-component")
            {
                MainWindow = new ShaderComponentWindow(int.Parse(e.Args[1]), e.Args.Length > 2 ? e.Args[2] : null);
                MainWindow.Show();
                return;
            }
            if (e.Args.Contains("--canvas-lab"))
            {
                MainWindow = new CanvasLabWindow();
                MainWindow.Show();
                return;
            }
            if (e.Args.Length > 0 && e.Args[0] is "--install" or "--disable" or "--uninstall" or "--status" or "--request" or "--canvas-bootstrap" or "--canvas-inspect")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                object result;
                switch (e.Args[0])
                {
                    case "--canvas-bootstrap": await CanvasProbeRegistration.EnsureAsync(); result = await new HostClient("PptEaseCanvasV3").RequestAsync("status"); break;
                    case "--canvas-inspect": result = await new HostClient("PptEaseCanvasV3").RequestAsync("inspectCanvas"); break;
                    case "--install": result = new { ok = true, message = await _installer.InstallAsync() }; break;
                    case "--disable": result = new { ok = true, message = await _installer.DisableAsync() }; break;
                    case "--uninstall": result = new { ok = true, message = await _installer.UninstallAsync() }; break;
                    case "--request":
                        if (e.Args.Length < 3) throw new ArgumentException("--request requires op and JSON payload");
                        using (var payload = JsonDocument.Parse(e.Args[2])) result = await _host.RequestAsync(e.Args[1], payload.RootElement.Clone());
                        break;
                    default: result = await _host.RefreshAsync(); break;
                }
                WriteResult(e.Args, result);
                Shutdown(0);
                return;
            }
            if (e.Args.Length == 0 || e.Args.Contains("--setup"))
            {
                MainWindow = new SetupWindow(_installer, _host);
                MainWindow.Show();
                return;
            }
            _window = new MainWindow();
            MainWindow = _window;
            _shader = new ShaderPreviewControl();
            _window.ShaderHost.Content = _shader;
            _window.RefreshRequested += async (_, _) => await RunUiAsync(RefreshAsync);
            _window.LockRequested += async (_, _) => await RunUiAsync(async () => _window.ApplySnapshot(await _host.SetLockedAsync(_window.SelectedShapes, true)));
            _window.UnlockRequested += async (_, _) => await RunUiAsync(async () => _window.ApplySnapshot(await _host.SetLockedAsync(_window.SelectedShapes, false)));
            _window.SelectRequested += async (_, _) => await RunUiAsync(async () => { if (_window.SelectedShapes.Count == 1) await _host.SelectAsync(_window.SelectedShapes[0]); });
            _window.InstallRequested += async (_, _) => await RunUiAsync(async () => { _window.AppendLog(await _installer.InstallAsync()); await RefreshAsync(); });
            _window.DisableRequested += async (_, _) => await RunUiAsync(async () => { _window.AppendLog(await _installer.DisableAsync()); await RefreshAsync(); });
            _window.UninstallRequested += async (_, _) => await RunUiAsync(async () => { _window.AppendLog(await _installer.UninstallAsync()); await RefreshAsync(); });
            _window.PickColorRequested += async (_, _) => await ColorActionAsync("pickColor");
            _window.ApplyColorRequested += async (_, _) => await ColorActionAsync("applyColor");
            _window.CancelColorRequested += async (_, _) => await ColorActionAsync("cancelColor");
            _window.Closed += (_, _) => { _refreshTimer?.Stop(); _shader.Dispose(); };
            if (e.Args.Length > 0 && e.Args[0] is "--self-test" or "--render-ui")
            {
                var directory = e.Args.Length > 1 ? Path.GetFullPath(e.Args[1]) : Path.Combine(Path.GetTempPath(), "PptEase-selftest");
                Directory.CreateDirectory(directory);
                _window.ShowActivated = false;
                _window.ShowInTaskbar = false;
                _window.Left = -18000; _window.Top = -18000;
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Show();
                await RefreshAsync();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                RenderWindow(_window, Path.Combine(directory, "manager.png"));
                if (e.Args[0] == "--self-test")
                {
                    _window.ObjectsPage.Visibility = Visibility.Collapsed;
                    _window.ShaderPage.Visibility = Visibility.Visible;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var shaderResult = await _shader.RunSelfTestAsync(directory).WaitAsync(TimeSpan.FromSeconds(50));
                    File.WriteAllText(Path.Combine(directory, "manager-selftest.json"), JsonSerializer.Serialize(new { uiCreated = true, snapshot = await _host.RefreshAsync(), shader = shaderResult }, new JsonSerializerOptions { WriteIndented = true }));
                }
                _window.Close();
                return;
            }
            _window.Show();
            await RefreshAsync();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += async (_, _) => { if (!_busy) await RunUiAsync(RefreshAsync, false); };
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            var directory = IntegrationInstaller.DataDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "manager-errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine);
            WriteResult(e.Args, new { ok = false, error = ex.Message });
            if (_window != null) _window.AppendLog(ex.Message);
            if (e.Args.Length > 0 && e.Args[0].StartsWith("--")) { _window?.Close(); Shutdown(1); }
            else if (_window == null) Shutdown(1);
        }
    }
    private async Task ColorActionAsync(string op) => await RunUiAsync(async () =>
    {
        var keys = _window!.SelectedShapes.Select(s => s.Key).ToArray();
        var response = await _host.RequestAsync(op, new { keys });
        _window.AppendLog(op == "pickColor" ? global::PptEase.Localization.L.T("已调用 PPT 原生吸管。点击取色；在 PPT 中按 Esc 取消。") : op == "applyColor" ? global::PptEase.Localization.L.T("赋色已准备。请在 PowerPoint 选择目标对象。") : global::PptEase.Localization.L.T("已取消赋色；原生吸管请在 PPT 中按 Esc 取消。"));
        await RefreshAsync();
    });
    private async Task RefreshAsync() => _window?.ApplySnapshot(await _host.RefreshAsync());
    private async Task RunUiAsync(Func<Task> action, bool logError = true)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { if (logError) _window?.AppendLog(ex.Message); }
        finally { _busy = false; }
    }
    private static void WriteResult(string[] args, object result)
    {
        var index = Array.IndexOf(args, "--result");
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        if (index >= 0 && index + 1 < args.Length) { var file = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, json); }
        else { Directory.CreateDirectory(IntegrationInstaller.DataDirectory); File.WriteAllText(Path.Combine(IntegrationInstaller.DataDirectory, "last-command.json"), json); }
    }
    private static void RenderWindow(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing=new DrawingVisual();
        using(var dc=drawing.RenderOpen())dc.DrawRectangle(new VisualBrush(visual){Stretch=Stretch.Fill},null,new Rect(0,0,visual.ActualWidth,visual.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
