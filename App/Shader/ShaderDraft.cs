using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PptEase.Shader;

public sealed partial class ShaderComponentWindow
{
    private readonly DraftPreviewHost draftPreview=new();
    private readonly DispatcherTimer draftDebounce=new(){Interval=TimeSpan.FromMilliseconds(650)};
    private readonly TextBlock draftState=new(){Text=global::PptEase.Localization.L.T("草稿预览 · 不写入 PPT"),Margin=new Thickness(0,6,0,6)};
    private bool draftCompiling,draftAgain,draftPaused;
    private ShaderComponent ReadDraft()
    {
        var draft=JsonSerializer.Deserialize<ShaderComponent>(JsonSerializer.Serialize(component))!;
        draft.Source=code.Text;draft.Parameters=ReadParameters();draft.Bindings.Clear();
        string[] size=dimensions.Text.ToLowerInvariant().Split('x','×');
        if(size.Length!=2||!int.TryParse(size[0],out int w)||!int.TryParse(size[1],out int h))throw new InvalidDataException(global::PptEase.Localization.L.T("尺寸格式应为 960x600。"));
        draft.Width=w;draft.Height=h;draft.Validate();return draft;
    }
    private void ScheduleDraftPreview()
    {
        if(closed)return;
        draftPreview.InvalidateCompilation();draftState.Text=global::PptEase.Localization.L.T("草稿已修改 · 等待预览");
        draftDebounce.Stop();draftDebounce.Start();
    }
    private async Task CompileDraft()
    {
        if(closed)return;
        if(draftCompiling){draftAgain=true;return;}
        draftCompiling=true;
        try{
            SaveLocalDraft();RefreshParameterPanel();draftState.Text=global::PptEase.Localization.L.T("正在编译草稿…原预览继续保留");
            if(await draftPreview.CompileAsync(ReadDraft())){errorLine=0;draftState.Text=global::PptEase.Localization.L.T("草稿预览已更新 · 尚未应用到 PPT");}
        }catch(Exception ex){SetCompileError(ex.Message);}
        finally{draftCompiling=false;if(draftAgain&&!closed){draftAgain=false;draftDebounce.Start();}}
    }
    private void SetupDraftPreview(DockPanel editor)
    {
        editor.Children.Remove(code);
        var split=new Grid();split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(3,GridUnitType.Star)});
        split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(6)});
        split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(2,GridUnitType.Star),MinHeight=120});
        var sourcePane=new DockPanel();
        var gutter=new ShaderLineNumbers(code);DockPanel.SetDock(gutter,Dock.Left);sourcePane.Children.Add(gutter);sourcePane.Children.Add(code);split.Children.Add(sourcePane);
        var divider=new GridSplitter{Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows};Grid.SetRow(divider,1);split.Children.Add(divider);
        var pane=new DockPanel();Grid.SetRow(pane,2);split.Children.Add(pane);
        var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Top);pane.Children.Add(actions);
        actions.Children.Add(WorkspaceButton(global::PptEase.Localization.L.T("编译草稿预览"),CompileDraft));
        var playback=WorkspaceButton(global::PptEase.Localization.L.T("暂停预览"),()=>Task.CompletedTask);
        playback.Click+=async(_,_)=>{draftPaused=!draftPaused;playback.Content=draftPaused?global::PptEase.Localization.L.T("继续预览"):global::PptEase.Localization.L.T("暂停预览");try{await draftPreview.SetPlaybackAsync(draftPaused,WindowState==WindowState.Minimized);}catch(Exception ex){draftState.Text=global::PptEase.Localization.L.T("预览状态更新失败：")+ex.Message;}};
        actions.Children.Add(playback);SetupEditorTools(actions);
        StateChanged+=async(_,_)=>{try{await draftPreview.SetPlaybackAsync(draftPaused,WindowState==WindowState.Minimized);}catch(Exception ex){draftState.Text=global::PptEase.Localization.L.T("预览状态更新失败：")+ex.Message;}};
        DockPanel.SetDock(draftState,Dock.Top);pane.Children.Add(draftState);pane.Children.Add(draftPreview);editor.Children.Add(split);
        draftPreview.Error+=message=>draftState.Text=global::PptEase.Localization.L.T("预览异常：")+message;
        code.TextChanged+=(_,_)=>ScheduleDraftPreview();
        dimensions.TextChanged+=(_,_)=>ScheduleDraftPreview();
        draftDebounce.Tick+=async(_,_)=>{draftDebounce.Stop();await CompileDraft();};
        baselineDraft=CaptureDraft();
        Loaded+=(_,_)=>draftDebounce.Start();
        Closed+=(_,_)=>{draftDebounce.Stop();draftPreview.Dispose();};
    }
}
