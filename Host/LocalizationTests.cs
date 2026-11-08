using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using PptEase.Host;
using PptEase.Localization;

public static class LocalizationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main()
    {
        try
        {
            var original = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var host = new Connect();
            L.SetLanguage(2052);
            var chinese = XDocument.Parse(host.GetCustomUI("Microsoft.PowerPoint.Presentation"));
            L.SetLanguage(1033);
            var english = XDocument.Parse(host.GetCustomUI("Microsoft.PowerPoint.Presentation"));
            Check(Thread.CurrentThread.CurrentCulture.Name == "de-DE", "UI language changed numeric culture");
            Check(english.Descendants().Attributes().Where(a => new[] { "label", "title", "description", "screentip" }.Contains(a.Name.LocalName)).All(a => !a.Value.Any(c => c >= '\u4e00' && c <= '\u9fff')), "Untranslated Ribbon text");
            foreach (string attribute in new[] { "id", "idMso", "onAction", "getContent", "getVisible", "getPressed", "imageMso" })
                Check(chinese.Descendants().Attributes(attribute).Select(a=>a.Value).SequenceEqual(english.Descendants().Attributes(attribute).Select(a=>a.Value)), "Ribbon protocol changed: " + attribute);
            Check(L.T("智能取色") == "Smart eyedropper", "English resource missing");
            Check(L.F("第 {0} 页 · 当前活动幻灯片", 12) == "Slide 12 · Active slide", "Formatted text failed");
            string xml = L.Ribbon("<menu><button label=\"对象\" tag=\"用户参数\" onAction=\"Apply\"/><button label=\"智能取色\"/></menu>");
            var buttons = XDocument.Parse(xml).Descendants("button").ToArray();
            Check((string)buttons[0].Attribute("label") == "对象" && (string)buttons[0].Attribute("tag") == "用户参数", "Translated document-owned names");
            var names = typeof(Connect).GetProperty("ThemeNames", BindingFlags.NonPublic | BindingFlags.Static);
            Check(((string[])names.GetValue(null, null))[0] == "Dark 1", "Theme names captured a stale language");
            L.SetLanguage(1028); Check(L.Language == "zh-CN" && L.T("智能取色") == "智能取色", "Chinese locale fallback failed");
            Check(((string[])names.GetValue(null, null))[0] == "深色 1", "Theme names failed to follow Chinese");
            L.SetLanguage(1041); Check(L.Language == "en", "Unsupported locale must fall back to English");
            L.SetLanguage(-999); Check(L.Language == "en", "Invalid LCID fallback failed");
            Check(L.T("myParameter_纹理") == "myParameter_纹理", "Unknown identifiers must remain unchanged");
            Thread.CurrentThread.CurrentCulture = original;
            Console.WriteLine("PASS localization: locales, fallback, Ribbon coverage and stable IDs, document names, theme names, numeric culture.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
