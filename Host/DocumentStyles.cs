using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Security;
using System.Text;
using System.Windows.Forms;

namespace PptEase.Host
{
    public sealed partial class Connect
    {
        private static readonly string[] ThemeNames = { "深色 1", "浅色 1", "深色 2", "浅色 2", "强调色 1", "强调色 2", "强调色 3", "强调色 4", "强调色 5", "强调色 6", "超链接", "已访问链接" };
        public void TightenTextBoxes(object control)
        {
            RibbonAction(delegate
            {
                dynamic selection = app.ActiveWindow.Selection;
                var shapes = new List<object>();
                if ((int)selection.Type == 3)
                {
                    var all = GetShapes();
                    string key = FindTextShapeKey((object)selection.TextRange, all);
                    shapes.Add((object)all.First(h => h.Key == key).Shape);
                }
                else shapes = SelectedStyleShapes();
                var restore = new List<Action>();
                var eligible = new List<object>();
                foreach (dynamic s in shapes)
                {
                    if ((int)s.Type != 17 && (int)s.Type != 14) continue;
                    if ((int)s.HasTextFrame == 0 || (int)s.TextFrame2.HasText == 0) continue;
                    if (ReadLocked(s)) throw new InvalidOperationException("请先解锁所选文本框。");
                    if (Math.Abs((double)s.Rotation) > 0.01 || (int)s.TextFrame2.Orientation != 1)
                        throw new InvalidOperationException("当前先支持未旋转的横排文本框，请单独选择。");
                    if ((int)s.TextFrame2.AutoSize == 2)
                        throw new InvalidOperationException("选区包含自动缩小文字的文本框，请先关闭文字自动缩小，再收紧，以免字号变化。");
                    eligible.Add((object)s);
                }
                if (eligible.Count == 0) throw new InvalidOperationException("请先选择有文字的文本框或文字占位符。");
                app.StartNewUndoEntry();
                try
                {
                    foreach (dynamic s in eligible)
                    {
                        dynamic box = s, frame = s.TextFrame2;
                        float left = (float)s.Left, top = (float)s.Top, width = (float)s.Width, height = (float)s.Height;
                        int auto = (int)frame.AutoSize, wrap = (int)frame.WordWrap, aspect = (int)s.LockAspectRatio;
                        restore.Add(delegate { box.TextFrame2.AutoSize = 0; box.LockAspectRatio = 0; box.TextFrame2.WordWrap = wrap; box.Width = width; box.Height = height; box.Left = left; box.Top = top; box.TextFrame2.AutoSize = auto; box.LockAspectRatio = aspect; });
                        dynamic text = frame.TextRange;
                        int lines = (int)text.Lines().Count;
                        float textLeft = (float)text.BoundLeft, textTop = (float)text.BoundTop;
                        s.LockAspectRatio = 0;
                        frame.AutoSize = 0;
                        if (lines == 1) frame.WordWrap = 0;
                        else frame.WordWrap = -1;
                        frame.AutoSize = 1; // Grow/shrink the box, never shrink the font.
                        if (lines > 1) s.Width = width;
                        // Preserve the visible text position, including centered/right aligned text.
                        s.Left = (float)s.Left + textLeft - (float)frame.TextRange.BoundLeft;
                        s.Top = (float)s.Top + textTop - (float)frame.TextRange.BoundTop;
                        if ((int)frame.TextRange.Lines().Count != lines) throw new InvalidOperationException("收紧改变了换行，本次操作已回退。");
                        s.LockAspectRatio = aspect;
                    }
                }
                catch
                {
                    foreach (Action undo in restore.AsEnumerable().Reverse()) try { undo(); } catch { }
                    throw;
                }
            });
        }

