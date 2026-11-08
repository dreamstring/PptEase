using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace PptEase.Shader;

public sealed partial class ShaderComponentWindow
{
    private readonly StackPanel parameterRows=new();
    private readonly StackPanel textureRows=new();
    private bool updatingPanel;
    private Action? refreshBindingAvailability;
    private static Brush Ink=new SolidColorBrush(Color.FromRgb(220,222,226));
    private static Brush Muted=new SolidColorBrush(Color.FromRgb(151,158,168));
    private static Brush Accent=new SolidColorBrush(Color.FromRgb(41,135,214));
    private Button WorkspaceButton(string label,Func<Task> action)
    {
        var button=new Button{Content=label,Margin=new Thickness(0,4,6,4),Padding=new Thickness(10,7,10,7)};
        button.Click+=async(_,_)=>await Run(action);return button;
    }
    private void BuildWorkspace(DockPanel old,WrapPanel toolbar,StackPanel settings)
    {
        Title="PptEase · Shader Studio";Width=1180;Height=790;MinWidth=980;MinHeight=650;
        Background=new SolidColorBrush(Color.FromRgb(24,25,28));Foreground=Ink;FontFamily=new FontFamily("Segoe UI");FontSize=13;
        InstallStudioTheme();
        old.Children.Remove(toolbar);old.Children.Remove(settings);old.Children.Remove(status);old.Children.Remove(code);
        var layout=new DockPanel{Margin=new Thickness(12),Background=Background};Content=layout;
        var header=new DockPanel{Margin=new Thickness(0,0,0,16)};DockPanel.SetDock(header,Dock.Top);layout.Children.Add(header);
        header.Children.Add(new TextBlock{Text="Shader Studio",FontSize=19,FontWeight=FontWeights.SemiBold});
        header.Children.Add(new TextBlock{Text="   GPU · 参数与放映交互",Foreground=Muted,VerticalAlignment=VerticalAlignment.Center});
        // Keep command handlers, but move texture controls into the resource rail.
        var resourceButtons=toolbar.Children.OfType<Button>().Where(b=>b.Content is string s&&(s.Contains("纹理"))).ToArray();
        foreach(var b in resourceButtons)toolbar.Children.Remove(b);
        toolbar.Children.Remove(textureName);toolbar.Children.Remove(roughness);
        foreach(var label in toolbar.Children.OfType<TextBlock>().Where(t=>t.Text=="粗糙度").ToArray())toolbar.Children.Remove(label);
        DockPanel.SetDock(toolbar,Dock.Top);toolbar.Margin=new Thickness(0,0,0,12);layout.Children.Add(toolbar);
        var footer=new Border{Background=PanelBrush,CornerRadius=new CornerRadius(3),Padding=new Thickness(12),Margin=new Thickness(0,12,0,0),Child=new ScrollViewer{Content=status,MaxHeight=110,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(290),MinWidth=180,MaxWidth=420});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});grid.ColumnDefinitions.Add(new ColumnDefinition());layout.Children.Add(grid);
        var railDivider=new GridSplitter{Width=6,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Stretch,ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext};Grid.SetColumn(railDivider,1);grid.Children.Add(railDivider);
        var sections=new StackPanel();var rail=sections;var railScroll=new ScrollViewer{Content=sections,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};grid.Children.Add(new Border{Background=PanelBrush,CornerRadius=new CornerRadius(3),Padding=new Thickness(12),Child=railScroll});
        void Heading(string text){rail=new StackPanel();sections.Children.Add(new Expander{Header=text,IsExpanded=text=="参数",Content=rail,Margin=new Thickness(0,8,0,8)});}
        Heading("贴图资源");rail.Children.Add(new TextBlock{Text="输入槽名，然后导入图片。纹理随组件保存。",Foreground=Muted,TextWrapping=TextWrapping.Wrap});
        textureName.Width=double.NaN;textureName.Margin=new Thickness(0,8,0,4);rail.Children.Add(textureName);
        var texturesBar=new WrapPanel();foreach(var b in resourceButtons){b.Padding=new Thickness(10,6,10,6);texturesBar.Children.Add(b);}rail.Children.Add(texturesBar);rail.Children.Add(textureRows);
        Heading("参数");

        rail.Children.Add(parameterRows);
        rail.Children.Add(WorkspaceButton("从源码刷新参数",()=>{RefreshParameterPanel();return Task.CompletedTask;}));
        rail.Children.Add(WorkspaceButton("加载 gain 调参示例",()=>{
            component.Backend="HLSL";code.Text="// @param gain 0 2 1\n"+D3DShaderRenderer.DefaultSource.Replace("base * glow *","base * gain * glow *");
            parameters.Text="{\"gain\":1}";component.Bindings.Clear();RefreshParameterPanel();status.Text="示例：gain 控制亮度。编译写入 → 播放 → 拖面板滑块；点创建形状滑块可绑定放映控件。";return Task.CompletedTask;
        }));
        settings.Children.Remove(parameters);
        var advanced=new Expander{Header="高级 · 参数 JSON",Content=parameters,Margin=new Thickness(0,12,0,0)};rail.Children.Add(advanced);
        // Output controls remain grouped at the bottom of the rail.
        var output=settings.Children.OfType<WrapPanel>().First();settings.Children.Remove(output);Heading("输出与播放");rail.Children.Add(output);
        var editor=new DockPanel();Grid.SetColumn(editor,2);grid.Children.Add(editor);
        var caption=new TextBlock{Text="SOURCE  /  HLSL · GLSL",Foreground=Muted,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)};DockPanel.SetDock(caption,Dock.Top);editor.Children.Add(caption);
        code.Background=new SolidColorBrush(Color.FromRgb(24,30,44));code.Foreground=new SolidColorBrush(Color.FromRgb(220,228,244));code.CaretBrush=Brushes.White;code.FontSize=14;code.Padding=new Thickness(18);code.VerticalContentAlignment=VerticalAlignment.Top;code.BorderThickness=new Thickness(0);editor.Children.Add(code);
        SetupDraftPreview(editor);
        parameters.VerticalContentAlignment=VerticalAlignment.Top;
        RefreshParameterPanel();RefreshTextures();
    }
    internal void PrepareWorkspaceFixture(){
        code.Text="// @param gain 0 2 1\n// @param axisX 0 1 0.5\n// @param axisY 0 1 0.5\n// @param enabled 0 1 1\n"+code.Text;
        RefreshParameterPanel();
    }
    private void RefreshTextures()
    {
        textureRows.Children.Clear();
        foreach(var texture in component.Textures){
            var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,6,0,6)};
            try{using var stream=new MemoryStream(Convert.FromBase64String(texture.Data));var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();row.Children.Add(new Image{Source=image,Width=44,Height=44,Stretch=Stretch.UniformToFill});}catch{}
            row.Children.Add(new TextBlock{Text=texture.Name,Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Center});textureRows.Children.Add(row);
        }
    }
    private async Task RequireSavedParameters(params string[] names)
    {
        if(key==null)throw new InvalidOperationException("请先编译并写入组件，再创建放映控件。");
        var reply=await host.RequestAsync("shaderGet",new{key});
        var saved=reply.GetProperty("Component").Deserialize<ShaderComponent>()??throw new InvalidDataException("文稿组件已不存在，请重新选择组件。");
        if(names.Any(name=>!saved.Parameters.ContainsKey(name)))throw new InvalidOperationException("选中的参数尚未写入文稿组件。请先编译并写入幻灯片，再绑定控件。");
    }
    private void RefreshParameterPanel()
    {
        if(updatingPanel)return;updatingPanel=true;
        try{
            var values=JsonSerializer.Deserialize<Dictionary<string,float>>(parameters.Text)??new();
            var ranges=new Dictionary<string,(double Min,double Max)>();
            foreach(Match match in Regex.Matches(code.Text,@"//\s*@param\s+(\w+)\s+([-+\d.eE]+)\s+([-+\d.eE]+)\s+([-+\d.eE]+)")){
                string name=match.Groups[1].Value;double min=double.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture),max=double.Parse(match.Groups[3].Value,CultureInfo.InvariantCulture);float initial=float.Parse(match.Groups[4].Value,CultureInfo.InvariantCulture);
                if(!double.IsFinite(min)||!double.IsFinite(max)||min>=max||!float.IsFinite(initial))throw new InvalidDataException("参数范围无效："+name);
                if(name=="time"||name=="resolution"||name=="roughness")continue;ranges[name]=(min,max);values.TryAdd(name,initial);
            }
            if(component.Backend=="GLSL")foreach(Match match in Regex.Matches(code.Text,@"\buniform\s+float\s+(\w+)\s*;")){string name=match.Groups[1].Value;if(name!="time"&&name!="roughness")values.TryAdd(name,0);}
            parameters.Text=JsonSerializer.Serialize(values);parameterRows.Children.Clear();
            var removedBindings=component.Bindings.Where(b=>!values.ContainsKey(b.Parameter)&&b.Parameter!="roughness").Select(b=>b.Parameter).Distinct().ToArray();
            if(removedBindings.Length>0)parameterRows.Children.Add(new TextBlock{Text="这些参数已从草稿移除，应用时将解除绑定："+string.Join("、",removedBindings),Foreground=Brushes.DarkOrange,TextWrapping=TextWrapping.Wrap});
            foreach(var pair in values.Where(p=>p.Key!="roughness")){
                string name=pair.Key;var range=ranges.TryGetValue(name,out var found)?found:(Math.Min(0,pair.Value),Math.Max(1,pair.Value));
                var box=new StackPanel{Margin=new Thickness(0,12,0,0)};parameterRows.Children.Add(box);
                var label=new TextBlock{Text=name,FontWeight=FontWeights.SemiBold};box.Children.Add(label);
                var slider=new Slider{Minimum=range.Item1,Maximum=range.Item2,Value=pair.Value,Margin=new Thickness(0,8,0,4)};var valueBox=new TextBox{Text=pair.Value.ToString("F1",CultureInfo.InvariantCulture),Width=62,Margin=new Thickness(8,2,0,2),VerticalContentAlignment=VerticalAlignment.Center};
                var valueRow=new DockPanel();DockPanel.SetDock(valueBox,Dock.Right);valueRow.Children.Add(valueBox);valueRow.Children.Add(slider);box.Children.Add(valueRow);
                void CommitValue(){if(double.TryParse(valueBox.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)&&double.IsFinite(v))slider.Value=Math.Clamp(v,slider.Minimum,slider.Maximum);valueBox.Text=slider.Value.ToString("F1",CultureInfo.InvariantCulture);}
                valueBox.LostKeyboardFocus+=(_,_)=>CommitValue();valueBox.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){CommitValue();e.Handled=true;}};
                slider.ValueChanged+=(_,_)=>{var current=JsonSerializer.Deserialize<Dictionary<string,float>>(parameters.Text)!;current[name]=(float)slider.Value;parameters.Text=JsonSerializer.Serialize(current);label.Text=name;valueBox.Text=slider.Value.ToString("F1",CultureInfo.InvariantCulture);if(!busy){parameterTimer.Stop();parameterTimer.Start();}};

            }
            var parameterContent=new StackPanel();foreach(UIElement child in parameterRows.Children.Cast<UIElement>().ToArray()){parameterRows.Children.Remove(child);parameterContent.Children.Add(child);}
            var bindingPanel=new StackPanel{Margin=new Thickness(0,12,0,0)};
            var tabs=new TabControl();tabs.Items.Add(new TabItem{Header="参数",Content=parameterContent});tabs.Items.Add(new TabItem{Header="控件绑定",Content=bindingPanel});parameterRows.Children.Add(tabs);
            var type=new ComboBox{ItemsSource=new[]{"滑块 · 单参数","圆盘 · X / Y","复选框 · 开关","方形坐标 · X / Y"},SelectedIndex=0,Margin=new Thickness(0,4,0,8)};
            var names=values.Keys.Where(n=>n!="roughness").ToArray();
            var target=new ComboBox{ItemsSource=names,SelectedIndex=names.Length>0?0:-1};
            var targetY=new ComboBox{ItemsSource=names,SelectedIndex=names.Length>1?1:-1,Margin=new Thickness(0,6,0,0),Visibility=Visibility.Collapsed};
            bindingPanel.Children.Add(new TextBlock{Text="控件类型",Foreground=Muted});bindingPanel.Children.Add(type);
            var xLabel=new TextBlock{Text="目标参数",Foreground=Muted};var yLabel=new TextBlock{Text="Y 轴参数",Foreground=Muted,Visibility=Visibility.Collapsed};
            bindingPanel.Children.Add(xLabel);bindingPanel.Children.Add(target);bindingPanel.Children.Add(yLabel);bindingPanel.Children.Add(targetY);
            var appearance=new ComboBox{ItemsSource=new[]{"深色 · 蓝","浅色 · 蓝","石墨 · 银","深色 · 琥珀"},SelectedIndex=0,Margin=new Thickness(0,4,0,6)};
            var numberField=new CheckBox{Content="放映数值框暂不可用 · 请用参数面板",IsEnabled=false,Margin=new Thickness(0,6,0,6)};
            bindingPanel.Children.Add(new TextBlock{Text="外观预设",Foreground=Muted,Margin=new Thickness(0,10,0,0)});bindingPanel.Children.Add(appearance);bindingPanel.Children.Add(numberField);
            var rangeLabel=new TextBlock{Text="最小值 / 最大值",Foreground=Muted,Margin=new Thickness(0,8,0,3)};
            TextBox low=new TextBox{Text="0",MinWidth=65},high=new TextBox{Text="1",MinWidth=65,Margin=new Thickness(8,0,0,0)};
            TextBox lowY=new TextBox{Text="0",MinWidth=65},highY=new TextBox{Text="1",MinWidth=65,Margin=new Thickness(8,0,0,0)};
            var rangeRow=new StackPanel{Orientation=Orientation.Horizontal};rangeRow.Children.Add(low);rangeRow.Children.Add(high);
            var yRow=new StackPanel{Visibility=Visibility.Collapsed};yRow.Children.Add(new TextBlock{Text="Y 最小值 / 最大值",Foreground=Muted});
            var yInputs=new StackPanel{Orientation=Orientation.Horizontal};yInputs.Children.Add(lowY);yInputs.Children.Add(highY);yRow.Children.Add(yInputs);
            bindingPanel.Children.Add(rangeLabel);bindingPanel.Children.Add(rangeRow);bindingPanel.Children.Add(yRow);
            void LoadRanges(){
                var px=target.SelectedItem as string;var py=targetY.SelectedItem as string;
                var x=px!=null&&ranges.TryGetValue(px,out var r)?r:(0d,1d);
                var y=py!=null&&ranges.TryGetValue(py,out var r2)?r2:(0d,1d);
                var savedX=component.Bindings.FirstOrDefault(b=>b.Parameter==px);var savedY=component.Bindings.FirstOrDefault(b=>b.Parameter==py);
                if(savedX!=null)x=(savedX.Minimum,savedX.Maximum);if(savedY!=null)y=(savedY.Minimum,savedY.Maximum);
                low.Text=x.Item1.ToString(CultureInfo.InvariantCulture);high.Text=x.Item2.ToString(CultureInfo.InvariantCulture);
                lowY.Text=y.Item1.ToString(CultureInfo.InvariantCulture);highY.Text=y.Item2.ToString(CultureInfo.InvariantCulture);
            }
            var state=new TextBlock{Foreground=Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,4)};
            bindingPanel.Children.Add(state);
            void UpdateBindingState(){
                string? name=target.SelectedItem as string;
                var binding=component.Bindings.FirstOrDefault(b=>b.Parameter==name);
                state.Text=name==null?"尚无可绑定参数。请声明参数并编译写入组件。":binding==null?"未绑定 · 选择参数，再选择 PPT 控件。":"已绑定 · "+name+"（控件值优先）";
                targetY.Visibility=yLabel.Visibility=yRow.Visibility=(type.SelectedIndex==1||type.SelectedIndex==3)?Visibility.Visible:Visibility.Collapsed;
                xLabel.Text=(type.SelectedIndex==1||type.SelectedIndex==3)?"X 轴参数":"目标参数";
                numberField.Visibility=type.SelectedIndex==2?Visibility.Collapsed:Visibility.Visible;
                rangeLabel.Text=type.SelectedIndex==2?"关闭值 / 开启值（开启值须大于关闭值）":"最小值 / 最大值";
            }
            bool loadingBinding=false;
            void LoadBinding(){
                if(loadingBinding)return;loadingBinding=true;
                try{
                    var binding=component.Bindings.FirstOrDefault(b=>b.Parameter==target.SelectedItem as string);
                    if(binding!=null){
                        type.SelectedIndex=binding.Checkbox?2:binding.Circular?1:binding.Kind=="ShapePadX"||binding.Kind=="ShapePadY"?3:0;
                        appearance.SelectedIndex=Math.Clamp(binding.StylePreset,0,3);numberField.IsChecked=binding.ShowValue;
                        if(binding.Kind=="ShapePadX"||binding.Kind=="ShapePadY"){
                            var x=component.Bindings.FirstOrDefault(b=>b.ShapeId==binding.ShapeId&&b.Kind=="ShapePadX");
                            var y=component.Bindings.FirstOrDefault(b=>b.ShapeId==binding.ShapeId&&b.Kind=="ShapePadY");
                            if(x!=null)target.SelectedItem=x.Parameter;if(y!=null)targetY.SelectedItem=y.Parameter;
                        }
                    }
                    LoadRanges();UpdateBindingState();
                }finally{loadingBinding=false;}
            }
            type.SelectionChanged+=(_,_)=>UpdateBindingState();target.SelectionChanged+=(_,_)=>LoadBinding();targetY.SelectionChanged+=(_,_)=>{if(!loadingBinding)LoadRanges();};LoadBinding();
            async Task Bind(bool selected){
                string? px=target.SelectedItem as string,py=(type.SelectedIndex==1||type.SelectedIndex==3)?targetY.SelectedItem as string:null;
                if(px==null)throw new InvalidOperationException("请先声明并选择参数。");
                if((type.SelectedIndex==1||type.SelectedIndex==3)&&(py==null||py==px))throw new InvalidOperationException("二维控件需要两个不同参数。");
                await RequireSavedParameters(py==null?new[]{px}:new[]{px,py});
                (double,double) ReadRange(TextBox a,TextBox b){
                    if(!double.TryParse(a.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var lo)||!double.TryParse(b.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var hi)||!double.IsFinite(lo)||!double.IsFinite(hi)||lo>=hi||Math.Abs(lo)>float.MaxValue||Math.Abs(hi)>float.MaxValue)throw new InvalidOperationException("请输入有效范围，最大值必须大于最小值。");
                    return(lo,hi);
                }
                var rx=ReadRange(low,high);var ry=py==null?(0d,1d):ReadRange(lowY,highY);
                var result=await host.RequestAsync("shaderShapeSlider",new{key,parameter=px,parameterY=py,minimum=rx.Item1,maximum=rx.Item2,minimumY=ry.Item1,maximumY=ry.Item2,selected,checkbox=type.SelectedIndex==2,square=type.SelectedIndex==3,stylePreset=appearance.SelectedIndex,showValue=false});
                component.Bindings=result.GetProperty("Component").Deserialize<ShaderComponent>()!.Bindings;
                UpdateBindingState();status.Text="控件已绑定。保存文稿后保留绑定。";
            }
            var bindSelected=WorkspaceButton("绑定选中控件",()=>Bind(true));
            var bindNew=WorkspaceButton("新建并绑定控件",()=>Bind(false));
            refreshBindingAvailability=()=>{bindSelected.IsEnabled=bindNew.IsEnabled=key!=null&&names.Length>0;bindSelected.ToolTip=bindNew.ToolTip=key==null?"请先编译并写入组件。":"选择控件类型及参数后绑定。";};
            refreshBindingAvailability();
            bindingPanel.Children.Add(bindSelected);bindingPanel.Children.Add(bindNew);
            bindingPanel.Children.Add(WorkspaceButton("解除当前参数绑定",async()=>{
                if(key==null||target.SelectedItem is not string name)throw new InvalidOperationException("请选择已保存组件的参数。");
                var result=await host.RequestAsync("shaderBindSlider",new{key,parameter=name,remove=true});
                component.Bindings=result.GetProperty("Component").Deserialize<ShaderComponent>()!.Bindings;UpdateBindingState();status.Text="绑定已解除，保留 PPT 控件。";
            }));

        }finally{updatingPanel=false;}
    }
}
