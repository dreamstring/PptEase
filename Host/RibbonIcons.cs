using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PptEase.Host
{
    public sealed partial class Connect
    {
        private static string CompleteRibbonIcons(string xml)
        {
            // Let Office select and render its own DPI-specific image assets.
            // A fixed-size IPictureDisp is resampled by the Ribbon on high-DPI monitors.
            var icons = new Dictionary<string, string>
            {
                { "PptEaseLockedList", "SelectionPane" },
                { "peDocumentFonts", "FontDialogPowerPoint" },
                { "peThemeColors", "ThemeColorsGallery" },
                { "PptEaseManager", "ApplicationOptionsDialog" },
                { "peSetTemplate", "SlideMasterInsertLayout" },
                { "peTemplateStyles", "SlideLayoutGallery" },
                { "peClearTemplate", "MasterViewClose" }
            };
            return Regex.Replace(xml, @"<(?:button|toggleButton|dynamicMenu|menu)\b[^>]*?/>", delegate(Match match)
            {
                if (match.Value.Contains("imageMso=") || match.Value.Contains("getImage=")) return match.Value;
                Match id = Regex.Match(match.Value, "\\bid=\"([^\"]+)\"");
                string image;
                if (!icons.TryGetValue(id.Groups[1].Value, out image))
                {
                    string command = match.Value;
                    if (command.Contains("UnlockTagged")) image = "UnlockObject";
                    else if (command.Contains("ApplyDocumentFont")) image = "FontDialogPowerPoint";
                    else if (command.Contains("ApplyTemplateStyle")) image = "SlideLayoutGallery";
                    else image = "SelectionPane";
                }
                return match.Value.Substring(0, match.Value.Length - 2) + " imageMso=\"" + image + "\"/>";
            });
        }
    }
}
