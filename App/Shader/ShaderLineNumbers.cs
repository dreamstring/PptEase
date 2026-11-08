using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PptEase.Shader;

internal sealed class ShaderLineNumbers : FrameworkElement
{
    private readonly TextBox editor;
    public ShaderLineNumbers(TextBox editor)
    {
        this.editor=editor; Width=52; ClipToBounds=true;
        editor.TextChanged+=(_,_)=>InvalidateVisual();
        editor.SizeChanged+=(_,_)=>InvalidateVisual();
        editor.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,_)=>InvalidateVisual()));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24,30,42)),null,new Rect(RenderSize));
        int first=editor.GetFirstVisibleLineIndex(), last=editor.GetLastVisibleLineIndex();
        if(first<0||last<0||editor.LineCount==0)return;
        last=Math.Min(last,editor.LineCount-1);
        var typeface=new Typeface(editor.FontFamily,editor.FontStyle,editor.FontWeight,editor.FontStretch);
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for(int line=first;line<=last;line++) {
            int index=editor.GetCharacterIndexFromLineIndex(line);if(index<0)continue;
            var rect=editor.GetRectFromCharacterIndex(index);if(rect.IsEmpty)continue;
            var label=new FormattedText((line+1).ToString(),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,editor.FontSize,Brushes.SlateGray,dpi);
            dc.DrawText(label,new Point(ActualWidth-label.Width-9,rect.Top));
        }
    }
}
