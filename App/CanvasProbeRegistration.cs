using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace PptEase;
internal static class CanvasProbeRegistration
{
    internal const string ProgId = "PptEase.CanvasInputV3";
    private const string ClassId = "{A2D4B783-8D73-4BDC-91F9-E1C622824AD3}";
    private static readonly string[] Keys = [@"Software\Classes\" + ProgId, @"Software\Classes\CLSID\" + ClassId, @"Software\Microsoft\Office\PowerPoint\Addins\" + ProgId];
    internal static async Task EnsureAsync()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "CanvasProbe", "PptEase.CanvasInputV3.dll");
        var expectedBuild = AssemblyName.GetAssemblyName(source).Version!.ToString();
        try
        {
            var status = await new HostClient("PptEaseCanvasV3").RequestAsync("status");
            if (status.GetProperty("Backend").GetString() == "native-canvas-input-v3" && status.GetProperty("HostBuild").GetString() == expectedBuild) return;
        }
        catch { }
        // A legacy preview shares the pipe name; stop it before connecting V2.
        foreach (var legacy in new[] { "PptEase.CanvasProbe", "PptEase.CanvasProbeV2", "PptEase.CanvasOriginV2" })
            try { await ComAutomation.SetAddinConnectedAsync(false, legacy); } catch { }
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        var folder = Path.Combine(AppContext.BaseDirectory, "CanvasProbe", hash);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "PptEase.CanvasInputV3.dll");
        if (!File.Exists(target)) File.Copy(source, target);
        if (!SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target)))) throw new IOException("画布预览模块校验失败。");
        var identity = AssemblyName.GetAssemblyName(target);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        foreach (var path in Keys)
        {
            using var key = root.OpenSubKey(path);
            if (key != null && Convert.ToInt32(key.GetValue("PptEaseOwned", 0)) != 1) throw new InvalidOperationException("同名画布注册项不属于本软件，已停止。");
        }
        using (var key = root.CreateSubKey(Keys[0])) { key.SetValue("PptEaseOwned", 1); using var cls = key.CreateSubKey("CLSID"); cls.SetValue("", ClassId); }
        using (var key = root.CreateSubKey(Keys[1]))
        {
            key.SetValue("PptEaseOwned", 1);
            using (var prog = key.CreateSubKey("ProgId")) prog.SetValue("", ProgId);
            using var server = key.CreateSubKey("InprocServer32"); server.SetValue("", "mscoree.dll"); server.SetValue("ThreadingModel", "Both");
            foreach (var sub in new[] { server, server.CreateSubKey(identity.Version!.ToString()) })
            {
                sub.SetValue("Class", "PptEase.CanvasInputV3.Connect"); sub.SetValue("Assembly", identity.FullName!); sub.SetValue("RuntimeVersion", "v4.0.30319"); sub.SetValue("CodeBase", new Uri(target).AbsoluteUri);
                if (!ReferenceEquals(sub, server)) sub.Dispose();
            }
            key.CreateSubKey(@"Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}").Dispose();
        }
        using (var key = root.CreateSubKey(Keys[2])) { key.SetValue("PptEaseOwned", 1); key.SetValue("FriendlyName", "PPT 增强·画布越界预览"); key.SetValue("Description", "用户触发的视口越界预览模块"); key.SetValue("LoadBehavior", 0); }
        await ComAutomation.SetAddinConnectedAsync(true, ProgId);
        var loaded = await new HostClient("PptEaseCanvasV3").RequestAsync("status");
        if (loaded.GetProperty("HostBuild").GetString() != expectedBuild || loaded.GetProperty("Backend").GetString() != "native-canvas-input-v3")
            throw new InvalidOperationException("PPT 仍驻留旧画布模块。请保存并完全退出 PPT 后再试。");
    }
    internal static async Task RemoveAsync()
    {
        try { await ComAutomation.SetAddinConnectedAsync(false, ProgId); } catch { }
        foreach (var legacy in new[] { "PptEase.CanvasProbe", "PptEase.CanvasProbeV2", "PptEase.CanvasOriginV2" })
            try { await ComAutomation.SetAddinConnectedAsync(false, legacy); } catch { }
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var legacyKeys = new[] { @"Software\Classes\PptEase.CanvasProbe", @"Software\Classes\CLSID\{A2D4B780-8D73-4BDC-91F9-E1C622824AD3}", @"Software\Microsoft\Office\PowerPoint\Addins\PptEase.CanvasProbe", @"Software\Classes\PptEase.CanvasProbeV2", @"Software\Classes\CLSID\{A2D4B781-8D73-4BDC-91F9-E1C622824AD3}", @"Software\Microsoft\Office\PowerPoint\Addins\PptEase.CanvasProbeV2" };
        foreach (var path in Keys.Concat(legacyKeys))
        {
            using var key = root.OpenSubKey(path);
            if (key != null && Convert.ToInt32(key.GetValue("PptEaseOwned", 0)) == 1) root.DeleteSubKeyTree(path, false);
        }
    }
}
