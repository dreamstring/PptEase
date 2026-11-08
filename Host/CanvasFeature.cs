using System;
using Microsoft.Win32;

namespace PptEase.Host
{
    public sealed partial class Connect
    {
        private PptEase.CanvasInputV3.CanvasInput canvasInput;
        private System.Windows.Forms.Timer canvasStartup;
        private bool canvasWanted;
        private string canvasError = "";
        private void SetupCanvasFeature()
        {
            canvasInput = new PptEase.CanvasInputV3.CanvasInput((object)app);
            try { using(var key=Registry.CurrentUser.OpenSubKey(@"Software\PptEase")) canvasWanted=key!=null&&Convert.ToInt32(key.GetValue("CanvasEnabled",0))==1; } catch {canvasWanted=false;}
            canvasStartup = new System.Windows.Forms.Timer { Interval=500 };
            canvasStartup.Tick += delegate
            {
                if(!canvasWanted||canvasInput.IsEnabled||stopping)return;
                try
                {
                    if((int)app.Presentations.Count==0)return;
                    int view=(int)app.ActiveWindow.ViewType;if(view!=1&&view!=9)return;
                    canvasInput.Enable();canvasError="";
                    if(ribbon!=null)ribbon.InvalidateControl("peCanvasMode");
                }
                catch(Exception ex)
                {
                    if(canvasError!=ex.Message){canvasError=ex.Message;Log("canvas-start: "+ex.Message);}
                    if(ex.Message.Contains(global::PptEase.Localization.L.T("版本"))||ex.Message.Contains(global::PptEase.Localization.L.T("校验")))canvasStartup.Stop();
                }
            };
            canvasStartup.Start();
        }
        private void StopCanvasFeature()
        {
            if(canvasStartup!=null){canvasStartup.Stop();canvasStartup.Dispose();canvasStartup=null;}
            if(canvasInput!=null){canvasInput.Dispose();canvasInput=null;}
            // A normal Office shutdown never clears the user's persisted preference.
        }
        public bool GetCanvasPressed(object control) { return canvasWanted; }
        public void ToggleCanvas(object control, bool pressed)
        {
            RibbonAction(delegate
            {
                try
                {
                    if(pressed)canvasInput.Enable();else canvasInput.Disable();
                    using(var key=Registry.CurrentUser.CreateSubKey(@"Software\PptEase"))key.SetValue("CanvasEnabled",pressed?1:0,RegistryValueKind.DWord);
                    canvasWanted=pressed;canvasError="";canvasStartup.Start();
                }
                finally { if(ribbon!=null)ribbon.InvalidateControl("peCanvasMode"); }
            });
        }
        public void FitCanvas(object control)
        {
            RibbonAction(delegate {canvasInput.ResetTransient();app.ActiveWindow.View.ZoomToFit=-1;});
        }
        private object CanvasFeatureStatus() {return new {Wanted=canvasWanted,Input=canvasInput==null?null:canvasInput.Status(),Error=canvasError};}
    }
}
