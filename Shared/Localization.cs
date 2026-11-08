using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml;

namespace PptEase.Localization
{
    // UI only: never change CurrentCulture, serialized numbers, identifiers or user content.
    public static class L
    {
        private static string language = Normalize(CultureInfo.CurrentUICulture.Name);
        private static readonly Dictionary<string, string> english = LoadEnglish();
        public static string Language { get { return language; } }
        public static string Normalize(string name)
        {
            return !String.IsNullOrEmpty(name) && (name.Equals("zh", StringComparison.OrdinalIgnoreCase) || name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)) ? "zh-CN" : "en";
        }
        public static void SetLanguage(string name) { language = Normalize(name); }
        public static void SetLanguage(int lcid)
        {
            try { SetLanguage(CultureInfo.GetCultureInfo(lcid).Name); }
            catch (ArgumentException) { SetLanguage("en"); }
        }
        public static string T(string source)
        {
            if (source == null) return String.Empty;
            return language == "en" && english.ContainsKey(source) ? english[source] : source;
        }
        public static string F(string source, params object[] args) { return String.Format(CultureInfo.CurrentCulture, T(source), args); }
        private static Dictionary<string, string> LoadEnglish()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var stream = typeof(L).Assembly.GetManifestResourceStream("PptEase.Strings.en.xml"))
            {
                if (stream == null) throw new InvalidOperationException("Missing PptEase English UI resources.");
                var document = new XmlDocument { XmlResolver = null };
                document.Load(stream);
                var root = document.DocumentElement;
                if (root == null) throw new InvalidDataException("Empty UI resource document.");
                foreach (XmlElement entry in root.ChildNodes)
                    result.Add(entry.GetAttribute("name"), entry.InnerText);
            }
            return result;
        }
        public static string Ribbon(string xml)
        {
            if (language != "en") return xml;
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(xml);
            var nodes = document.SelectNodes("//*");
            if (nodes == null) return xml;
            foreach (XmlElement element in nodes)
                foreach (string attribute in new[] { "label", "title", "screentip", "supertip", "description" })
                {
                    // Tagged items contain document font/object names, not UI resource keys.
                    if (attribute == "label" && element.HasAttribute("tag")) continue;
                    if (element.HasAttribute(attribute)) element.SetAttribute(attribute, T(element.GetAttribute(attribute)));
                }
            return document.OuterXml;
        }
    }
}
