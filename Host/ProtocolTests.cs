using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PptEase.Host;

public class FakeShape
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int ShapeType = 1;
    public int Type { get { return ShapeType; } }
    public int Visible { get { return -1; } }
    public bool RejectWrites;
    public int Writes;
    private int locked;
    public int Locked { get { return locked; } set { if (RejectWrites) throw new InvalidOperationException("simulated host refusal"); locked = value; Writes++; } }
    public void Select(int replace) { Selected = true; }
    public bool Selected;
    public FakeFill Fill = new FakeFill();
    public FakeLine Line = new FakeLine();
    public FakeTextFrame TextFrame;
    public FakeShape() { TextFrame = new FakeTextFrame(this); }
}
public class FakeColor { public int RGB = 0x334455; }
public class FakeFill { public int Type = 1; public int Visible = -1; public FakeColor ForeColor = new FakeColor(); }
public class FakeLine { public int Visible = -1; public FakeColor ForeColor = new FakeColor(); }
public class FakeFont { public FakeColor Color = new FakeColor(); }
public class FakeTextFrame { public FakeShape Parent; public FakeTextRange TextRange; public FakeTextFrame(FakeShape shape) { Parent = shape; TextRange = new FakeTextRange(this); } }
public class FakeTextRange
{
    public object Parent;
    public int Start = 1;
    public int Length = 5;
    public string Text = "abcde";
    public FakeFont Font = new FakeFont();
    public FakeTextRange Fragment;
    public FakeTextRange(object parent) { Parent = parent; }
    public FakeTextRange Characters(int start, int length) { if (Fragment != null && Fragment.Start == start && Fragment.Length == length) return Fragment; throw new InvalidOperationException("wrong character range"); }
}
public class FakeCollection
{
    public List<FakeShape> Items = new List<FakeShape>();
    public int Count { get { return Items.Count; } }
    public FakeShape Item(int index) { return Items[index - 1]; }
}
public class FakeSlide { public int SlideID = 256; public int SlideIndex = 1; public FakeCollection Shapes = new FakeCollection(); }
public class FakeView { public FakeSlide Slide = new FakeSlide(); }
public class FakeSelection { public FakeCollection ShapeRange = new FakeCollection(); public int Type = 2; public FakeTextRange TextRange; }
public class FakeWindow { public FakeView View = new FakeView(); public FakeSelection Selection = new FakeSelection(); }
public class FakePresentation { public string Name = "Fixture.pptx"; }
public class FakePresentations { public int Count { get { return 1; } } }
public class FakeApp
{
    public FakeCommandBars CommandBars = new FakeCommandBars();
    public string Version = "16.0";
    public FakePresentations Presentations = new FakePresentations();
    public FakePresentation ActivePresentation = new FakePresentation();
    public FakeWindow ActiveWindow = new FakeWindow();
    public int UndoEntries;
    public void StartNewUndoEntry() { UndoEntries++; }
}
public class FakeCommandBars
{
    public string LastCommand;
    public bool Enabled = true;
    public bool GetEnabledMso(string command) { return Enabled; }
    public void ExecuteMso(string command) { LastCommand = command; }
}
public static class ProtocolTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static object Call(Connect host, string op, Dictionary<string, object> payload)
    {
        try { return typeof(Connect).GetMethod("HandleRequest", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(host, new object[] { new Dictionary<string, object> { { "op", op }, { "payload", payload } } }); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    private static Dictionary<string, object> Obj(object x) { return Json.Deserialize<Dictionary<string, object>>(Json.Serialize(x)); }
    private static void Assert(bool value, string label) { if (!value) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); }
    private static string[] Keys(Connect host)
    {
        Dictionary<string, object> snapshot = Obj(Call(host, "status", new Dictionary<string, object>()));
        System.Collections.IList shapes = (System.Collections.IList)snapshot["Shapes"];
        string[] keys = new string[shapes.Count];
        for (int i = 0; i < shapes.Count; i++) keys[i] = (string)((Dictionary<string, object>)shapes[i])["Key"];
        return keys;
    }
    public static int Main()
    {
        try
        {
            Connect host = new Connect(); FakeApp app = new FakeApp();
            FakeShape first = new FakeShape { Id = 3, Name = "A" }, second = new FakeShape { Id = 4, Name = "B" };
            app.ActiveWindow.View.Slide.Shapes.Items.Add(first); app.ActiveWindow.View.Slide.Shapes.Items.Add(second);
            typeof(Connect).GetField("app", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host, app);
            string[] keys = Keys(host);
            Call(host, "lock", new Dictionary<string, object> { { "keys", keys } });
            Assert(first.Locked == -1 && second.Locked == -1 && app.UndoEntries == 1, "batch lock starts one native undo boundary");
            Call(host, "unlock", new Dictionary<string, object> { { "keys", keys } });
            Assert(first.Locked == 0 && second.Locked == 0, "batch unlock restores state");
            int before = first.Writes + second.Writes;
            app.ActiveWindow.View.Slide.SlideID++;
            bool rejected = false; try { Call(host, "lock", new Dictionary<string, object> { { "keys", keys } }); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && first.Writes + second.Writes == before && app.UndoEntries == 2, "stale slide key rejected before mutation or undo");
            keys = Keys(host); app.ActivePresentation = new FakePresentation();
            rejected = false; try { Call(host, "lock", new Dictionary<string, object> { { "keys", keys } }); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && first.Writes + second.Writes == before, "stale presentation identity rejected");
            keys = Keys(host); second.RejectWrites = true;
            rejected = false; try { Call(host, "lock", new Dictionary<string, object> { { "keys", keys } }); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && first.Locked == 0, "partial batch failure rolls back changed shape");
            second.RejectWrites = false; first.Locked = -1;
            rejected = false; try { Call(host, "select", new Dictionary<string, object> { { "key", keys[0] } }); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && !first.Selected, "locked shape not explicitly selected by manager");
            rejected = false; try { Call(host, "eval", new Dictionary<string, object>()); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "arbitrary command rejected");
            Console.WriteLine("All protocol safety tests passed. These fakes do not validate PowerPoint mouse behavior."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
