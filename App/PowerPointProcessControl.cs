using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PptEase;
internal static class PowerPointProcessControl
{
    internal sealed record Target(int Id, long StartedUtcTicks);
    internal static string Open()
    {
        if(Capture().Length>0)return global::PptEase.Localization.L.T("PowerPoint 已在运行。");
        using var process=Process.Start(new ProcessStartInfo("powerpnt.exe") { UseShellExecute=true });
        return global::PptEase.Localization.L.T("已请求打开 PowerPoint。");
    }
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal static Target[] Capture()
    {
        var targets = new List<Target>();
        foreach (var process in Process.GetProcessesByName("POWERPNT"))
        {
            using (process)
                try { targets.Add(new Target(process.Id, process.StartTime.ToUniversalTime().Ticks)); }
                catch (InvalidOperationException) { }
        }
        return targets.ToArray();
    }
    internal static async Task<string> RequestCloseAsync()
    {
        var targets = Capture();
        if (targets.Length == 0) return global::PptEase.Localization.L.T("没有正在运行的 PowerPoint 进程。");
        var ids = targets.Select(t => (uint)t.Id).ToHashSet();
        int windows = 0, failed = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if (!ids.Contains(process)) return true;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() == "PPTFrameClass")
            {
                if (PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) windows++; else failed++;
            }
            return true;
        }, IntPtr.Zero);
        await Task.Delay(1500);
        int remaining = Capture().Length;
        if (remaining == 0) return global::PptEase.Localization.L.T("所有 PowerPoint 进程已退出。");
        return global::PptEase.Localization.L.F("已向 {0} 个 PPT 窗口请求正常关闭，仍有 {1} 个进程。请先处理 PPT 的保存提示；若取消关闭，文稿会保持打开。", (object)(windows), (object)(remaining)) +
            (failed > 0 ? global::PptEase.Localization.L.F("另有 {0} 个窗口未接受关闭请求。", (object)(failed)) : "") +
            global::PptEase.Localization.L.T("确认不再需要未保存内容后，才使用“强制结束”。");
    }
    internal static async Task<string> ForceCloseAsync(Target[] confirmedTargets)
    {
        int ended = 0; var errors = new List<string>();
        foreach (var target in confirmedTargets)
        {
            try
            {
                using var process = Process.GetProcessById(target.Id);
                // Never terminate a new process that reused a PID after confirmation.
                if (!process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase) || process.StartTime.ToUniversalTime().Ticks != target.StartedUtcTicks) continue;
                process.Kill(entireProcessTree: false); ended++;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex) { errors.Add($"PID {target.Id}：{ex.Message}"); }
        }
        await Task.Delay(800);
        return global::PptEase.Localization.L.F("已向 {0} 个已确认的 PPT 进程发送结束请求；当前剩余 {1} 个。", (object)(ended), (object)(Capture().Length)) + (errors.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, errors));
    }
}
