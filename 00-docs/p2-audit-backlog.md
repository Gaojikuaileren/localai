# P2 收尾审查 · 未修项 backlog

> 2026-07-28 · 对抗性审查(4 维并行 + 逐条核验)共 **57 条**发现。
> **确认严重的 11 条已全部修复**(见 worklog 与 git log)。本文件记**未修的**,以免丢失。
> 每条都标了为什么当时没修 —— 不是遗忘,是排序。

## 已修(不在本 backlog,列此以便对照)

- [high] `caller_identity.py` — _tcp_rows() 返回的 ctypes 数组指向【已释放】的内存(use-after-free)—— D30 身份判定建立在读野指针上。实测已复现读出垃圾 PID。
- [medium] `caller_identity.py` — IPv6 回环(::1)完全绕过 D30 调用方身份校验:classify_caller 放行 ::1,但 resolve_peer_pid 只查 AF_INET(IPv4)表,永远查不到 → fail-open 成 t
- [high] `gateway.py` — 流式路径的异常全部逃出 try:后端未起时返回 200 + 空 body(实测),而不是 503 —— 正好是 §8.1.4 明令禁止的「静默降级」。上游状态码也被整个丢弃。
- [high] `gateway.py` — E1 只扫「最后一条 user 消息的 type=='text' 部分」,凭证放在 system 消息、上一轮 user 消息、或无 type 的 content part 里全部原样转发给后端(三种实测均已确认绕过)。
- [high] `e1_detector.py` — high_entropy 对普通长标识符/文件路径/URL 路径全部误报(实测 6/6 命中),而 Open WebUI 无法发 X-LocalAI-E1-Override,导致误报变成【无法解除的硬拦截】。
- [high] `install-postgres.ps1` — 重跑 install-postgres.ps1 会重置 ai-mem 密码但只同步 pg-mem,把 Qdrant/Qdrant-s2/Embedding 的存储凭据全部作废
- [high] `install-openwebui.ps1` — 第 88 行没传 --host,Open WebUI 绑 0.0.0.0:8081 暴露到局域网,且 ENABLE_SIGNUP 默认开着
- [high] `install-embedding.ps1` — Embedding 服务加载的代码/venv/模型三处都对 ai-exec/ai-asset 可写,构成一跳打穿 D30 的提权路径;第 105-108 行的 RX 授权并不能收紧任何东西
- [medium] `install-postgres.ps1` — 第 161-171 行无条件整体覆写 pg_hba.conf / pg_ident.conf,会抹掉 apply-schema.ps1 追加的 ai_mem_local / ai_mem_remote 映射
- [medium] `apply-schema.ps1` — Invoke-AsAiMem 的等待循环和 Start-ScheduledTask 抢跑:可能在任务还没进入 Running 就退出,拿到 267011 并把仍在跑的任务拆掉
- [low] `install-qdrant.ps1` — 第 143 行(及 install-embedding.ps1 第 118 行)在同步密码后无谓 Restart-Service 活库,且用 -EA SilentlyContinue 吞掉启动失败

---

## 未修 · 按维度

### 网关代码（9）

**[medium] 除 ConnectError 外的 httpx 异常全部裸奔成 500:ConnectTimeout/ReadTimeout/RemoteProtocolError 都不是 ConnectError 的子类;上游返回非 JSON 或空 body 时 r.json() 也直接 500。**

- 影响:实测 `issubclass(httpx.ConnectTimeout, httpx.ConnectError) == False`(它走的是 TimeoutException → TransportError 分支),ReadTimeout、RemoteProtocolError 同样不是。`httpx.Timeout(300.0, connect=5.0)` 明确配了 5s 连接超时和 300s 读超时,也就是说这两条**被显式配置出来的**失败路径一条都没被接住:llama-server 正在加载模型/被防火墙 DROP → ConnectTimeout → 500 Internal Server Error 裸奔;推理超过 300s → ReadTimeout → 500;后端中途崩掉连接 → RemoteProtocolError → 500。
另外 `data = r.json
- 修法:把 503 分支抽成函数,扩到整个 TransportError 家族,并保护 json 解析:
```python
except (httpx.ConnectError, httpx.ConnectTimeout, httpx.TransportError) as ex:
    return _backend_unavailable(alias, backend, entry, detail=type(ex).__name__)
```
(`TransportError` 是 ConnectError/TimeoutException/RemoteProtocolError 的共同基类,一条就够;保留具体类型名进 detail 便于排障。)
非流式响应体:
```python
r = await _client.post(upstream_url, json=fwd)
try:
   

**[medium] 身份解析全程是同步阻塞调用,却直接跑在 async 端点里:WMI 子进程实测 ~250ms(超时上限 6s)会冻结整个事件循环,连带卡住所有在途流式响应。**

