using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PptEase.Shader;

// Only the two-rectangle component's bounds accept input during a slide show.
// Editing mode continues to use ordinary PowerPoint shapes.
internal sealed class ShapeSliderWindow:Window,IDisposable
{
    private readonly Canvas canvas=new();
    private readonly Rectangle thumb=new();
    private readonly Rectangle coverPlate=new(){IsHitTestVisible=false};
    private Brush? surfaceBackground;
    private readonly Rectangle trackVisual=new(){IsHitTestVisible=false};
    private readonly TextBox numberX=new(),numberY=new();
    private bool showValue;private double surfaceWidth=280,surfaceHeight=24;private int preset;
    private static readonly HashSet<IntPtr> inputWindows=new();
    private bool OwnerActive()=>GetForegroundWindow()==owner||inputWindows.Contains(GetForegroundWindow());
    internal static bool TryNumeric(string text,double minimum,double maximum,out double value){bool valid=double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out value)&&double.IsFinite(value);if(valid)value=Math.Clamp(value,minimum,maximum);return valid;}
    private void CommitNumber(TextBox field,bool y){
        if(TryNumeric(field.Text,y?minY:min,y?maxY:max,out double value)){
            if(y)fractionY=(value-minY)/(maxY-minY);else fraction=(value-min)/(max-min);
            if(circular){var c=ClampDisk(fraction,fractionY);fraction=c.X;fractionY=c.Y;}
            pending=(float)(min+(max-min)*fraction);pendingY=pad?(float)(minY+(maxY-minY)*fractionY):null;interaction=DateTime.UtcNow;
        }
        field.Text=((y?minY:min)+((y?maxY:max)-(y?minY:min))*(y?fractionY:fraction)).ToString("F1",System.Globalization.CultureInfo.InvariantCulture);DrawThumb();
    }
    private readonly Func<float,float?,Task> changed;
    private readonly DispatcherTimer send=new(){Interval=TimeSpan.FromMilliseconds(33)};
    private IntPtr hwnd,owner;
    private double min,max,fraction=.5,thumbRatio=.08;
    private bool checkbox;
    private readonly TextBlock checkMark=new(){Text="✓",Foreground=Brushes.White,FontWeight=FontWeights.Bold,IsHitTestVisible=false};
    private bool circular;private bool pad;private double minY,maxY,fractionY=.5,ratioY=.08;private float? pendingY;
    private readonly Line crossX=new(){Stroke=Brushes.White,Opacity=.4,IsHitTestVisible=false},crossY=new(){Stroke=Brushes.White,Opacity=.4,IsHitTestVisible=false};
    private bool dragging,busy,disposed,visible;
    private float? pending;
    private DateTime interaction;
    private long presses,moves,sent;private string lastError="";
    internal object Diagnostics()=>new{Visible=visible,Pad=pad,Dragging=dragging,Presses=presses,Moves=moves,Sent=sent,LastError=lastError,Owner=owner.ToInt64(),Hwnd=hwnd.ToInt64()};
    internal bool IsAlive=>IsWindow(hwnd);
    internal Task InvokeChangeForTest(float value)=>changed(value,null);
    internal static double PositionFraction(double x,double width,double ratio)=>Math.Clamp((x-width*ratio/2)/Math.Max(1,width*(1-ratio)),0,1);
    public ShapeSliderWindow(Func<float,float?,Task> changed)
    {
        this.changed=changed;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowActivated=false;ShowInTaskbar=false;
        AllowsTransparency=true;Background=Brushes.Transparent;Width=280;Height=24;Left=-20000;Top=-20000;Content=canvas;canvas.Background=Brushes.Transparent;canvas.Children.Add(coverPlate);canvas.Children.Add(trackVisual);canvas.Children.Add(crossX);canvas.Children.Add(crossY);canvas.Children.Add(thumb);canvas.Children.Add(checkMark);
        foreach(var field in new[]{numberX,numberY}){
            field.Width=64;field.Height=28;field.FontSize=12;field.Padding=new Thickness(5,3,5,3);field.BorderThickness=new Thickness(1);field.VerticalContentAlignment=VerticalAlignment.Center;
            field.PreviewMouseLeftButtonDown+=(_,_)=>{SetForegroundWindow(hwnd);field.Focus();};
            field.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){CommitNumber(field,field==numberY);SetForegroundWindow(owner);e.Handled=true;}else if(e.Key==Key.Escape){field.Text=(field==numberY?minY+(maxY-minY)*fractionY:min+(max-min)*fraction).ToString("F1",System.Globalization.CultureInfo.InvariantCulture);SetForegroundWindow(owner);e.Handled=true;}};
            field.LostKeyboardFocus+=(_,_)=>CommitNumber(field,field==numberY);canvas.Children.Add(field);
        }
        canvas.PreviewMouseLeftButtonDown+=(_,e)=>{if(IsNumericSource(e.OriginalSource as DependencyObject))return;BeginPointer(e.GetPosition(canvas));e.Handled=true;};
        canvas.MouseMove+=(_,e)=>{if(dragging){moves++;SetFromPointer(e.GetPosition(canvas));e.Handled=true;}};
        canvas.MouseLeftButtonUp+=(_,e)=>{if(dragging){SetFromPointer(e.GetPosition(canvas));dragging=false;canvas.ReleaseMouseCapture();e.Handled=true;}};
        canvas.LostMouseCapture+=(_,_)=>dragging=false;
        canvas.SizeChanged+=(_,_)=>DrawThumb();
        send.Tick+=async(_,_)=>{if(visible&&(!OwnerActive()||!IsWindowVisible(owner)||IsIconic(owner)))HideSurface();if(busy||pending==null||disposed)return;float value=pending.Value;float? y=pendingY;pending=null;pendingY=null;busy=true;try{await changed(value,y);sent++;lastError="";}catch(Exception ex){lastError=ex.Message;HideSurface();}finally{busy=false;}};
        hwnd=new WindowInteropHelper(this).EnsureHandle();HwndSource.FromHwnd(hwnd).AddHook((IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled)=>{if(msg==0x21){handled=true;return new IntPtr(3);}return IntPtr.Zero;});
        inputWindows.Add(hwnd);Show();SetLong(hwnd,-20,new IntPtr(GetLong(hwnd,-20).ToInt64()|0x08000080));ShowNative(hwnd,0);send.Start();
    }
    internal static object RenderControlGallery(string path){
        var gallery=new Grid{Width=1120,Height=680,Background=new SolidColorBrush(Color.FromRgb(24,25,28))};
        for(int i=0;i<4;i++){gallery.ColumnDefinitions.Add(new ColumnDefinition());gallery.RowDefinitions.Add(new RowDefinition());}
        bool numeric=true;
        for(int theme=0;theme<4;theme++)for(int kind=0;kind<4;kind++){
            using var control=new ShapeSliderWindow((_,_)=>Task.CompletedTask);
            control.preset=theme;control.pad=kind==1||kind==2;control.circular=kind==1;control.checkbox=kind==3;control.showValue=kind!=3;
            control.surfaceWidth=kind==0?160:kind==3?28:105;control.surfaceHeight=kind==0?28:kind==3?28:105;
            control.min=-2;control.max=2;control.minY=0;control.maxY=1;control.fraction=.65;control.fractionY=.5;
            control.Width=control.surfaceWidth+80;control.Height=control.surfaceHeight+6;
            control.DrawThumb();control.canvas.Measure(new Size(control.Width,control.Height));control.canvas.Arrange(new Rect(0,0,control.Width,control.Height));control.canvas.UpdateLayout();
            control.numberX.Text="1.7";control.CommitNumber(control.numberX,false);numeric&=Math.Abs(control.fraction-.925)<.00001||control.circular;
            if(control.pad&&!control.circular){control.numberY.Text="2.5";control.CommitNumber(control.numberY,true);numeric&=control.fractionY==1;}
            control.numberX.Text="NaN";double previous=control.fraction;control.CommitNumber(control.numberX,false);numeric&=control.fraction==previous;
            control.canvas.UpdateLayout();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)control.Width,(int)control.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(control.canvas);
            var card=new StackPanel{Margin=new Thickness(16)};card.Children.Add(new TextBlock{Text=new[]{global::PptEase.Localization.L.T("滑块"),global::PptEase.Localization.L.T("圆盘 X / Y"),global::PptEase.Localization.L.T("方形 X / Y"),global::PptEase.Localization.L.T("复选框")}[kind]+" · "+new[]{global::PptEase.Localization.L.T("深色蓝"),global::PptEase.Localization.L.T("浅色蓝"),global::PptEase.Localization.L.T("石墨银"),global::PptEase.Localization.L.T("琥珀")}[theme],Foreground=Brushes.White,Margin=new Thickness(0,0,0,12)});card.Children.Add(new Image{Source=bitmap,Stretch=Stretch.None,HorizontalAlignment=HorizontalAlignment.Left});Grid.SetColumn(card,theme);Grid.SetRow(card,kind);gallery.Children.Add(card);
            control.HideSurface();numeric&=control.pending==null&&control.pendingY==null;
        }
        gallery.Measure(new Size(1120,680));gallery.Arrange(new Rect(0,0,1120,680));gallery.UpdateLayout();
        var result=new System.Windows.Media.Imaging.RenderTargetBitmap(1120,680,96,96,PixelFormats.Pbgra32);result.Render(gallery);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(result));using(var file=System.IO.File.Create(path))encoder.Save(file);
        return new{passed=numeric,scope="direct control numeric commits + rendering, no synthetic input",variants=16};
    }
    private bool IsNumericSource(DependencyObject? source){
        // Keyboard focus can stay in a textbox after typing or window activation.
        // Only the current pointer target should exclude a drag.
        while(source!=null){if(ReferenceEquals(source,numberX)||ReferenceEquals(source,numberY))return true;
            source=source is Visual||source is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source);
        }return false;
    }
    private void BeginPointer(Point point){
        if(numberX.IsKeyboardFocusWithin)CommitNumber(numberX,false);
        if(numberY.IsKeyboardFocusWithin)CommitNumber(numberY,true);
        Keyboard.ClearFocus();FocusManager.SetFocusedElement(this,null);
        presses++;if(checkbox){ToggleCheckbox();return;}
        dragging=true;canvas.CaptureMouse();SetFromPointer(point);
    }
    internal static bool TestDragAfterNumericFocus(){
        using var control=new ShapeSliderWindow((_,_)=>Task.CompletedTask);
        control.min=0;control.max=2;control.surfaceWidth=280;control.surfaceHeight=28;control.showValue=true;
        FocusManager.SetFocusedElement(control,control.numberX);
        bool targetCheck=control.IsNumericSource(control.numberX)&&!control.IsNumericSource(control.thumb)&&!control.IsNumericSource(control.canvas);
        control.BeginPointer(new Point(250,14));bool slider=control.dragging&&control.pending>1.5&&control.coverPlate.Visibility==Visibility.Visible&&control.coverPlate.Fill!=null&&control.coverPlate.Width==280&&control.coverPlate.Height==28;
        control.HideSurface();control.pad=true;control.minY=0;control.maxY=1;control.surfaceWidth=180;control.surfaceHeight=180;
        FocusManager.SetFocusedElement(control,control.numberY);control.BeginPointer(new Point(160,20));
        bool xy=control.dragging&&control.pending>1.5&&control.pendingY>.8;
        control.HideSurface();return targetCheck&&slider&&xy&&!control.dragging&&control.pending==null;
    }
    private void ToggleCheckbox(){fraction=fraction>=.5?0:1;pending=(float)(min+(max-min)*fraction);pendingY=null;interaction=DateTime.UtcNow;DrawThumb();}
    internal static bool TestCheckboxTransitions(){
        using var control=new ShapeSliderWindow((_,_)=>Task.CompletedTask);
        control.checkbox=true;control.min=-2;control.max=3;control.fraction=0;
        control.ToggleCheckbox();bool on=control.pending==3&&control.checkMark.Visibility==Visibility.Visible&&!control.dragging;
        control.ToggleCheckbox();bool off=control.pending==-2&&control.checkMark.Visibility==Visibility.Collapsed;
        control.ToggleCheckbox();control.HideSurface();return on&&off&&control.pending==null&&!control.canvas.IsMouseCaptured;
    }
    internal static Point ClampDisk(double x,double y){double dx=x*2-1,dy=y*2-1,len=Math.Sqrt(dx*dx+dy*dy);if(len>1){dx/=len;dy/=len;}return new Point((dx+1)/2,(dy+1)/2);}
    private void SetFromPointer(Point point)
    {
        double width=surfaceWidth,knob=width*thumbRatio;
        fraction=PositionFraction(point.X,width,thumbRatio);if(pad){fractionY=1-PositionFraction(point.Y,surfaceHeight,ratioY);if(circular){var c=ClampDisk(fraction,fractionY);fraction=c.X;fractionY=c.Y;}pendingY=(float)(minY+(maxY-minY)*fractionY);}interaction=DateTime.UtcNow;pending=(float)(min+(max-min)*fraction);DrawThumb();
    }
    private void DrawThumb(){
        double w=surfaceWidth,h=surfaceHeight;
        Brush bg=preset==1?new SolidColorBrush(Color.FromRgb(244,244,245)):new SolidColorBrush(Color.FromRgb(39,39,42));
        Brush line=preset==1?Brushes.LightGray:new SolidColorBrush(Color.FromRgb(82,82,91));
        Brush accent=preset==2?Brushes.Silver:preset==3?Brushes.Goldenrod:new SolidColorBrush(Color.FromRgb(59,130,246));
        // The native PPT thumb is the editing cover. An opaque playback plate
        // replaces it inside the control bounds; a transparent rail exposes a duplicate.
        coverPlate.Width=w;coverPlate.Height=h;coverPlate.Fill=surfaceBackground??bg;
        coverPlate.Visibility=!pad&&!checkbox?Visibility.Visible:Visibility.Collapsed;
        trackVisual.Fill=bg;trackVisual.Stroke=line;trackVisual.StrokeThickness=1;
        trackVisual.Width=w;trackVisual.Height=pad||checkbox?h:4;trackVisual.RadiusX=trackVisual.RadiusY=circular?w/2:checkbox?4:pad?6:2;
        Canvas.SetTop(trackVisual,pad||checkbox?0:(h-4)/2);
        thumb.Fill=accent;thumb.Stroke=preset==1?Brushes.White:new SolidColorBrush(Color.FromRgb(212,212,216));thumb.StrokeThickness=1.2;
        thumb.Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=4,ShadowDepth=1,Opacity=.35};
        checkMark.Visibility=checkbox&&fraction>=.5?Visibility.Visible:Visibility.Collapsed;checkMark.FontSize=Math.Max(10,h*.68);Canvas.SetLeft(checkMark,h*.18);Canvas.SetTop(checkMark,0);
        if(checkbox){thumb.Visibility=Visibility.Hidden;crossX.Visibility=crossY.Visibility=Visibility.Hidden;trackVisual.Fill=fraction>=.5?accent:bg;}
        else{
            thumb.Visibility=Visibility.Visible;thumb.RadiusX=thumb.RadiusY=100;
            thumb.Width=Math.Max(8,w*thumbRatio);thumb.Height=thumb.Width;
            Canvas.SetLeft(thumb,Math.Max(0,w-thumb.Width)*fraction);Canvas.SetTop(thumb,pad?Math.Max(0,h-thumb.Height)*(1-fractionY):(h-thumb.Height)/2);
            crossX.Visibility=crossY.Visibility=pad?Visibility.Visible:Visibility.Hidden;crossX.Stroke=crossY.Stroke=line;
            crossX.X1=w*.15;crossX.X2=w*.85;crossX.Y1=crossX.Y2=h/2;crossY.Y1=h*.15;crossY.Y2=h*.85;crossY.X1=crossY.X2=w/2;
        }
        numberX.ToolTip=pad?global::PptEase.Localization.L.T("X 轴数值 · Enter 应用，Esc 取消"):global::PptEase.Localization.L.T("数值 · Enter 应用，Esc 取消");numberY.ToolTip=global::PptEase.Localization.L.T("Y 轴数值 · Enter 应用，Esc 取消");
        numberX.Visibility=showValue&&!checkbox?Visibility.Visible:Visibility.Collapsed;numberY.Visibility=showValue&&pad?Visibility.Visible:Visibility.Collapsed;
        foreach(var field in new[]{numberX,numberY}){field.Background=bg;field.Foreground=preset==1?Brushes.Black:Brushes.White;field.BorderBrush=line;Canvas.SetLeft(field,w+8);}
        Canvas.SetTop(numberX,Math.Max(0,(h-(pad?62:28))/2));Canvas.SetTop(numberY,Math.Max(0,(h-62)/2)+34);
        if(!numberX.IsKeyboardFocusWithin)numberX.Text=(min+(max-min)*fraction).ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
        if(!numberY.IsKeyboardFocusWithin)numberY.Text=(minY+(maxY-minY)*fractionY).ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
    }
    private static Brush ColorBrush(int rgb)=>new SolidColorBrush(Color.FromRgb((byte)rgb,(byte)(rgb>>8),(byte)(rgb>>16)));
    public void Update(JsonElement spec)
    {
        var p=spec.GetProperty("Placement");if(!p.GetProperty("Visible").GetBoolean()){HideSurface();return;}
        owner=new IntPtr(p.GetProperty("Owner").GetInt64());
        if(!IsWindowVisible(owner)||IsIconic(owner)||!OwnerActive()){HideSurface();return;}
        min=spec.GetProperty("Minimum").GetDouble();max=spec.GetProperty("Maximum").GetDouble();thumbRatio=spec.GetProperty("ThumbRatio").GetDouble();
        surfaceBackground=spec.TryGetProperty("SurfaceColor",out var surface)&&surface.ValueKind==JsonValueKind.Number?ColorBrush(surface.GetInt32()):null;
        showValue=false; // Slideshow numeric entry suspended until focus lifecycle is validated.
        preset=spec.TryGetProperty("StylePreset",out var theme)?theme.GetInt32():0;
        checkbox=spec.TryGetProperty("Checkbox",out var toggle)&&toggle.GetBoolean();
        circular=spec.TryGetProperty("Circular",out var disk)&&disk.GetBoolean();
        pad=spec.TryGetProperty("ParameterY",out var vertical)&&vertical.ValueKind==JsonValueKind.String;
        if(pad){minY=spec.GetProperty("MinimumY").GetDouble();maxY=spec.GetProperty("MaximumY").GetDouble();ratioY=spec.GetProperty("ThumbRatioY").GetDouble();if(!dragging&&!busy&&pending==null&&(DateTime.UtcNow-interaction).TotalMilliseconds>250)fractionY=Math.Clamp((spec.GetProperty("ValueY").GetDouble()-minY)/(maxY-minY),0,1);}
        if(!dragging&&!busy&&pending==null&&(DateTime.UtcNow-interaction).TotalMilliseconds>250)fraction=Math.Clamp((spec.GetProperty("Value").GetDouble()-min)/(max-min),0,1);
        if(circular){var bounded=ClampDisk(fraction,fractionY);fraction=bounded.X;fractionY=bounded.Y;}
        surfaceWidth=p.GetProperty("Width").GetInt32();surfaceHeight=p.GetProperty("Height").GetInt32();
        var transform=System.Windows.PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice??Matrix.Identity;surfaceWidth*=transform.M11;surfaceHeight*=transform.M22;
        SetLong(hwnd,-20,new IntPtr(showValue?GetLong(hwnd,-20).ToInt64()&~0x08000000L:GetLong(hwnd,-20).ToInt64()|0x08000000L));
        SetLong(hwnd,-8,owner);bool topmost=(GetLong(owner,-20).ToInt64()&8)!=0;
        SetWindowPos(hwnd,new IntPtr(topmost?-1:-2),p.GetProperty("X").GetInt32(),p.GetProperty("Y").GetInt32(),p.GetProperty("Width").GetInt32()+(showValue?(int)Math.Ceiling(76/transform.M11):0),Math.Max(p.GetProperty("Height").GetInt32(),showValue?32:1),0x10);
        int left=p.GetProperty("ClipLeft").GetInt32(),top=p.GetProperty("ClipTop").GetInt32(),right=p.GetProperty("ClipRight").GetInt32(),bottom=p.GetProperty("ClipBottom").GetInt32();
        if(right<=left||bottom<=top){HideSurface();return;}if(showValue){right+=(int)Math.Ceiling(76/transform.M11);bottom=Math.Max(bottom,32);if(GetWindowRect(owner,out var ownerRect)){right=Math.Min(right,ownerRect.Right-p.GetProperty("X").GetInt32());bottom=Math.Min(bottom,ownerRect.Bottom-p.GetProperty("Y").GetInt32());}}
        IntPtr region=CreateRectRgn(left,top,right,bottom);if(circular&&!showValue){IntPtr ellipse=CreateEllipticRgn(0,0,p.GetProperty("Width").GetInt32(),p.GetProperty("Height").GetInt32());CombineRgn(region,region,ellipse,1);DeleteObject(ellipse);}if(SetWindowRgn(hwnd,region,true)==0)DeleteObject(region);
        DrawThumb();if(!visible){ShowNative(hwnd,8);visible=true;}
        ToolTip=global::PptEase.Localization.L.T("参数 ")+spec.GetProperty("Parameter").GetString()+"："+(min+(max-min)*fraction).ToString("0.###")+(pad?" / "+spec.GetProperty("ParameterY").GetString()+"："+(minY+(maxY-minY)*fractionY).ToString("0.###"):"");
    }
    public void HideSurface(){if(canvas.IsMouseCaptured)canvas.ReleaseMouseCapture();dragging=false;pending=null;pendingY=null;if(visible){ShowNative(hwnd,0);visible=false;}}
    public void Dispose(){if(disposed)return;disposed=true;send.Stop();HideSurface();inputWindows.Remove(hwnd);Close();}
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetLong(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetLong(IntPtr h,int index,IntPtr value);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="ShowWindow")]private static extern bool ShowNative(IntPtr h,int command);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]private struct WindowRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out WindowRect rect);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateEllipticRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")]private static extern int CombineRgn(IntPtr dest,IntPtr a,IntPtr b,int mode);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr region);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);
}
