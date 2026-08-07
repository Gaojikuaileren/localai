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

// ═════════════════════════════════════════════════════════════════════════════
//  D?(P5 语音 v1)· 按住说话的【采集】—— 一段录音,不是一条实时流
// ═════════════════════════════════════════════════════════════════════════════
//
//  ★★★ 架构底线(STATE 与 InterpretState 顶部都写着,这里是它的落点之一):
//        **同传可以失败,用户的麦克风不可以。**
//
//  本类对那条底线的落实是**结构性的,不是 try-catch**:
//
//    · 它**不认识任何网络类型**。整个类里没有 HttpClient、没有 SpeechClient、
//      没有 Transport、没有 TheApp —— 采集完把 WAV 字节交出去就结束了。
//    · ⇒ 语音服务挂掉 / 权重被删 / 端口被占,**在代码路径上都够不着这里**。
//      录音照录、字节照给;失败的只是"这段话转成了什么字"。
//    · 反过来这也是本类的硬约束:**永远不要在这里加一条"顺便发出去"的捷径**。
//      加了之后那条底线就从结构性退化成靠自觉,而这个项目已经踩过太多次
//      「靠自觉的底线不是底线」。⇒ 已用断言钉死(Selftest 的「麦克风独立性」一节)。
//
//  ★ 为什么用 WinMM(waveIn*)而不是 WASAPI 采集:
//    本文件上半已经用 WASAPI **枚举**端点了,而 waveIn 的**录音**只要几个 P/Invoke,
//    不引任何 NuGet(与本仓「全本地、不加第三方二进制」同一条口径)。
//    代价如实记:waveIn 走系统默认输入设备,**选设备那一项它管不着** ——
//    见 <see cref="DeviceSelectionSupported"/>,界面据此如实说明,不摆假开关。
//
//  ★ 16 kHz 单声道 16-bit:whisper 的原生采样率。在这里就录成它要的形状,
//    省掉一次重采样 —— 也省掉「重采样写错导致识别率莫名变差」这类查不出来的问题。
public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    /// <summary>
    /// 这条采集路径**支不支持按端点 ID 选设备**。今天是 false —— waveIn 只认系统默认输入。
    /// ★ 如实暴露成一个常量,而不是让界面摆一个"选了不生效"的下拉框:
    ///   一个看起来能选、其实不生效的设置,比没有这个设置更坏。
    /// </summary>
    public const bool DeviceSelectionSupported = false;

    readonly object _gate = new();
    readonly List<byte> _pcm = new();
    readonly List<IntPtr> _buffers = new();
    IntPtr _handle;
    bool _recording;
    WaveInProc? _proc;      // ★ 必须存成字段:委托被 GC 掉的话回调会打进已释放的内存

    public bool Recording { get { lock (_gate) return _recording; } }

    /// <summary>已经录到的秒数(界面显示"按住了多久")。</summary>
    public double Seconds
    {
        get { lock (_gate) return _pcm.Count / (double)(SampleRate * Channels * (BitsPerSample / 8)); }
    }

    /// <summary>开始录。返回空串 = 开始了;否则是**为什么没开始**(直接拿给用户看)。</summary>
    public string Start()
    {
        lock (_gate)
        {
            if (_recording) return "";
            _pcm.Clear();
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1,                                  // PCM
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = Channels * BitsPerSample / 8,
                cbSize = 0,
            };
            fmt.nAvgBytesPerSec = (uint)(fmt.nSamplesPerSec * fmt.nBlockAlign);

            _proc = OnWaveIn;
            var r = waveInOpen(out _handle, WAVE_MAPPER, ref fmt, _proc, IntPtr.Zero, CALLBACK_FUNCTION);
            if (r != 0)
            {
                _handle = IntPtr.Zero;
                _proc = null;
                // ★ 说得出**是哪一步**失败 ——「录不了音」这句话本身帮不上任何忙。
                //   最常见的两种:没有输入设备、或 Windows 的麦克风隐私开关关着。
                return r is MMSYSERR_NODRIVER or MMSYSERR_BADDEVICEID
                    ? "打不开麦克风:这台机器上没有可用的输入设备(或者它被禁用了)。"
                    : $"打不开麦克风(waveInOpen 返回 {r})—— 请检查 Windows 设置里的麦克风权限。";
            }

            for (var i = 0; i < BufferCount; i++) AddBuffer();
            if (waveInStart(_handle) != 0) { CloseLocked(); return "麦克风打开了,但启动录音失败。"; }
            _recording = true;
            return "";
        }
    }

    /// <summary>
    /// 停止并取回这一段的 WAV 字节。★ 一次按住 = 一段完整的录音,**不是流**。
    /// 一个采样都没录到时返回 null(而不是一段 0 字节的 wav —— 那会让下游读成"识别不出来")。
    /// </summary>
    public byte[]? StopAndTakeWav()
    {
        lock (_gate)
        {
            if (!_recording) return null;
            CloseLocked();
            _recording = false;
            return _pcm.Count == 0 ? null : WavFromPcm(_pcm.ToArray(), SampleRate, Channels, BitsPerSample);
        }
    }

    /// <summary>
    /// 把裸 PCM 包成 WAV。★ **纯函数,单独抽出来** —— 采集要真麦克风才跑得起来,
    /// 而这一段是断言在无人值守的门禁里唯一验得了的部分(头 44 字节 / 长度 / 采样率)。
    /// </summary>
    public static byte[] WavFromPcm(byte[] pcm, int rate, int channels, int bits)
    {
        var blockAlign = channels * bits / 8;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + pcm.Length);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                       // fmt chunk size
        w.Write((short)1);                 // PCM
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * blockAlign);        // byte rate
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    // ── WinMM 回调:把每个填满的缓冲区收进 _pcm,再把它还回去继续录 ──────────
    void OnWaveIn(IntPtr h, uint msg, IntPtr inst, ref WAVEHDR hdr, IntPtr p2)
    {
        if (msg != WIM_DATA) return;
        lock (_gate)
        {
            if (!_recording) return;
            var n = (int)hdr.dwBytesRecorded;
            if (n > 0)
            {
                var buf = new byte[n];
                Marshal.Copy(hdr.lpData, buf, 0, n);
                _pcm.AddRange(buf);
            }
            waveInAddBuffer(_handle, ref hdr, Marshal.SizeOf<WAVEHDR>());
        }
    }

    void AddBuffer()
    {
        var bytes = SampleRate * Channels * (BitsPerSample / 8) / 10;   // 100 ms
        var data = Marshal.AllocHGlobal(bytes);
        var hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
        var hdr = new WAVEHDR { lpData = data, dwBufferLength = (uint)bytes };
        Marshal.StructureToPtr(hdr, hdrPtr, false);
        waveInPrepareHeader(_handle, hdrPtr, Marshal.SizeOf<WAVEHDR>());
        waveInAddBuffer(_handle, hdrPtr, Marshal.SizeOf<WAVEHDR>());
        _buffers.Add(hdrPtr);
    }

    void CloseLocked()
    {
        if (_handle == IntPtr.Zero) return;
        try { waveInStop(_handle); waveInReset(_handle); } catch { }
        foreach (var p in _buffers)
        {
            try
            {
                waveInUnprepareHeader(_handle, p, Marshal.SizeOf<WAVEHDR>());
                var h = Marshal.PtrToStructure<WAVEHDR>(p);
                Marshal.FreeHGlobal(h.lpData);
                Marshal.FreeHGlobal(p);
            }
            catch { }
        }
        _buffers.Clear();
        try { waveInClose(_handle); } catch { }
        _handle = IntPtr.Zero;
        _proc = null;
    }

    public void Dispose() { lock (_gate) { _recording = false; CloseLocked(); } }

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    const int BufferCount = 8;
    const uint WAVE_MAPPER = 0xFFFFFFFF;
    const uint CALLBACK_FUNCTION = 0x00030000;
    const uint WIM_DATA = 0x3C0;
    const int MMSYSERR_BADDEVICEID = 2;
    const int MMSYSERR_NODRIVER = 6;

    delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, ref WAVEHDR hdr, IntPtr p2);

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, uint dev, ref WAVEFORMATEX f, WaveInProc cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, ref WAVEHDR hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
}
