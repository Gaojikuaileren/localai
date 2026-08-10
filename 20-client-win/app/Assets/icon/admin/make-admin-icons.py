#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""管理端图标的生成脚本(实机反馈⑩ · V29 做出来的那一套,V29b 入库)。

═══════════════════════════════════════════════════════════════════════════════
 ★★★ 为什么这个文件必须在版本库里

 V29 把 30 张 PNG + 两个 .ico 提交进来了,而**怎么做出来的只以决议包散文形式存在** ——
 下次要换颜色、换图案、补一个尺寸,得有人重新推一遍「怎么把黑换成红并镜像」。
 那正是本仓反复记的那类账:**产物在、做法不在** ⇒ 下一个人只能重新发明,
 而重新发明出来的那一版与这一版差在哪里,**不会有任何东西红**。

 ★ 它同时是**判据的另一半**:`admin/SelftestIcon.cs` 钉的是产物的性质
   (是红的 · 是镜像 · 与客户端分得开),这个脚本钉的是**怎么再做一份出来**。

═══════════════════════════════════════════════════════════════════════════════
 用法(在仓库任意位置):

     python 20-client-win/app/Assets/icon/admin/make-admin-icons.py

 它读 `../icon-*.png`(客户端那套)与 `../app.ico` 的帧尺寸,写回本目录。
 ★ 幂等:跑几次结果一样。★ 跑完请重新 `dotnet build localai-admin.csproj`
   并跑一遍 `localai-admin --selftest` —— 图标那一节会当场验红占比与镜像。

 依赖:Pillow(`pip install pillow`)。
═══════════════════════════════════════════════════════════════════════════════
"""

from pathlib import Path
import sys

try:
    from PIL import Image
except ImportError:                                        # noqa: BLE001
    sys.exit("需要 Pillow:pip install pillow")

HERE = Path(__file__).resolve().parent          # .../Assets/icon/admin
CLIENT = HERE.parent                            # .../Assets/icon

# ★★ 这个色值**不是随便挑的**:它是 `app/Theme/Tokens.xaml` 里
#    `<SolidColorBrush x:Key="RiskDanger" Color="#D93025"/>` 那一个 —— 仓里那条
#    「恒定风险语义色」(设计 §7:三皮肤禁改)。管理端 = 会动整套栈的那一端,
#    用这条色是有含义的,不是"随手一个红"。
#  ★ 改这里之前请连同 Tokens.xaml 一起看:两处漂了不会有任何东西红。
RED = (0xD9, 0x30, 0x25)

# .ico 里放哪几帧 —— 与客户端 `app.ico` 对齐(16/24/32/48/64/256),多给一个 128。
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]


def recolour(im: "Image.Image") -> "Image.Image":
    """左右镜像 + 把黑换成红。

    ★ 白保持白、alpha 原样;抗锯齿的灰按**亮度**在 红↔白 之间插值 ——
      直接"整块涂红"会让边缘发毛(黑白之间那圈过渡像素会跳成纯红)。
    ★★ 镜像成立的前提是**图案不对称**(星芒压一侧、一只耳朵有缺口)。
      对称的话镜像等于什么都没做 —— 这一点核实过:
      客户端原图与它自己的镜像只有 52% 一致,确实不对称。
    """
    im = im.convert("RGBA").transpose(Image.FLIP_LEFT_RIGHT)
    px = im.load()
    w, h = im.size
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            lum = (299 * r + 587 * g + 114 * b) // 1000     # 0=黑 255=白
            t = lum / 255.0
            px[x, y] = (
                int(RED[0] + (255 - RED[0]) * t),
                int(RED[1] + (255 - RED[1]) * t),
                int(RED[2] + (255 - RED[2]) * t),
                a,
            )
    return im


def main() -> int:
    sources = sorted(CLIENT.glob("icon-*.png"))
    if not sources:
        sys.exit(f"在 {CLIENT} 底下找不到客户端的 icon-*.png —— 路径不对?")

    made = 0
    for src in sources:
        # ★ **逐尺寸**各转一次,不是从 1024 缩下来:小尺寸那几张是手调过的,
        #   从大图缩会把它们的清晰度丢掉。
        recolour(Image.open(src)).save(HERE / src.name)
        made += 1

    def frames(sizes):
        return [Image.open(HERE / f"icon-{s}x{s}.png").convert("RGBA") for s in sizes]

    have = frames(ICO_SIZES)
    have[-1].save(HERE / "admin.ico", format="ICO", sizes=[(s, s) for s in ICO_SIZES])
    frames([48])[0].save(HERE / "favicon.ico", format="ICO", sizes=[(16, 16), (32, 32), (48, 48)])

    print(f"写出 {made} 张 PNG + admin.ico({len(ICO_SIZES)} 帧)+ favicon.ico")
    print("★ 接着:dotnet build localai-admin.csproj 再跑 localai-admin --selftest")
    print("  —— 图标那一节会验「是红的(阈值 30%)」与「与客户端是镜像」")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
