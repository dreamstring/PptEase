using System;
using System.Reflection;
using System.Windows.Forms;
using PptEase.Host;

public static class UnlockChooserTests
{
    public static int Main()
    {
        try
        {
            Connect host = new Connect(); FakeApp app = new FakeApp();
            FakeShape a = new FakeShape { Id = 3, Name = "A" }, b = new FakeShape { Id = 4, Name = "B" };
            a.Locked = -1; b.Locked = -1;
            app.ActiveWindow.View.Slide.Shapes.Items.Add(a); app.ActiveWindow.View.Slide.Shapes.Items.Add(b);
            typeof(Connect).GetField("app", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host, app);
            object handles = typeof(Connect).GetMethod("GetShapes", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(host, null);
            using (ContextMenuStrip menu = (ContextMenuStrip)typeof(Connect).GetMethod("BuildUnlockChooser", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(host, new[] { handles }))
            {
                if (a.Locked != -1 || b.Locked != -1 || app.UndoEntries != 0) throw new Exception("Creating chooser modified lock state");
                ToolStripMenuItem choice = null; int count = 0;
                foreach (ToolStripItem item in menu.Items) if (item.Tag is string) { count++; if (item.Text == "解锁：A") choice = (ToolStripMenuItem)item; }
                if (count != 2 || choice == null) throw new Exception("Missing explicit object choices");
                choice.PerformClick(); // Isolated fake object; no screen input or Office UI involved.
                if (a.Locked != 0 || b.Locked != -1 || app.UndoEntries != 1) throw new Exception("Chosen item did not unlock exactly one target");
            }
            Console.WriteLine("PASS unlock chooser creation performs no mutation; explicit named choice unlocks one target only.");
            Console.WriteLine("No native PowerPoint interaction or automated mouse input was performed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
