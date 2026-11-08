using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace PptEase;
public sealed class IntegrationInstaller
{
    public const string ProgId = "PptEase.Connect";
    public const string ClassId = "{84A31892-2FDB-4B79-A832-B0DA779BD41D}";
    private const string ClassName = "PptEase.Host.Connect";
    private static readonly string[] Keys = [@"Software\Classes\" + ProgId, @"Software\Classes\CLSID\" + ClassId, @"Software\Microsoft\Office\PowerPoint\Addins\" + ProgId];
    public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptEase");
    private static readonly string RecordPath = Path.Combine(DataDirectory, "installation.json");
    internal static string UnloadedGuidance()
    {
        try
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var addin = user.OpenSubKey(Keys[2]);
            if (addin == null || Convert.ToInt32(addin.GetValue("PptEaseOwned", 0)) != 1)
                return "尚未安装 PPT 增强。请在“连接与诊断”点击“安装并启用”。";
            if (Convert.ToInt32(addin.GetValue("LoadBehavior", 0)) == 0)
                return "PPT 增强已停用。请在“连接与诊断”点击“安装并启用”。";
            return "PPT 增强已注册，但尚未建立连接。请保存文稿，正常退出所有 PowerPoint 窗口后重新打开；若仍未加载，请反馈诊断状态。";
        }
        catch { return "尚未连接 PPT 增强，且无法读取安装状态。请检查“连接与诊断”。"; }
    }
    public async Task<string> InstallAsync()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Host", "PptEase.Host.dll");
        if (!File.Exists(bundled)) throw new FileNotFoundException("安装包缺少宿主模块，请解压完整文件夹。", bundled);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundled))).ToLowerInvariant();
        var targetDirectory = Path.Combine(DataDirectory, "Host", hash);
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, "PptEase.Host.dll");
        if (!File.Exists(target)) File.Copy(bundled, target);
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant() != hash) throw new IOException("宿主模块校验失败。");
        var identity = AssemblyName.GetAssemblyName(target).FullName;
        var managerPath = await MaintenanceDeployment.DeployAsync();
        if (!File.Exists(managerPath)) throw new FileNotFoundException("安装包缺少管理器 EXE。", managerPath);
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        foreach (var path in Keys)
        {
            using var existing = user.OpenSubKey(path);
            if (existing != null && Convert.ToInt32(existing.GetValue("PptEaseOwned", 0)) != 1) throw new InvalidOperationException("检测到同名但不属于 PPT 增强的注册项，已停止安装。");
        }
        // These unique keys are admitted only when absent or already owned. No unrelated key is replaced.
        using (var prog = user.CreateSubKey(Keys[0])) { prog.SetValue("", "PPT 增强"); prog.SetValue("PptEaseOwned", 1); using var cls = prog.CreateSubKey("CLSID"); cls.SetValue("", ClassId); }
        using (var cls = user.CreateSubKey(Keys[1]))
        {
            cls.SetValue("", ClassName); cls.SetValue("PptEaseOwned", 1);
            using (var prog = cls.CreateSubKey("ProgId")) prog.SetValue("", ProgId);
            using var server = cls.CreateSubKey("InprocServer32");
            server.SetValue("", "mscoree.dll"); server.SetValue("ThreadingModel", "Both");
            foreach (var key in new[] { server, server.CreateSubKey(AssemblyName.GetAssemblyName(target).Version!.ToString()) })
            {
                key.SetValue("Class", ClassName); key.SetValue("Assembly", identity!); key.SetValue("RuntimeVersion", "v4.0.30319"); key.SetValue("CodeBase", new Uri(target).AbsoluteUri);
                if (!ReferenceEquals(key, server)) key.Dispose();
            }
            cls.CreateSubKey(@"Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}").Dispose();
        }
        using (var addin = user.CreateSubKey(Keys[2]))
        {
            addin.SetValue("PptEaseOwned", 1); addin.SetValue("FriendlyName", "PPT 增强"); addin.SetValue("Description", "对象锁定、右键管理与材质预览入口"); addin.SetValue("LoadBehavior", 3); addin.SetValue("CommandLineSafe", 0);
        }
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(RecordPath, JsonSerializer.Serialize(new { schema = 1, installedAt = DateTimeOffset.Now, target, sha256 = hash, assembly = identity, classId = ClassId, progId = ProgId, registryKeys = Keys, managerPath }, new JsonSerializerOptions { WriteIndented = true }));
        using (var config = user.CreateSubKey(@"Software\PptEase")) config.SetValue("ManagerPath", managerPath);
        MaintenanceDeployment.Register(managerPath);
        if (Process.GetProcessesByName("POWERPNT").Length == 0) return "已为当前用户安装。下次打开 PowerPoint 时加载增强。";
        try { await ComAutomation.SetAddinConnectedAsync(true); }
        catch (COMException ex) when (unchecked((uint)ex.HResult) == 0x8002000B)
        {
            return "已为当前用户安装。正在运行的 PowerPoint 尚未发现新加载项；保存后正常退出所有 PPT 窗口，再重新打开即可尝试加载。";
        }
        var snapshot = await new HostClient().RefreshAsync();
        if (!snapshot.IntegrationLoaded)
            return "注册已完成，但宿主连接尚未验证成功。" + snapshot.Status;
        var expectedBuild = AssemblyName.GetAssemblyName(target).Version!.ToString();
        if (snapshot.HostBuild != expectedBuild)
            return "新版本已注册，当前 PowerPoint 仍运行另一版本的增强（" + snapshot.HostBuild + "）。请保存后完全退出 PowerPoint，再重新打开。";
        return "已安装并验证 PowerPoint 宿主连接，版本 " + snapshot.HostBuild + "。";
    }
    public async Task<string> DisableAsync()
    {
        await CanvasProbeRegistration.RemoveAsync();
        if (Process.GetProcessesByName("POWERPNT").Length > 0)
        {
            try { await ComAutomation.SetAddinConnectedAsync(false); }
            catch (COMException) { }
        }
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var addin = user.OpenSubKey(Keys[2], true);
        if (addin != null)
        {
            if (Convert.ToInt32(addin.GetValue("PptEaseOwned", 0)) != 1) throw new InvalidOperationException("注册项不属于 PPT 增强，未修改。");
            addin.SetValue("LoadBehavior", 0);
        }
        return "已停用增强，下次启动也不会自动加载。对象原生锁定状态不受影响。";
    }
    public async Task<string> UninstallAsync()
    {
        await DisableAsync();
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        foreach (var path in Keys)
        {
            using (var key = user.OpenSubKey(path))
                if (key != null && Convert.ToInt32(key.GetValue("PptEaseOwned", 0)) != 1) throw new InvalidOperationException("注册项已变化，未移除：" + path);
            user.DeleteSubKeyTree(path, false);
        }
        // Keep only this application's installation record and hashed module files for diagnostics;
        // a loaded CLR may still hold the file until PowerPoint exits.
        MaintenanceDeployment.RemoveEntry();
        return "已移除本软件的加载入口。下次打开 PowerPoint 不会加载；已锁定对象仍保留文稿中的原生状态。";
    }
}
