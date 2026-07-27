# 记忆库骨架 · 落地设计(P2/P3a)

> 决议 D30(2026-07-27)。据方案书 §3.1/§4.x/§6.8/§6.9/§8.5.5/§9/§13/§14 + 联网调研 + 三维对抗性核验综合。
> 全量分析证据见工作流 `memory-backbone-install-design`(wf_ab2b6b3f-06a)。

## 1. 架构:双库双轨

| | 存什么 | 为什么 |
|---|---|---|
| **PostgreSQL**(结构化轨) | L1 会话摘要 · L2 情节结构化部分(与 Qdrant 双写)· L3 语义事实 · 实体图谱(Person/Project/Device/Place/Thing/Event/Preference)· 元数据列(asserted_at·confidence·source_ref·superseded_by·origin_device_id·write_seq·provenance·source_confidence)· pending_review 队列 · `v_memory_nons2` 视图 · 审计日志 · 指标时序 · **secret_ref 登记表** · 凭证取用审计 · vigil_observations · 交易账本空表 | 精确查询「结构化轨」;`write_seq` 由 PG advisory lock 全局单调分配 |
| **Qdrant**(向量轨) | L2 情节的 bge-m3 向量(**1024 维,Cosine**)· collection `mem_main`(非 S2)· `mem_s2`(S2) | ANN 检索「相关的是什么」 |

- **pgvector 不装。** 全方案向量存取一律 Qdrant,PG 从不做向量距离计算(§13 的余弦预筛也是 CPU 复用 bge-m3)。这对裸机 Windows 是实质简化——pgvector 无官方 Windows 预编译二进制,略过它可直接用官方 PG 包。
- embedding/rerank 是网关路由到 `127.0.0.1:18084` 的**独立 CPU 服务**,既不落 PG 也不在 Qdrant 内。

## 2. 版本与安装形态

