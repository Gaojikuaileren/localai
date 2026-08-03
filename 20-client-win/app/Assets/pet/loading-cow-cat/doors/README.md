# Loading Cow Cat 门资源 v1 · revision 5

两扇门都按同一只未缩放的猫设计，画布为 `128×128`，`ground_y=112`。

- `door_desktop`：右侧桌面出口，是安装在家门下方的大号上翻猫洞。它不是一扇缩小的人门。
- `door_corridor` / `door_other_client`：左侧跨客户端入口。外观是厚薄不均、顶部有压痕、底部有近景软唇的毛绒猫窝式穿行洞；不是建筑拱门，也不是供猫睡觉的窝。

地板、背景、睡垫、猫窝室内空间和挠门特效都不属于本资源。

## 正式门文件

| 入口 | 兼容合成图 | back | leaf | front | portal stencil |
|---|---|---|---|---|---|
| Desktop | `door_desktop.png`，4 帧 | `door_desktop_back.png` | `door_desktop_leaf.png`，4 帧 | `door_desktop_front.png` | `door_desktop_portal_mask.png` |
| Other client | `door_corridor.png`，1 帧 | `door_corridor_back.png` | 无 | `door_corridor_front.png` | `door_corridor_portal_mask.png` |

所有门 PNG 都是单色 alpha mask：实体像素只能是 `RGBA(255,255,255,255)`，其余只能是 `RGBA(0,0,0,0)`。运行时读取 alpha 后用当前皮肤色填充；无抗锯齿、灰边和半透明。

`portal_mask` 不直接绘制。白区只表示角色位于门后时仍可显示的 door-local 区域，裁切对象必须同时包含猫和头部加载环。

## 角色穿门动画

角色源 sheet 不放在本目录，而在：

- `../source/body-clips/door_enter_left_1x.png`
- `../source/body-clips/door_exit_left_1x.png`

两张均为 `512×128`，4 个 `128×128` cell，6 fps，每格 1 tick。每格保存完整四足猫；角色消失和出现由固定 portal stencil 与前景软唇完成，不在源 PNG 中擦除身体。

正式绑定写在 `door-assets-v1.json > clip_bindings`：

- `door_enter`：右侧 Desktop 猫洞，左向母版运行时镜像；第 2 帧触发 `portal_enter`。
- `door_exit`：左侧软猫窝门，左向母版运行时镜像；第 4 帧触发 `portal_exit`。

猫在门外：

```text
Desktop: back → leaf(open) → cat → front
Other client: back → cat → front
```

猫越过门平面后：

```text
Desktop: back → cat(portal clip) → leaf(open) → front
Other client: back → cat(portal clip) → front
```

`front` 必须实际遮住至少一部分猫，否则穿洞会像贴纸横移。门固定在场景侧，不跟随角色镜像。最后一个角色 tick 的 `root_delta` 负责彻底清门，然后才进入 `BehindDoor` 或 `Stand`。

## 软材质的二值表达

跨客户端门不能依赖阴影或灰阶表现柔软，因此使用这些轮廓约束：

- 整体低而宽，不采用半圆拱顶加长直门柱。
- 顶部由两块被压缩的软垫轮廓组成，中间保留开放式压痕。
- 左右边缘分段鼓起和收窄，洞口侧边每段直线不超过合同上限。
- 前景层集中在右侧和底部，形成猫真正跨过的近景软唇。
- 没有床垫式横向底座、屋顶、门牌、符号或发光效果。

## 构建、验证与预览

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-door-masks.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\validate-door-layers.ps1
```

角色 sheet 的严格黑白透明、完整猫轮廓、接地、逐帧 hash、门锚位移、profile、事件和实际可见比例由下列脚本验证：

```powershell
python ..\source\body-clips\validate-door-body-clips.py `
  --preview-dir ..\source\body-clips\previews
```

`previews/*.gif` 和 `*-preview-strip.png` 仅用于审片，不由运行时加载。运行时正式输入仍是 PNG sheets、门 mask 与 JSON manifest。
预览中的棕色深浅只用于把 `back/front` 层次显示给审片者；正式门文件仍是无颜色语义的纯白 alpha mask。