- 影响:`classify_caller` → `account_from_request` → `_owner_via_wmi` → `subprocess.run(powershell..., timeout=6)` 全是同步的,而 `chat_completions` 是 `async def`,没有 `run_in_threadpool`。实测 `_owner_via_wmi(self)` 耗时 253ms,查一个不存在的 PID 也要 200ms。README 自己指出:跨用户时令牌快路径**必然失败**,所以只要网关(ai-mem)和调用方(人类账户的 Open WebUI)不同账户,**每个新 PID、每 15 秒缓存过期都要走一次 WMI**。这 250ms 里整个 uvicorn 事件循环停转:其他请求排队,正在流式输出的对话卡顿。PowerShell 启动一旦被杀毒扫描拖慢或挂
- 修法:把三类阻塞工作移出事件循环:
```python
from starlette.concurrency import run_in_threadpool

caller = await run_in_threadpool(classify_caller, request)
...
e1r = await run_in_threadpool(e1.scan, _scannable_text(body))
...
await run_in_threadpool(log_gate_rejection, session_id, e1r.categories, outcome)
```
另外把 `_owner_via_wmi` 的 `timeout=6` 降到 2s(GetOwner 正常 250ms,6s 只会放大停顿),并加请求体大小上限(超过 N KB 直接 413,或只扫前 N KB 并在响

**[medium] 三种正常客户端会发的请求体让网关 500 崩:content part 里 text 为 null、messages 不是数组、请求体不是合法 JSON。**

- 影响:实测(TestClient,raise_server_exceptions=False)三条全部返回 `500 Internal Server Error`:
1. `{"role":"user","content":[{"type":"text","text":null}]}` → `_last_user_text` 的 `" ".join(p.get("text", ""))` 抛 `TypeError: sequence item 0: expected str instance, NoneType found`。`p.get("text", "")` 的默认值只在 key 缺失时生效,key 存在但值为 null 时拿到的是 None,多模态客户端发 null 很常见。
2. `"messages": 5` → `reversed(5)` 抛 `TypeError: 'int' o
- 修法:在 `_last_user_text`/`_scannable_text` 里全程做类型校验(见上一条的 `isinstance(p.get("text"), str)` 写法,同时解决 null 和缺失),`messages` 非 list 直接当空处理;请求体解析加保护并返回 OpenAI 形状的 400:
```python
try:
    body = await request.json()
except Exception:
    return JSONResponse(status_code=400, content={"error": {
        "message": "请求体不是合法 JSON", "type": "invalid_request_error"}})
if not isinstance(body, dict):
    return JSONR

**[medium] _owner_via_wmi 用 PATH 查找 powershell,而它正是 D30 身份判定的唯一权威来源 —— PATH 劫持可同时伪造身份并在网关账户下拿到代码执行。**

- 影响:`subprocess.run(["powershell", ...])` 不带绝对路径,Windows 会依次搜当前目录(视 shell 而定)和 PATH。README 的威胁模型明确是「ai-asset 跑 ComfyUI 第三方节点、可能被投毒」,D31 也专门规定「秘密不得放进 10-core、服务读代码走 RX 授权」——同样的谨慎必须适用于**可执行文件的解析路径**。如果 PATH 里存在任何 ai-asset/ai-exec 可写的目录且排在 System32 之前(第三方工具装 Python/Node/CUDA 时把 `%LOCALAPPDATA%` 下的目录塞进 PATH 是常态),被投毒的账户就能同时做到两件事:(a) 让 GetOwner 返回伪造的 owner,把自己伪装成人类账户从而通过 D30 检查;(b) 在网关进程的账户上下文里执行任意代码。实测 `sh
- 修法:1. 用绝对路径调用,从环境变量取根目录以避开 §11.1 的硬编码盘符钩子:
```python
import os
_PWSH = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                     "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
```
启动时校验它存在,不存在就让身份解析走 fail-closed 而不是静默降级。更好的做法是彻底不用子进程:直接经 `comtypes`/`win32com` 调 WMI,或用 `NtQueryInformationProcess` + `OpenProcessToken`(以 SYSTEM 运行的辅助服务),彻底消掉 PATH 面和 250ms 开销。
2. 不缓存失败:`i

**[medium] 两个安全审计写入(gate_rejection / denied_access)用裸 except 吞掉所有异常,写不进去时完全无声;且 fallback 目录指向 10-core 代码树内(与 D31 的 RX 授权冲突)。**

- 影响:`log_gate_rejection` 和 `log_denied_access` 都是 `except Exception: pass`。§6.8 要求隔离账户触达网关必须写审计,D30 的整个拒绝链靠这条记录来事后追溯——但只要 `D:\AI\state\logs` 的 ACL 不给网关账户(ai-mem)写权、或磁盘满、或文件被独占,**每一次 ai-asset 的越权尝试都会被静默丢弃**,而请求照样返回 403,从外部完全看不出审计已经全线失效。安全控制可以失败,但不能无声地失败。
配套问题:`_logs_dir()` 读不到 `paths.toml` 的 `[state] logs` 时回退到 `Path(__file__).with_name("_logs")`,也就是 `10-core/gateway/_logs/`。D31 规定 ai-mem 对 10-core 只有 
- 修法:审计失败必须有声:
```python
import logging
_log = logging.getLogger("localai.gateway.audit")
...
except Exception as ex:
    _log.error("AUDIT WRITE FAILED (%s): %s", d, ex)   # 至少进 uvicorn stderr
```
拦截本身仍然不因审计失败而放行(现有取舍是对的),但要留下痕迹。
`_logs_dir()` 的 fallback 改为不落在代码树里:优先 `%LOCALAPPDATA%\localai-hub\logs`,并且在读不到 paths.toml 时 `_log.error` 一次,而不是无声退化。启动时做一次可写性自检(创建目录 + 写一行 `{"ts":...,"event":"audit_start"}`)

**[medium] id_doc 正则对大写十六进制片段和「字母+9位数字」误报(实测均命中),而该类别在 E3_CATEGORIES 里,会真的拦截并将来阻断记忆写入。**

- 影响:第二条分支 `\b[CFGHJKLMNPRTVWXYZ][0-9A-Z]{8}[0-9]\b` 只要求 10 位大写字母数字混合,没有任何校验和(注释自己标注为「近似」)。实测:`hash 前缀 F0A1B2C3D4 请对照` → `['id_doc']`;`订单 P200000000 已发货` → `['id_doc']`。前者是截断的大写哈希,后者是「字母+9位数字」的订单号/编号——两者在本项目日常对话里都会出现。
和 high_entropy 不同的是,`id_doc` **在 E3_CATEGORIES 里**(`E3_CATEGORIES = 除 high_entropy 外全部`),所以它不仅在 E1 拦截,将来接上 Memory Gate 后还会成为拒绝写入记忆的理由。e1_detector 的模块文档专门论证了「带校验和的类别才用来拦,否则会训练用户一律点继续」,但 id
- 修法:两条路选一:
(a) 收紧:第二条分支要求字母数字**交替出现**(真实德国身份证序列号是字母数字混合而非连续 8 位同类),并排除纯 `[0-9A-F]` 组成的 token(消掉大写哈希),同时要求前后有身份证/护照语境词(`Ausweis|Reisepass|Personalausweis|护照|身份证|证件号`)才判定——对「最保守、无字段级校验」的类别加语境门是标准做法。
(b) 降级:既然没有校验和,就把 `id_doc` 挪出 `E3_CATEGORIES`,和 high_entropy 同等对待(只提示不拦、不阻断记忆写入),直到能拿到真正的校验规则。
无论哪种,把 `F0A1B2C3D4` 和 `P200000000` 加进 test_e1.py 反例区。

**[low] _tcp_rows 的两次 GetExtendedTcpTable 调用之间存在竞态:第一次的返回值被忽略,第二次遇到 ERROR_INSUFFICIENT_BUFFER 不重试,直接返回空表 → 身份解析 fail-open。**

- 影响:标准的「先问大小、再取数据」两段式调用必须循环重试:两次调用之间连接表可能增长(本机实测当前有 146~161 行,且 TIME_WAIT 行数分钟级波动),此时第二次调用返回 ERROR_INSUFFICIENT_BUFFER(122)而不是 NO_ERROR,代码走 `return []`。结果是 `resolve_peer_pid` 返回 None → `classify_caller` fail-open 成 trusted-local。也就是说,**网络活动越繁忙,D30 检查越容易被跳过**,而且同样是无声的(fail-open 路径不记账)。等记忆端点改成 fail-closed 后,这会变成随机的请求失败。
- 修法:合并进 blocker 那条的重写(已给出完整代码):循环最多 5 次,只有 `rc == NO_ERROR` 才解析,`rc == ERROR_INSUFFICIENT_BUFFER(122)` 时用新的 size 重新分配再试,其他错误码才放弃。另外分配时给一点余量(`size.value + 4096`)减少重试次数。

**[low] 模块级 httpx.AsyncClient 从不关闭:没有 lifespan/shutdown 钩子,实测 app 上没有注册任何 shutdown handler。**

- 影响:`_client = httpx.AsyncClient(...)` 在 import 时创建,`gateway.app.router.on_shutdown` 实测为空。uvicorn 退出时连接池里的 keep-alive 连接不会优雅关闭,会在退出路径上留下 `Unclosed client session` 类告警,并且在 `--reload` 开发模式下每次重载都泄漏一个连接池。当前单实例长驻场景影响有限,所以定 low;但它同时挡住了一件将来要做的事——一旦 `escalate.cloud` 出境闸门上线,需要为云端 provider 用**另一个**配置不同(超时、代理、证书)的 client,那时没有生命周期管理会很麻烦。
- 修法:改用 lifespan,顺便让 client 的创建发生在事件循环内:
```python
from contextlib import asynccontextmanager

@asynccontextmanager
async def lifespan(app: FastAPI):
    global _client
    _client = httpx.AsyncClient(timeout=httpx.Timeout(300.0, connect=5.0))
    try:
        yield
    finally:
        await _client.aclose()

app = FastAPI(title="LocalAI Hub Gateway", version="0.1.0-p2", lifespan=lifespan)
```

**[low] 第 82 行的「None 安全」测试恒为真、永远不会失败,却计入 README 宣称的「41 例」。**

- 影响:`check("None 安全", not scan(None).blocked if False else True)` —— 条件表达式的 `if False` 让它无条件求值为 `True`,`scan(None)` 根本不会被调用。这条 check 无论 `scan` 怎么改都会 PASS。实际上 `scan(None)` 是安全的(`if not text: return E1Result()` 挡住了),所以被测行为没问题,但**测试本身是假的**,而 gateway 确实可能给它传 None(`_last_user_text` 在异常路径下的返回值)。项目的诚实原则明确要求「未实测的不得说成已验证」,一条结构上不可能失败的断言计进通过数,正好违反这一条。
- 修法:改成真的断言,并补上 gateway 实际会传进来的几种非法类型:
```python
check("None 安全", not scan(None).blocked)
check("空串安全", not scan("").blocked)
```
同时全仓 grep 一下有没有别的 `if False else True` / 恒真断言(这类写法通常成对出现),再核对 README 里的用例计数。

### 安装脚本（11）

**[medium] 安全模型所依赖的 icacls / nssm native 命令一律不查退出码,失败也照打绿勾**

- 影响:第 80-83 行三条 icacls 全部 `| Out-Null`,第 84 行无条件 `Write-Host "✓ ACL 已设"`。D22 说 NTFS ACL 是主要保护层,那么「ACL 可能没设上但报告说设上了」正是诚实原则要禁的那类声称。install-postgres.ps1 第 129 行同病。install-qdrant.ps1 Install-QdrantSvc(148-157 行)七条 `& $Nssm set` 无一检查;install-embedding.ps1 第 122-130 行同样:若 nssm remove 后服务处于 marked-for-deletion,紧接着的 nssm install 失败,`Get-WmiObject ... Name='Embedding'` 返回 $null,脚本以「设服务账户失败 RV=」退出,而原来那个能用的服务已经被
- 修法:每条 icacls/nssm 之后 `if ($LASTEXITCODE -ne 0) { throw ... }`;nssm remove 之后轮询 `Get-Service $Name -EA SilentlyContinue` 直到真正消失(带超时)再 install;install 之后先断言 `Get-Service $Name` 存在再去 WMI 设账户。

**[medium] 阶段 4 的环境变量配置只在首次启动生效,「幂等」的说法不成立**

- 影响:已读实装源码 config.py:3186 `ENABLE_PERSISTENT_CONFIG` 默认 True,而 `openai.api_base_urls`(2795)、`ui.enable_signup`(3043)、`rag.embedding_engine`(2875)都是 PersistentConfig 项:首启写进数据库,之后每次启动**数据库值覆盖环境变量**。所以以后改了网关端口再重跑这个脚本,第 75 行的 OPENAI_API_BASE_URL 完全不起作用,而脚本仍然打印「指向网关 :8080」,人会以为改上了。
- 修法:要么加 `$env:ENABLE_PERSISTENT_CONFIG = 'false'` 让环境变量始终是权威源;要么在脚本里写明阶段 4 只配置首启,后续变更必须在 UI 的 Admin Settings 里改,并把这条写进 00-docs。

**[medium] 只设了 OPENAI_API_BASE_URL,RAG/STT/TTS/图像四条子链路仍默认指向 api.openai.com,且 RAG 用本地 MiniLM 而不是刚建好的 bge-m3**

- 影响:config.py 第 313-337 行取环境变量拼出 OPENAI_API_BASE_URLS(聊天走这个,是对的),但第 357 行紧接着把标量 `OPENAI_API_BASE_URL` 无条件重置回 'https://api.openai.com/v1';随后 RAG_OPENAI_API_BASE_URL(1080)、IMAGES_OPENAI_API_BASE_URL(1464)、AUDIO_STT_OPENAI_API_BASE_URL(1539)、AUDIO_TTS_OPENAI_API_BASE_URL(1580)都以这个标量为默认。目前这几条默认关着,属于潜伏问题:在 UI 里一勾就把对话内容直接发往 OpenAI,绕过网关、绕过 E1、不进审计。另外 config.py:984 `RAG_EMBEDDING_ENGINE` 默认 '' = 本地 SentenceTr
- 修法:补上 `$env:RAG_OPENAI_API_BASE_URL`、`$env:AUDIO_STT_OPENAI_API_BASE_URL`、`$env:AUDIO_TTS_OPENAI_API_BASE_URL`、`$env:IMAGES_OPENAI_API_BASE_URL` 全部指向 http://127.0.0.1:8080/v1;再设 `$env:RAG_EMBEDDING_ENGINE='openai'` + `$env:RAG_EMBEDDING_MODEL='bge-m3'` 让检索经网关走到 bge-m3。

**[medium] 第 87 行 Push-Location 到 venv\Scripts,导致 JWT 签名密钥 .webui_secret_key 被写进 venv,且该目录对 ai-exec/ai-asset 可写**

- 影响:open_webui/__init__.py 第 13 行 `KEY_FILE = Path.cwd() / '.webui_secret_key'` 按 CWD 计算,第 87 行把 CWD 设成 D:\AI\venvs\openwebui\Scripts。实测该路径继承 Authenticated Users: Modify → ai-exec/ai-asset 能读到签名密钥并伪造 admin JWT;它也不在 $DataDir 内,第 68 行注释说的「纳入备份」覆盖不到它;而且换个 CWD 跑一次就重新生成,所有会话失效。
- 修法:改 `Push-Location $DataDir`(或显式设 `$env:WEBUI_SECRET_KEY_FILE`),启动后对该文件 `icacls <key> /inheritance:r /grant "<你的账户>:F" "Administrators:F" "SYSTEM:F"`。

**[medium] 阶段 4 自测跑在管理员身份下,阶段 [7] 服务却以 ai-mem 跑;自测通过不代表服务能起来,而阶段 8 唯一的失败线索指向一个可能写不出来的日志文件**

- 影响:第 81-86 行 `Push-Location $SvcDir; & $VPy embedding_service.py --selftest` 用的是当前交互式管理员身份;第 [7] 步注册的服务用 .\ai-mem。两者在 torch 加载、HF_HOME 访问、$LogDir 写入上的权限完全不同,自测绿灯对服务能否起来零信息量。第 142 行失败时让人去看 $LogDir\embedding.err.log —— 但那个文件是 NSSM 以 ai-mem 身份创建的,ai-mem 今天能写 D:\AI\state\logs 纯粹因为继承了 Authenticated Users: Modify(实测),而这正是上面第 3 条建议要拿掉的东西。本机目前 Embedding 服务尚不存在,说明阶段 5 从未跑通过,这段代码是未实测的。
- 修法:阶段 5 里显式 `icacls $LogDir /grant "ai-mem:(OI)(CI)M"`,不要靠继承;Start-Service 之后除了 /health 还要断言 `(Get-WmiObject Win32_Service -Filter "Name='Embedding'").StartName -eq '.\ai-mem'`,并打一次真实 /embeddings 请求确认返回 1024 维 —— 用服务身份测,而不是用管理员自测的结果代替。

**[medium] 三处服务同步循环都用字符串字面量 '.\\ai-mem' 匹配 StartName,一旦有服务被手工修过就会永久漏掉**

- 影响:install-qdrant.ps1:142、apply-schema.ps1:87、install-embedding.ps1:115 都是 `$_.StartName -eq '.\ai-mem'`。SCM 原样保存你填的字符串:通过 services.msc 修一次(会填成 HONGKONGPINGPON\ai-mem)或用 `sc config obj=` 指定别的写法,该服务就从此退出所有同步循环,下次密码重置后在重启时 1069,而排查方向会完全指向别处。
- 修法:把 StartName 解析成 SID 再比:`try { ([System.Security.Principal.NTAccount]($_.StartName -replace '^\.\\', "$env:COMPUTERNAME\\")).Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $aiMemSid } catch { $false }`。

**[low] 第 69 行 $DataDir 由 (Get-Path 'db')\..\openwebui 推导,今天结果正确但会随 [state] db 键静默漂移**

- 影响:Join-Path 'D:\AI\state\db' '..\openwebui' 经 GetFullPath 得到 D:\AI\state\openwebui,当前是对的。但它把 WebUI 数据目录的位置绑死在「db 必须直接位于 state 下」这个未写下来的假设上:哪天把 db 改成 D:\AI\state\db\pg,DATA_DIR 就变成 D:\AI\state\db\openwebui,已有的 SQLite 库和全部账户被孤立,而 Open WebUI 会安静地建一个空库让你重新注册 admin。§11.1 的本意是「唯一路径配置源」,靠相对跳转推导等于绕开了这个契约。
- 修法:在 config/paths.toml 的 [state] 段加 `openwebui = 'D:\AI\state\openwebui'`,脚本改成 `$DataDir = Get-Path 'openwebui'`。

**[low] 「$pw = $null; [GC]::Collect() → 明文密码只活在 LSA 里了」是过度声称**

- 影响:第 222 行(install-qdrant.ps1:162、apply-schema.ps1:139、install-embedding.ps1:135 同样)。.NET string 不可变,ConvertTo-SecureString -AsPlainText、字符串拼接、WMI marshaling 都会留下副本;GC 回收只是把内存标为可用,不清零。明文会在进程堆里存活到被无关分配覆盖,可能进崩溃转储或页面文件。命令行/日志/历史那几条防护做得是对的(Register-ScheduledTask -Password、WMI Change 都是进程内传参,Win32_Process.CommandLine 确实看不到),不必因为这条否定它们 —— 但注释按现在的写法会让人以为内存里也清干净了。
- 修法:把注释改成事实描述(如「密码已交给 LSA;进程内明文副本无法确定性擦除,故本脚本不做无人值守长驻」);或用 char[] 生成、只在 WMI 调用点转成 string,并在 finally 里 Array.Clear。

**[low] 日志文件同时被 Add-Content(ANSI/GBK)和 Tee-Object(UTF-8)写入,失败时又用无 -Encoding 的 Get-Content 读回**

- 影响:本机实测:PS 5.1 下 Add-Content 默认写 ANSI(中文 = D6 D0 CE C4 …GBK),Tee-Object -FilePath 新建文件时写 UTF-8 带 BOM。install-embedding.ps1 的 Say(第 32 行)与各步的 Tee-Object 写的是同一个 $Log,于是文件里两种编码混排;第 57/62/76/85 行失败路径又用裸 `Get-Content $Log -Tail 15` 按 ANSI 读回,traceback 或 Windows 错误串里的非 ASCII 正好在你最需要看懂它的时候变成乱码。install-openwebui.ps1 第 53 行同病。apply-schema.ps1 第 128/130 行已经为 verify.log 修过这个坑(显式 -Encoding UTF8),但 schema.log / r
- 修法:Say 里改 `Add-Content $Log $line -Encoding UTF8`,所有 `Get-Content $Log -Tail N` 加 `-Encoding UTF8`(含 apply-schema 的 107/116 行);脚本开头显式设 `[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)` 让 native 输出的解码可复现。

**[low] 第 199-200 行的鉴权核验在服务不可达时会打出绿色的「✓ 无 key 被拒(HTTP 0)」**

- 影响:catch 块取 `[int]$_.Exception.Response.StatusCode.value__`,连接被拒时 Response 为 $null,[int]$null = 0,于是打印「✓ 无 key 被拒(HTTP 0)」并标绿。这是把「服务根本没起来」误报成「鉴权生效」,而这一行输出正是要贴回来当作实测证据的 —— 会污染证据链。
- 修法:取到状态码后断言 `$code -in 401,403`,否则红字报「核验不可信:未拿到 401/403(code=$code)」并 exit 1。

**[nit] 第 52 行注释「不加入任何组(默认就不在 Users)—— 最小权限」与实际不符**

- 影响:实测本机 BUILTIN\Users 的成员里含 `NT AUTHORITY\Authenticated Users`,所以三个 ai-* 账户登录后令牌里**是**带 BUILTIN\Users 的(这也正是它们能读 C:\Windows\System32、服务能跑起来的原因)。真实的隔离边界完全落在 memory 目录那两条显式 Deny ACE 上(这两条实测确认是 Deny FullControl,有效),而不是「不在任何组」。注释按现在的写法,会让下一个推理隔离边界的人得出错误结论,并且解释不了为什么仓库和 D:\AI\* 对这三个账户可写。
- 修法:把注释改成:「不显式加组;但 BUILTIN\Users 嵌套了 Authenticated Users,故三账户仍传递性拥有 Users 权限。隔离依赖显式 Deny ACE,凡需隔离的目录都必须显式 deny,不能靠『没授予』。」

### 安全姿态（8）

**[medium] require_trusted_local(gateway.py:112)在生产路径上零调用点 —— 全仓库仅 test_caller_policy.py 的 4 条断言引用它。D30「记忆敏感路径 fail-closed」当前生效的路径数是 0。**

- 影响:如实说:这不是隐瞒 —— gateway.py:99-100、README.md:70、DECISIONS.md:888 三处都标注了「留给将来记忆代理端点」,诚实性这一关过了。但「测试 11 项全过」容易被读成「fail-closed 已落地」,实际它是一段未接线的代码。真正的风险在默认方向:网关的认证写在 handler 函数体内,新增端点默认继承的是 classify_caller 的 fail-open 语义;接线全靠「记得改」这个人工约定,没有任何机制会在忘记时报错。
- 修法:把默认反过来:做成 FastAPI 依赖 —— 新端点默认 `Depends(require_trusted_local)`(fail-closed),chat 路径显式声明 `Depends(allow_unidentified_local)` 作为白名单例外。再在 test_caller_policy.py 加一条元测试:遍历 `gateway.app.routes`,断言除显式白名单(/health、/v1/models、/v1/chat/completions)外每条路由都挂了 fail-closed 依赖 —— 这样「忘记接线」会变成红色测试而不是沉默。同时在 STATE.md 里把这项从「已实装」措辞改为「已实现但尚无调用点」。

**[medium] E1 只扫最后一条 user 消息(gateway.py:65-77,183),而给出的理由「历史进来时已扫过」对一个无状态网关 + 第三方前端不成立。system / assistant 角色的内容永不被扫。**

- 影响:messages 数组完全由客户端构造。具体绕过:① 凭证放在 `role: "system"` 或 `role: "assistant"` 里 —— Open WebUI 的 RAG/文件附件与自定义系统提示走的正是这条,永不过 E1;② Open WebUI 支持导入会话,整段历史从未经过网关;③ 任何本机进程直接构造 messages。今天的爆炸半径有限(后端全是本机 llama-server,D22 下内容不出机),但 registry.toml:50-54 已列 escalate.cloud(egress_gate=true),一旦出境闸门挂到同一条 /v1/chat/completions,未扫的历史就是直接外发。另外 x-localai-e1-override 是纯客户端头:任何本机调用方一律带上就永久关掉 E1,而设计文案假设它由「用户点了按钮」产生;Open WebUI 
- 修法:把 `_last_user_text(messages)` 换成扫全数组(含 system/assistant)的 `_all_text(messages)`;性能用「已扫过消息的内容哈希集合」缓存(哈希整条消息、不哈希凭证片段,不违 §6.9.8)。删掉 gateway.py:66 那句错误的理由,改写为实情。把「E1 覆盖全部 messages」定为 escalate.cloud / 出境闸门落地的前置硬门。override 改为一次性 nonce(网关拦截时下发,前端回传)而非静态头值。

**[medium] 后端 llama-server 无任何鉴权,网关不是咽喉:README.md:87 与 gateway.py:14 的启动命令都不带 --api-key,全仓库 grep `--api-key` 零命中。llama-server 绑 127.0.0.1 → 按 D30 §3 自己论证的 Windows loopback 事实,对 ai-asset/ai-exec 完全开放。**

- 影响:D30 花大力气做的「拒隔离账户」只锁了网关这一扇门,模型平面另有一扇没锁的门。ai-asset 可直连 :18081/:18082 无限使用本机模型(算力盗用),也可以直接把凭证喂进模型 —— 完全绕过 E1、调用方身份校验与全部审计,网关侧什么都看不到。这不构成记忆泄露(模型不持有记忆),所以不是 blocker;但它使「E1 在网关侧做,不信任前端」这条保证只对自愿走网关的客户端成立,而文档没有标注这个边界。
- 修法:llama-server 启动加 `--api-key <随机值>`,key 存 {state}/memory(强 ACL)由网关读取注入 —— 与 Qdrant api_key 同一套模型。若认为 P2 阶段不值当,至少在 README「明确未实装」表格与 STATE.md 里如实加一行:「模型后端未鉴权;E1/身份校验/审计只覆盖经网关的流量,本机任意账户可直连后端绕过」。

**[medium] GET /health(gateway.py:140)与 GET /v1/models(gateway.py:145)完全不调用 classify_caller —— 既不拒隔离账户,也不走 D28 的远程 401 分支。**

- 影响:今天网关只绑 127.0.0.1,远程到不了,所以不是当下可利用漏洞。但 D28 明确规划远程入口(chat 里的 401 分支已经写好),一旦网关开始监听 tailnet 接口,这两条会向【未认证的远程调用方】暴露完整别名清单 + 契约 —— 等于告诉外面这台机器装了哪些模型、哪些能力档位。结构性问题与上一条同源:认证写在各 handler 体内,新端点默认无认证,而不是默认拒绝。
- 修法:与 require_trusted_local 那条合并解决:上中间件或路由级依赖做默认拒绝。/health 若确需匿名可达(健康探针),显式白名单并把响应收窄为 `{"status":"ok"}`,不回别名清单;/v1/models 归入需身份校验的一组。

**[medium] 审计落盘的 fallback(gateway.py:37-43)在读不到 config/paths.toml 时静默把 gate_rejection.jsonl / denied_access.jsonl 写进 git 工作树内的 10-core/gateway/_logs。触发条件真实可达:gateway.py:34 运行时要读 <repo>/config/paths.toml,而 D31 的 RX 授权(install-embedding.ps1:105-108 的模式)只覆盖 10-core 下的服务代码子目录,没有 config/。**

- 影响:网关一旦从「以管理员手动跑」转成 ai-mem 服务(D31 正是为此写的),读 paths.toml 就可能失败,然后【静默】降级 —— 审计照写,只是写错地方,没有任何告警。后果有两层:审计数据落进 git 工作树,而 .gitignore 既不含 `*.jsonl` 也不含 `_logs/`,一次 `git add -A` 就把凭证命中时间线提交进版本历史(不可撤销地扩散);且按 D31 该目录对 ai-asset 可读。更根本的是「审计目录解析不出来还继续跑」这个 fail-open 姿态本身 —— 无审计的运行应当是响亮的失败。
- 修法:① paths.toml 读失败改为启动即 exit(或至少 log 到 stderr 并拒绝服务),删掉静默 fallback;② 把 config/ 加进 D31 的 ai-mem RX 授权清单,并在 gateway 的 install 脚本里落实;③ 兜底把 `_logs/` 与 `*.jsonl` 加进 .gitignore(注意 .gitignore 现在只有 `*.log`,jsonl 不在内)。

**[low] roles.sql:51-54 的 ALTER DEFAULT PRIVILEGES 全部限定 `FOR ROLE mem_rw`,但 roles.sql 本身由 apply-schema.ps1:113 以 postgres 应用,而 postgres 超级用户按上面第 3 条对任何 ai-mem 进程都是现成可用的。**

- 影响:以 postgres 做的任何 schema 修补,新建的【函数】会拿到 PostgreSQL 默认的 `PUBLIC EXECUTE` —— 正是 roles.sql:23-27 那段注释刚吃过一次亏的坑(REVOKE ... FROM ai_mem_remote 撤不掉 PUBLIC 那条),只是换了个创建者身份。届时 ai_mem_remote 又能执行本不该执行的函数。这是个「同一个教训的第二个入口」,现在补成本极低。
- 修法:在 roles.sql 补 `ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA mem REVOKE ALL ON FUNCTIONS FROM PUBLIC;`(表同理)。更稳的做法是在 verify.sql 加一条持续断言而不是靠默认权限:`SELECT count(*) FROM information_schema.routine_privileges WHERE specific_schema='mem' AND grantee='PUBLIC'` 期望 0,`SELECT ... FROM information_schema.table_privileges WHERE table_schema='mem' AND grantee='PUBLIC'` 期望 0 —— 这样无论谁建的对象都能抓到。

**[low] 流式分支(gateway.py:231-240)的 try/except 包不住错误:`return StreamingResponse(gen(), ...)` 立即返回,gen() 的 httpx.ConnectError 在生成器首次迭代时才抛出,已经逃出 except,于是后端未起时流式请求不会得到 §8.1.4 要求的「503 带缺口」,而是中途异常/空流。同时 `aiter_raw()` 直通,出站方向没有任何检查点。**

- 影响:§8.1.4「不静默降级」在流式路径上没有兑现 —— 而流式是聊天的默认模式,非流式反而是少数情况;也就是说这条被实测覆盖的错误处理,在最常用的路径上是失效的(README.md:36-37 的实测记录用的是非流式)。安全相关的部分:§4.6 出境闸门将来要挂在响应方向,当前 aiter_raw 直通不给任何插入点,等于流式路径天生绕过出境检查。
- 修法:把 `_client.stream(...)` 的连接建立提到生成器之外(或在 gen() 内对首个 chunk 单独 try,失败时改走 JSONResponse 503),使流式与非流式的错误语义一致;并在流式分支预留出站过滤钩子(逐 chunk 交给一个可插拔的 egress filter),免得 §4.6 落地时发现只能重写这段。

**[nit] 「有没有秘密进 10-core 或 git 跟踪文件」这一项 —— 查完,没有,D23/D31 这条通过,记录在此以免将来重复排查。**

- 影响:`git grep -iE "api[_-]?key|password|secret|dsn|token|bearer"` 覆盖 10-core / config / 90-ops,命中的全是标识符、注释和正则模式,无一处是值。Qdrant api_key 由 install-qdrant.ps1:131 运行时 New-Secret 32 生成、直接写 {state}/memory 下的 config.yaml(仓库外、强 ACL);PG 走 SSPI 无 DB 口令;ai-mem 账户密码只活在脚本进程内存里,经 Set-LocalUser / WMI Change 配进 LSA,不过命令行,脚本还统一 Set-PSReadLineOption -HistorySaveStyle SaveNothing。test_e1.py 里的 hunter2Xy / Tr0ub4dor3 / DE
- 修法:无需修改。建议把这条排查结论写进 D31 正文(「2026-07-28 全仓库核验:无秘密进入 10-core / git,核验方式为 …」),并考虑加一个 pre-commit 规则扫 jsonl / 高熵串,把这项从「每次人工排查」变成自动化 —— 现有 pre-commit 只查绝对路径。

### 文档一致性（6）

**[medium] STATE.md contains three mutually contradictory P1 status blocks and a stale header/next-step section; most dangerously line 219 declares B3b/A3/C3/E5/VLM results void and needing retest, while line 153 says 17/17 done and registry.toml binds assistant.deep to exactly those voided numbers.**

- 影响:In one committed file: 当前阶段 (line 21) = "P0 · 地基"; 进行中 (line 61) = "（无）" while P2 is mid-flight; 下一步 (line 67-73) lists "P1 基准测试" and "P2 之前的命名落地" — both finished, the latter contradicted by line 172 in the same file; 阻塞表 (line 89) still lists "★ 语音栈/VLM 未选型" as blocking A2/A5/C4/A8, and 待决事项 (line 230) repeats it, though D27 settled it and line 162 declares the stack 定稿; line 204 says E5's 脉冲 未测 
- 修法:Do one pruning pass over STATE.md rather than another append: set 当前阶段 to P2 and 进行中 to the actual P2 work; delete or strike lines 200-222's superseded P1 blocks, keeping the single 17/17 summary; delete line 219 outright (the worklog records the retest) or rewrite it as "三次无人尝试作废 → 2026-07-27 交互式重测全过,结论以重测为准"; clear the resolved 语音栈/VLM rows from 阻塞 and 待决事项; add pg-mem / Qdrant×2 / embedding row

**[medium] STATE's "P2 剩" list (注册 ai-mem 服务 · Open WebUI) omits two §14 P2 checkboxes that are entirely undone — WebAuthn 设备身份 + 权限档位, and 无 Broker 期的显存过渡措施 — making P2 look ~2 items from complete when it is 4-5.**

- 影响:PROJECT_PLAN_v2.2.md §14 P2 has 12 boxes. Undone: line 1975 "统一入口 + **WebAuthn 设备身份**(不要先实现 bearer API key)+ 权限档位" — README.md:78-81 correctly lists 远程 WebAuthn / 权限六元组+工具池 / 出境闸门 as 未实装, so the code docs are honest, but STATE never surfaces it as an open P2 item; and line 1981 "无 Broker 期的显存过渡措施" — nothing in 90-ops or 10-core delivers this, and registry.toml:11 + README.md:26 both reference a "无
- 修法:Rewrite STATE.md:175's next-step clause to enumerate all open P2 items against §14: 注册 ai-mem 服务 · Open WebUI 配置首启 · **远程 WebAuthn + 权限档位** · **无 Broker 期显存过渡措施**. Either write the static-launch script the docs promise, or change registry.toml:11 and README.md:26 to say the backend URLs are hand-maintained until P4's Broker.

**[medium] The design doc contradicts its own §3 security correction in §4 and §1: it says an "已实装的" gateway fronts Qdrant with api_key injection so "Qdrant 本体不对外可达", and that the gateway routes embedding/rerank — the gateway does neither.**

- 影响:Line 44: "§6.8「前置只认本地 token 的代理」由**已实装的 127.0.0.1 网关**充当(补上 §3 的调用方鉴权后):网关持 admin token 注入 `api-key` 头,外部只打网关,Qdrant 本体不对外可达." gateway.py has exactly three routes — /health, /v1/models, /v1/chat/completions — no Qdrant proxy, no api_key injection, no PG path. And the "不对外可达" clause is refuted by §3 of the same document twelve lines earlier: "Windows 的 127.0.0.1 对本机**任意账户开放**… `ai-asset` **能直接 TCP 
- 修法:Rewrite line 44 in future tense and align it with §3: the Qdrant-fronting proxy is **not** built; Qdrant remains reachable on 6333/6335 by any local account, with api_key as the sole runtime barrier; when the proxy is built it must use `require_trusted_local` (fail-closed), per gateway.py:112-123. Fix line 14 to "embedding/rerank 为独立 CPU 服务(:18084),客户端直连;网关目前不代理该平面(别名已登记但 chat 路由拒非 chat kind)", an

**[medium] D28 is materially amended by D30 and the D30 补记 but carries no annotation, breaking the file's own supersession convention — and the three documents now describe three different caller policies (positive check / allowlist / denylist).**

- 影响:DECISIONS.md maintains an explicit convention: D2 and D9 are marked "已被 D22 推翻" (line 167-168), D8's triggers are marked 作废 by D24 (line 506), and a whole block at line 215 is fenced with "⚠ 以下这段已被同日稍后的 D22 推翻". D28 (line 714-750) gets none of it, and it needs it most: it says 本机 loopback + 登录用户 → "trusted-local **全权限**" with the implementation constraint "校验连接来自本机登录用户的账户" (line 742). D30 (line 82
- 修法:Add a header note to D28 in the D2/D9 style: "★ 已由 D30 + D30 补记(2026-07-28)修正:回环本身不再等于 trusted-local;实装为拒绝隔离账户的黑名单 + chat 路径 fail-open,尚未实现本条要求的『校验为登录用户』". Then pick one policy and make design §3 and the code agree — an allowlist is what D30 specified and is strictly safer than the current two-name denylist, which silently admits any future service account.

**[low] §5 presents PG 角色二分 as structural isolation under the banner "不信任运行时过滤", but both roles map from the same Windows account, so SSPI does not separate them — the confinement is a runtime choice by memory-service, undisclosed.**

- 影响:§5:48 states the principle "优先结构分隔,不信任运行时过滤", then §5:50 lists "PG 角色二分:`ai_mem_local`(基表全表)vs `ai_mem_remote`(仅 GRANT SELECT ON v_memory_nons2)" as one of the structural mechanisms, adjacent to the genuinely structural Qdrant dual-instance split. But apply-schema.ps1:66-73 adds pg_hba lines and pg_ident entries mapping `ai-mem → ai_mem_local` *and* `ai-mem → ai_mem_remote` (alongside install-post
- 修法:Add one sentence to §5 after the role-split bullet: "★ 两个角色经 pg_ident 均由同一 Windows 账户 ai-mem 映射,SSPI 不区分二者 —— 角色二分防的是『远程句柄拿到过宽权限』,不防『ai-mem 进程本身被攻陷』。真正结构级的是 Qdrant 双实例。" Optionally soften "行级 S2 实攻" to "行级 S2 权限用例(SET ROLE)" in STATE.md:175.

**[nit] Assorted small factual drifts: a wrong venv path, a stale `updated` date, a download figure the project already disproved, a filename that doesn't exist, and a README table that contradicts its own body.**

- 影响:None of these is load-bearing alone, but they are the kind of drift that erodes trust in the docs that are accurate. (1) paths.toml:12 `updated = "2026-07-26"` though the whole `[memory]` block was added 2026-07-27 with D30. (2) paths.toml:120 justifies the hf-cache GC exception with "实测下载仅 ~11 MB/s(17GB 约需 27 分钟)" — STATE.md:117 explicitly retracts that number ("~~之前「~11 MB/s,瓶颈在接入侧」~~ **是错的**", 
- 修法:Bump paths.toml:12 to the real date; replace the ~11 MB/s justification with the corrected 29 MiB/s figure (the GC exception still holds on 重获成本 grounds, so only the number changes); fix STATE.md:169 to `D:\AI\venvs\speech`; make memory-backbone-design.md:101 and schema.sql:2 say `schema.sql`; change README.md:81's 审计 row to "部分:E1/denied 落 JSONL,§9 审计表与 §9.3 告警未做"; drop the misleading first claus
