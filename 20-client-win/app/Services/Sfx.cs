// P3c -- 界面音效。目前只有一个:卡片落地的闷响(拖语言进池子时)。
//
// 用户裁定(2026-07-30):堆叠卡片 + 落地扬尘 + 音效【只给暖萌皮肤】——
//   苹果风(微风)和黑白(墨白)要更克制,不出声、不扬尘。所以【是否播放由调用方按皮肤决定】,
//   这里只负责"怎么响"。
//
// ★ 声音是【当场合成】的,不带任何音频素材文件:
//   一个低频正弦的快速衰减 + 一点点噪声当作"落在木头上"的质感,总长 90ms。
//   这样做的理由有三个:发布仍是单文件、不引入二进制资源;音量与音色可调;
//   而且不用去解决"素材放哪、发布时怎么带上"这类和功能无关的问题。
//
// ★ 出错一律吞掉:没有声卡、被独占、远程桌面会话… 都不该让界面报错,更不该崩。
//   音效是锦上添花,失败就当没有。

using System.Media;

namespace LocalAI.Client.Services;

public static class Sfx
{
    const int SampleRate = 22050;

    static byte[]? _drop;

    /// <summary>卡片落地:一声短促的闷响。合成一次,之后复用同一段字节。</summary>
    public static void PlayDrop()
    {
        try
        {
            _drop ??= BuildDrop();
            using var ms = new MemoryStream(_drop, writable: false);
            var player = new SoundPlayer(ms);
            player.Play();          // 异步播放,不挡界面
        }
        catch { /* 没声卡/被占用 —— 音效不是功能,失败就当没有 */ }
    }

    /// <summary>
    /// 合成 90ms 的"闷响":110Hz 正弦(木头感的基频)+ 指数衰减,尾巴掺一点噪声。
    /// 纯函数,便于自检直接验字节头是不是合法 WAV。
    /// </summary>
    public static byte[] BuildDrop()
    {
        const double seconds = 0.09;
        var n = (int)(SampleRate * seconds);
        var pcm = new short[n];
        var rng = new Random(20260730);      // 固定种子:每次响得一模一样,不要随机音色
        for (int i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            var env = Math.Exp(-38 * t);                       // 快速衰减 = "咚"而不是"嗡"
            var tone = Math.Sin(2 * Math.PI * 110 * t);
            var noise = (rng.NextDouble() * 2 - 1) * 0.25;      // 一点点颗粒感
            var v = (tone * 0.8 + noise) * env * 0.32;          // 0.32:整体压低,不刺耳
            pcm[i] = (short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return Wav(pcm);
    }

    /// <summary>把 16bit 单声道采样打包成最简 WAV(44 字节头 + 数据)。</summary>
    static byte[] Wav(short[] pcm)
    {
        var data = pcm.Length * 2;
        using var ms = new MemoryStream(44 + data);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + data);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                    // PCM 头长度
        w.Write((short)1);              // PCM
        w.Write((short)1);              // 单声道
        w.Write(SampleRate);
        w.Write(SampleRate * 2);        // 字节率
        w.Write((short)2);              // 块对齐
        w.Write((short)16);             // 位深
        w.Write("data"u8.ToArray());
        w.Write(data);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