- **PostgreSQL 18.x**(最新补丁)· 官方 **windows-x64 binaries ZIP**(**非** EDB 图形安装器——它的 locale bug #17887 会把库静默建成 SQL_ASCII/GBK,中文记忆不可逆损坏)· 手动 `initdb --encoding=UTF8 --locale=C --data-checksums` · `pg_ctl register`。前置:VC++ 2015-2022 x64 Redistributable(ZIP 不自带)。
- **Qdrant v1.18.3** · 官方**原生 windows-msvc 二进制**(`qdrant-x86_64-pc-windows-msvc.zip`,来源必须是 qdrant/qdrant 的 GitHub Release,**不是** sourceforge 镜像)· **NSSM** 包装成服务(qdrant.exe 非 SCM 感知,裸 `sc create` 报 1053)。pin 死版本、关自动升级(snapshot 只能恢复进同版本或更新次版本)。
- **不走 Docker/WSL。** 容器/WSL 里进程不是 ai-mem,精心配的 NTFS ACL + 三账户 Deny 被绕开,与「OS 隔离是主防线(D22)」直接冲突。原生二进制严格优于 Docker。

## 3. ★ 安全模型纠正(D30 的核心)

**草案曾把「回环绑定 + NTFS Deny ACL」当成运行时主防线。这对活着的数据库端口是错的:**

> Windows 的 `127.0.0.1` 对本机**任意账户开放**,没有 per-user 的 socket ACL。`ai-asset`(跑 ComfyUI 第三方节点)**能直接 TCP 连** `127.0.0.1:5432` / Qdrant 端口。NTFS Deny ACL 只挡**静态文件**,对活 socket 零作用。在纯口令(SCRAM)方案下,运行时唯一真屏障就是那个**共享口令**——这恰恰要求把明文 DB 口令存在磁盘,与 D22 初衷相反。

**修法 = §6.8 原本要求、草案偷偷丢掉的 SSPI:**

- **PostgreSQL 用 SSPI 认证**,把 DB 连接绑到 **Windows 账户 SID**:`pg_hba` 用 `host <db> <role> 127.0.0.1/32 sspi include_realm=0`,`pg_ident` 映射 `ai-mem → mem_rw`(以及 `ai-mem → postgres` 供备份)。standalone 无域走 NTLM SSPI。
  - 效果:`ai-asset` 即使拿到口令,其 SID 映射不到 `mem_rw`,**照样连不上**;§6.8「按 SID 拒绝并告警」得以落地;**且彻底不用在磁盘存 mem_rw / postgres 的 DB 口令**。
- **Qdrant 无 SSPI 等价物** → 运行时屏障只能是 **api_key**(bearer)。因此 api_key **只能被以 ai-mem 运行的进程持有**,绝不可落到 ai-asset 可读处。
- **网关/memory-service 必须认「调用方身份」,不能「回环就放行」。** 现网关 `classify_caller` 对任何 loopback 返回 trusted-local——**这是漏洞**(混淆代理:ai-asset→网关→Qdrant 携 admin key = 读全库)。落地要用:命名管道 + `GetNamedPipeClientProcessId`/`ImpersonateNamedPipeClient` 核对调用方 SID,或 TCP 对端端口→PID→token SID,拒绝非 ai-mem 调用方。**(此项属 memory-service / 网关加固,随其实装补;建库本身不阻塞。)**

## 4. 部署拓扑(全在 ai-mem 下,只听回环)

| 服务 | 账户 | 监听 | 数据(全在 `{state}/memory` 强 ACL 内) |
|---|---|---|---|
| `pg-mem`(PostgreSQL) | `.\ai-mem` | `listen_addresses='127.0.0.1'`(**IPv4-only**,不用 'localhost' 以免带出 ::1)· :5432 | `pg\18\{bin,data}` |
| `Qdrant`(mem_main) | `.\ai-mem` | `service.host: 127.0.0.1` · :6333/6334 | `qdrant\{bin,config,storage,snapshots,tmp,logs}` |
| `Qdrant-s2`(mem_s2) | `.\ai-mem` | `127.0.0.1` · **:6335/6336(独立端口)** | `qdrant-s2\{config,storage,snapshots,tmp,logs}`(共用 `qdrant\bin\qdrant.exe`) |

- 两库都**不开入站防火墙**(回环不需要;可选加显式 BLOCK)。
- §6.8「前置只认本地 token 的代理」由**已实装的 127.0.0.1 网关**充当(补上 §3 的调用方鉴权后):网关持 admin token 注入 `api-key` 头,外部只打网关,Qdrant 本体不对外可达。全机同址 loopback,故不开 TLS。

## 5. S2 结构性隔离(§4.11.4 · 非运行时过滤)

原则:**「漏 collection 句柄是响亮的异常,漏 payload filter 是沉默的」→ 优先结构分隔,不信任运行时过滤。**

- **PG 角色二分**:`ai_mem_local`(基表全表)vs `ai_mem_remote`(仅 `GRANT SELECT ON v_memory_nons2`)。
- **Qdrant 实例二分**(用户 2026-07-27 拍板:现在就上双实例):`mem_main`(6333,非 S2,远程句柄)vs `mem_s2`(6335,S2,local 独有,独立端口 + 独立 api-key)。
- 凭证**值**永不进库,只存 `secret_ref` 句柄(D23);凭证元数据一律 S2,排除出 `v_memory_nons2`,Qdrant 仅入 `mem_s2`。远程角色查 `secret_ref` 必须抛异常。

## 6. 四个已决问题(2026-07-27)

1. **PG 认证** = **SSPI**(见 §3)。—— 用户拍板。
2. **mem_s2 隔离** = **现在就上双 Qdrant 实例 / 双端口**(6335/6336),忠于 §4.11.4。—— 用户拍板。
3. **crypto_tier 列** = 建成**可空预留列**(`crypto_tier text NULL DEFAULT NULL`,注释「D22 已停用加密,本列预留」)。**绝不 NOT NULL**(今天无有意义值可填)。—— Claude 定,与被强制的 `sensitivity_domain`(NOT NULL 无 DEFAULT)相反。
4. **L4 程序记忆** = **git 跟踪(code 根)+ 独立签名文件 + PG 轻量登记表** `l4_procedure`(name·version·git_ref·sha256·signature_ref·signed_at·仅 trusted-local 可写)。代码 body 不进 PG。远程档位无 L4 写端点,远程检索默认排除 L4。—— Claude 定。

## 7. 数据布局(见 config/paths.toml `[memory]`)

```
{state}/memory/                     ← 强 ACL(ai-mem FullControl / ai-asset,ai-exec Deny · 继承关)
  pg/18/bin/                        PG 二进制(在硬化根内 → 防 DLL 投毒)
  pg/18/data/                       ★ 活集群 · 绝不文件级复制 · pg_dump
  qdrant/bin/qdrant.exe             两实例共用
  qdrant/config/config.yaml         含 api_key(S2)· 仅 NTFS ACL 保护
  qdrant/{storage,snapshots,tmp,logs}   ★ storage 活库 · snapshot API
  qdrant-s2/config/config.yaml      mem_s2 独立 api_key
  qdrant-s2/{storage,snapshots,tmp,logs}
```

## 8. 备份(§8.5.5 · 记忆库 P3a 才存在,当前不阻塞;此处记 P3a 必做)

- **PG**:`pg_dumpall --globals-only`(抓 mem_rw 角色定义 + SCRAM 哈希——**SSPI 下无 DB 口令,但 globals 仍需捕获角色**;备份认证走 SSPI `ai-mem→postgres`,**无需存 postgres 口令**)+ `pg_dump -Fc -d memory`。随附 `PG_VERSION.txt`(大版本 + initdb 全参数)。
- **Qdrant**:snapshot API(`POST /collections/{c}/snapshots?wait=true`,**含 mem_s2,两端口各来一遍**)。随附 `QDRANT_VERSION.txt`。
- **P3a 必修的现存隐患**(复核 backup 维度):
  1. 现 `backup.ps1` 用 robocopy 整根复制 `{state}` → P3a 后会**文件级复制活的 pg data / qdrant storage**(§8.5.5 违反)。必须:注入 `memory-dump.ps1`(在 STATE 复制**之前**)+ 用**绝对路径 /XD** 从 robocopy 排除活引擎目录(严禁传裸目录名,`/XD logs` 会误伤 `state\logs`)。
  2. **PG↔Qdrant 双写跨库一致性**:备份期 `Stop-Service memory-service`(只停写入方)数秒,固定 PG→Qdrant 顺序;或采两库 `write_seq` 高水位 + 恢复期对账。
  3. 备份把 `config.yaml`(api_key)/ memory-service 配置**明文拷到移动盘**且 ai-asset 挂载期可读 → 从备份**排除**这些密钥文件(恢复时重装重配);或密钥改存 Windows Credential Manager/DPAPI(P7 已选此存储)。
  4. `BACKUP-REPORT.md` 自动生成的恢复步骤仍写 `robocopy state\` 还原 → 必须改成 `pg_restore` / `qdrant --snapshot`,并写明 `memory-db\` 是唯一合法恢复源。
  5. 每件产物**逐件验真**(pg_dump 退出码 / snapshot HTTP 200 + 文件字节>0),否则半截产物冒充成功。
  6. `backup.ps1:110` 同盘保护改 **fail-closed**(目标盘号解析不出即拒绝,而非跳过)。
- **P3a 验收硬门**:真跑通「建库→写→dump/snapshot→到全新空目录恢复→数据一致」——没演练过的备份不算备份。

## 9. 密钥清单(装库后实际存在的秘密 —— SSPI 已大幅收缩)

| 秘密 | 落点 | 说明 |
|---|---|---|
| **ai-mem Windows 账户密码** | 各服务的 **LSA secret**(pg-mem / Qdrant / Qdrant-s2 / 将来 memory-service) | 装库时一次性随机重置并配进服务;人不记、不落盘、不进 paths.toml/配置、**不进 LLM 上下文**。★ 轮换必须同步进所有服务(`sc config` / NSSM),否则服务 1069 |
| **Qdrant api_key**(mem_main / mem_s2 各一) | 各自 `config.yaml`(强 ACL) | 无 SSPI 等价物,只能 bearer;由 ai-mem 进程 + 网关持有 |
| ~~mem_rw DB 口令~~ / ~~postgres DB 口令~~ | **不存在** | ★ SSPI 消除了这两个磁盘明文口令 |

**铁律**:任何 `$pw`/`$apikey` 触碰的安装步骤**由用户在自己的管理员 PowerShell 跑,绝不贴进 Claude 工具**(错误回显可能把密钥带进上下文/worklog,违 D23);命令行**不过明文密码**(`Win32_Process.CommandLine` 可见 + PSReadLine history 落盘)——服务凭据经 services.msc/NSSM 输入;跑前 `Set-PSReadLineOption -HistorySaveStyle SaveNothing`。

## 10. 安装顺序

1. **PostgreSQL**(`install-postgres.ps1`,用户跑)→ Claude 只读核验(netstat 回环、`SHOW server_encoding`=UTF8、ACL Deny 未松、pg_ident 生效)。
2. **Qdrant ×2**(`install-qdrant.ps1`,用户跑)→ 核验(两端口回环、带/不带 api-key 鉴权、`Get-Process qdrant` UserName=ai-mem)。
3. **Schema**(`memory-schema.sql`,以 ai-mem 应用)→ 角色二分、`v_memory_nons2`、pending_review、`secret_ref` 登记表、审计/指标/vigil/账本空表、`l4_procedure`;`sensitivity_domain` NOT NULL 无 DEFAULT,`crypto_tier` 可空。
4. **E1 入口凭证检测器 + secret_ref 建表**(§6.9.0/§14,P2 必做且先行)。
5. 启动编排:`memory-service` 依赖 pg-mem + Qdrant + Qdrant-s2 + embedding 全就绪(SCM `DependOnService` + 健康门)。
