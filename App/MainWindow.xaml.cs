using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PptEase.Models;

namespace PptEase;

public partial class MainWindow : Window
{
    private bool _hostConnected;
    private bool _integrationLoaded;
    public event EventHandler? RefreshRequested;
    public event EventHandler? LockRequested;
    public event EventHandler? UnlockRequested;
    public event EventHandler? SelectRequested;
    public event EventHandler? InstallRequested;
    public event EventHandler? DisableRequested;
    public event EventHandler? UninstallRequested;
    public event EventHandler? PickColorRequested;
    public event EventHandler? ApplyColorRequested;
    public event EventHandler? CancelColorRequested;

    public MainWindow()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ShapeItem> SelectedShapes => ShapesGrid.SelectedItems.Cast<ShapeItem>().ToList();

    public void ApplySnapshot(HostSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplySnapshot(snapshot));
            return;
        }
        var selectedKeys = SelectedShapes.Select(s => s.Key).ToHashSet();
        _hostConnected = snapshot.Connected;
        _integrationLoaded = snapshot.IntegrationLoaded;
        ConnectionText.Text = snapshot.IntegrationLoaded ? "已连接 PowerPoint" : snapshot.Connected ? "PowerPoint 已运行 · 增强未加载" : "尚未连接 PowerPoint";
        ConnectionDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(snapshot.IntegrationLoaded ? "#4C8C61" : snapshot.Connected ? "#B88635" : "#9AA3AD"));
        PresentationLabel.Text = string.IsNullOrWhiteSpace(snapshot.PresentationName) ? "打开 PowerPoint 后点击刷新" : snapshot.PresentationName;
        SlideLabel.Text = snapshot.SlideIndex > 0 ? $"第 {snapshot.SlideIndex} 页 · 当前活动幻灯片" : "尚无活动幻灯片";
        HostVersionLabel.Text = snapshot.Connected ? snapshot.HostVersion : "未连接";
        IntegrationLabel.Text = snapshot.IntegrationLoaded ? "已加载" : "尚未加载";
        PickColorButton.IsEnabled = snapshot.IntegrationLoaded;
        ApplyColorButton.IsEnabled = snapshot.IntegrationLoaded;
        CancelColorButton.IsEnabled = snapshot.IntegrationLoaded;
        ShapesGrid.ItemsSource = snapshot.Shapes;
        foreach (var item in snapshot.Shapes.Where(s => selectedKeys.Contains(s.Key))) ShapesGrid.SelectedItems.Add(item);
        ObjectCountLabel.Text = $"{snapshot.Shapes.Count} 个对象";
        EmptyLabel.Visibility = snapshot.Shapes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Text = snapshot.IntegrationLoaded ? "当前幻灯片没有可管理的对象" : "请在“连接与诊断”安装并启用增强";
        StatusText.Text = string.IsNullOrWhiteSpace(snapshot.Status) ? (snapshot.Connected ? "就绪" : "尚未连接 PowerPoint") : snapshot.Status;
        UpdateSelectionActions();
    }

    public void AppendLog(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AppendLog(text)); return; }
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        LogBox.ScrollToEnd();
        StatusText.Text = text.Replace(Environment.NewLine, " ");
    }

    private void UpdateSelectionActions()
    {
        var count = ShapesGrid.SelectedItems.Count;
        SelectionLabel.Text = count > 0 ? $"已选择 {count} 个对象" : "按住 Ctrl 或 Shift 可多选对象";
        LockButton.IsEnabled = _integrationLoaded && count > 0;
        UnlockButton.IsEnabled = _integrationLoaded && count > 0;
        LocateButton.IsEnabled = _integrationLoaded && count == 1;
    }

    private void ShowPage(int page)
    {
        // Navigation Checked can run during InitializeComponent before named pages exist.
        if (ObjectsPage == null || ShaderPage == null || DiagnosticsPage == null) return;
        ObjectsPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShaderPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ObjectsNav_Checked(object sender, RoutedEventArgs e) => ShowPage(0);
    private void ShaderNav_Checked(object sender, RoutedEventArgs e) => ShowPage(1);
    private void DiagnosticsNav_Checked(object sender, RoutedEventArgs e) => ShowPage(2);
    private void ShapesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectionLabel != null && LockButton != null) UpdateSelectionActions();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void Lock_Click(object sender, RoutedEventArgs e) => LockRequested?.Invoke(this, EventArgs.Empty);
    private void Unlock_Click(object sender, RoutedEventArgs e) => UnlockRequested?.Invoke(this, EventArgs.Empty);
    private void Select_Click(object sender, RoutedEventArgs e) => SelectRequested?.Invoke(this, EventArgs.Empty);
    private void Install_Click(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);
    private void Disable_Click(object sender, RoutedEventArgs e) => DisableRequested?.Invoke(this, EventArgs.Empty);
    private void Uninstall_Click(object sender, RoutedEventArgs e) => UninstallRequested?.Invoke(this, EventArgs.Empty);
    private void PickColor_Click(object sender, RoutedEventArgs e) => PickColorRequested?.Invoke(this, EventArgs.Empty);
    private void ApplyColor_Click(object sender, RoutedEventArgs e) => ApplyColorRequested?.Invoke(this, EventArgs.Empty);
    private void CancelColor_Click(object sender, RoutedEventArgs e) => CancelColorRequested?.Invoke(this, EventArgs.Empty);
}
