using System.Globalization;
using System.Windows.Markup;
using Microsoft.Win32;
using PptEase.Localization;

namespace PptEase;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension(string key) { Key = key; }
    public string Key { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}

internal static class UiLanguage
{
    public static async Task InitializeAsync(string[] args)
    {
        // Explicit override is useful for support and deterministic UI tests.
        var language = Environment.GetEnvironmentVariable("PPTEASE_UI_LANGUAGE");
        if (!string.IsNullOrWhiteSpace(language)) { L.SetLanguage(language); return; }
        language = Environment.GetEnvironmentVariable("PPTEASE_HOST_UI_LANGUAGE");
        if (!string.IsNullOrWhiteSpace(language)) { L.SetLanguage(language); return; }
        L.SetLanguage(CultureInfo.CurrentUICulture.Name);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common\LanguageResources");
            var value = key?.GetValue("UILanguage");
            if (value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out int lcid)) L.SetLanguage(lcid);
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        // Tests must never depend on an open presentation. Normal launches prefer the host's actual UI language.
        if (args.Any(a => a.EndsWith("-test", StringComparison.Ordinal))) return;
        int? pid = args.Length > 1 && args[0].StartsWith("--shader-", StringComparison.Ordinal) && int.TryParse(args[1], out int parsed) ? parsed : null;
        try
        {
            var response = await new HostClient(targetPid: pid).RequestAsync("uiLanguage", timeoutMs: 800).WaitAsync(TimeSpan.FromMilliseconds(1000));
            if (response.TryGetProperty("language", out var name)) L.SetLanguage(name.GetString() ?? "en");
        }
        catch (Exception) { /* Older/unavailable hosts: Office registry, then OS UI language. */ }
    }
}
