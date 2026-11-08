using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace PptEase;
internal static class MaintenanceDeployment
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PptEase.PowerPointEnhancer";
    internal static async Task<string> DeployAsync()
    {
        string source = Path.GetFullPath(AppContext.BaseDirectory);
        var identities = new List<string>();
        foreach (var relative in new[] { "PptEase.dll", "PptEase.deps.json", @"Host\PptEase.Host.dll", @"Shader\preview.html" })
            identities.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, relative)))));
        string package = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(":", identities)))).ToLowerInvariant();
        string target = Path.Combine(IntegrationInstaller.DataDirectory, "App", package);
        if (!string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            await Task.Run(() =>
            {
                Directory.CreateDirectory(target);
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(source, file);
                    if (relative.StartsWith(@"CanvasProbe\", StringComparison.OrdinalIgnoreCase) || relative.Equals("canvas-lab-results.json", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    if (relative.StartsWith(@"Host\", StringComparison.OrdinalIgnoreCase) && !relative.Equals(@"Host\PptEase.Host.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string destination = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, true);
                }
                foreach (var relative in new[] { "PptEase.dll", @"Host\PptEase.Host.dll" })
                    if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(source, relative))).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, relative)))))
                        throw new IOException(global::PptEase.Localization.L.T("维护程序复制校验失败。"));
            });
        string exe = Path.Combine(target, "PptEase.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException(global::PptEase.Localization.L.T("安装包缺少维护程序。"), exe);
        return exe;
    }
    internal static void Register(string exe)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var existing = root.OpenSubKey(UninstallKey);
        if (existing != null && Convert.ToInt32(existing.GetValue("PptEaseOwned", 0)) != 1) throw new InvalidOperationException(global::PptEase.Localization.L.T("同名维护入口不属于本软件。"));
        using var key = root.CreateSubKey(UninstallKey);
        key.SetValue("PptEaseOwned", 1); key.SetValue("DisplayName", global::PptEase.Localization.L.T("PPT 增强")); key.SetValue("DisplayVersion", "0.1");
        key.SetValue("InstallLocation", Path.GetDirectoryName(exe)!); key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", "\"" + exe + "\" --uninstall"); key.SetValue("ModifyPath", "\"" + exe + "\" --setup");
        key.SetValue("NoModify", 0); key.SetValue("NoRepair", 0);
    }
    internal static void RemoveEntry()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(UninstallKey);
        if (key != null && Convert.ToInt32(key.GetValue("PptEaseOwned", 0)) == 1) root.DeleteSubKeyTree(UninstallKey, false);
    }
}
