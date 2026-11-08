using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PptEase.Host
{
    public sealed partial class Connect
    {
        private string colorMode = "Idle";
        private string currentColor = "";
        private object lastColorOperation;
        private int brushColor;
        private string colorContext;
        private string brushSourceSignature;
        private DateTime colorDeadline;
        private Timer colorTimer;
        
        private bool applyingColor;
        private SelectionChangeHandler selectionHandler;
        private SelectionChangeHandler slideChangeHandler;

        public delegate void SelectionChangeHandler([In, MarshalAs(UnmanagedType.IDispatch)] object selection);

        private void SetupColorTools()
        {
            selectionHandler = OnColorSelectionChange;
            ComEventsHelper.Combine((object)app, EApplication, 2001, selectionHandler);
            slideChangeHandler = OnSlideSelectionChanged;
            ComEventsHelper.Combine((object)app, EApplication, 2016, slideChangeHandler);
            colorTimer = new Timer { Interval = 500 };
            colorTimer.Tick += delegate
            {
                if (colorMode == "Idle") return;
                try
                {
                    if (DateTime.UtcNow > colorDeadline) CancelColorMode(global::PptEase.Localization.L.T("30秒未完成，已取消"));
                    else if (CurrentColorContext() != colorContext) CancelColorMode(global::PptEase.Localization.L.T("页面或文稿已切换，已取消"));
                }
                catch { CancelColorMode(global::PptEase.Localization.L.T("当前编辑视图已变化，已取消")); }
            };
            colorTimer.Start();
        }

        private void TeardownColorTools()
        {
            try { if (slideChangeHandler != null && app != null) ComEventsHelper.Remove((object)app, EApplication, 2016, slideChangeHandler); } catch { }
            slideChangeHandler = null;
            try { if (selectionHandler != null && app != null) ComEventsHelper.Remove((object)app, EApplication, 2001, selectionHandler); } catch { }
            try { if (colorTimer != null) { colorTimer.Stop(); colorTimer.Dispose(); } } catch { }
            colorTimer = null;
            CancelColorMode(global::PptEase.Localization.L.T("增强已断开"));
        }

        public void PickColor(object control) { RibbonAction(StartNativePick); }
        public void ApplyColor(object control) { CancelColorMode(global::PptEase.Localization.L.T("选区赋色功能已移除")); }
        public void CancelColor(object control) { CancelColorMode(global::PptEase.Localization.L.T("用户取消")); }

        private string CurrentColorContext()
        {
            dynamic pres = app.ActivePresentation;
            dynamic slide = app.ActiveWindow.View.Slide;
            return nonce + ":" + Identity((object)pres) + ":" + Convert.ToString((int)slide.SlideID);
        }

        private void StartNativePick() { StartNativePick(null); }
        private void StartNativePick(List<string> explicitKeys)
        {
            CancelColorMode(global::PptEase.Localization.L.T("切换为原生吸管"));
            List<ShapeHandle> all = GetShapes();
            List<string> keys = explicitKeys != null && explicitKeys.Count > 0 ? explicitKeys.Distinct().ToList() : SelectedKeys();
            List<ShapeHandle> selected = keys.Select(k => all.FirstOrDefault(h => h.Key == k)).ToList();
            if (selected.Any(h => h == null)) throw new InvalidOperationException(global::PptEase.Localization.L.T("选区已过期，请刷新后重试。"));
            // Preserve a live text range when the manager refers to the same native selection.
            bool sameSelection = new HashSet<string>(keys).SetEquals(SelectedKeys());
            bool textSelection = (int)app.ActiveWindow.Selection.Type == 3 && sameSelection;
            if (selected.Count == 0 && !textSelection) throw new InvalidOperationException(global::PptEase.Localization.L.T("请先选中文字、文本框、形状或线条。"));
            string command = textSelection ? "EyedropperFillText" : NativePickCommand(selected);
            if (explicitKeys != null && explicitKeys.Count > 0 && !sameSelection)
            {
                for (int i = 0; i < selected.Count; i++) selected[i].Shape.Select(i == 0 ? -1 : 0);
            }
            if (!(bool)app.CommandBars.GetEnabledMso(command))
                throw new InvalidOperationException(global::PptEase.Localization.L.T("当前选区不能使用原生吸管。请退出对话框，选择可编辑的文字或图形后重试。"));
            app.CommandBars.ExecuteMso(command);
            lastColorOperation = new { State = global::PptEase.Localization.L.T("已调用PPT原生吸管；点击取色，Esc取消"), Direction = "native-eyedropper", Command = command, TargetCount = selected.Count, Utc = DateTime.UtcNow.ToString("o") };
            InvalidateColorRibbon();
        }

        private static string NativePickCommand(List<ShapeHandle> selected)
        {
            string command = null;
            foreach (ShapeHandle h in selected)
            {
                int type = (int)h.Shape.Type;
                bool text = type == 17;
                if (type == 14)
                {
                    int placeholder = (int)h.Shape.PlaceholderFormat.Type;
                    text = new[] { 1, 2, 3, 4, 5, 6, 13, 14, 15, 16 }.Contains(placeholder);
                }
                if (type == 6 || type == 19) throw new InvalidOperationException(global::PptEase.Localization.L.T("请进入组合或表格，选择具体对象或文字。"));
                string next = text ? "EyedropperFillText" : type == 9 ? "EyedropperOutline" : "EyedropperFill";
                if (command != null && command != next) throw new InvalidOperationException(global::PptEase.Localization.L.T("混合选区包含不同颜色通道，请单独选择文字、形状或线条。"));
                command = next;
            }
            if (command == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("请先选择取色目标。"));
            return command;
        }
        private void StartColorBrush() { StartColorBrush(null); }
        private void StartColorBrush(List<string> explicitKeys)
        {
            CancelColorMode(global::PptEase.Localization.L.T("切换颜色工具"));
            List<ColorTarget> sources = explicitKeys != null && explicitKeys.Count > 0 ? CaptureKeyColorTargets(explicitKeys) : CaptureColorTargets((object)app.ActiveWindow.Selection);
            int rgb = ReadTargetColor(ResolveTarget(sources[0]), sources[0]);
            for (int i = 1; i < sources.Count; i++) if (ReadTargetColor(ResolveTarget(sources[i]), sources[i]) != rgb) throw new InvalidOperationException(global::PptEase.Localization.L.T("源选区包含多种颜色，请选一个颜色一致的对象或文字片段。"));
            if (rgb < 0) throw new InvalidOperationException(global::PptEase.Localization.L.T("源文字包含混合颜色，请选择颜色一致的文字片段。"));
            brushColor = rgb;
            currentColor = ToHex(rgb);
            colorContext = CurrentColorContext();
            brushSourceSignature = TargetSignature(sources);
            colorDeadline = DateTime.UtcNow.AddSeconds(30);
            colorMode = "ApplyNextSelection";
            lastColorOperation = new { State = global::PptEase.Localization.L.T("已吸取选区颜色；请在同一页选择一个不同目标以赋色"), Direction = "selection-to-next-selection", Color = currentColor, SourceKinds = sources.Select(t => t.Kind).ToArray(), Utc = DateTime.UtcNow.ToString("o") };
            InvalidateColorRibbon();
        }

        private void OnSlideSelectionChanged(object slideRange)
        {
            // Never retain a previous page's right-click targets or open unlock chooser.
            contextKeys.Clear();
            lastRightClickAt = DateTime.MinValue;
            try { if (unlockChooser != null) unlockChooser.Close(); } catch { }
            try
            {
                if (ribbon == null) return;
                ribbon.InvalidateControl("PptEaseLockedList");
                for (int i = 0; i < 6; i++) ribbon.InvalidateControl("PptEaseContext" + i);
            }
            catch (Exception ex) { Log("slide-menu-refresh-error " + ex.Message); }
        }

        private void OnColorSelectionChange(object selection)
        {
            try { if (ribbon != null) ribbon.InvalidateControl("PptEaseLockedList"); } catch { }
            if (colorMode != "ApplyNextSelection" || applyingColor) return;
            try
            {
                if (DateTime.UtcNow > colorDeadline || CurrentColorContext() != colorContext) { CancelColorMode(global::PptEase.Localization.L.T("页面、文稿或等待时间已变化")); return; }
                List<ColorTarget> targets;
                try { targets = CaptureColorTargets(selection); } catch { return; } // Empty canvas does not paint.
                if (TargetSignature(targets) == brushSourceSignature) return;
                EnsureWritable(targets);
                colorMode = "Idle"; // One-shot, before triggering any host event.
                CommitColors(targets, brushColor);
                lastColorOperation = new { State = global::PptEase.Localization.L.T("已把源颜色赋给新选区"), Direction = "selection-to-next-selection", Color = currentColor, TargetCount = targets.Count, Kinds = targets.Select(t => t.Kind).ToArray(), Utc = DateTime.UtcNow.ToString("o") };
                InvalidateColorRibbon();
            }
            catch (Exception ex) { ColorFailure(ex.Message); }
        }

        private void CancelColorMode(string reason)
        {
            bool hadMode = colorMode != "Idle";
            colorMode = "Idle";
            if (hadMode) lastColorOperation = new { State = reason, Canceled = true, Utc = DateTime.UtcNow.ToString("o") };
            brushSourceSignature = null; colorContext = null;
            InvalidateColorRibbon();
        }

        private void ColorFailure(string error)
        {
            colorMode = "Idle";
            lastColorOperation = new { State = global::PptEase.Localization.L.T("颜色操作未完成"), Error = error, Utc = DateTime.UtcNow.ToString("o") };
            Log("color-error " + error);
            InvalidateColorRibbon();
        }

        private void InvalidateColorRibbon() { try { if (ribbon != null) ribbon.Invalidate(); } catch { } }

        private List<ColorTarget> CaptureColorTargets(object selection)
        {
            dynamic sel = selection;
            List<ShapeHandle> all = GetShapes();
            List<ColorTarget> targets = new List<ColorTarget>();
            if ((int)sel.Type == 3)
            {
                dynamic text = sel.TextRange;
                int start = (int)text.Start, length = (int)text.Length;
                if (length <= 0) throw new InvalidOperationException(global::PptEase.Localization.L.T("请选中文字片段，或退出文字编辑后选择文本框。"));
                string key = FindTextShapeKey((object)text, all);
                targets.Add(new ColorTarget { Key = key, Kind = "TextRange", Start = start, Length = length });
            }
            else
            {
                List<string> keys = SelectedKeys(selection);
                foreach (string key in keys)
                {
                    ShapeHandle h = all.First(x => x.Key == key);
                    int type = (int)h.Shape.Type;
                    if (type == 6 || type == 19) throw new InvalidOperationException(global::PptEase.Localization.L.T("请进入组合或表格，选择要赋色的具体对象或文字。"));
                    targets.Add(new ColorTarget { Key = key, Kind = type == 17 ? "TextBoxFont" : type == 9 ? "Line" : "Fill" });
                }
            }
            if (targets.Count == 0) throw new InvalidOperationException(global::PptEase.Localization.L.T("请先在 PowerPoint 中选择文字、文本框、线条或形状。"));
            return targets;
        }

        private List<ColorTarget> CaptureKeyColorTargets(List<string> keys)
        {
            List<ShapeHandle> all = GetShapes();
            List<ColorTarget> targets = new List<ColorTarget>();
            foreach (string key in keys.Distinct())
            {
                ShapeHandle h = all.FirstOrDefault(x => x.Key == key);
                if (h == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("颜色目标已过期，请刷新对象列表。"));
                int type = (int)h.Shape.Type;
                if (type == 6 || type == 19) throw new InvalidOperationException(global::PptEase.Localization.L.T("请进入组合或表格，选择具体对象或文字。"));
                targets.Add(new ColorTarget { Key = key, Kind = type == 17 ? "TextBoxFont" : type == 9 ? "Line" : "Fill" });
            }
            return targets;
        }

        private string FindTextShapeKey(object textRange, List<ShapeHandle> all)
        {
            dynamic item = textRange;
            for (int depth = 0; depth < 8; depth++)
            {
                try
                {
                    int id = (int)item.Id;
                    List<ShapeHandle> match = all.Where(h => h.Id == id).ToList();
                    if (match.Count == 1) return match[0].Key;
                }
                catch { }
                try { item = item.Parent; } catch { break; }
            }
            throw new InvalidOperationException(global::PptEase.Localization.L.T("无法稳定定位这段文字所属对象；请先选择独立文本框。"));
        }

        private ShapeHandle ResolveTarget(ColorTarget target)
        {
            ShapeHandle h = GetShapes().FirstOrDefault(x => x.Key == target.Key);
            if (h == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("颜色目标已过期或页面已切换，请重新开始。"));
            return h;
        }

        private void EnsureWritable(List<ColorTarget> targets)
        {
            List<ShapeHandle> all = GetShapes();
            foreach (ColorTarget target in targets)
            {
                ShapeHandle h = all.FirstOrDefault(x => x.Key == target.Key);
                if (h == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("颜色目标已失效，请刷新。"));
                if (all.Any(parent => (parent.Key == target.Key || target.Key.StartsWith(parent.Key + ":", StringComparison.Ordinal)) && ReadLocked(parent.Shape))) throw new InvalidOperationException(global::PptEase.Localization.L.T("目标对象或父组已锁定；颜色工具不会自动解锁。"));
                if (target.Kind == "Fill" && (int)h.Shape.Fill.Type != 1) throw new InvalidOperationException(global::PptEase.Localization.L.T("当前颜色工具支持纯色填充；渐变、图片或图案填充请用原生格式工具处理。"));
            }
        }

        private static string TargetSignature(List<ColorTarget> targets) { return String.Join("|", targets.Select(t => t.Key + ":" + t.Kind + ":" + t.Start + ":" + t.Length).OrderBy(s => s, StringComparer.Ordinal)); }
        private dynamic TextRange(ShapeHandle h, ColorTarget target)
        {
            dynamic text = h.Shape.TextFrame.TextRange;
            if (target.Kind == "TextRange")
            {
                if (target.Start < 1 || target.Length < 1 || target.Start + target.Length - 1 > (int)text.Length) throw new InvalidOperationException(global::PptEase.Localization.L.T("文字内容长度已变化，请重新选择文字后取色。"));
                return text.Characters(target.Start, target.Length);
            }
            return text;
        }
        private int ReadTargetColor(ShapeHandle h, ColorTarget target)
        {
            if (target.Kind == "TextRange" || target.Kind == "TextBoxFont") return (int)TextRange(h, target).Font.Color.RGB;
            if (target.Kind == "Line") { if ((int)h.Shape.Line.Visible == 0) throw new InvalidOperationException(global::PptEase.Localization.L.T("源线条没有可见颜色。")); return (int)h.Shape.Line.ForeColor.RGB; }
            if ((int)h.Shape.Fill.Visible == 0 || (int)h.Shape.Fill.Type != 1) throw new InvalidOperationException(global::PptEase.Localization.L.T("源对象没有可吸取的纯色填充。"));
            return (int)h.Shape.Fill.ForeColor.RGB;
        }

        private ColorOriginal CaptureOriginal(ShapeHandle h, ColorTarget target)
        {
            return new ColorOriginal { Handle = h, Target = target, Rgb = ReadTargetColorForWrite(h, target), Visible = target.Kind == "Line" ? (int)h.Shape.Line.Visible : target.Kind == "Fill" ? (int)h.Shape.Fill.Visible : -1 };
        }
        private int ReadTargetColorForWrite(ShapeHandle h, ColorTarget target)
        {
            if (target.Kind == "TextRange" || target.Kind == "TextBoxFont")
            {
                int color = (int)TextRange(h, target).Font.Color.RGB;
                if (color < 0) throw new InvalidOperationException(global::PptEase.Localization.L.T("目标文字包含混合颜色；请先缩小为颜色一致的片段，以保证失败时可完整恢复。"));
                return color;
            }
            return target.Kind == "Line" ? (int)h.Shape.Line.ForeColor.RGB : (int)h.Shape.Fill.ForeColor.RGB;
        }
        private void WriteTargetColor(ShapeHandle h, ColorTarget target, int rgb, int? visible)
        {
            if (target.Kind == "TextRange" || target.Kind == "TextBoxFont") TextRange(h, target).Font.Color.RGB = rgb;
            else if (target.Kind == "Line") { h.Shape.Line.ForeColor.RGB = rgb; h.Shape.Line.Visible = visible.HasValue ? visible.Value : -1; }
            else { h.Shape.Fill.ForeColor.RGB = rgb; h.Shape.Fill.Visible = visible.HasValue ? visible.Value : -1; }
        }

        private void CommitColors(List<ColorTarget> targets, int rgb)
        {
            EnsureWritable(targets);
            List<ColorOriginal> originals = targets.Select(t => CaptureOriginal(ResolveTarget(t), t)).ToList();
            List<ColorOriginal> attempted = new List<ColorOriginal>();
            applyingColor = true;
            try
            {
                app.StartNewUndoEntry();
                foreach (ColorOriginal item in originals)
                {
                    attempted.Add(item);
                    WriteTargetColor(item.Handle, item.Target, rgb, null);
                    if (ReadTargetColor(item.Handle, item.Target) != rgb) throw new InvalidOperationException(global::PptEase.Localization.L.T("宿主未应用目标颜色。"));
                }
                Log("color-write targets=" + originals.Count + " rgb=" + ToHex(rgb));
            }
            catch
            {
                foreach (ColorOriginal item in attempted.AsEnumerable().Reverse()) { try { WriteTargetColor(item.Handle, item.Target, item.Rgb, item.Visible); } catch { } }
                throw;
            }
            finally { applyingColor = false; }
        }

        private static string ToHex(int rgb) { return "#" + (rgb & 255).ToString("X2") + ((rgb >> 8) & 255).ToString("X2") + ((rgb >> 16) & 255).ToString("X2"); }
        private sealed class ColorTarget { public string Key; public string Kind; public int Start; public int Length; }
        private sealed class ColorOriginal { public ShapeHandle Handle; public ColorTarget Target; public int Rgb; public int Visible; }

    }
}
