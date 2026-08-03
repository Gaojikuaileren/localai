# 提权护栏问错了问题:该问"密钥打不打得开",不是"我是不是管理员"(决议草案 · 取号待定)

> 日期:2026-08-03
> 提出并已在 client 侧落地:**client**(`20-client-win/**`,提交 `eb9bf0a`)
> 需要同样改的车道:**core**(`10-core/lan-edge/**`、`10-core/identity/**`)
> 性质:**草案**,取号按 D75 办。
> ★ 这条**推翻了** `identity-elevation-guard-2026-08-03.md` 的核心主张 —— 见 §6。

---

## 1. 实测事实(不是推断)

用户在主机上点「一次装好这台主机」,拿到的是 Edge 的护栏报错。查下来:

| 测量 | 结果 |
|---|---|
| `HKLM\...\Policies\System\EnableLUA` | **0 —— UAC 彻底关闭** |
| 桌面 `explorer.exe` 的完整性 | `S-1-16-12288`(**High**) |
| 客户端进程完整性 | `S-1-16-12288`(High) |
| `CngKey.Open("localai-ca-f6hsduipeesexb6f", "Microsoft Platform Crypto Provider")` 在 High 进程里 | **✓ 成功**(ECDSA) |
| `CngKey.Open("localai-server-f6hsduipeesexb6f", "Microsoft Software Key Storage Provider")` | **✓ 成功** |

⇒ 这台机器上**根本不存在**普通(Medium)身份的进程:桌面 shell 自己就是 High,
双击出来的一切都是 High。中枢身份当初也就是在 High 下铸的,**在 High 下能正常打开**。

## 2. 所以护栏错在哪

它真正关心的只有一件事:**密钥打不打得开**。
它实际问的却是**代理指标**:

- `10-core/lan-edge/Program.cs` 的 `IsElevated()` → `IsInRole(Administrator)`;
- 客户端旧版 → 遍历 `WindowsIdentity.Groups` 找完整性 SID(那个 SID 根本不在 TokenGroups 里,
  所以它**从来没生效过** —— 另一个 bug,见 `c8ede7f`)。

UAC 一关,`IsInRole(Administrator)` 对管理员账户**恒为 true**。
⇒ 护栏把一台**完全健康**的机器判成不能用,而且给出的理由(「密钥集不存在」)**是假的** ——
密钥就在那儿,打得开。

★ 这是今天第三次撞见同一个形状:**检查的是代理指标,不是它真正关心的事实。**
前两次:「探测失败 = 这台不是主机」、`IndexOf(a) < IndexOf(b)` 在 a 缺失时恒真。

## 3. 正解

```csharp
// 先问真问题:要用的那把密钥打不打得开。打得开就放行 —— 无论完整性等级是什么。
// 打不开【而且】当前是 High,才是真正要拦的情形,那时理由也是真的。
if (IsElevated() && !KeyUsable(out var note))
{
    Console.WriteLine("✗ 打不开身份密钥,而且本进程是管理员身份 —— 多半是身份在普通用户下铸的。");
    Console.WriteLine("  " + note);
    return 3;
}
```

三种机器上都对:

| 机器 | 密钥铸于 | 当前进程 | 打得开? | 结果 |
|---|---|---|---|---|
| UAC 开,正常用 | Medium | Medium | ✓ | 放行(对) |
| UAC 开,误提权 | Medium | High | ✗ | 拦住,**理由真实**(对) |
| **UAC 关(本机)** | High | High | ✓ | **放行**(对 —— 旧版在这里误判) |

## 4. 请 **core 车道**改的

### 4.1 `10-core/lan-edge/Program.cs`
`RunLan` 开头那段(搜「检测到以【管理员】身份运行」)改成 §3 的形状:
先试着打开 CA 密钥(locator 见 `{state}/secrets/identity-locators.json` 的
`ca_provider` / `ca_key_name`),打得开就继续。

### 4.2 `10-core/identity/Program.cs`
`identity-elevation-guard-2026-08-03.md` 要求给 `init` 等子命令加提权拒绝 ——
**那条要按本包修正**:
- `init`(此时还没有密钥可开)**不能**用"打不打得开"判。它要防的是
  「在 A 等级铸、将来在 B 等级用」。★ 正确做法是**把铸造时的完整性等级记进
  `identity-locators.json`**(如 `minted_integrity_rid`),
  之后所有会用到 CA 的地方拿当前等级与它比对,不一致才拦、并说出两个值;
- 在 UAC 关闭的机器上两者恒等,一切正常;UAC 开启的机器上误提权会被准确拦下。

### 4.3 验收断言
- UAC 关闭的机器上:`run-lan` 必须能起来(现在起不来 —— 这就是今天的阻塞);
- 密钥打不开 + High 时必须 exit 3,且错误文案里带上**实际的打开失败原因**,不是套话;
- 按项目纪律:先把实现改坏、确认断言变红,再改回。

## 5. 用户侧的另一条路(不推荐,但要写明)

把 UAC 打开(`EnableLUA=1` + 重启)也能让旧护栏成立。**但不该要求用户为了跑我们的软件
去改系统安全设置** —— 而且他们已有的身份是在 High 下铸的,UAC 打开后反而会**真的**打不开,
必须重置重铸、所有设备重配。**所以这条路比 §4 更糟,记在这里只为免得有人再想起它。**

## 6. ★ 对既有决议包的更正

`identity-elevation-guard-2026-08-03.md` 说「CA 私钥在你普通用户的 TPM 上下文里,
管理员进程访问会报『密钥集不存在』」—— 那句话**在这台机器上不成立**,实测密钥在 High 下打得开。

它的**结论仍然有效**(identity 缺少护栏、而且不可回退的错误要在铸之前拦),
但**判据要换**:不是"是不是管理员",而是"当前完整性等级与铸造时是否一致"(§4.2)。
请 core 车道按本包实现,不要照抄那一包里的 `IsElevated()` 版本。
