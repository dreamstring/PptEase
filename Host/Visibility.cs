using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Windows.Forms;
namespace PptEase.Host {
 public sealed partial class Connect {
  private static bool IsHidden(dynamic shape) { return (int)shape.Visible == 0; }
  private void SetVisibleKeys(List<string> keys, bool visible) {
   var targets=GetShapes().Where(h=>keys.Contains(h.Key)).ToList();
   if(targets.Count==0) throw new InvalidOperationException(PptEase.Localization.L.T("请先选择对象。"));
   app.StartNewUndoEntry();
   foreach(var h in targets) h.Shape.Visible=visible ? -1 : 0;
   if(ribbon!=null) ribbon.Invalidate();
  }
  public void HideSelection(object control) { RibbonAction(delegate { SetVisibleKeys(SelectedKeys(),false); }); }
  public void ShowHiddenTagged(object control) { RibbonAction(delegate { dynamic c=control; SetVisibleKeys(new List<string>{(string)c.Tag},true); }); }
  public string GetHiddenMenu(object control) {
   var x=new StringBuilder("<menu xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\">");
   try { int i=0; foreach(var h in GetShapes().Where(h=>IsHidden(h.Shape))) {
    x.Append("<button id=\"peHiddenItem").Append(i++).Append("\" label=\"").Append(SecurityElement.Escape(PptEase.Localization.L.T("显示：")+SafeString(delegate{return (string)h.Shape.Name;},h.Id.ToString()))).Append("\" tag=\"").Append(SecurityElement.Escape(h.Key)).Append("\" onAction=\"ShowHiddenTagged\"/>");
   } if(i==0)x.Append("<button id=\"peHiddenNone\" label=\"本页没有隐藏对象\" enabled=\"false\"/>");
   } catch {x.Append("<button id=\"peHiddenError\" label=\"当前视图不可用，请打开普通编辑视图\" enabled=\"false\"/>");}
   return CompleteRibbonIcons(x.Append("</menu>").ToString());
  }
 }
}

