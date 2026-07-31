// P3c -- 音频设备枚举(同传要选输入/输出)。
//
// ★ 这里【不依赖语音链路】:列出机器上有哪些麦克风/播放设备,是现在就能做也现在就有用的事 ——
//   选好了存下来,等采集接上直接照着用,不用那时再回头补界面。
//
// 做法:WASAPI 的 IMMDeviceEnumerator。这是 Windows 上枚举音频端点的唯一正路
//   (端点由内核 KS 拓扑推导,用户态只能【读】,不能注册 —— 这也正是虚拟麦克风必须写驱动的原因)。
//
// ★ 全部包在 try 里:声卡驱动出问题、远程桌面会话、设备热插拔都可能让 COM 调用抛 ——
//   枚举不出来就如实返回空列表,界面说"读不到设备",绝不因此崩掉整个界面。

using System.Runtime.InteropServices;

namespace LocalAI.Client.Services;

/// <param name="Id">端点 ID(稳定,存档用)</param>
/// <param name="Name">给人看的名字</param>
public sealed record AudioDeviceInfo(string Id, string Name);

public static class AudioDevices
{
    public static List<AudioDeviceInfo> Inputs() => Enumerate(EDataFlow.Capture);
    public static List<AudioDeviceInfo> Outputs() => Enumerate(EDataFlow.Render);

    static List<AudioDeviceInfo> Enumerate(EDataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator? en = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            en.EnumAudioEndpoints(flow, DeviceStateActive, out var col);
            // ★ 设备集合本身也是 RCW,要释放(审计 2026-07-31):此前只释放了枚举器,
            //   集合 / 每个设备 / 属性存储的 RCW 与 PROPVARIANT 全泄漏,而这条路每次刷新走两遍。
            try
            {
                col.GetCount(out var n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice? dev = null;
                    IPropertyStore? store = null;
                    try
                    {
                        col.Item(i, out dev);
                        dev.GetId(out var id);
                        dev.OpenPropertyStore(StgmRead, out store);
                        store.GetValue(ref PkeyDeviceFriendlyName, out var v);
                        var name = v.pwszVal != IntPtr.Zero ? Marshal.PtrToStringUni(v.pwszVal) ?? id : id;
                        PropVariantClear(ref v);   // PROPVARIANT 里的 BSTR 得还给 COM,不能只读不放
                        list.Add(new AudioDeviceInfo(id, name));
                    }
                    catch { /* 单个设备读不到就跳过,不因此丢掉整份列表 */ }
                    finally
                    {
                        if (store is not null) Marshal.ReleaseComObject(store);
                        if (dev is not null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally { Marshal.ReleaseComObject(col); }
        }
        catch { /* 枚举整体失败 -> 空列表,界面如实说"读不到" */ }
        finally { if (en is not null) Marshal.ReleaseComObject(en); }
        return list;
    }

    // ---------------------------------------------------------------- COM 互操作(最小集)
    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant pvar);

    const uint DeviceStateActive = 0x1;
    const uint StgmRead = 0x0;

    static PropertyKey PkeyDeviceFriendlyName = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    enum EDataFlow { Render = 0, Capture = 1, All = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out uint pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, out PropVariant pv);
        int SetValue(ref PropertyKey key, ref PropVariant propvar);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; }
    }

    /// <summary>只取得到字符串就够 —— 名字是我们唯一要的字段。</summary>
    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }
}
