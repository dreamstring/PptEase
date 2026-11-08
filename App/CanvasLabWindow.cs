using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PptEase;

// A user-operated probe: never synthesizes input or modifies slide objects.
public sealed class CanvasLabWindow : Window
{
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
    private readonly List<object> records = [];
    private readonly WrapPanel buttons = new() { Orientation = Orientation.Horizontal };
    private bool cleanupDone;
    public CanvasLabWindow()
    {
        Title = "PPT 自由画布 · V3";
        Width = 780; Height = 430;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var intro = new TextBlock { Text = "先点“启用画布接管”，再回 PPT：空格＋左键或中键拖动；滚轮平移，Shift＋滚轮横移，Ctrl＋滚轮以鼠标位置缩放。接管仅限编辑画布。关闭本窗口会停用。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(intro, Dock.Top); panel.Children.Add(intro);
        DockPanel.SetDock(buttons, Dock.Top); panel.Children.Add(buttons);
        Add("读取视口", "read"); Add("向负坐标越界", "negative"); Add("向正坐标越界", "positive"); Add("居中适配", "fit");
        Add("启用画布接管", "enableCanvasInput"); Add("停用画布接管", "disableCanvasInput"); Add("接管状态", "status");
        panel.Children.Add(log); Content = panel;
    }
    private void Add(string text, string operation)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += async (_, _) =>
        {
            buttons.IsEnabled = false;
            try
            {
                object result;
                if (operation is "negative" or "positive")
                {
                    await CanvasProbeRegistration.EnsureAsync();
                    int sign = operation == "negative" ? -1 : 1;
                    result = await new HostClient("PptEaseCanvasV3").RequestAsync("panCanvas", new { dx = sign * 400, dy = sign * 300 });
                }
                else if (operation is "enableCanvasInput" or "disableCanvasInput" or "status")
                {
                    await CanvasProbeRegistration.EnsureAsync();
                    result = await new HostClient("PptEaseCanvasV3").RequestAsync(operation);
                }
                else result = await ComAutomation.RunStaAsync(() => Probe(operation));
                records.Add(result);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                log.AppendText(json + Environment.NewLine); log.ScrollToEnd();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "canvas-lab-results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { log.AppendText(ex.Message + Environment.NewLine); }
            finally { buttons.IsEnabled = true; }
        };
        buttons.Children.Add(button);
    }
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!cleanupDone)
        {
            e.Cancel = true; buttons.IsEnabled = false;
            try { await CanvasProbeRegistration.RemoveAsync(); }
            finally { cleanupDone = true; Close(); }
        }
        base.OnClosing(e);
    }
    private static object Probe(string operation)
    {
        dynamic app = ComAutomation.GetPowerPoint();
        dynamic window = app.ActiveWindow;
        if ((int)window.ViewType != 9 && (int)window.ViewType != 1) throw new InvalidOperationException("请切换到普通编辑视图。");
        dynamic slide = window.View.Slide;
        int beforeSlide = (int)slide.SlideID;
        float x0 = (float)window.PointsToScreenPixelsX(0), y0 = (float)window.PointsToScreenPixelsY(0);
        int zoomBefore = (int)window.View.Zoom;
        if (operation == "negative") window.ScrollIntoView(-1500, -1500, 100, 100, -1);
        else if (operation == "positive") window.ScrollIntoView(2500, 2500, 100, 100, -1);
        else if (operation == "fit") window.View.ZoomToFit = -1;
        float x1 = (float)window.PointsToScreenPixelsX(0), y1 = (float)window.PointsToScreenPixelsY(0);
        return new { operation, utc = DateTime.UtcNow, presentation = (string)app.ActivePresentation.Name, beforeSlide, afterSlide = (int)window.View.Slide.SlideID, zoomBefore, zoomAfter = (int)window.View.Zoom, beforeOrigin = new[] { x0, y0 }, afterOrigin = new[] { x1, y1 }, moved = x0 != x1 || y0 != y1 };
    }
}
