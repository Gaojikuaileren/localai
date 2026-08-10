r"""网关 ↔ 模型后端 的**唯一**鉴权物料 —— 一处生成,三处消费(D? · 方向 B)。

════════════════════════════════════════════════════════════════════════════
 ★★★ 它消掉的那条债(STATE「已知技术债」原文)

   「后端 llama-server **无鉴权** —— 网关不是唯一咽喉,同机进程可直连 18081
     绕过 E1/审计。★ D65 把这条从"技术债"升级为"待决项 6 的一半":
     Windows 防火墙不过滤回环、账户 ACL 管不了 TCP,所以对一个跑在本机的
     Agent Worker,这条**默认成立且无法用现有手段封堵**。」

 ⇒ ACL 管不了 TCP,但管得了**文件**。把后端的门锁上、钥匙放进一个
   `ai-exec` / `ai-asset` 读不到的目录 —— 跑腿 worker 于是拿不到钥匙,
   回环连得上也没用。这是 D65 三条路里唯一**今天就能消掉这条债**的那条。

════════════════════════════════════════════════════════════════════════════
 ★★★ 实测清单(2026-08-10 · llama-server version 10107 · 本机真起过)
      —— 下面每一条都改变了本文件的写法,不是背景资料。

 ① `/health` **不受 key 约束**:不带 key 也回 200。`/v1/models` 同样不受约束。
    受约束的是 `/props` 与 `/v1/chat/completions`(不带 key → **401**)。
    ⇒ **就绪探测不会因为加了 key 而变红**(那是动工前最担心的一格,已排除);
    ⇒ 但反过来:**只探 `/health` 的就绪闸看不见"钥匙对不对"**。
      钥匙不匹配时栈会"启动成功",然后每一次对话都 401。
      所以两处就绪闸都补了一次**带鉴权的探测**(见 `AUTH_PROBE_PATH`)。

 ② `--api-key-file` 指向**不存在**的文件 ⇒ llama-server **拒绝启动**(fail-closed,好)。

 ③ ★★★ `--api-key-file` 指向**空文件** ⇒ llama-server **照常启动,而且完全不鉴权**
    (实测:空 key 文件起的实例,不带 key 打 `/props` 回 **200**)。
    ⇒ 一个 0 字节的文件会**静默地把门重新打开**,而所有"启动成功"的迹象都还在。
    ⇒ 因此本模块**绝不把没通过校验的内容交出去**:写入走 `os.replace` 原子落盘,
      读取端逐条校验长度与单行性。这条是本文件最重要的一条 fail-closed。

 ④ key 文件的行尾 llama-server 会自己剥(CRLF 文件配无 `\r` 的 key,实测 200)。
    但我们仍然按**无行尾**写:少一个"两边各自 strip 得不一样"的机会。

════════════════════════════════════════════════════════════════════════════
 ★★ 钥匙放哪 —— 也是实测,不是偏好

 `{state}\secrets` 的 ACL 只给 SYSTEM / BUILTIN\Administrators / ai-mem,
 而 UAC 过滤令牌里 Administrators 是 **deny-only 组**,那条 allow ACE 不生效。
 实测(`runas /trustlevel:0x20000` 去读该目录):**READ_DENIED**。
 而网关**就是**中等完整性进程,并且是**有意的**(D46 护栏:被提权启动就自己降权重开,
 因为 TPM/CNG 用户密钥绑定铸造时的完整性等级)。
 ⇒ 钥匙放 secrets = 用户双击管理端时栈起不来。故另起 `{state}\backend-auth`,
   理由与恢复语义写在 `config/paths.toml` 的 `backend_auth` 那一段。

 ★ 目录 ACL **每次用之前重新施加**,不假定装机时设过 —— {state} 根继承下来的是
   `Authenticated Users:(M)`,不断继承就等于没设。施加失败**拒绝交出钥匙**。

════════════════════════════════════════════════════════════════════════════
 ★★ 丢了怎么办(用户看不见这把钥匙,所以这一问必须有答案)

   删掉即可,下一次起栈自动重新生成。**代价只有一个**:此刻还在跑的后端
   握着旧钥匙,网关会 401 —— 而 ① 说了 `/health` 看不见这件事,所以那一格
   由带鉴权的就绪探测负责报出来,消息里直说"把后端一起重启"。
 ★ 不做自动轮换:轮换必须同时重启所有后端,而这把钥匙从不离开本机、
   也没有可撤销的对端 —— 自动轮换只会制造"轮换那一秒对话全 401"的窗口。
   要手工轮换:停栈 → 删文件 → 起栈。

════════════════════════════════════════════════════════════════════════════
 用法(两种消费方式,**同一份实现**):
   · Python:  `import backend_key` → `ensure_key_file()` / `auth_header()`
   · PowerShell:`python backend_key.py ensure` → stdout 打印 key 文件路径
"""
from __future__ import annotations

