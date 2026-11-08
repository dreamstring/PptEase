using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PptEase.Host;

public static class ColorProtocolTests
{
    private static object Call(Connect host, string op, Dictionary<string, object> payload)
    {
        try { return typeof(Connect).GetMethod("HandleRequest", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(host, new object[] { new Dictionary<string, object> { { "op", op }, { "payload", payload } } }); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    private static string[] Keys(Connect host)
    {
        JavaScriptSerializer json = new JavaScriptSerializer();
        Dictionary<string, object> snapshot = json.Deserialize<Dictionary<string, object>>(json.Serialize(Call(host, "status", new Dictionary<string, object>())));
        System.Collections.IList shapes = (System.Collections.IList)snapshot["Shapes"];
        string[] keys = new string[shapes.Count];
        for (int i = 0; i < shapes.Count; i++) keys[i] = (string)((Dictionary<string, object>)shapes[i])["Key"];
        return keys;
    }
    private static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
    private static string Mode(Connect host) { return (string)typeof(Connect).GetField("colorMode", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(host); }
    private static void Change(Connect host, FakeSelection selection) { try { typeof(Connect).GetMethod("OnColorSelectionChange", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(host, new object[] { selection }); } catch (TargetInvocationException e) { throw e.InnerException; } }
    private static Dictionary<string, object> Source(string key) { return new Dictionary<string, object> { { "keys", new[] { key } } }; }
    public static int Main()
    {
        try
        {
            Connect host = new Connect(); FakeApp app = new FakeApp();
            FakeShape source = new FakeShape { Id = 3, Name = "Source" }, target = new FakeShape { Id = 4, Name = "Target" };
            source.Fill.ForeColor.RGB = 0x2244aa; target.Fill.ForeColor.RGB = 0x996633;
            app.ActiveWindow.View.Slide.Shapes.Items.Add(source); app.ActiveWindow.View.Slide.Shapes.Items.Add(target);
            typeof(Connect).GetField("app", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host, app);
            string[] keys = Keys(host);
            Call(host, "applyColor", Source(keys[0]));
            Assert(Mode(host) == "ApplyNextSelection" && target.Fill.ForeColor.RGB == 0x996633, "color brush arms without painting unrelated targets");
            app.ActiveWindow.Selection.ShapeRange.Items.Add(source); Change(host, app.ActiveWindow.Selection);
            Assert(Mode(host) == "ApplyNextSelection", "same source selection does not consume one-shot brush");
            app.ActiveWindow.Selection.ShapeRange.Items.Clear(); app.ActiveWindow.Selection.ShapeRange.Items.Add(target); Change(host, app.ActiveWindow.Selection);
            Assert(Mode(host) == "Idle" && target.Fill.ForeColor.RGB == source.Fill.ForeColor.RGB && app.UndoEntries == 1, "next selection receives source fill with one undo boundary");
            source.Fill.ForeColor.RGB = 0x00bb66; Call(host, "applyColor", Source(keys[0])); target.Locked = -1; Change(host, app.ActiveWindow.Selection);
            Assert(Mode(host) == "Idle" && target.Fill.ForeColor.RGB != source.Fill.ForeColor.RGB, "locked target is refused and brush exits without unlocking");
            target.Locked = 0; Call(host, "applyColor", Source(keys[0])); app.ActiveWindow.View.Slide.SlideID++; Change(host, app.ActiveWindow.Selection);
            Assert(Mode(host) == "Idle", "page switch cancels color brush");
            keys = Keys(host); Call(host, "applyColor", Source(keys[0])); Call(host, "cancelColor", new Dictionary<string, object>());
            Assert(Mode(host) == "Idle", "explicit cancel clears color mode");
            source.ShapeType = 17; source.TextFrame.TextRange.Font.Color.RGB = 0x123abc; source.Fill.ForeColor.RGB = 0x888888;
            target.ShapeType = 9; target.Line.ForeColor.RGB = 0x000000;
            Call(host, "applyColor", Source(keys[0])); Change(host, app.ActiveWindow.Selection);
            Assert(target.Line.ForeColor.RGB == 0x123abc && source.Fill.ForeColor.RGB == 0x888888, "textbox source uses font color and line target uses line color");
            target.ShapeType = 17; target.TextFrame.TextRange.Font.Color.RGB = 0x111111;
            target.TextFrame.TextRange.Fragment = new FakeTextRange(target.TextFrame) { Start = 2, Length = 2 };
            target.TextFrame.TextRange.Fragment.Font.Color.RGB = 0x333333;
            app.ActiveWindow.Selection.Type = 3; app.ActiveWindow.Selection.TextRange = target.TextFrame.TextRange.Fragment;
            Call(host, "applyColor", Source(keys[0])); Change(host, app.ActiveWindow.Selection);
            Assert(target.TextFrame.TextRange.Fragment.Font.Color.RGB == 0x123abc && target.TextFrame.TextRange.Font.Color.RGB == 0x111111, "text target writes exact Start/Length range without changing whole textbox");
            app.ActiveWindow.Selection.Type = 2;
            app.ActiveWindow.Selection.ShapeRange.Items.Clear();
            app.ActiveWindow.Selection.ShapeRange.Items.Add(source);
            source.ShapeType = 1;
            Call(host, "pickColor", new Dictionary<string, object>());
            Assert(app.CommandBars.LastCommand == "EyedropperFill", "shape invokes native fill eyedropper");
            source.ShapeType = 17;
            Call(host, "pickColor", new Dictionary<string, object>());
            Assert(app.CommandBars.LastCommand == "EyedropperFillText", "textbox invokes native text eyedropper");
            source.ShapeType = 9;
            Call(host, "pickColor", new Dictionary<string, object>());
            Assert(app.CommandBars.LastCommand == "EyedropperOutline", "line invokes native outline eyedropper");
            source.ShapeType = 1;
            source.Selected = false;
            app.ActiveWindow.Selection.Type = 3;
            Call(host, "pickColor", Source(keys[0]));
            Assert(app.CommandBars.LastCommand == "EyedropperFillText" && !source.Selected, "live text selection overrides shape fill and is preserved");
            app.CommandBars.Enabled = false;
            bool refused = false;
            try { Call(host, "pickColor", new Dictionary<string, object>()); }
            catch (InvalidOperationException) { refused = true; }
            Assert(refused, "disabled native command gives an explicit error");
            Console.WriteLine("Routing tests use fake COM objects; real native eyedropper interaction requires user acceptance."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
