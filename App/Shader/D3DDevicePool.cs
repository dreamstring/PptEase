using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PptEase.Shader;

public sealed partial class D3DShaderRenderer
{
    // Exclusive leases: an immediate context is never shared by live renderers.
    // Only empty devices survive page changes; all page textures/views/windows are released.
    private sealed record IdleDevice(IntPtr Device, IntPtr Context, uint Level, string Kind, long Returned);
    private static readonly object PoolLock = new();
    private static readonly List<IdleDevice> IdleDevices = new();
    private static bool poolClosed;
    private static readonly Timer PoolTimer = new(_ => TrimDevicePool(false), null, 30000, 30000);
    internal static int IdleDeviceCount { get { lock(PoolLock) return IdleDevices.Count; } }
    static D3DShaderRenderer() { AppDomain.CurrentDomain.ProcessExit += (_, _) => TrimDevicePool(true); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceStatus(IntPtr self);

    private bool TryRentDevice()
    {
        if(ForceWarpForTest) return false;
        lock(PoolLock) {
            while(IdleDevices.Count > 0) {
                var index=IdleDevices.Count-1; var item=IdleDevices[index]; IdleDevices.RemoveAt(index);
                if(Method<DeviceStatus>(item.Device,39)(item.Device)<0) { DestroyDevice(item); continue; }
                _device=item.Device; _context=item.Context; FeatureLevel=item.Level; DeviceKind=item.Kind;
                return true;
            }
        }
        return false;
    }
    private void ReturnDevice()
    {
        if(_device!=IntPtr.Zero && _context!=IntPtr.Zero && !ForceWarpForTest &&
            Method<DeviceStatus>(_device,39)(_device)>=0) {
            lock(PoolLock) {
                if(!poolClosed && IdleDevices.Count<2) {
                    IdleDevices.Add(new(_device,_context,FeatureLevel,DeviceKind,Environment.TickCount64));
                    _device=_context=IntPtr.Zero; return;
                }
            }
        }
        LastContextReleaseCount=Release(ref _context); LastDeviceReleaseCount=Release(ref _device);
    }
    internal static void TrimDevicePool(bool shutdown, long idleMilliseconds=120000)
    {
        lock(PoolLock) {
            if(shutdown) poolClosed=true;
            for(int i=IdleDevices.Count-1;i>=0;i--) {
                if(!shutdown && Environment.TickCount64-IdleDevices[i].Returned<idleMilliseconds) continue;
                DestroyDevice(IdleDevices[i]); IdleDevices.RemoveAt(i);
            }
        }
    }
    private static void DestroyDevice(IdleDevice item)
    {
        var context=item.Context; var device=item.Device;
        LastContextReleaseCount=Release(ref context); LastDeviceReleaseCount=Release(ref device);
    }
}
