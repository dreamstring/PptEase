using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace PptEase.Host
{
 public sealed partial class Connect
 {
  private const string SpawnTag="PPTEASE_SPAWN_OBJECT";
  private const string SpawnedTag="PPTEASE_SPAWNED_FROM";
  private SelectionChangeHandler newSlideHandler;
  private bool spawning;
  private void SetupPageObjects(){newSlideHandler=OnPageCreated;ComEventsHelper.Combine((object)app,EApplication,2008,newSlideHandler);}
  private void StopPageObjects(){try{if(newSlideHandler!=null)ComEventsHelper.Remove((object)app,EApplication,2008,newSlideHandler);}catch{}newSlideHandler=null;}
  private void OnPageCreated(object slide){try{CreatePageObjects(slide);}catch(Exception e){Log("page-object-create "+e.Message);}}
  public void MarkPageObject(object control){RibbonAction(delegate{
   if((int)app.ActiveWindow.ViewType!=2)throw new InvalidOperationException("请在“视图→幻灯片母版”中，选择版式上的普通图形后标记。");
   dynamic selection=app.ActiveWindow.Selection;
   if((int)selection.Type!=2)throw new InvalidOperationException("请先选中普通图形。");
   var list=new List<object>();
   for(int i=1;i<=(int)selection.ShapeRange.Count;i++){
    dynamic s=selection.ShapeRange.Item(i);
    if((int)s.Type!=1 || (int)s.Fill.Type!=1 || (float)s.Fill.Transparency!=0 || (int)s.Shadow.Visible!=0 || (int)s.TextFrame.HasText!=0)
     throw new InvalidOperationException("首版支持不透明、纯色、无阴影的普通形状（例如浅绿色矩形）；占位符、半透明蒙版和组合暂不支持，以免重复叠加。");
    list.Add((object)s);
   }
   app.StartNewUndoEntry();
   foreach(dynamic s in list)if(String.IsNullOrEmpty((string)s.Tags.Item(SpawnTag)))s.Tags.Add(SpawnTag,Guid.NewGuid().ToString("N"));
   System.Windows.Forms.MessageBox.Show("已标记。新建页面会生成普通图形；已有页面请点击“补齐页面对象”。生成后可独立编辑，不是占位符。","PPT 增强");
  });}
  public void UnmarkPageObject(object control){RibbonAction(delegate{
   if((int)app.ActiveWindow.ViewType!=2)throw new InvalidOperationException("请在母版视图中选择原图形。");
   dynamic range=app.ActiveWindow.Selection.ShapeRange;app.StartNewUndoEntry();
   for(int i=1;i<=(int)range.Count;i++)range.Item(i).Tags.Delete(SpawnTag);
  });}
  public void FillPageObjects(object control){RibbonAction(delegate{
   if((int)app.ActiveWindow.ViewType!=9&&(int)app.ActiveWindow.ViewType!=1)throw new InvalidOperationException("请返回普通页面编辑视图。");
   app.StartNewUndoEntry();CreatePageObjects((object)app.ActiveWindow.View.Slide);
  });}
  private void CreatePageObjects(object value){
   if(spawning)return;spawning=true;
   var created=new List<object>();
   try{
    dynamic slide=value;var existing=new HashSet<string>();
    for(int i=1;i<=(int)slide.Shapes.Count;i++){string key=(string)slide.Shapes.Item(i).Tags.Item(SpawnedTag);if(!String.IsNullOrEmpty(key))existing.Add(key);}
    foreach(object page in new object[]{(object)slide.Master,(object)slide.CustomLayout}){
     dynamic sourcePage=page;
     for(int i=1;i<=(int)sourcePage.Shapes.Count;i++){
      dynamic source=sourcePage.Shapes.Item(i);string key=(string)source.Tags.Item(SpawnTag);
      if(String.IsNullOrEmpty(key)||existing.Contains(key))continue;
      if((int)source.Type!=1||(int)source.Fill.Type!=1||(float)source.Fill.Transparency!=0||(int)source.Shadow.Visible!=0||(int)source.TextFrame.HasText!=0)continue;
      dynamic target=slide.Shapes.AddShape((int)source.AutoShapeType,(float)source.Left,(float)source.Top,(float)source.Width,(float)source.Height);
      created.Add((object)target);source.PickUp();target.Apply();target.Rotation=source.Rotation;
      target.Visible=source.Visible;
      target.Tags.Add(SpawnedTag,key);target.Name="页面对象 · "+(string)source.Name;
      existing.Add(key);
     }
    }
   }catch{foreach(dynamic s in created)try{s.Delete();}catch{}throw;}finally{spawning=false;}
  }
 }
}
