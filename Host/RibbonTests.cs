using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using PptEase.Host;

public class FakeRibbonControl { public string Id = "testContext"; }
public static class RibbonTests
{
    public static int Main()
    {
        try
        {
            Connect host = new Connect();
            MethodInfo paletteBuilder = typeof(Connect).GetMethod("BuildDocumentThemePalette", BindingFlags.NonPublic | BindingFlags.Static);
            var documentColors = new Dictionary<int, int> { { 0, 8 }, { 0xffffff, 4 }, { 0x8eeb0e, 3 }, { 0x5c6153, 2 } };
            int[] palette = (int[])paletteBuilder.Invoke(null, new object[] { documentColors });
            if (palette.Length != 12 || palette.Any(rgb => !documentColors.ContainsKey(rgb)) || palette.Distinct().Count() != documentColors.Count)
                throw new Exception("Theme must replace all slots using only document colors");
            if (palette[0] != 0 || palette[1] != 0xffffff) throw new Exception("Theme dark/light slots reversed");
            XDocument xml = XDocument.Parse(host.GetCustomUI("Microsoft.PowerPoint.Presentation"));
            XNamespace ns = "http://schemas.microsoft.com/office/2009/07/customui";
            string[] expected = { "peLock", "peUnlock", "pePickColor", "peCanvasMode", "peCanvasFit" };
            string[] ids = xml.Descendants().Attributes("id").Select(a => a.Value).ToArray();
            if (ids.Distinct().Count() != ids.Length) throw new Exception("Duplicate static Ribbon IDs");
            foreach (XElement command in xml.Descendants().Where(e => e.Name.LocalName == "button" || e.Name.LocalName == "toggleButton" || e.Name.LocalName == "dynamicMenu" || (e.Name.LocalName == "menu" && e.Attribute("id") != null)))
            {
                if (command.Attribute("imageMso") == null && command.Attribute("getImage") == null) throw new Exception("Missing Ribbon command icon");
                if (command.Attribute("imageMso") == null && (string)command.Attribute("getImage") != "GetCheckboxIcon") throw new Exception("Static Ribbon icons must use Office's native imageMso path");
                if (command.Attribute("getImage") != null && typeof(Connect).GetMethod((string)command.Attribute("getImage")) == null) throw new Exception("Missing icon callback");
            }
            if (ids.Contains("peApplyColor") || ids.Contains("peCancelColor")) throw new Exception("Removed brush controls returned");
            foreach (string id in new[] { "peSaveDocumentTheme", "peThemeColors", "peDocumentFonts" })
            {
                XElement entry = xml.Descendants().Single(e => (string)e.Attribute("id") == id);
                if (entry.Attribute("getVisible") != null) throw new Exception("Document styles must not depend on template designation");
                if ((string)entry.Parent.Attribute("id") != "peDocumentStyles") throw new Exception("Document styles group missing");
                string callback = (string)(entry.Attribute("onAction") ?? entry.Attribute("getContent"));
                if (typeof(Connect).GetMethod(callback) == null) throw new Exception("Missing document styles callback");
                if (entry.Name.LocalName == "dynamicMenu" && (string)entry.Attribute("invalidateContentOnDrop") != "true") throw new Exception("Document style menu must refresh on open");
            }
            foreach (string id in expected)
            {
                XElement button = xml.Descendants().Single(e => (string)e.Attribute("id") == id);
                if (button.Attribute("imageMso") == null) throw new Exception("Missing command image: " + id);
                string callback = (string)button.Attribute("onAction");
                if (typeof(Connect).GetMethod(callback, BindingFlags.Public | BindingFlags.Instance) == null) throw new Exception("Missing callback " + callback);
            }
            XElement toggle = xml.Descendants(ns + "toggleButton").Single(e => (string)e.Attribute("id") == "peCanvasMode");
            MethodInfo action = typeof(Connect).GetMethod((string)toggle.Attribute("onAction"));
            if (action.GetParameters().Length != 2 || action.GetParameters()[1].ParameterType != typeof(bool)) throw new Exception("Canvas toggle action ABI mismatch");
            MethodInfo pressed = typeof(Connect).GetMethod((string)toggle.Attribute("getPressed"));
            if (pressed == null || pressed.ReturnType != typeof(bool)) throw new Exception("Missing persisted toggle state callback");
            FakeApp app = new FakeApp(); FakeShape shape = new FakeShape { Id = 3, Name = "A&B\"x" }; shape.Locked = -1;
            app.ActiveWindow.View.Slide.Shapes.Items.Add(shape);
            typeof(Connect).GetField("app", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host, app);
            XDocument menu = XDocument.Parse(host.GetLockedMenu(new FakeRibbonControl()));
            XElement item = menu.Descendants(ns + "button").Single();
            if ((string)item.Attribute("label") != "解锁：A&B\"x" || item.Attribute("tag") == null) throw new Exception("Menu label/key escaping failed");
            if ((string)item.Attribute("imageMso") != "UnlockObject") throw new Exception("Missing dynamic unlock icon");
            Console.WriteLine("PASS five stable QAT commands, canvas toggle callback ABI, native context menus and escaped labels.");
            Console.WriteLine("Manual Office UI verification is still required for QAT and context menus."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

