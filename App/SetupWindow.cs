using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PptEase;
public sealed class SetupWindow : Window
{
    private readonly IntegrationInstaller installer;
    private readonly HostClient host;
    private readonly WrapPanel actions = new() { Orientation = Orientation.Horizontal };
    private Button? forceClose;
    private Button? pptAction;
    private bool pptRunning;
    private bool closeRequested;
    private readonly DispatcherTimer processTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) };
    private readonly TextBlock connection = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock shaderStatus = new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0) };
    public SetupWindow(IntegrationInstaller installer, HostClient host)
    {
        this.installer=installer;this.host=host;
        Title="PPT 增强 · 安装与维护";Width=700;Height=420;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var body=new StackPanel { Margin=new Thickness(24) };
        body.Children.Add(new TextBlock { Text="PPT 增强",FontSize=24,Margin=new Thickness(0,0,0,12) });
        body.Children.Add(new TextBlock { Text="安装一次后，PowerPoint 启动时自动加载。日常功能在“PPT 增强”选项卡中；本窗口可以关闭。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18) });
        Add("安装 / 修复",installer.InstallAsync);Add("停用增强",installer.DisableAsync);Add("卸载加载项",installer.UninstallAsync);
        pptAction=Add("打开 PPT",async()=>
        {
            // Follow the displayed action; a newly started PPT must not turn an
            // "open" click into an unexpected close request.
            if(!pptRunning)
            {
                closeRequested=false;
                return PowerPointProcessControl.Open();
            }
            closeRequested=true;
            string message=await PowerPointProcessControl.RequestCloseAsync();
            return message;
        },refresh:false);
        forceClose=Add("强制结束 PPT…",async()=>
        {
            var targets=PowerPointProcessControl.Capture();
            if(targets.Length==0)return "没有正在运行的 PowerPoint 进程。";
            var answer=MessageBox.Show(this,$"强制结束这 {targets.Length} 个 PowerPoint 进程？\nPID：{string.Join(", ",targets.Select(t=>t.Id))}\n\n未保存的文稿修改可能丢失。建议先返回 PPT 保存，或取消此操作。","强制结束 PowerPoint",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No);
            if(answer!=MessageBoxResult.Yes)return "已取消强制结束，PPT 未被强制关闭。";
            return await PowerPointProcessControl.ForceCloseAsync(targets);
        },refresh:false);
        forceClose.Visibility=Visibility.Collapsed;
        body.Children.Add(actions);body.Children.Add(result);body.Children.Add(connection);body.Children.Add(shaderStatus);Content=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        processTimer.Tick+=(_,_)=>{if(actions.IsEnabled)UpdateProcessAction();ReadShaderStatus();};
        Loaded+=async(_,_)=>{UpdateProcessAction();processTimer.Start();await Refresh();};
        Closed+=(_,_)=>processTimer.Stop();
    }
    private void ReadShaderStatus()
    {
        try{string path=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PptEase","shader-runtime-status.txt");
            shaderStatus.Text=System.IO.File.Exists(path)?"Shader 最近状态：\n"+System.IO.File.ReadAllText(path):"Shader：GPU 播放版；当前尚无后台状态。";}catch{}
    }
    private Button Add(string label,Func<Task<string>> operation,bool refresh=true)
    {
        var button=new Button {Content=label,Padding=new Thickness(14,8,14,8),Margin=new Thickness(0,0,10,0)};
        button.Click+=async(_,_)=>
        {
            actions.IsEnabled=false;result.Text="正在处理，请稍候……";
            try
            {
                result.Text=await operation();
                if(refresh)await Refresh();
                else connection.Text="当前 PowerPoint 进程数："+PowerPointProcessControl.Capture().Length;
            }
            catch(Exception ex){result.Text=ex.Message;}
            finally{UpdateProcessAction();actions.IsEnabled=true;}
        };
        actions.Children.Add(button);
        return button;
    }
    private void UpdateProcessAction()
    {
        try
        {
            pptRunning=PowerPointProcessControl.Capture().Length>0;
            if(!pptRunning)closeRequested=false;
            if(pptAction!=null)pptAction.Content=pptRunning?"关闭所有 PPT":"打开 PPT";
            if(forceClose!=null)forceClose.Visibility=pptRunning&&closeRequested?Visibility.Visible:Visibility.Collapsed;
        }
        catch(Exception ex){connection.Text="读取 PowerPoint 状态失败："+ex.Message;}
    }
    private async Task Refresh()
    {
        var snapshot=await host.RefreshAsync();
        connection.Text=snapshot.IntegrationLoaded?"当前 PPT 已连接 · 宿主版本 "+snapshot.HostBuild:snapshot.Status;
        if(snapshot.IntegrationLoaded){try{var state=await host.RequestAsync("shaderScene",new{});bool current=state.TryGetProperty("InteractionVersion",out var v)&&v.GetInt32()>=2;connection.Text+=current?"\n已支持二维十字轴交互。":"\n当前PPT仍加载旧宿主，二维控件不可用。请安装修复后保存文稿，关闭所有PPT进程（包括后台残留）再重新打开。";}catch{}}
    }
}
