# `{state}` 子目录 ACL 加固

> 2026-08-03 · 起因见下方「实测基线」。**脚本改 NTFS ACL,属系统安全设置变更 —— AI 不代跑,机主自己在提权终端执行。**

## 这是在修什么

`Get-Acl D:\AI\state` 逐目录实测:

| 目录 | 断继承 | `Authenticated Users` |
|---|---|---|
| `memory` · `secrets` | ✅ Protected=True | 已排除 |
| `db` · `identity` · `logs` · `openwebui` · `quarantine` · `tickets` | ❌ **Protected=False** | **继承 Modify** |

后果具体到两个文件:

- **`identity\store.json`** —— 成员表,D45「按成员可见范围」的**唯一**来源;
- **`logs\gate_rejection.jsonl`** —— E1 凭证拦截审计(实测 35 KB 真实数据)。

两者对**每一个已认证账户**可写,**包括网关在 API 层明令拒绝的 `ai-exec` / `ai-asset`**。
网关在门口拒了它,它转身直接改文件就行 —— 这正是本项目固定审查视角说的
**「看着有防护、实际没有」**。`PLAN §6.7.5` 自己也写过「P6 建立专用账户之前 OS ACL 层是零防护」。

## ACL 表的取证依据(不是猜的)

按 `Get-Acl` 逐文件读 Owner 统计:

```
logs        37 个文件:34 × ai-mem · 3 × Administrators
identity     4 个文件:4  × Administrators
openwebui    5 个文件:3  × 机主 · 2 × Administrators
db / tickets 空 · quarantine 1 × Administrators
```

⇒ 只有 `logs` 需要额外授 `ai-mem`;其余用 base 表(SYSTEM + Administrators + 机主)就够。
**这一步不能猜** —— 授漏了服务的日志会静默消失,授多了等于没加固。

## 怎么用

```bash
powershell -ExecutionPolicy Bypass -File .\verify-state-acl.ps1
```

先跑只读复核看现状（**加固前预期大量 FAIL,那是如实反映**）。然后演练：

```bash
powershell -ExecutionPolicy Bypass -File .\harden-state-acl.ps1 -WhatIf
```

确认计划无误后正式施加（逐步确认，每步都要敲 `y`）：

```bash
powershell -ExecutionPolicy Bypass -File .\harden-state-acl.ps1
```

加固后**重新跑一遍 `verify-state-acl.ps1`**，并重启 `start-stack.ps1` 的服务，确认 `ai-mem` 仍能写 `logs`、网关仍能读 `identity`。要退回：

```bash
powershell -ExecutionPolicy Bypass -File .\harden-state-acl.ps1 -Revert
```

## 诚实声明 —— 本套脚本【没有】解决的

1. **审计文件与服务 stdout/stderr 日志躺在同一个目录里。** 断继承之后，能写服务日志的账户仍然能改审计文件。真正的修法是把审计挪进独立的 append-only 目录（D71 的哈希链 + 跨账户锚点正是为此），**不在本脚本范围内**。本脚本只把「所有已认证账户」收敛到「少数具名账户」。
2. **对本机管理员，这一层不构成边界。** 管理员随时能改回任何 ACL。与 `DEC:1822` 把「主机时钟篡改 = 本机管理员」判为 out-of-scope 是同一条边界。
3. **`quarantine` 与 `tickets` 目前是空的。** 加固它们是为了「将来往里写东西时不必再想一遍」，不是因为现在有东西要保护。

## ★★ 一条对 §6.5 隔离区设计有直接影响的实测

把 `{state}\openwebui` 归档进 `quarantine` 之后，发现它 `Protected=True`、DACL 里赫然还有 `Authenticated Users : Modify` —— **宽泛权限跟着数据一起搬过去了**。

原因：**同卷 Move 是重命名**。NTFS 为了让对象在新位置保持相同的有效访问，会把它原先*继承来的* ACE **转成显式**带过去，并置上 Protected 位。

> **「移进一个加固过的目录」不等于「变成加固过的」。**

这条对 §6.5「隔离区 = delete 的替代品，永不 delete」是直接的设计影响：**P6 的执行器把一个人人可写的文件移进隔离区之后，它还是人人可写** —— 隔离区看着把东西关起来了，实际只是换了个位置。

**入区时必须**：

```bash
icacls "<隔离区目标>" /reset /T
```

`/reset` 丢掉显式 DACL、改回从隔离区继承；`/T` 递归到每个子项。

已立成断言：`verify-state-acl.ps1` ⑤ 段逐个检查隔离区条目 `AreAccessRulesProtected -eq $false` 且三个宽泛主体无写权限。

## 复核脚本的一个坑（留个记号）

`verify-state-acl.ps1` 第一版的 `Get-EffectiveWrite` **只比对账户自己的 SID**，于是「`ai-asset` 写不了 `logs`」报 **PASS —— 假 PASS**。`ai-asset` 没有具名 ACE 不代表它写不了：它在 `Authenticated Users` 里，而那条组 ACE 有 Modify。

有效权限算的是**令牌里所有 SID 的并集**，不是账户那一个 SID。现在由 `Get-TokenSids` 穷举本地组成员关系 + 补 well-known 组（`S-1-5-11` / `S-1-1-0`）。

——「只看具名 ACE 在不在」正是这个函数的注释里警告过的失效模式，而第一版自己踩了。
