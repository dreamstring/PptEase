using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PptEase;
internal static class ComAutomation
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll", PreserveSig = false)] private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    public static dynamic GetPowerPoint()
    {
        var hr = CLSIDFromProgID("PowerPoint.Application", out var clsid);
        Marshal.ThrowExceptionForHR(hr);
        GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
        return instance;
    }
    public static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.TrySetResult(action()); } catch (Exception ex) { completion.TrySetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
    public static async Task SetAddinConnectedAsync(bool connected, string progId = IntegrationInstaller.ProgId)
    {
        await RunStaAsync(() =>
        {
            object? applicationObject=null,addinsObject=null,addinObject=null;
            try
            {
                dynamic application = applicationObject = GetPowerPoint();
                dynamic addins = addinsObject = application.COMAddIns;
                addins.Update();
                dynamic addin = addinObject = addins.Item(progId);
                addin.Connect = connected;
                if ((bool)addin.Connect != connected) throw new InvalidOperationException("PowerPoint 没有接受加载状态，请查看诊断日志。");
                return true;
            }
            finally
            {
                // Balance the COM objects acquired by this one-shot maintenance operation.
                foreach(var item in new[]{addinObject,addinsObject,applicationObject})
                    if(item!=null&&Marshal.IsComObject(item))
                        try {Marshal.ReleaseComObject(item);} catch(InvalidComObjectException) { }
            }
        });
    }
}