        private static StringBuilder StyleMenu() { return new StringBuilder("<menu xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\">"); }
        private static void StyleItem(StringBuilder x, string id, string label, string tag, string action)
        {
            x.Append("<button id=\"").Append(id).Append("\" label=\"").Append(SecurityElement.Escape(label))
             .Append("\" tag=\"").Append(SecurityElement.Escape(tag)).Append("\" onAction=\"").Append(action).Append("\"");
            if (action == "ApplyThemeColor") x.Append(" getImage=\"GetThemeColorImage\"");
            x.Append("/>");
        }
        private sealed class StylePicture : AxHost
        {
            private StylePicture() : base("") { }
            internal static object FromImage(Image image) { return GetIPictureDispFromPicture(image); }
        }
        public object GetThemeColorImage(object control)
        {
            try
            {
                int slot = Int32.Parse(DocumentStyleTag(control));
                int rgb = (int)app.ActiveWindow.View.Slide.ThemeColorScheme.Colors(slot).RGB;
                using (var bitmap = new Bitmap(16, 16))
                using (Graphics g = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(Color.FromArgb(rgb & 255, (rgb >> 8) & 255, (rgb >> 16) & 255)))
                {
                    g.FillRectangle(brush, 0, 0, 16, 16); g.DrawRectangle(Pens.Gray, 0, 0, 15, 15);
                    return StylePicture.FromImage(bitmap);
                }
            }
            catch { return null; }
        }
        private static void StyleError(StringBuilder x) { x.Append("<button id=\"peStyleUnavailable\" label=\"请先打开可编辑文稿\" enabled=\"false\"/>"); }

