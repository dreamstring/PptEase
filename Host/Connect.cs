using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Reflection;
using System.Security;

[assembly: ComVisible(false)]
[assembly: System.Reflection.AssemblyVersion("0.1.*")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.0.0")]

namespace PptEase.Host
{
    [ComVisible(true), Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IDTExtensibility2
    {
        [DispId(1)] void OnConnection([In, MarshalAs(UnmanagedType.IDispatch)] object application, int connectMode, [In, MarshalAs(UnmanagedType.IDispatch)] object addInInst, [In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(2)] void OnDisconnection(int removeMode, [In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(3)] void OnAddInsUpdate([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(4)] void OnStartupComplete([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
        [DispId(5)] void OnBeginShutdown([In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref Array custom);
    }

    [ComVisible(true), Guid("000C0396-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IRibbonExtensibility
    {
        [DispId(1)]
        [return: MarshalAs(UnmanagedType.BStr)] string GetCustomUI([MarshalAs(UnmanagedType.BStr)] string ribbonId);
    }

    [ComVisible(true), Guid("84A31892-2FDB-4B79-A832-B0DA779BD41D"), ProgId("PptEase.Connect"), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed partial class Connect : IDTExtensibility2, IRibbonExtensibility
    {
        private dynamic app;
        private dynamic ribbon;
        private Control dispatcher;
        private Thread pipeThread;
        private volatile bool stopping;
        private NamedPipeServerStream activePipe;
        private readonly object pipeGate = new object();
        private string nonce = Guid.NewGuid().ToString("N");
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 };
        private RightClickHandler rightClickHandler;
        private static readonly Guid EApplication = new Guid("914934C2-5A91-11CF-8700-00AA0060263B");
        private List<string> contextKeys = new List<string>();
        private object lastRightClick;
        private DateTime lastRightClickAt;
        private long rightClickCount;
        private ContextMenuStrip unlockChooser;
        private string connectionError = "";
        private string pipeName;

        public delegate void RightClickHandler([In, MarshalAs(UnmanagedType.IDispatch)] object selection, [In, Out] ref bool cancel);

        public void OnConnection(object application, int connectMode, object addInInst, ref Array custom)
        {
            try
            {
                app = application;
                stopping = false;
                nonce = Guid.NewGuid().ToString("N");
                dispatcher = new Control();
                IntPtr handle = dispatcher.Handle; // Created on Office's STA.
                pipeName = "PptEase-" + Process.GetCurrentProcess().Id;
                rightClickHandler = OnBeforeRightClick;
                ComEventsHelper.Combine((object)app, EApplication, 2002, rightClickHandler);
                SetupColorTools();
                SetupCanvasFeature();
                SetupTemplateStyles(); SetupPageObjects(); SetupShaderRuntime();
                pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "PptEase host pipe" };
                pipeThread.Start();
                Log("connected version=0.1.0 pid=" + Process.GetCurrentProcess().Id);
            }
            catch (Exception ex) { connectionError = ex.Message; Log("connect-error " + ex); }
        }

        public void OnDisconnection(int removeMode, ref Array custom) { Stop(); }
        public void OnBeginShutdown(ref Array custom) { Stop(); }
        public void OnAddInsUpdate(ref Array custom) { }
        public void OnStartupComplete(ref Array custom) { }

        private void Stop()
        {
            if (stopping) return;
            stopping = true;
            StopShaderRuntime(); StopPageObjects(); StopTemplateStyles();
            StopCanvasFeature();
            TeardownColorTools();
            try { if (unlockChooser != null) { unlockChooser.Close(); unlockChooser.Dispose(); } } catch { }
            unlockChooser = null;
            try { if (app != null && rightClickHandler != null) ComEventsHelper.Remove((object)app, EApplication, 2002, rightClickHandler); } catch { }
            lock (pipeGate) { try { if (activePipe != null) activePipe.Dispose(); } catch { } activePipe = null; }
            try { if (dispatcher != null) dispatcher.Dispose(); } catch { }
            dispatcher = null; ribbon = null; app = null; contextKeys.Clear();
            Log("disconnected");
        }

        public string GetCustomUI(string ribbonId)
        {
            string[] menus = { "ContextMenuFrame", "ContextMenuShape", "ContextMenuPicture", "ContextMenuObjectsGroup", "ContextMenuShapeConnector", "ContextMenuShapeFreeform" };
            StringBuilder x = new StringBuilder("<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\" onLoad=\"RibbonLoaded\"><ribbon><tabs><tab id=\"PptEaseTab\" label=\"PPT 增强\"><group id=\"PptEaseSelectGroup\" label=\"对象与选择\"><button id=\"peLock\" label=\"锁定所选\" size=\"large\" imageMso=\"LockObject\" onAction=\"LockSelection\"/><button id=\"peUnlock\" label=\"解锁对象\" size=\"large\" imageMso=\"UnlockObject\" onAction=\"UnlockSelection\"/><dynamicMenu id=\"PptEaseLockedList\" label=\"本页锁定对象\" size=\"large\" invalidateContentOnDrop=\"true\" getContent=\"GetLockedMenu\"/></group><group id=\"peColorGroup\" label=\"取色\"><button id=\"pePickColor\" label=\"智能取色\" size=\"large\" imageMso=\"ShapeFillColorPicker\" onAction=\"PickColor\"/></group><group id=\"PptEaseAppGroup\" label=\"增强工具\"><button id=\"PptEaseManager\" label=\"打开管理器\" size=\"large\" onAction=\"OpenManager\"/></group></tab></tabs></ribbon><contextMenus>");
            x.Replace("<group id=\"PptEaseAppGroup\"", "<group id=\"peCanvasGroup\" label=\"自由画布\"><toggleButton id=\"peCanvasMode\" label=\"自由画布\" size=\"large\" imageMso=\"ZoomDialog\" getPressed=\"GetCanvasPressed\" onAction=\"ToggleCanvas\" screentip=\"空格或中键拖动；滚轮平移；Ctrl滚轮以鼠标缩放\"/><button id=\"peCanvasFit\" label=\"居中适配\" imageMso=\"ZoomFitToWindow\" onAction=\"FitCanvas\"/></group><group id=\"PptEaseAppGroup\"");
            x.Replace("label=\"打开管理器\"", "label=\"安装与维护\"");
            x.Replace("<group id=\"PptEaseAppGroup\"", "<group id=\"peDocumentStyles\" label=\"文档字体与配色\"><button id=\"peTightenText\" label=\"收紧文本框\" size=\"large\" imageMso=\"TextBoxInsert\" onAction=\"TightenTextBoxes\" screentip=\"单行收紧宽高，多行保留宽度；文字位置不变\"/><button id=\"peSaveDocumentTheme\" label=\"文档颜色存为主题\" size=\"large\" imageMso=\"ShapeFillColorPicker\" onAction=\"SaveDocumentThemeColors\" screentip=\"提取本文档文字、形状及渐变色标，最多12种颜色\"/><dynamicMenu id=\"peThemeColors\" label=\"主题颜色\" size=\"large\" invalidateContentOnDrop=\"true\" getContent=\"GetThemeColors\"/><dynamicMenu id=\"peDocumentFonts\" label=\"本文档字体\" size=\"large\" invalidateContentOnDrop=\"true\" getContent=\"GetDocumentFonts\"/></group><group id=\"PptEaseAppGroup\"");
            x.Replace("<group id=\"PptEaseAppGroup\"", "<group id=\"peTemplateGroup\" label=\"模板页\"><button id=\"peMarkPageObject\" label=\"标记为页面对象\" imageMso=\"SlideMasterInsertLayout\" onAction=\"MarkPageObject\"/><button id=\"peFillPageObjects\" label=\"补齐页面对象\" imageMso=\"SlideLayoutGallery\" onAction=\"FillPageObjects\"/><button id=\"peUnmarkPageObject\" label=\"取消生成标记\" imageMso=\"MasterViewClose\" onAction=\"UnmarkPageObject\"/><button id=\"peSetTemplate\" label=\"设本页为模板\" onAction=\"SetTemplateSlide\"/><button id=\"peClearTemplate\" label=\"取消模板页\" getVisible=\"GetTemplateVisible\" onAction=\"ClearTemplateSlide\"/><dynamicMenu id=\"peTemplateStyles\" label=\"模板样式\" size=\"large\" getVisible=\"GetTemplateVisible\" invalidateContentOnDrop=\"true\" getContent=\"GetTemplateStyles\"/></group><group id=\"PptEaseAppGroup\"");
            for (int i = 0; i < menus.Length; i++)
                x.Append("<contextMenu idMso=\"").Append(menus[i]).Append("\"><dynamicMenu id=\"PptEaseContext").Append(i).Append("\" label=\"PPT 增强：管理锁定对象\" invalidateContentOnDrop=\"true\" getContent=\"GetLockedMenu\"/></contextMenu>");
            x.Replace("<group id=\"PptEaseAppGroup\"", "<group id=\"peShaderGroup\" label=\"Shader 组件\"><button id=\"peInsertShader\" label=\"插入 Shader\" imageMso=\"ShapeFillColorPicker\" onAction=\"InsertShaderComponent\"/><menu id=\"peControlPalette\" label=\"插入交互控件\" imageMso=\"ShapeRectangle\" itemSize=\"large\"><button id=\"peShapePad\" label=\"方形坐标\" imageMso=\"ShapeRectangle\" onAction=\"InsertShapePad\"/><button id=\"peShapeDisk\" label=\"圆盘\" description=\"同时控制两个参数，圆内自由拖动\" imageMso=\"ShapeOval\" onAction=\"InsertShapeDisk\"/><button id=\"peShapeSlider\" label=\"滑块\" description=\"连续调整一个数值参数\" imageMso=\"ShapeRectangle\" onAction=\"InsertShapeSlider\"/><button id=\"peShapeCheckbox\" label=\"复选框\" description=\"点击切换关闭与开启值\" imageMso=\"Checkbox\" onAction=\"InsertShapeCheckbox\"/></menu><button id=\"peEditShader\" label=\"编辑 Shader\" imageMso=\"ApplicationOptionsDialog\" onAction=\"EditShaderComponent\"/></group><group id=\"PptEaseAppGroup\"");
            return CompleteRibbonIcons(x.Append("</contextMenus></customUI>").ToString());
        }

        public void RibbonLoaded(object ribbonUI) { ribbon = ribbonUI; Log("ribbon-loaded"); }
        public void LockSelection(object control) { RibbonAction(delegate { SetLocked(SelectedKeys(), true); }); }
        public void UnlockSelection(object control)
        {
            RibbonAction(delegate
            {
                List<ShapeHandle> all = GetShapes();
                HashSet<string> selected = new HashSet<string>(SelectedKeys());
                List<string> selectedLocked = all.Where(h => selected.Contains(h.Key) && ReadLocked(h.Shape)).Select(h => h.Key).ToList();
                if (selectedLocked.Count > 0) { SetLocked(selectedLocked, false); return; }
                // A native-locked object may not be selectable. QAT still has a useful,
                // explicit per-object unlock path instead of guessing or unlocking a page.
                ContextMenuStrip menu = BuildUnlockChooser(all);
                ContextMenuStrip previous = unlockChooser;
                unlockChooser = menu;
                if (previous != null) { previous.Close(); previous.Dispose(); }
                menu.Closed += delegate { if (unlockChooser == menu) unlockChooser = null; menu.Dispose(); };
                menu.Show(Cursor.Position);
            });
        }

        private ContextMenuStrip BuildUnlockChooser(List<ShapeHandle> all)
        {
            ContextMenuStrip menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Items.Add(new ToolStripMenuItem("选择要解锁的对象（当前页）") { Enabled = false });
            List<ShapeHandle> locked = all.Where(h => ReadLocked(h.Shape)).ToList();
            if (locked.Count == 0) { menu.Items.Add(new ToolStripMenuItem("本页没有锁定对象") { Enabled = false }); return menu; }
            List<ShapeHandle> recent = DateTime.UtcNow - lastRightClickAt < TimeSpan.FromSeconds(5)
                ? locked.Where(h => contextKeys.Contains(h.Key)).ToList() : new List<ShapeHandle>();
            if (recent.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("最近右键事件的锁定目标") { Enabled = false });
                foreach (ShapeHandle h in recent.Take(80)) AddUnlockChoice(menu, h);
            }
            List<ShapeHandle> remaining = locked.Where(h => !recent.Contains(h)).ToList();
            if (remaining.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                if (recent.Count > 0) menu.Items.Add(new ToolStripMenuItem("本页其他锁定对象") { Enabled = false });
                foreach (ShapeHandle h in remaining.Take(80)) AddUnlockChoice(menu, h);
            }
            if (recent.Count > 80 || remaining.Count > 80)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem more = new ToolStripMenuItem("更多对象…打开管理器");
                more.Click += delegate { OpenManager(null); };
                menu.Items.Add(more);
            }
            return menu;
        }

        private void AddUnlockChoice(ContextMenuStrip menu, ShapeHandle h)
        {
            string key = h.Key;
            string name = SafeString(delegate { return (string)h.Shape.Name; }, "对象 " + h.Id);
            ToolStripMenuItem item = new ToolStripMenuItem("解锁：" + new String(' ', Math.Min(h.Depth, 8) * 2) + name) { Tag = key };
            item.Click += delegate { RibbonAction(delegate { SetLocked(new List<string> { key }, false); }); };
            menu.Items.Add(item);
        }
        public void UnlockTagged(object control) { RibbonAction(delegate { dynamic c = control; SetLocked(new List<string> { (string)c.Tag }, false); }); }

        public void OpenManager(object control)
        {
            RibbonAction(delegate
            {
                string exe = Convert.ToString(Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\Software\\PptEase", "ManagerPath", ""));
                if (String.IsNullOrWhiteSpace(exe)) exe = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "PptEase.exe");
                if (!File.Exists(exe)) throw new FileNotFoundException("找不到管理器，请从已安装的软件目录启动。", exe);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            });
        }

        private void RibbonAction(Action action)
        {
            try { action(); }
            catch (Exception ex) { Log("ribbon-error " + ex); MessageBox.Show(ex.Message, "PPT 增强", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        public string GetLockedMenu(object control)
        {
            string prefix = "PptEaseMenu";
            try { dynamic c = control; prefix += Convert.ToString(c.Id); } catch { }
            prefix = new string(prefix.Where(ch => Char.IsLetterOrDigit(ch) || ch == '_').ToArray());
            StringBuilder x = new StringBuilder("<menu xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\">");
            try
            {
                List<ShapeHandle> all = GetShapes();
                List<ShapeHandle> locked = all.Where(h => ReadLocked(h.Shape)).Take(80).ToList();
                List<ShapeHandle> hit = locked.Where(h => contextKeys.Contains(h.Key)).ToList();
                int index = 0;
                if (hit.Count > 0)
                {
                    x.Append("<menuSeparator id=\"").Append(prefix).Append("HitHeading\" title=\"右键事件目标\"/>");
                    foreach (ShapeHandle h in hit) MenuButton(x, h, prefix, ref index);
                    x.Append("<menuSeparator id=\"").Append(prefix).Append("AllHeading\" title=\"本页其他锁定对象\"/>");
                }
                foreach (ShapeHandle h in locked.Where(h => !hit.Contains(h))) MenuButton(x, h, prefix, ref index);
                if (index == 0) x.Append("<button id=\"").Append(prefix).Append("None\" label=\"本页没有锁定对象\" enabled=\"false\"/>");
            }
            catch (Exception ex) { Log("menu-error " + ex); x.Append("<button id=\"").Append(prefix).Append("Error\" label=\"当前视图不可用，请打开普通编辑视图\" enabled=\"false\"/>"); }
            return CompleteRibbonIcons(x.Append("</menu>").ToString());
        }

        private static void MenuButton(StringBuilder x, ShapeHandle h, string prefix, ref int index)
        {
            string label = "解锁：" + SafeString(delegate { return (string)h.Shape.Name; }, "对象 " + h.Id);
            x.Append("<button id=\"").Append(prefix).Append("UnlockItem").Append(index++).Append("\" label=\"").Append(SecurityElement.Escape(label)).Append("\" tag=\"").Append(SecurityElement.Escape(h.Key)).Append("\" onAction=\"UnlockTagged\"/>");
        }

        private void OnBeforeRightClick(object selection, ref bool cancel)
        {
            try
            {
                contextKeys = SelectedKeys(selection);
                lastRightClickAt = DateTime.UtcNow;
                rightClickCount++;
                lastRightClick = new { Count = rightClickCount, Utc = DateTime.UtcNow.ToString("o"), SelectionKeys = contextKeys.ToArray(), CancelWas = cancel, UsesGeometricFallback = false };
                if (ribbon != null) ribbon.Invalidate();
                Log("right-click count=" + rightClickCount + " selection-count=" + contextKeys.Count);
                // Keep Cancel unchanged. Native PowerPoint context menu remains intact.
            }
            catch (Exception ex) { Log("right-click-error " + ex); }
        }

        private void PipeLoop()
        {
            while (!stopping)
            {
                try
                {
                    PipeSecurity security = new PipeSecurity();
                    SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
                    security.SetOwner(user);
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security))
                    {
                        lock (pipeGate) { if (stopping) return; activePipe = pipe; }
                        pipe.WaitForConnection();
                        using (StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, true))
                        using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, true) { AutoFlush = true })
                        {
                            string line;
                            while (!stopping && pipe.IsConnected && (line = ReadBoundedLine(reader, 12 * 1024 * 1024)) != null)
                            {
                                object id = null;
                                try
                                {
                                    Dictionary<string, object> request = json.Deserialize<Dictionary<string, object>>(line);
                                    if (request == null) throw new InvalidOperationException("请求不能为空。");
                                    request.TryGetValue("id", out id);
                                    object result = OnSta(delegate { return HandleRequest(request); });
                                    writer.WriteLine(json.Serialize(new { id = id, ok = true, result = result, error = (string)null }));
                                }
                                catch (Exception ex) { writer.WriteLine(json.Serialize(new { id = id, ok = false, result = (object)null, error = Friendly(ex) })); }
                            }
                        }
                    }
                }
                catch (Exception ex) { if (!stopping) { Log("pipe-error " + ex); Thread.Sleep(200); } }
                finally { lock (pipeGate) { activePipe = null; } }
            }
        }

        private object OnSta(Func<object> action)
        {
            Control control = dispatcher;
            if (stopping || control == null || control.IsDisposed) throw new InvalidOperationException("增强已断开。");
            if (!control.InvokeRequired) return action();
            object value = null; Exception failure = null;
            bool canceled = false; object gate = new object();
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                control.BeginInvoke((Action)delegate
                {
                    lock (gate) { if (canceled) return; }
                    try { value = action(); } catch (Exception ex) { failure = ex; }
                    finally { lock (gate) { if (!canceled) done.Set(); } }
                });
                if (!done.WaitOne(15000)) { lock (gate) { canceled = true; } throw new TimeoutException("PowerPoint 正忙，操作结果可能尚未返回。请刷新状态后再操作。"); }
            }
            if (failure != null) throw failure;
            return value;
        }

        private object HandleRequest(Dictionary<string, object> request)
        {
            object opValue; request.TryGetValue("op", out opValue);
            string op = Convert.ToString(opValue);
            Dictionary<string, object> payload = null; object p;
            if (request.TryGetValue("payload", out p)) payload = p as Dictionary<string, object>;
            if (payload == null) payload = new Dictionary<string, object>();
            switch (op)
            {
                case "status": case "snapshot": return Snapshot();
                case "lock": SetLocked(ReadKeys(payload), true); return Snapshot();
                case "unlock": SetLocked(ReadKeys(payload), false); return Snapshot();
                case "select": SelectShape(Convert.ToString(payload["key"])); return Snapshot();
                case "capture": return CaptureSelected();
                case "pickColor": StartNativePick(ReadKeys(payload)); return Snapshot();
                case "panCanvas": return NativeCanvasBridge.PanCanvas(app, Convert.ToInt32(payload["dx"]), Convert.ToInt32(payload["dy"]));
                case "canvasStatus": return CanvasFeatureStatus();
                case "shaderGet": return ShaderGet(payload);
                case "shaderScene": return ShaderSceneSnapshot(payload);
                case "shaderSetParameters": return ShaderSetParameters(payload);
                case "shaderBindSlider": return ShaderBindSlider(payload);
                case "shaderShapeSlider": return ShaderShapeSlider(payload);
                case "shaderSliderValue": return ShaderSliderValue(payload);
                case "shaderEnsureRuntime": return EnsureShaderRuntime();
                case "shaderCreate": return ShaderStore(payload, true);
                case "shaderUpdate": return ShaderStore(payload, false);
                case "shaderFrame": return ShaderFrame(payload);
                case "applyColor": throw new InvalidOperationException("选区赋色功能已移除，请使用智能取色。");
                case "cancelColor": CancelColorMode("用户取消"); return Snapshot();
                default: throw new InvalidOperationException("不支持的命令：" + op);
            }
        }

        private object Snapshot()
        {
            if (app == null) return new { Connected = false, IntegrationLoaded = false, Status = "PowerPoint 连接已结束。" };
            string name = "未打开文稿"; int slideIndex = 0; List<object> shapes = new List<object>(); string status = "增强已加载";
            try
            {
                if ((int)app.Presentations.Count > 0)
                {
                    dynamic pres = app.ActivePresentation;
                    name = (string)pres.Name;
                    dynamic slide = app.ActiveWindow.View.Slide;
                    slideIndex = (int)slide.SlideIndex;
                    HashSet<string> selected = new HashSet<string>(SelectedKeys());
                    foreach (ShapeHandle h in GetShapes()) shapes.Add(new { Id = h.Id, SlideId = h.SlideId, Name = SafeString(delegate { return (string)h.Shape.Name; }, "对象"), Type = TypeName((int)h.Shape.Type), Locked = ReadLocked(h.Shape), Visible = (int)h.Shape.Visible != 0, Key = h.Key, Selected = selected.Contains(h.Key), Depth = h.Depth });
                }
            }
            catch (Exception ex) { status = "请切换到普通编辑视图：" + Friendly(ex); }
            return new { Connected = true, HostVersion = SafeString(delegate { return (string)app.Version; }, ""), PresentationName = name, SlideIndex = slideIndex, IntegrationLoaded = !stopping, Status = status, Shapes = shapes, Pid = Process.GetCurrentProcess().Id, ProtocolVersion = 1, HostBuild = Assembly.GetExecutingAssembly().GetName().Version.ToString(), HostAssembly = Assembly.GetExecutingAssembly().FullName, HostPath = Assembly.GetExecutingAssembly().Location, LastRightClick = lastRightClick, ConnectionError = connectionError, ColorMode = colorMode, CurrentColor = currentColor, LastColorOperation = lastColorOperation };
        }

        private List<ShapeHandle> GetShapes()
        {
            if (app == null || (int)app.Presentations.Count == 0) return new List<ShapeHandle>();
            dynamic pres = app.ActivePresentation;
            dynamic slide = app.ActiveWindow.View.Slide;
            int slideId = (int)slide.SlideID;
            string prefix = nonce + ":" + Identity((object)pres) + ":" + slideId;
            List<ShapeHandle> result = new List<ShapeHandle>();
            Walk(slide.Shapes, prefix, slideId, 0, result);
            return result;
        }

        private void Walk(dynamic collection, string prefix, int slideId, int depth, List<ShapeHandle> result)
        {
            if (depth > 8) return;
            int count = Math.Min((int)collection.Count, 1000);
            for (int i = 1; i <= count && result.Count < 1000; i++)
            {
                dynamic shape = collection.Item(i); int id = (int)shape.Id;
                string key = prefix + ":" + id;
                result.Add(new ShapeHandle { Shape = shape, Id = id, SlideId = slideId, Key = key, Depth = depth });
                if ((int)shape.Type == 6) Walk(shape.GroupItems, key, slideId, depth + 1, result);
            }
        }

        private List<string> SelectedKeys() { return SelectedKeys((object)app.ActiveWindow.Selection); }
        private List<string> SelectedKeys(object selection)
        {
            List<string> result = new List<string>();
            try
            {
                dynamic selected = selection;
                List<ShapeHandle> all = GetShapes();
                dynamic range = selected.ShapeRange;
                for (int i = 1; i <= Math.Min((int)range.Count, 1000); i++)
                {
                    dynamic shape = range.Item(i); int id = (int)shape.Id;
                    List<ShapeHandle> matches = all.Where(h => h.Id == id).ToList();
                    if (matches.Count == 1) result.Add(matches[0].Key);
                }
            }
            catch { }
            return result.Distinct().ToList();
        }

        private void SetLocked(List<string> keys, bool value)
        {
            if (keys.Count == 0) throw new InvalidOperationException("请先选择对象，或从本页锁定对象列表选择解锁目标。");
            Dictionary<string, ShapeHandle> all = GetShapes().ToDictionary(h => h.Key);
            List<ShapeHandle> targets = new List<ShapeHandle>();
            foreach (string key in keys.Distinct()) { ShapeHandle h; if (!all.TryGetValue(key, out h)) throw new InvalidOperationException("目标已过期或文稿/页面已切换，请刷新对象列表。"); targets.Add(h); }
            List<KeyValuePair<ShapeHandle, bool>> changed = new List<KeyValuePair<ShapeHandle, bool>>();
            try
            {
                app.StartNewUndoEntry();
                foreach (ShapeHandle h in targets)
                {
                    bool old = ReadLocked(h.Shape); if (old == value) continue;
                    h.Shape.Locked = value ? -1 : 0;
                    changed.Add(new KeyValuePair<ShapeHandle, bool>(h, old));
                    if (ReadLocked(h.Shape) != value) throw new InvalidOperationException("宿主未应用该对象的锁定状态。");
                }
                if (ribbon != null) ribbon.Invalidate();
                Log((value ? "lock" : "unlock") + " changed=" + changed.Count);
            }
            catch
            {
                foreach (KeyValuePair<ShapeHandle, bool> item in changed.AsEnumerable().Reverse()) { try { item.Key.Shape.Locked = item.Value ? -1 : 0; } catch { } }
                throw;
            }
        }

        private void SelectShape(string key)
        {
            ShapeHandle found = GetShapes().FirstOrDefault(h => h.Key == key);
            if (found == null) throw new InvalidOperationException("目标已过期，请刷新后重试。");
            if (ReadLocked(found.Shape)) throw new InvalidOperationException("该对象已锁定，请先解锁再定位选择。");
            found.Shape.Select(-1);
        }

        private object CaptureSelected()
        {
            List<string> keys = SelectedKeys();
            if (keys.Count != 1) throw new InvalidOperationException("请在 PowerPoint 中选择一个对象后再载入预览。");
            ShapeHandle shape = GetShapes().First(h => h.Key == keys[0]);
            string dir = Path.Combine(Path.GetTempPath(), "PptEase", Process.GetCurrentProcess().Id.ToString());
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "preview-" + Guid.NewGuid().ToString("N") + ".png");
            shape.Shape.Export(file, 2); // ppShapeFormatPNG
            return new { Path = file, ShapeKey = shape.Key, Format = "png", Scope = "Explicit selected-object preview export" };
        }

        private static List<string> ReadKeys(Dictionary<string, object> payload)
        {
            object value;
            if (!payload.TryGetValue("keys", out value) || !(value is IEnumerable)) return new List<string>();
            List<string> keys = new List<string>();
            foreach (object item in (IEnumerable)value) { if (keys.Count >= 1000) throw new InvalidOperationException("单次最多处理1000个对象。"); keys.Add(Convert.ToString(item)); }
            return keys;
        }

        private static bool ReadLocked(dynamic shape) { return (int)shape.Locked != 0; }
        private static string Identity(object value) { IntPtr p = Marshal.GetIUnknownForObject(value); try { return p.ToInt64().ToString("x"); } finally { Marshal.Release(p); } }
        private static string TypeName(int type) { switch (type) { case 1: return "形状"; case 6: return "组合"; case 9: return "线条"; case 13: return "图片"; case 17: return "文本框"; case 19: return "表格"; case 28: return "SVG 图形"; case 5: return "自由形状"; case 16: return "媒体"; default: return "对象 (" + type + ")"; } }
        private static string SafeString(Func<string> get, string fallback) { try { return get(); } catch { return fallback; } }
        private static string Friendly(Exception ex) { return ex is COMException ? "PowerPoint 暂时无法执行：" + ex.Message : ex.Message; }
        private static string ReadBoundedLine(StreamReader reader, int maximum)
        {
            StringBuilder b = new StringBuilder(); int c;
            while ((c = reader.Read()) != -1) { if (c == '\n') return b.ToString().TrimEnd('\r'); if (b.Length >= maximum) throw new InvalidOperationException("请求超过允许大小。"); b.Append((char)c); }
            return b.Length == 0 ? null : b.ToString();
        }
        private static void Log(string message)
        {
            try { string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptEase"); Directory.CreateDirectory(dir); string file = Path.Combine(dir, "host.log"); if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.WriteAllText(file, ""); File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine); } catch { }
        }

        private sealed class ShapeHandle { public dynamic Shape; public int Id; public int SlideId; public string Key; public int Depth; }
    }
}




