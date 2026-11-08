using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PptEase.Shader;

public sealed partial class ShaderComponentWindow
{
    private string baselineDraft="";
    private int errorLine;
    private sealed record DraftFile(string Source,string Parameters,string Dimensions,string Fps,string Backend,float Roughness,List<ShaderTexture> Textures);
    private static string DraftDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PptEase","ShaderDrafts");
    private string draftSession=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
    private string DraftPath=>Path.Combine(DraftDirectory,draftSession+".json");
    private string CaptureDraft()=>JsonSerializer.Serialize(new DraftFile(code.Text,parameters.Text,dimensions.Text,frameRate.Text,component.Backend,(float)roughness.Value,component.Textures));
    private void SaveLocalDraft(bool force=false)
    {
        var text=CaptureDraft();if(!force&&text==baselineDraft)return;
        Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
        File.WriteAllText(DraftPath+".tmp",text);File.Move(DraftPath+".tmp",DraftPath,true);
        // Keep independent editor sessions recoverable, with a bounded local history.
        foreach(var old in new DirectoryInfo(DraftDirectory).GetFiles("*.json").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(30)) {
            if(old.FullName!=DraftPath)try{old.Delete();}catch(IOException){}
        }
    }
    private Task RestoreLocalDraft()
    {
        Directory.CreateDirectory(DraftDirectory);
        var picker=new Microsoft.Win32.OpenFileDialog{Title="选择要恢复的 Shader 草稿",InitialDirectory=DraftDirectory,Filter="Shader 草稿 (*.json)|*.json"};
        if(picker.ShowDialog(this)!=true)return Task.CompletedTask;
        var saved=JsonSerializer.Deserialize<DraftFile>(File.ReadAllText(picker.FileName))??throw new InvalidDataException("草稿损坏。");
        if(saved.Source==null||saved.Parameters==null||saved.Textures==null||saved.Dimensions==null||saved.Fps==null||!float.IsFinite(saved.Roughness)||saved.Textures.Any(t=>t==null)||saved.Backend is not ("HLSL" or "GLSL"))throw new InvalidDataException("草稿格式无效。");
        // Preserve current edits before loading another session, including the same file.
        SaveLocalDraft();
        draftSession=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
        component.Backend=saved.Backend;component.Textures=saved.Textures;parameters.Text=saved.Parameters;
        dimensions.Text=saved.Dimensions;frameRate.Text=saved.Fps;roughness.Value=saved.Roughness;code.Text=saved.Source;
        RefreshTextures();ScheduleDraftPreview();draftState.Text="已恢复本地草稿，正在更新预览，尚未写入 PPT";return Task.CompletedTask;
    }
    private void SetupEditorTools(Panel actions)
    {
        actions.Children.Add(WorkspaceButton("保存草稿",()=>{SaveLocalDraft(true);draftState.Text="草稿已保存在本机，未写入 PPT";return Task.CompletedTask;}));
        actions.Children.Add(WorkspaceButton("恢复草稿",RestoreLocalDraft));
        actions.Children.Add(WorkspaceButton("定位错误",()=>{if(errorLine>0){int index=code.GetCharacterIndexFromLineIndex(Math.Min(code.LineCount-1,errorLine-1));code.Focus();code.Select(Math.Max(0,index),0);code.ScrollToLine(Math.Max(0,errorLine-1));}return Task.CompletedTask;}));
        var caret=new TextBlock{Margin=new Thickness(8),VerticalAlignment=VerticalAlignment.Center};actions.Children.Add(caret);
        code.SelectionChanged+=(_,_)=>{int line=code.GetLineIndexFromCharacterIndex(code.CaretIndex);caret.Text=$"行 {line+1} / {code.LineCount}";};
        code.PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.Tab&&(Keyboard.Modifiers==ModifierKeys.None||Keyboard.Modifiers==ModifierKeys.Shift)){
                var edit=IndentSelection(code.Text,code.SelectionStart,code.SelectionLength,Keyboard.Modifiers==ModifierKeys.Shift);
                code.BeginChange();try{code.Select(edit.Start,edit.Length);code.SelectedText=edit.Replacement;code.Select(edit.Start+edit.CaretOffset,edit.SelectionLength);}finally{code.EndChange();}
                e.Handled=true;return;
            }
            if(e.Key!=Key.Enter||Keyboard.Modifiers!=ModifierKeys.None)return;
            int line=code.GetLineIndexFromCharacterIndex(code.CaretIndex);string text=code.GetLineText(line);
            int insertion=code.SelectionStart;
            string indent=Regex.Match(text,@"^[\t ]*").Value;
            int offset=code.CaretIndex-code.GetCharacterIndexFromLineIndex(line);
            if(text[..Math.Min(offset,text.Length)].TrimEnd().EndsWith("{"))indent+="    ";
            code.SelectedText=Environment.NewLine+indent;code.CaretIndex=insertion+Environment.NewLine.Length+indent.Length;code.SelectionLength=0;e.Handled=true;
        };
        Closing+=(_,e)=>{try{SaveLocalDraft();}catch(Exception ex){status.Text="本地草稿保存失败："+ex.Message;e.Cancel=true;MessageBox.Show(this,"草稿尚未保存，请复制源码备份后重试。\n"+ex.Message,"草稿保存失败",MessageBoxButton.OK,MessageBoxImage.Warning);}};
    }
    internal static (int Start,int Length,string Replacement,int CaretOffset,int SelectionLength) IndentSelection(string text,int start,int length,bool remove)
    {
        if(length==0&&!remove)return(start,0,"    ",4,0);
        int first=start==0?0:text.LastIndexOf('\n',start-1)+1;
        int end=start+length;
        if(length>0&&end>first&&text[end-1]=='\n')end--;
        int last=text.IndexOf('\n',end);if(last<0)last=text.Length;
        string block=text.Substring(first,last-first);
        string[] lines=block.Split('\n');int removedFirst=0;
        for(int i=0;i<lines.Length;i++){
            int n=0;
            if(remove){if(lines[i].StartsWith("\t"))n=1;else while(n<Math.Min(4,lines[i].Length)&&lines[i][n]==' ')n++;lines[i]=lines[i][n..];}
            else lines[i]="    "+lines[i];
            if(i==0)removedFirst=n;
        }
        string replacement=string.Join("\n",lines);
        return(first,last-first,replacement,length==0?Math.Max(0,start-first-removedFirst):0,length==0?0:replacement.Length);
    }
    private void SetCompileError(string message)
    {
        var match=Regex.Match(message,@"\((\d+),\d+\)|ERROR:\s*\d+:(\d+):");
        errorLine=match.Success?int.Parse(match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value):0;
        draftState.Text="编译失败，保留上一有效预览"+(errorLine>0?$" · 第 {errorLine} 行":"");status.Text=message;
    }
}