        public string GetDocumentFonts(object control)
        {
            StringBuilder x = StyleMenu();
            try
            {
                dynamic p = app.ActivePresentation;
                var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                for (int i = 1; i <= (int)p.Fonts.Count; i++)
                {
                    string name = (string)p.Fonts.Item(i).Name;
                    if (!String.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                int index = 0;
                foreach (string name in names) StyleItem(x, "peDocumentFont" + index++, name, Identity((object)p) + "|" + name, "ApplyDocumentFont");
                if (names.Count == 0) x.Append("<button id=\"peNoDocumentFonts\" label=\"本文档暂无字体\" enabled=\"false\"/>");
            }
            catch { StyleError(x); }
            return CompleteRibbonIcons(x.Append("</menu>").ToString());
        }

        public string GetThemeColors(object control)
        {
            StringBuilder x = StyleMenu();
            try
            {
                dynamic p = app.ActivePresentation;
                dynamic scheme = app.ActiveWindow.View.Slide.ThemeColorScheme;
                var seen = new HashSet<int>();
                for (int i = 1; i <= 12; i++)
                {
                    int rgb = (int)scheme.Colors(i).RGB;
                    if (seen.Add(rgb)) StyleItem(x, "peThemeColor" + i, ThemeNames[i - 1] + "  " + ToHex(rgb), Identity((object)p) + "|" + i, "ApplyThemeColor");
                }
            }
            catch { StyleError(x); }
            return CompleteRibbonIcons(x.Append("</menu>").ToString());
        }

        private string DocumentStyleTag(object control)
        {
            dynamic c = control;
            string tag = (string)c.Tag;
            int separator = tag.IndexOf('|');
            if (separator < 0 || tag.Substring(0, separator) != Identity((object)app.ActivePresentation))
                throw new InvalidOperationException("文稿已切换，请重新展开菜单。");
            return tag.Substring(separator + 1);
        }

        private void VisitStyleShape(object value, Action<object> visitor)
        {
            dynamic shape = value;
            if ((int)shape.Type == 6)
            {
                for (int i = 1; i <= (int)shape.GroupItems.Count; i++) VisitStyleShape((object)shape.GroupItems.Item(i), visitor);
                return;
            }
            if ((int)shape.HasTable != 0)
            {
                for (int row = 1; row <= (int)shape.Table.Rows.Count; row++)
                    for (int col = 1; col <= (int)shape.Table.Columns.Count; col++) visitor((object)shape.Table.Cell(row, col).Shape);
                return;
            }
            visitor(value);
        }

        private List<object> SelectedStyleShapes()
        {
            dynamic sel = app.ActiveWindow.Selection;
            if ((int)sel.Type != 2) throw new InvalidOperationException("请先选择文字、文本框或形状。");
            var result = new List<object>();
            for (int i = 1; i <= (int)sel.ShapeRange.Count; i++)
            {
                dynamic root = sel.ShapeRange.Item(i);
                if (ReadLocked(root)) throw new InvalidOperationException("选区包含锁定对象，请先解锁。");
                VisitStyleShape((object)root, result.Add);
            }
            foreach (dynamic shape in result)
            {
                try { if ((int)shape.Locked != 0) throw new InvalidOperationException("选区包含锁定对象，请先解锁。"); }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            }
            return result;
        }

        public void ApplyDocumentFont(object control)
        {
            RibbonAction(delegate
            {
                string font = DocumentStyleTag(control);
                bool exists = false;
                dynamic p = app.ActivePresentation;
                for (int i = 1; i <= (int)p.Fonts.Count; i++)
                    if ((string)p.Fonts.Item(i).Name == font) { exists = true; break; }
                if (!exists) throw new InvalidOperationException("字体列表已变化，请重新展开菜单。");
                dynamic sel = app.ActiveWindow.Selection;
                var ranges = new List<object>();
                if ((int)sel.Type == 3) ranges.Add((object)sel.TextRange);
                else foreach (dynamic shape in SelectedStyleShapes())
                    if ((int)shape.HasTextFrame != 0) ranges.Add((object)shape.TextFrame.TextRange);
                if (ranges.Count == 0) throw new InvalidOperationException("选区没有可设置字体的文字。");
                app.StartNewUndoEntry();
                foreach (dynamic range in ranges) { range.Font.Name = font; range.Font.NameFarEast = font; }
            });
        }

        public void ApplyThemeColor(object control)
        {
            RibbonAction(delegate
            {
                int slot = Int32.Parse(DocumentStyleTag(control));
                if (slot < 1 || slot > 12) throw new InvalidOperationException("主题颜色已失效。");
                dynamic sel = app.ActiveWindow.Selection;
                // Explicit theme colors; avoid slide-dependent Text/Background aliases.
                int[] objectSlots = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
                if ((int)sel.Type == 3)
                {
                    app.StartNewUndoEntry();
                    sel.TextRange.Font.Color.ObjectThemeColor = objectSlots[slot - 1];
                    sel.TextRange.Font.Color.Brightness = 0;
                    return;
                }
                List<object> shapes = SelectedStyleShapes();
                app.StartNewUndoEntry();
                foreach (dynamic shape in shapes)
                {
                    bool text = (int)shape.Type == 17;
                    if ((int)shape.Type == 14)
                    {
                        int placeholder = (int)shape.PlaceholderFormat.Type;
                        text = new[] { 1, 2, 3, 4, 5, 6, 13, 14, 15, 16 }.Contains(placeholder);
                    }
                    if (text)
                    {
                        shape.TextFrame.TextRange.Font.Color.ObjectThemeColor = objectSlots[slot - 1];
                        shape.TextFrame.TextRange.Font.Color.Brightness = 0;
                    }
                    else if ((int)shape.Type == 9)
                    {
                        shape.Line.ForeColor.ObjectThemeColor = objectSlots[slot - 1];
                        shape.Line.ForeColor.Brightness = 0; shape.Line.Visible = -1;
                    }
                    else
                    {
                        shape.Fill.Solid(); shape.Fill.ForeColor.ObjectThemeColor = objectSlots[slot - 1];
                        shape.Fill.ForeColor.Brightness = 0; shape.Fill.Transparency = 0; shape.Fill.Visible = -1;
                    }
                }
            });
        }

        private static void CountStyleColor(Dictionary<int, int> colors, int rgb)
        {
            if (rgb < 0) return;
            rgb &= 0xffffff;
            int count; colors.TryGetValue(rgb, out count); colors[rgb] = count + 1;
        }
        private void CollectFillColors(object value, Dictionary<int, int> colors)
        {
            dynamic fill = value;
            try
            {
                if ((int)fill.Visible == 0) return;
                int type = (int)fill.Type;
                if (type == 1) CountStyleColor(colors, (int)fill.ForeColor.RGB);
                else if (type == 3)
                    for (int i = 1; i <= (int)fill.GradientStops.Count; i++) CountStyleColor(colors, (int)fill.GradientStops.Item(i).Color.RGB);
                else if (type == 2) { CountStyleColor(colors, (int)fill.ForeColor.RGB); CountStyleColor(colors, (int)fill.BackColor.RGB); }
            }
            catch { }
        }
        private void CollectShapeColors(object value, Dictionary<int, int> colors)
        {
            dynamic shape = value;
            try { if ((int)shape.Visible == 0) return; } catch { }
            CollectFillColors((object)shape.Fill, colors);
            try { if ((int)shape.Line.Visible != 0) CountStyleColor(colors, (int)shape.Line.ForeColor.RGB); } catch { }
            try
            {
                if ((int)shape.HasTextFrame == 0 || (int)shape.TextFrame.HasText == 0) return;
                dynamic runs = shape.TextFrame2.TextRange.Runs();
                for (int i = 1; i <= (int)runs.Count; i++)
                    CollectFillColors((object)shape.TextFrame2.TextRange.Runs(i, 1).Font.Fill, colors);
            }
            catch (Exception ex) { Log("document-text-color-read " + ex.Message); }
        }

        private void CollectPageShapeColors(object value, Dictionary<int, int> colors)
        {
            dynamic page = value;
            for (int j = 1; j <= (int)page.Shapes.Count; j++)
                VisitStyleShape((object)page.Shapes.Item(j), delegate(object s) { CollectShapeColors(s, colors); });
        }

        private Dictionary<int, int> CollectDocumentColors()
        {
            dynamic p = app.ActivePresentation;
            var colors = new Dictionary<int, int>();
            var layouts = new HashSet<string>();
            var masters = new HashSet<string>();
            for (int i = 1; i <= (int)p.Slides.Count; i++)
            {
                dynamic slide = p.Slides.Item(i);
                CollectFillColors((object)slide.Background.Fill, colors);
                CollectPageShapeColors((object)slide, colors);
                // Layout and master graphics are rendered on slides, but do not
                // belong to Slide.Shapes. Scan only layouts/masters actually used.
                dynamic layout = slide.CustomLayout;
                if (layouts.Add(Identity((object)layout))) CollectPageShapeColors((object)layout, colors);
                dynamic master = slide.Master;
                if (masters.Add(Identity((object)master))) CollectPageShapeColors((object)master, colors);
            }
            return colors;
        }

        private static double ColorLuminance(int rgb)
        {
            return 0.2126 * (rgb & 255) + 0.7152 * ((rgb >> 8) & 255) + 0.0722 * ((rgb >> 16) & 255);
        }
        private static int[] BuildDocumentThemePalette(Dictionary<int, int> colors)
        {
            var ranked = colors.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(12).Select(kv => kv.Key).ToList();
            if (ranked.Count == 0) return new int[0];
            var remaining = new List<int>(ranked);
            var result = new List<int>();
            // Keep the native dark/light slots meaningful, then use frequency
            // order for accents. Every written color came from the document.
            for (int slot = 0; slot < 4; slot++)
            {
                if (remaining.Count == 0) break;
                int color = slot % 2 == 0 ? remaining.OrderBy(ColorLuminance).First() : remaining.OrderByDescending(ColorLuminance).First();
                result.Add(color); remaining.Remove(color);
            }
            result.AddRange(remaining);
            for (int i = result.Count; i < 12; i++) result.Add(ranked[(i - ranked.Count) % ranked.Count]);
            return result.ToArray();
        }

        public void SaveDocumentThemeColors(object control)
        {
            RibbonAction(delegate
            {
                dynamic p = app.ActivePresentation;
                var colors = CollectDocumentColors();
                int[] palette = BuildDocumentThemePalette(colors);
                if (palette.Length == 0) throw new InvalidOperationException("文稿中未找到可读取的文字、形状或渐变颜色。");
                var schemes = new List<object>();
                var originals = new List<int[]>();
                for (int i = 1; i <= (int)p.Designs.Count; i++)
                {
                    dynamic scheme = p.Designs.Item(i).SlideMaster.Theme.ThemeColorScheme;
                    var old = new int[12];
                    for (int j = 1; j <= 12; j++) old[j - 1] = (int)scheme.Colors(j).RGB;
                    schemes.Add((object)scheme); originals.Add(old);
                }
                app.StartNewUndoEntry();
                try
                {
                    foreach (dynamic scheme in schemes)
                        for (int j = 0; j < palette.Length; j++) scheme.Colors(j + 1).RGB = palette[j];
                }
                catch
                {
                    for (int i = 0; i < schemes.Count; i++)
                        for (int j = 0; j < 12; j++) try { ((dynamic)schemes[i]).Colors(j + 1).RGB = originals[i][j]; } catch { }
                    throw;
                }
                if (ribbon != null) ribbon.InvalidateControl("peThemeColors");
                MessageBox.Show("已保存 " + Math.Min(colors.Count, 12) + " 种文档颜色，已替换全部主题槽位。" + (colors.Count > 12 ? "文稿颜色超过主题的12个槽位，已按出现次数选取前12种。" : "不足12种时使用文档颜色填满剩余槽位，菜单自动去重。") + "\n已使用主题色的对象会随主题更新；请保存文稿保留设置。", "PPT 增强", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }
    }
}
