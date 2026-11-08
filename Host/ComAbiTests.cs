using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TYPEATTR = System.Runtime.InteropServices.ComTypes.TYPEATTR;
using TYPEFLAGS = System.Runtime.InteropServices.ComTypes.TYPEFLAGS;
using PptEase.Host;

public static class ComAbiTests
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RibbonFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string id, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddInsUpdateFn(IntPtr self, ref IntPtr custom);
    private static void CheckType(Type type, int expectedFunctions)
    {
        IntPtr ptr = Marshal.GetITypeInfoForType(type), attr = IntPtr.Zero;
        ITypeInfo info = (ITypeInfo)Marshal.GetObjectForIUnknown(ptr);
        try
        {
            info.GetTypeAttr(out attr); TYPEATTR a = (TYPEATTR)Marshal.PtrToStructure(attr, typeof(TYPEATTR));
            if ((a.wTypeFlags & TYPEFLAGS.TYPEFLAG_FDUAL) == 0) throw new Exception(type.Name + " not dual");
            if (a.cFuncs != expectedFunctions) throw new Exception(type.Name + " unexpected function count " + a.cFuncs);
            Console.WriteLine("PASS " + type.Name + " exported FDUAL, functions=" + a.cFuncs);
        }
        finally { if (attr != IntPtr.Zero) info.ReleaseTypeAttr(attr); Marshal.Release(ptr); }
    }
    public static int Main()
    {
        try
        {
            CheckType(typeof(IDTExtensibility2), 12); CheckType(typeof(IRibbonExtensibility), 8);
            Connect host = new Connect(); IntPtr unknown = Marshal.GetIUnknownForObject(host), ribbon = IntPtr.Zero, extension = IntPtr.Zero;
            try
            {
                Guid rg = typeof(IRibbonExtensibility).GUID, eg = typeof(IDTExtensibility2).GUID;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref rg, out ribbon));
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref eg, out extension));
                IntPtr rvtable = Marshal.ReadIntPtr(ribbon), rmethod = Marshal.ReadIntPtr(rvtable, 7 * IntPtr.Size), result = IntPtr.Zero;
                RibbonFn get = (RibbonFn)Marshal.GetDelegateForFunctionPointer(rmethod, typeof(RibbonFn));
                Marshal.ThrowExceptionForHR(get(ribbon, "Microsoft.PowerPoint.Presentation", out result));
                try { if (Marshal.PtrToStringBSTR(result).IndexOf("peUnlock", StringComparison.Ordinal) < 0) throw new Exception("Native Ribbon vtable returned wrong content"); }
                finally { if (result != IntPtr.Zero) Marshal.FreeBSTR(result); }
                IntPtr evtable = Marshal.ReadIntPtr(extension), emethod = Marshal.ReadIntPtr(evtable, 9 * IntPtr.Size), empty = IntPtr.Zero;
                AddInsUpdateFn update = (AddInsUpdateFn)Marshal.GetDelegateForFunctionPointer(emethod, typeof(AddInsUpdateFn));
                Marshal.ThrowExceptionForHR(update(extension, ref empty));
                Console.WriteLine("PASS native CCW vtable GetCustomUI slot7 and OnAddInsUpdate slot9. No Office instance used.");
            }
            finally { if (ribbon != IntPtr.Zero) Marshal.Release(ribbon); if (extension != IntPtr.Zero) Marshal.Release(extension); Marshal.Release(unknown); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
