using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PptEase.Shader;

/// <summary>Real, isolated material preview. It does not hook PowerPoint's canvas.</summary>
public sealed class ShaderPreviewControl : UserControl, IDisposable
{
    private readonly ComboBox _backend = new() { Width = 176, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox _code = new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(218, 224, 230)) };
    private readonly TextBlock _status = new() { Text = global::PptEase.Localization.L.T("选择后端，编辑源码后点击编译。"), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(80, 94, 107)), Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _clock = new() { Text = "0.00 s", Width = 70, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _play = MakeButton(global::PptEase.Localization.L.T("播放"));
    private readonly Slider _roughness = new() { Minimum = 0, Maximum = 1, Value = .3, Width = 130, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _preview = new() { Background = new SolidColorBrush(Color.FromRgb(235, 239, 243)), MinHeight = 260 };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly WebView2 _web = new() { Visibility = Visibility.Visible, DefaultBackgroundColor = System.Drawing.Color.FromArgb(235, 239, 243) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private D3DShaderRenderer? _d3d;
    private Task? _webInitialization;
    private string _glslSource = "";
    private string _hlslSource = D3DShaderRenderer.DefaultSource;
    private int _lastBackend = -1;
    private bool _playing, _changingBackend, _renderBusy, _disposed;
    private float _time;
    public event EventHandler<string>? LogMessage;

    public ShaderPreviewControl()
    {
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var note = new TextBlock { Text = global::PptEase.Localization.L.T("软件内材质预览 · GLSL ES 3.00 / HLSL ps_5_0。此处尚未接入 PPT 原位或放映画布。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), Foreground = new SolidColorBrush(Color.FromRgb(85, 99, 112)) }; root.Children.Add(note);
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; Grid.SetRow(bar, 1); root.Children.Add(bar);
        _backend.Items.Add("GLSL ES · WebGL 2"); _backend.Items.Add("HLSL · Direct3D 11"); bar.Children.Add(_backend);
        var compile = MakeButton(global::PptEase.Localization.L.T("编译并预览")); compile.Click += async (_, _) => await CompileAsync(); bar.Children.Add(compile);
        _play.Click += async (_, _) => { _playing = !_playing; _play.Content = _playing ? global::PptEase.Localization.L.T("暂停") : global::PptEase.Localization.L.T("播放"); await UpdateWebStateAsync(); }; bar.Children.Add(_play);
        var reset = MakeButton(global::PptEase.Localization.L.T("重置时间")); reset.Click += async (_, _) => { _time = 0; await RenderAsync(); }; bar.Children.Add(reset);
        bar.Children.Add(new TextBlock { Text = global::PptEase.Localization.L.T("粗糙度"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) }); bar.Children.Add(_roughness); bar.Children.Add(_clock);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); Grid.SetRow(body, 2); root.Children.Add(body);
        body.Children.Add(_code); Grid.SetColumn(_preview, 2); body.Children.Add(_preview); _preview.Children.Add(_image); _preview.Children.Add(_web);
        Grid.SetRow(_status, 3); root.Children.Add(_status); Content = root;
        _backend.SelectionChanged += async (_, _) => await ChangeBackendAsync();
        _roughness.ValueChanged += async (_, _) => { if (!_changingBackend) await RenderAsync(); };
        _timer.Tick += async (_, _) => { if (_playing) { _time += .066f; await RenderAsync(); } };
        Loaded += async (_, _) => { if (_disposed) return; try { await EnsureWebAsync(); if (_backend.SelectedIndex < 0) _backend.SelectedIndex = 0; _timer.Start(); } catch (Exception ex) { ShowError(ex.Message); } };
        Unloaded += (_, _) => { _playing = false; _play.Content = global::PptEase.Localization.L.T("播放"); _timer.Stop(); _ = UpdateWebStateAsync(); };
    }

    private static Button MakeButton(string text) => new() { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), MinHeight = 32 };
    private async Task EnsureWebAsync()
    {
        _webInitialization ??= InitializeWebAsync(); await _webInitialization;
    }
    private async Task InitializeWebAsync()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptEase", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, data);
        await _web.EnsureCoreWebView2Async(environment);
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.CoreWebView2.NavigationStarting += (_, e) => { if (!string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        _web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _web.CoreWebView2.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !e.Request.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Offline preview", "Content-Type: text/plain");
        };
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(object? _, CoreWebView2NavigationCompletedEventArgs e) { _web.NavigationCompleted -= Complete; if (e.IsSuccess) loaded.TrySetResult(true); else loaded.TrySetException(new InvalidOperationException(global::PptEase.Localization.L.T("材质预览页面加载失败：") + e.WebErrorStatus)); }
        _web.NavigationCompleted += Complete;
        string htmlPath = Path.Combine(AppContext.BaseDirectory, "Shader", "preview.html");
        if (!File.Exists(htmlPath)) throw new FileNotFoundException(global::PptEase.Localization.L.T("缺少离线材质预览页面。"), htmlPath);
        _web.NavigateToString((await File.ReadAllTextAsync(htmlPath)).Replace("<html>", "<html lang=\"" + global::PptEase.Localization.L.Language + "\">"));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        string result = await _web.ExecuteScriptAsync("window.preview?.defaultSource ?? ''");
        _glslSource = JsonSerializer.Deserialize<string>(result) ?? "";
        if (string.IsNullOrEmpty(_glslSource)) throw new InvalidOperationException(global::PptEase.Localization.L.T("WebGL 2 初始化失败。请切换 HLSL 后端，或检查 WebView2 图形支持。"));
    }
    private async Task ChangeBackendAsync()
    {
        if (_changingBackend || _disposed) return;
        _changingBackend = true; _playing = false; _play.Content = global::PptEase.Localization.L.T("播放");
        try
        {
            if (_lastBackend == 0) _glslSource = _code.Text;
            if (_lastBackend == 1) _hlslSource = _code.Text;
            _lastBackend = _backend.SelectedIndex;
            if (_backend.SelectedIndex == 0)
            {
                _web.Visibility = Visibility.Visible; _image.Visibility = Visibility.Collapsed;
                await EnsureWebAsync(); _code.Text = _glslSource;
            }
            else
            {
                _web.Visibility = Visibility.Collapsed; _image.Visibility = Visibility.Visible;
                _d3d ??= new D3DShaderRenderer(); _code.Text = _hlslSource;
                await UpdateWebStateAsync();
            }
            await CompileAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _changingBackend = false; }
    }
    private async Task CompileAsync()
    {
        try
        {
            if (_backend.SelectedIndex == 0)
            {
                await EnsureWebAsync(); string raw = await _web.ExecuteScriptAsync("window.preview.compile(" + JsonSerializer.Serialize(_code.Text) + ")");
                using var result = JsonDocument.Parse(raw);
                if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.RootElement.GetProperty("error").GetString());
                _glslSource = _code.Text; _status.Text = global::PptEase.Localization.L.T("GLSL ES 编译成功 · WebGL 2 纹理预览");
            }
            else { _d3d ??= new D3DShaderRenderer(); _d3d.CompilePixelShader(_code.Text); _status.Text = global::PptEase.Localization.L.T("HLSL 编译成功 · D3D11 ") + _d3d.DeviceKind + global::PptEase.Localization.L.T(" · 像素回读路径"); }
            _status.Foreground = new SolidColorBrush(Color.FromRgb(43, 107, 72)); await RenderAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async Task RenderAsync()
    {
        if (_disposed || _renderBusy) return;
        _renderBusy = true;
        try
        {
            _clock.Text = _time.ToString("0.00") + " s";
            if (_backend.SelectedIndex == 0) await UpdateWebStateAsync();
            else if (_backend.SelectedIndex == 1 && _d3d != null)
            {
                byte[] pixels = _d3d.Render(_time, (float)_roughness.Value);
                var bitmap = BitmapSource.Create(_d3d.Width, _d3d.Height, 96, 96, PixelFormats.Bgra32, null, pixels, _d3d.Width * 4); bitmap.Freeze(); _image.Source = bitmap;
            }
        }
        catch (Exception ex) { _playing = false; _play.Content = global::PptEase.Localization.L.T("播放"); ShowError(ex.Message); }
        finally { _renderBusy = false; }
    }
    private async Task UpdateWebStateAsync()
    {
        if (_disposed || _web.CoreWebView2 == null) return;
        await _web.ExecuteScriptAsync("window.preview?.setState(" + JsonSerializer.Serialize(new { time = _time, roughness = _roughness.Value, playing = _playing && _backend.SelectedIndex == 0 }) + ")");
    }
    private void ShowError(string text) { _status.Foreground = new SolidColorBrush(Color.FromRgb(163, 51, 21)); _status.Text = text; LogMessage?.Invoke(this, global::PptEase.Localization.L.T("材质预览：") + text); }

    public async Task<object> RunSelfTestAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        _playing = false; _timer.Stop();
        var renderer = new D3DShaderRenderer(64, 64);
        object hlsl;
        try
        {
            byte[] zero = renderer.Render(0, .3f), one = renderer.Render(1, .3f), repeat = renderer.Render(0, .3f);
            bool invalidRejected = false; try { renderer.CompilePixelShader("broken shader"); } catch (InvalidOperationException) { invalidRejected = true; }
            byte[] after = renderer.Render(0, .3f);
            string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
            bool drawn = zero.Where((_, i) => i % 4 != 3).Any(b => b != 0);
            hlsl = new { ok = drawn && Hash(zero) != Hash(one) && Hash(zero) == Hash(repeat) && invalidRejected && Hash(after) == Hash(zero), backend = "D3D11", renderer.DeviceKind, renderer.FeatureLevel, drawn, timeChangesPixels = Hash(zero) != Hash(one), repeatDeterministic = Hash(zero) == Hash(repeat), invalidRejected, oldShaderSurvives = Hash(after) == Hash(zero), hash0 = Hash(zero), hash1 = Hash(one) };
            var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, zero, 64 * 4); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var png = File.Create(Path.Combine(outputDirectory, "hlsl-preview.png")); encoder.Save(png);
        }
        finally { renderer.Dispose(); }
        object glsl;
        try { await EnsureWebAsync(); string raw = await _web.ExecuteScriptAsync("window.preview.selfTest()"); glsl = JsonSerializer.Deserialize<JsonElement>(raw); }
        catch (Exception ex) { glsl = new { ok = false, error = ex.Message }; }
        var result = new { scope = "Software-internal material preview; not PowerPoint canvas or slideshow", hlsl, glsl };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "shader-selftest.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (IsLoaded) _timer.Start(); return result;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _d3d?.Dispose(); _d3d = null; _web.Dispose();
    }
}