import os
import re
import secrets as _sec
import subprocess
import sys
from pathlib import Path

#: paths.toml 里的键名。★ §11.1:代码里不写绝对路径,也不从别的根**推导** ——
#  推导要先假定「某个根的父目录就是它」,那是一次猜测(paths.toml 自己那段
#  `[memory] venv` 的注释写着为什么不许猜)。
PATHS_KEY = "backend_auth"

#: 密钥文件名。
KEY_FILENAME = "llama-api.key"

#: 生成长度(字节)。32 字节 → 64 个十六进制字符。
KEY_BYTES = 32

#: 判"这不是一把能用的钥匙"的下限。★ 存在的理由是实测 ③:空/短文件会让后端敞着门。
MIN_KEY_CHARS = 32

#: 带鉴权的就绪探测打哪个端点。★ 选 `/props` 而不是 `/v1/chat/completions`:
#  它是 GET、不吃 token、不占 slot,而实测同样**不带 key 就 401** ——
#  也就是说它能回答「钥匙对不对」,却不用真的推理一次。
AUTH_PROBE_PATH = "/props"


class BackendKeyError(RuntimeError):
    """拿不到 / 不敢用这把钥匙。★ 抛而不是返回 None —— 调用方必须**停下来**,
    绝不允许"取不到就不带 Authorization 继续发" —— 那正好把这条债又打开。"""


# ── 落点 ────────────────────────────────────────────────────────────
def _paths_value(key: str) -> Path:
    """从 config/paths.toml 读一个路径。★ 与 model_loader._paths_root 同款极简解析器
    (只认 `key = 'value'` 单引号),故意**不 import model_loader** ——
    依赖方向是 model_loader → backend_key,反过来会成环。"""
    here = Path(__file__).resolve()
    for p in [here.parents[i] for i in range(2, 6)]:
        toml = p / "config" / "paths.toml"
        if toml.exists():
            m = re.search(rf"^\s*{re.escape(key)}\s*=\s*'([^']+)'",
                          toml.read_text(encoding="utf-8"), re.M)
            if m:
                return Path(m.group(1))
            raise BackendKeyError(
                f"paths.toml 里没有 {key} —— 拒绝猜一个路径(§11.1)。"
                f"修法:在 [state] 段登记 {key}")
    raise BackendKeyError("找不到 config/paths.toml —— 拒绝猜一个路径(§11.1)")


def key_dir() -> Path:
    return _paths_value(PATHS_KEY)


def key_file() -> Path:
    return key_dir() / KEY_FILENAME


# ── 目录 ACL ────────────────────────────────────────────────────────
#  ★ 用 PowerShell 而不是 icacls:判据要拿 **SID** 比,不拿显示名比。
#    icacls 打的是本地化的显示名(`Authenticated Users` / `Users` 在别的
#    语言版 Windows 上是另一串字),拿它做判据 = 判据的成败取决于系统语言。
#    (同款教训:ASSERTION-PITFALLS 8 —— 判据别踩编码/本地化。)
#  ★ 目录**经环境变量**传进去,不拼进脚本文本:`powershell -Command <脚本> <参数>`
#    会把后面的参数**接在脚本文本后面**当代码解析(不是填进 $args),而拼字符串
#    又要自己处理引号。环境变量两头都不用转义。
_ACL_DIR_ENV = "LOCALAI_BACKEND_KEY_DIR"

