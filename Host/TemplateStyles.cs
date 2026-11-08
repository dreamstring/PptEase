using System;
using System.Collections.Generic;
using System.Security;
using System.Text;
using System.Windows.Forms;

namespace PptEase.Host
{
    public sealed partial class Connect
    {
        private const string TemplateTag = "PPTEASE_TEMPLATE_SLIDE";
        private Timer templateTimer;
        private bool templateWasVisible;

        private dynamic FindTemplateSlide()
        {
            try
            {
                dynamic p = app.ActivePresentation;
                int id;
                if (!Int32.TryParse((string)p.Tags.Item(TemplateTag), out id)) return null;
                return p.Slides.FindBySlideID(id);
            }
            catch { return null; }
        }

        public bool GetTemplateVisible(object control) { return FindTemplateSlide() != null; }

        private void SetupTemplateStyles()
        {
            templateTimer = new Timer { Interval = 500 };
            templateTimer.Tick += delegate
            {
                bool visible = GetTemplateVisible(null);
                if (visible == templateWasVisible) return;
                templateWasVisible = visible;
                try { if (ribbon != null) ribbon.Invalidate(); } catch { }
            };
            templateTimer.Start();
        }

        private void StopTemplateStyles()
        {
            if (templateTimer == null) return;
            templateTimer.Stop(); templateTimer.Dispose(); templateTimer = null;
        }

        public void SetTemplateSlide(object control)
        {
            RibbonAction(delegate
            {
                dynamic slide = app.ActiveWindow.View.Slide;
                app.ActivePresentation.Tags.Add(TemplateTag, Convert.ToString((int)slide.SlideID));
                if (ribbon != null) ribbon.Invalidate();
            });
        }

        public void ClearTemplateSlide(object control)
        {
            RibbonAction(delegate
            {
                app.ActivePresentation.Tags.Delete(TemplateTag);
                if (ribbon != null) ribbon.Invalidate();
            });
        }

        public string GetTemplateStyles(object control)
        {
            // No stored style snapshots: resolve the designated slide on every expansion.
            StringBuilder x = new StringBuilder("<menu xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\">");
            dynamic slide = FindTemplateSlide();
            if (slide != null)
            {
                int count = (int)slide.Shapes.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic shape = slide.Shapes.Item(i);
                    string label = (string)shape.Name;
                    try
                    {
                        if ((int)shape.HasTextFrame != 0 && (int)shape.TextFrame.HasText != 0)
                        {
                            dynamic text = shape.TextFrame.TextRange;
                            string sample = ((string)text.Text).Replace("\r", " ").Replace("\n", " ");
                            if (sample.Length > 24) sample = sample.Substring(0, 24) + "…";
                            label = sample + " · " + Convert.ToString(text.Font.Name) + " · " + Convert.ToString(text.Font.Size) + " pt";
                        }
                    }
                    catch { }
                    x.Append("<button id=\"peTemplateStyle").Append(i).Append("\" label=\"").Append(SecurityElement.Escape(label))
                     .Append("\" tag=\"").Append((int)slide.SlideID).Append(":").Append((int)shape.Id).Append(":").Append(Identity((object)app.ActivePresentation))
                     .Append("\" onAction=\"ApplyTemplateStyle\" screentip=\"从模板源对象实时复制完整格式（含填充与渐变）\"/>");
                }
                if (count == 0) x.Append("<button id=\"peTemplateEmpty\" label=\"模板页没有对象\" enabled=\"false\"/>");
            }
            return CompleteRibbonIcons(x.Append("</menu>").ToString());
        }

        public void ApplyTemplateStyle(object control)
        {
            RibbonAction(delegate
            {
                dynamic c = control;
                string[] key = ((string)c.Tag).Split(':');
                dynamic slide = FindTemplateSlide();
                if (key.Length != 3 || key[2] != Identity((object)app.ActivePresentation) || slide == null || Convert.ToString((int)slide.SlideID) != key[0]) throw new InvalidOperationException(global::PptEase.Localization.L.T("模板页已变化，请重新展开菜单。"));
                dynamic source = null;
                for (int i = 1; i <= (int)slide.Shapes.Count; i++)
                {
                    dynamic candidate = slide.Shapes.Item(i);
                    if (Convert.ToString((int)candidate.Id) == key[1]) { source = candidate; break; }
                }
                if (source == null) throw new InvalidOperationException(global::PptEase.Localization.L.T("模板对象已删除，请重新展开菜单。"));
                dynamic selection = app.ActiveWindow.Selection;
                if ((int)selection.Type != 2) throw new InvalidOperationException(global::PptEase.Localization.L.T("请先选择要应用完整样式的图形或文本框。"));
                var targets = new List<object>();
                for (int i = 1; i <= (int)selection.ShapeRange.Count; i++) targets.Add((object)selection.ShapeRange.Item(i));
                app.StartNewUndoEntry();
                source.PickUp();
                foreach (dynamic target in targets) target.Apply();
            });
        }
    }
}