_ACL_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$d = $env:LOCALAI_BACKEND_KEY_DIR
if (-not $d) { throw '没收到密钥目录(LOCALAI_BACKEND_KEY_DIR 为空)' }
$me = ([Security.Principal.WindowsIdentity]::GetCurrent()).User
$keep = @($me.Value, 'S-1-5-32-544', 'S-1-5-18')   # 机主 / Administrators / SYSTEM
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)        # 断继承,且【不保留】继承来的
$rights = [System.Security.AccessControl.FileSystemRights]::FullControl
$inh    = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$prop   = [System.Security.AccessControl.PropagationFlags]::None
$allow  = [System.Security.AccessControl.AccessControlType]::Allow
foreach ($s in $keep) {
    $sid = New-Object Security.Principal.SecurityIdentifier($s)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $sid, $rights, $inh, $prop, $allow)))
}
Set-Acl -LiteralPath $d -AclObject $acl
# ── 施加完**回来验**:只凭 Set-Acl 没抛就宣布成功,是这个仓库反复吃过亏的形状 ──
$now = Get-Acl -LiteralPath $d
if (-not $now.AreAccessRulesProtected) { throw 'DACL 仍在继承 —— {state} 根的 Authenticated Users:(M) 会漏下来' }
$extra = @()
foreach ($r in $now.Access) {
    $sid = $r.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
    if ($keep -notcontains $sid) { $extra += ($sid + '(' + $r.IdentityReference.Value + ')') }
}
if ($extra.Count -gt 0) { throw ('DACL 里多出授权: ' + ($extra -join ', ')) }
Write-Output 'BACKEND_KEY_ACL_OK'
"""

_ACL_OK_MARK = "BACKEND_KEY_ACL_OK"


def harden_dir(d: Path) -> None:
    """把目录收敛到「只有机主 / Administrators / SYSTEM」,并**回来核验**。

    ★ 失败即抛:一个 ACL 没设成的密钥目录,与"没有这把锁"是一回事,
      而它长得像成功 —— 那正是 §12.3 禁止的静默降级。
    """
    if os.name != "nt":
        raise BackendKeyError(
            "本模块只在 Windows 上有意义:密钥的强度来自 NTFS ACL。"
            f"当前 os.name={os.name!r} —— 拒绝在没有那把锁的地方生成钥匙")
    try:
        r = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive",
             "-ExecutionPolicy", "Bypass", "-Command", _ACL_SCRIPT],
            capture_output=True, text=True, timeout=60,
            env=dict(os.environ, **{_ACL_DIR_ENV: str(d)}),
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except Exception as e:                                   # noqa: BLE001
        raise BackendKeyError(f"设密钥目录 ACL 时起不了 powershell:{type(e).__name__}: {e}") from e
    if _ACL_OK_MARK not in (r.stdout or ""):
        raise BackendKeyError(
            f"密钥目录 ACL 没设成:{d}\n"
            f"  {(r.stderr or r.stdout or '(无输出)').strip()[:400]}\n"
            f"  ★ 不带这把锁的钥匙谁都读得到,等于没有做隔离 —— 拒绝继续。")


# ── 读 / 校验 ───────────────────────────────────────────────────────
def _validate(raw: bytes, kf: Path) -> str:
    try:
        text = raw.decode("ascii")
    except UnicodeDecodeError as e:
        raise BackendKeyError(
            f"密钥文件不是 ASCII({kf})—— 它要原样进 HTTP 头,非 ASCII 会在"
            f"哪一层被怎么编码是不确定的。删掉它重起栈即可重新生成。") from e
    key = text.strip()
    if len(key) < MIN_KEY_CHARS:
        raise BackendKeyError(
            f"密钥文件内容太短({len(key)} < {MIN_KEY_CHARS} 字符):{kf}\n"
            f"  ★★ 这条不是洁癖:实测**空 key 文件会让 llama-server 完全不鉴权**"
            f"(不带 key 打 /props 回 200)—— 门会静默地重新打开。\n"
            f"  修法:删掉这个文件,重起栈会重新生成(在跑的后端要一起重启)。")
    if "\n" in key or "\r" in key:
        raise BackendKeyError(
            f"密钥文件不止一行:{kf}\n"
            f"  ★ llama-server 把**每一行**都当成一把有效的 key,而网关只会发第一份内容 ——"
            f"两边从此对不齐。删掉它重起栈。")
    return key


def load_key() -> str:
    """读并校验。★ **不创建、不改 ACL** —— 这是每次转发都要走的路径,
    它只回答「现在这把钥匙能不能用」。"""
    kf = key_file()
    try:
        raw = kf.read_bytes()
    except FileNotFoundError as e:
        raise BackendKeyError(
            f"后端密钥文件不在:{kf}\n"
            f"  ★ 起栈时会自动生成它;现在读不到,说明后端也不是这一版起的。\n"
            f"  修法:停栈重起(`90-ops\\start-stack.ps1`,或管理端的启动)。") from e
    except OSError as e:
        raise BackendKeyError(f"读不到后端密钥文件({kf}):{type(e).__name__}: {e}") from e
    return _validate(raw, kf)


def ensure_key_file() -> Path:
    """**起后端之前**调它:保证目录 ACL 对、钥匙在、且是一把能用的钥匙。返回 key 文件路径。

    ★ 已存在且合法就**原样保留** —— 不重新生成。理由:重新生成会让此刻还在跑的
      后端(可能是上一次会话留下的、被 `ModelLoader` 认领的那些)当场变成 401,
      而那件事在 `/health` 上看不见。钥匙是可再生的,但"什么时候再生"必须是人的决定。
    """
    d = key_dir()
    try:
        d.mkdir(parents=True, exist_ok=True)
    except OSError as e:
        raise BackendKeyError(f"建不了密钥目录({d}):{type(e).__name__}: {e}") from e
    harden_dir(d)

    kf = d / KEY_FILENAME
    if kf.exists() and kf.stat().st_size == 0:
        # ★ 0 字节:本模块**写不出**这种文件(下面走 os.replace 原子落盘),
        #   所以它只可能来自外部截断或一次崩掉的写。当成"从来没生成过"补上,
        #   比抛出去把栈卡死更合适 —— 而危险的那一面(空文件喂给后端)已经被
        #   下面的 `_validate` 挡住了,这里补生成不会放过它。
        kf.unlink()
    if not kf.exists():
        tmp = d / (KEY_FILENAME + ".tmp")
        tmp.write_bytes(_sec.token_hex(KEY_BYTES).encode("ascii"))
        os.replace(tmp, kf)                                  # ★ 原子:不会留下半份

    _validate(kf.read_bytes(), kf)                           # ★ 生成完也要过一遍同一条校验
    return kf


def auth_header() -> dict:
    """出站转发要带的头。★ 取不到就抛 —— 调用方不许"取不到就不带头继续发"。"""
    return {"Authorization": f"Bearer {load_key()}"}


# ── CLI(给 PowerShell 用;实现只有这一份)────────────────────────────
def _main(argv: list) -> int:
    cmd = argv[1] if len(argv) > 1 else "ensure"
    try:
        if cmd == "ensure":
            print(str(ensure_key_file()))
            return 0
        if cmd == "check":
            load_key()
            print(str(key_file()))
            return 0
        if cmd == "probe-path":                              # 就绪探测要打的路径
            print(AUTH_PROBE_PATH)
            return 0
    except BackendKeyError as e:
        print(str(e), file=sys.stderr)
        return 2
    print(f"未知子命令:{cmd}(可用:ensure / check / probe-path)", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(_main(sys.argv))
