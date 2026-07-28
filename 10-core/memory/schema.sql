-- =====================================================================
-- memory-schema.sql  ·  PostgreSQL 18  ·  DB=memory  ·  OWNER=mem_rw
-- 幂等 (IF NOT EXISTS / DO 守卫 / CREATE OR REPLACE) · 可重复应用
-- 以 ai-mem 身份 (SSPI ai-mem->mem_rw) 应用,对象 OWNER=mem_rw
-- 综合: 记忆核心 + 凭证审计 + 辅助表 三份规格 · 依据 D30/D23/D22
--
-- 工程说明: 原文多处只给散文级字段,未定字段级 DDL。以下带
--   [推断] 标记的列为满足"可直接应用"而拟定的最小骨架,可在
--   P3a/B4 最终化;其类型为"约束意图"的工程取值,不改变语义硬约束。
-- =====================================================================

SET client_min_messages = warning;

-- 0) 专用 schema(结构性隔离的命名空间;远程角色只经 USAGE+视图 SELECT 触达)
CREATE SCHEMA IF NOT EXISTS mem AUTHORIZATION mem_rw;
SET search_path = mem, public;

-- =====================================================================
-- 1) 枚举类型(DO 块 catch duplicate_object → 幂等)
-- =====================================================================
DO $$ BEGIN
  CREATE TYPE mem.provenance AS ENUM
    ('user_typed','user_voice_asr','tool_result','rag_chunk','web_content');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  CREATE TYPE mem.sensitivity_domain AS ENUM ('S0','S1','S2');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  CREATE TYPE mem.value_kind AS ENUM ('string','file','certificate','otp');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  CREATE TYPE mem.secret_request_outcome AS ENUM
    ('granted','denied','timeout','provider_error','cancelled');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  CREATE TYPE mem.cred_pattern_class AS ENUM
    ('iban','tax_id_de','card_pan','id_doc','secret_phrase','high_entropy');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
  CREATE TYPE mem.claim_class AS ENUM
    ('irreversible_loss','financial','system_degradation','self_commitment');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
-- 注: intent / purpose_tag / metric_name 取值集原文未穷举 → 建为 text +
--     应用层受控枚举,避免臆造完整值集并保留低摩擦扩值(见列注释)。

-- =====================================================================
-- 2) write_seq 分配: 共享序列 + 事务级 advisory lock → 全局单调唯一
--    客户端不参与: BEFORE INSERT 触发器强制覆写为服务端分配值。
-- =====================================================================
CREATE SEQUENCE IF NOT EXISTS mem.write_seq_seq AS bigint MINVALUE 1 START 1;

CREATE OR REPLACE FUNCTION mem.next_write_seq() RETURNS bigint
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = mem, public AS $$
BEGIN
  -- 固定 key: 序列化全库 write_seq 分配,使 write_seq 顺序贴合提交顺序
  PERFORM pg_advisory_xact_lock(4021768042176804::bigint);
  RETURN nextval('mem.write_seq_seq');
END $$;

CREATE OR REPLACE FUNCTION mem.tg_set_write_seq() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = mem, public AS $$
BEGIN
  NEW.write_seq := mem.next_write_seq();   -- 忽略调用方自报值
  RETURN NEW;
END $$;

-- 用户/自动事实判定(DB 层 backstop;权威判定在应用层 §4.4.2 服务端可验证信号)
CREATE OR REPLACE FUNCTION mem.is_user_fact(p mem.provenance, sc numeric)
  RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
  -- ★★ 判据只看【来源】,不看置信度。sc 保留在签名里但【故意不参与判断】。
  --
  -- 这里曾经写作 `... AND coalesce(sc,0) >= 1.0`,与 §4.4.2 正面冲突:
  --   §4.4.2 规定 1.0 必须有 panel_ticket 背书
  --   ⇒ 正常渠道写入的用户话语一律 < 1.0
  --   ⇒ 它们统统不算"用户事实"
  --   ⇒ §4.5 铁律实际上【一条用户事实都没保护住】,任何 tool_result/web_content
  --      都能悄悄覆盖用户亲口说的话,而且不报错。
  -- 2026-07-28 实测发现:此前 verify.sql 的 B5 用例因夹具写 1.0 插不进去、
  -- UPDATE 匹配 0 行而"没有报错",于是这个洞一直伪装成通过。
  --
  -- 概念上的错误在于把两件事混为一谈:
  --   置信度  = 这条内容有多可信
  --   来源    = 这话是谁说的
  -- 「能否被自动流程覆盖」取决于后者。用户说错了话仍然是用户说的,
  -- 纠正它的正当路径是用户再说一次、或走面板确认 —— 而不是让管线自行改写。
  SELECT p IN ('user_typed','user_voice_asr');
$$;
COMMENT ON FUNCTION mem.is_user_fact(mem.provenance, numeric) IS
  '§4.5:是否属于"用户亲口所述"。只看 provenance;第二参数保留仅为签名兼容,不参与判断';

-- 铁律: 自动事实不得 supersede 用户事实(§4.5)。superseded_by 由 NULL→值 时校验。
CREATE OR REPLACE FUNCTION mem.tg_block_auto_supersede_user() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE np mem.provenance; nsc numeric;
BEGIN
  IF NEW.superseded_by IS NOT NULL
     AND OLD.superseded_by IS DISTINCT FROM NEW.superseded_by THEN
    EXECUTE format(
      'SELECT provenance, source_confidence FROM %I.%I WHERE id = $1',
      TG_TABLE_SCHEMA, TG_TABLE_NAME)
      INTO np, nsc USING NEW.superseded_by;
    -- OLD = 被取代的既有事实; NEW.superseded_by 指向取代它的新行
    IF mem.is_user_fact(OLD.provenance, OLD.source_confidence)
       AND NOT mem.is_user_fact(np, nsc) THEN
      RAISE EXCEPTION
        '自动事实(id=%)不得 supersede 用户事实(id=%) — §4.5 铁律',
        NEW.superseded_by, OLD.id USING ERRCODE = 'check_violation';
    END IF;
  END IF;
  RETURN NEW;
END $$;

-- =====================================================================
-- 3) 记忆内容表(L1–L3 + 实体图谱)
--    通用元数据列集(backbone §1):
--      asserted_at·confidence·source_ref·superseded_by·origin_device_id
--      ·write_seq·provenance·source_confidence·sensitivity_domain·crypto_tier
--    D30: sensitivity_domain NOT NULL 无 DEFAULT;crypto_tier NULL 预留。
-- =====================================================================

-- L1 会话摘要(纯 PG,不入 Qdrant;滚动压缩;存续数天)
CREATE TABLE IF NOT EXISTS mem.l1_session_summary (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  session_id         text        NOT NULL,                    -- [推断]
  summary_text       text        NOT NULL,                    -- [推断] 滚动压缩正文
  covers_from        timestamptz,                             -- [推断]
  covers_to          timestamptz,                             -- [推断]
  updated_at         timestamptz NOT NULL DEFAULT now(),      -- 服务端为准
  sensitivity_domain mem.sensitivity_domain NOT NULL,         -- D30 无 DEFAULT
  crypto_tier        text        NULL DEFAULT NULL            -- D22 停用加密,预留
);
COMMENT ON COLUMN mem.l1_session_summary.crypto_tier IS 'D22 已停用加密,本列预留';
COMMENT ON TABLE  mem.l1_session_summary IS
  'L1 会话摘要;§6.9.10② 要求 L1 表与 pg_dump 产物均不得含凭证串';

-- L2 情节记忆(PG 结构化部分 + Qdrant 双写;带时间戳;长期)
CREATE TABLE IF NOT EXISTS mem.l2_episode (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  body               text,                                    -- [推断] 结构化正文/摘要
  event_at           timestamptz NOT NULL,                    -- 事件时间,服务端权威
  qdrant_point_id    uuid,                                    -- [推断] 与向量的关联键
  qdrant_collection  text,                                    -- [推断] mem_main / mem_s2
  asserted_at        timestamptz NOT NULL DEFAULT now(),
  confidence         numeric     CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
  source_ref         jsonb,        -- 两态: {kind:'snapshot',...} 或 {kind:'flow',session_id,...}
  superseded_by      bigint      REFERENCES mem.l2_episode(id),
  origin_device_id   text,
  write_seq          bigint      NOT NULL UNIQUE,             -- 全局单调(共享序列保证跨表唯一)
  provenance         mem.provenance NOT NULL,
  source_confidence  numeric     CHECK (source_confidence IS NULL OR source_confidence BETWEEN 0 AND 1),
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text        NULL DEFAULT NULL,
  -- ★★ allowlist 形状,不是 denylist(2026-07-28 审查后改写)。
  --   原写法是 `provenance NOT IN ('tool_result','web_content','rag_chunk') OR ...`,
  --   于是**将来任何新增的枚举值都会让 NOT IN 为真 ⇒ 整条 CHECK 恒真 ⇒ 不受任何约束**。
  --   也就是说"加一个 provenance 忘了同步加约束"这个疏漏,后果是【默认自由】而不是默认受限,
  --   而且不报错。改成正面列举"什么是用户直述",其余一律封顶 0.4。
  --   同一处教训与 §4.5 铁律那次(NULL 绕过 CHECK)同源:约束要写成拒绝优先。
  CONSTRAINT l2_derived_conf_cap CHECK
    (provenance IN ('user_typed','user_voice_asr')
     OR source_confidence IS NULL OR source_confidence <= 0.4)
);
COMMENT ON COLUMN mem.l2_episode.crypto_tier IS 'D22 已停用加密,本列预留';

-- L3 语义事实(纯 PG;长期;"确切是什么"结构化查询轨核心)
CREATE TABLE IF NOT EXISTS mem.l3_fact (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  statement          text        NOT NULL,                    -- [推断] 事实文本
  subject            text,                                    -- [推断]
  predicate          text,                                    -- [推断]
  object             text,                                    -- [推断]
  asserted_at        timestamptz NOT NULL DEFAULT now(),      -- 服务端权威断言时间
  confidence         numeric     CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
  source_ref         jsonb,
  superseded_by      bigint      REFERENCES mem.l3_fact(id),  -- 自引用;NULL=当前活跃
  origin_device_id   text,
  write_seq          bigint      NOT NULL UNIQUE,
  provenance         mem.provenance NOT NULL,                 -- Gate 输入必带
  source_confidence  numeric     CHECK (source_confidence IS NULL OR source_confidence BETWEEN 0 AND 1),
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text        NULL DEFAULT NULL,
  -- ★★ allowlist 形状(理由同 l2_derived_conf_cap)
  CONSTRAINT l3_derived_conf_cap CHECK
    (provenance IN ('user_typed','user_voice_asr')
     OR source_confidence IS NULL OR source_confidence <= 0.4)
);
COMMENT ON COLUMN mem.l3_fact.crypto_tier IS 'D22 已停用加密,本列预留';
COMMENT ON COLUMN mem.l3_fact.superseded_by IS
  '不覆盖: 冲突时 INSERT 新行并将旧行 superseded_by 指向新行,保留完整历史';

-- 实体图谱: 7 类节点表共享元数据列集(§4.3)。以 DO 块批量建以保持一致 + 幂等。
-- label = 供远程视图投影的人类可读显示名(绝非凭证列)。
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'entity_person','entity_event','entity_preference','entity_project',
    'entity_device','entity_place','entity_thing'] LOOP
    EXECUTE format($f$
      CREATE TABLE IF NOT EXISTS mem.%1$I (
        id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        label              text,                              -- [推断] 显示名/主键属性
        attrs              jsonb,                             -- [推断] 节点专有属性容器
        asserted_at        timestamptz NOT NULL DEFAULT now(),
        confidence         numeric CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
        source_ref         jsonb,
        superseded_by      bigint REFERENCES mem.%1$I(id),
        origin_device_id   text,
        write_seq          bigint NOT NULL UNIQUE,
        provenance         mem.provenance NOT NULL,
        source_confidence  numeric CHECK (source_confidence IS NULL OR source_confidence BETWEEN 0 AND 1),
        sensitivity_domain mem.sensitivity_domain NOT NULL,
        crypto_tier        text NULL DEFAULT NULL,
        -- ★★ allowlist 形状(理由同 l2_derived_conf_cap)
        CONSTRAINT %1$s_derived_conf_cap CHECK
          (provenance IN ('user_typed','user_voice_asr')
           OR source_confidence IS NULL OR source_confidence <= 0.4)
      )$f$, t);
    EXECUTE format('COMMENT ON COLUMN mem.%I.crypto_tier IS %L', t, 'D22 已停用加密,本列预留');
  END LOOP;
END $$;

-- Person 关系边字段(称谓/亲疏/重要日期) — [推断],原文未定字段级
ALTER TABLE mem.entity_person   ADD COLUMN IF NOT EXISTS appellation    text;
ALTER TABLE mem.entity_person   ADD COLUMN IF NOT EXISTS closeness      text;
ALTER TABLE mem.entity_person   ADD COLUMN IF NOT EXISTS important_dates jsonb;
-- Event 时间戳 + Preference 时效性 — [推断]
ALTER TABLE mem.entity_event    ADD COLUMN IF NOT EXISTS event_at   timestamptz;
ALTER TABLE mem.entity_preference ADD COLUMN IF NOT EXISTS expires_at timestamptz;
ALTER TABLE mem.entity_preference ADD COLUMN IF NOT EXISTS review_due timestamptz;

-- Event"可关联任意实体": 单一多态邻接表 [推断建模],替代 per-type 边表
CREATE TABLE IF NOT EXISTS mem.entity_edge (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  from_kind    text NOT NULL,   -- person|event|preference|project|device|place|thing
  from_id      bigint NOT NULL,
  to_kind      text NOT NULL,
  to_id        bigint NOT NULL,
  relation     text,            -- [推断] 边语义
  asserted_at  timestamptz NOT NULL DEFAULT now(),
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier  text NULL DEFAULT NULL
);
COMMENT ON TABLE mem.entity_edge IS
  '[推断] 实体关联多态邻接表;建模方式原文未定。★ 本表【整表】不入 v_memory_nons2 —— 即
   S0/S1/S2 全部行对远程都不可见(比行级排除更严),因为边的存在性本身可能泄露 S2 实体的关联。';

-- write_seq / supersede 触发器绑定(记忆内容表)
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'l2_episode','l3_fact',
    'entity_person','entity_event','entity_preference','entity_project',
    'entity_device','entity_place','entity_thing'] LOOP
    EXECUTE format('DROP TRIGGER IF EXISTS trg_write_seq ON mem.%I', t);
    EXECUTE format('CREATE TRIGGER trg_write_seq BEFORE INSERT ON mem.%I
                    FOR EACH ROW EXECUTE FUNCTION mem.tg_set_write_seq()', t);
    EXECUTE format('DROP TRIGGER IF EXISTS trg_supersede_guard ON mem.%I', t);
    EXECUTE format('CREATE TRIGGER trg_supersede_guard BEFORE UPDATE ON mem.%I
                    FOR EACH ROW EXECUTE FUNCTION mem.tg_block_auto_supersede_user()', t);
  END LOOP;
END $$;

-- 当前活跃事实部分索引 + 时序索引
CREATE INDEX IF NOT EXISTS idx_l3_active   ON mem.l3_fact (id) WHERE superseded_by IS NULL;
CREATE INDEX IF NOT EXISTS idx_l3_subject  ON mem.l3_fact (subject) WHERE superseded_by IS NULL;
CREATE INDEX IF NOT EXISTS idx_l2_event_at ON mem.l2_episode (event_at);
CREATE INDEX IF NOT EXISTS idx_edge_from   ON mem.entity_edge (from_kind, from_id);
CREATE INDEX IF NOT EXISTS idx_edge_to     ON mem.entity_edge (to_kind, to_id);

-- =====================================================================
-- 4) 候选待审队列 pending_review
--    张力解决(open_issue): 候选入队即须先定级 → sensitivity_domain NOT NULL,
--    Gate 正则命中服务端覆写为 S2(应用层)。write_seq 由触发器分配。
-- =====================================================================
CREATE TABLE IF NOT EXISTS mem.pending_review (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  candidate_body     jsonb       NOT NULL,     -- 候选 body + provenance 快照(E3 输入)
  provenance         mem.provenance NOT NULL,
  source_confidence  numeric     CHECK (source_confidence IS NULL OR source_confidence BETWEEN 0 AND 1),
  supersedes_ref     bigint      REFERENCES mem.l3_fact(id),  -- "将取代哪条现有事实"(主视觉)
  status             text        NOT NULL DEFAULT 'pending',
  origin_device_id   text,
  session_id         text,
  write_seq          bigint      NOT NULL UNIQUE,
  asserted_at        timestamptz NOT NULL DEFAULT now(),      -- 服务端为准
  created_at         timestamptz NOT NULL DEFAULT now(),
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text        NULL DEFAULT NULL,
  -- ★★ allowlist 形状(理由同 l2_derived_conf_cap)。
  --   这一条尤其要紧:它决定"哪些来源必须停在待审队列里"。写成 denylist 时,
  --   新增来源会**直接跳过人工确认**进库 —— 这正是外联通道最想走的那条捷径。
  CONSTRAINT pr_derived_forced_pending CHECK
    (provenance IN ('user_typed','user_voice_asr')
     OR (status = 'pending'
         AND (source_confidence IS NULL OR source_confidence <= 0.4)))
);
COMMENT ON TABLE mem.pending_review IS
  '逐条确认;禁批量;积压>50 告警并暂停远程候选(应用层 §4.4.2/§9.3);Gate 拒绝候选不入本表(§6.9.8)';
COMMENT ON COLUMN mem.pending_review.crypto_tier IS 'D22 已停用加密,本列预留';

DROP TRIGGER IF EXISTS trg_write_seq ON mem.pending_review;
CREATE TRIGGER trg_write_seq BEFORE INSERT ON mem.pending_review
  FOR EACH ROW EXECUTE FUNCTION mem.tg_set_write_seq();
CREATE INDEX IF NOT EXISTS idx_pr_status ON mem.pending_review (status);

-- =====================================================================
-- 5) L4 程序记忆指针表(D30) — 代码 body 不进 PG;仅 trusted-local 可写;
--    远程无写端点、远程检索默认排除 L4(结构性: 不入 v_memory_nons2,无 remote 授权)
-- =====================================================================
CREATE TABLE IF NOT EXISTS mem.l4_procedure (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  name               text        NOT NULL,     -- 工作流名(稳定标识)
  version            text        NOT NULL,
  git_ref            text        NOT NULL,     -- 指向 code 根版本化条目
  sha256             text        NOT NULL,     -- 执行前与最近批准哈希比对(§4.4.3)
  signature_ref      text,                     -- 独立签名文件引用
  signed_at          timestamptz,
  sensitivity_domain mem.sensitivity_domain NOT NULL,   -- 显式定级(承 D30)
  crypto_tier        text        NULL DEFAULT NULL,
  CONSTRAINT l4_name_version_uk UNIQUE (name, version)  -- supersede=需批准的版本升级
);
COMMENT ON TABLE mem.l4_procedure IS
  'L4=git 跟踪+独立签名+PG 指针表;§4.5 自动裁决对 L4 不适用;远程默认排除 L4';
COMMENT ON COLUMN mem.l4_procedure.crypto_tier IS 'D22 已停用加密,本列预留';

-- =====================================================================
-- 6) 凭证元数据(整表 S2) — 值永不进库(D23);排除出 v_memory_nons2;
--    ai_mem_remote 无任何权限;远程查询必抛异常(靠无 GRANT + 视图不引用)
-- =====================================================================

-- 6a) secret_ref 引用登记表(§6.9.3/§6.9.7);随 pg_dump 进 STATE 备份
CREATE TABLE IF NOT EXISTS mem.secret_ref (
  ref                text PRIMARY KEY,          -- 四段 <domain>.<region>.<entity>.<field>
  value_kind         mem.value_kind NOT NULL,   -- string|file|certificate|otp
  issuer             text,                      -- S2 辨识字段(例 'Sparkasse')
  last4              text,                      -- S2 账户尾号(最小充分辨识)
  purpose_tag        text,                      -- 受控枚举(应用层;例 'salary'),非自由文本
  version            text,
  provider_kind      text,                      -- manual|external...
  provider_locator   jsonb,                     -- 不透明 JSON(1Password/Bitwarden 定位);S2
  created_at         timestamptz NOT NULL DEFAULT now(),
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text NULL DEFAULT NULL,
  CONSTRAINT secret_ref_is_s2 CHECK (sensitivity_domain = 'S2'),
  -- ref 四段全小写;每段仅 [a-z0-9_-];3 个点分隔(entity 段禁点号由此保证)
  CONSTRAINT secret_ref_naming CHECK
    (ref ~ '^[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+$')
  -- ★ 绝无 value 列;ref 不含机构名/尾号由应用层保证;任何自由文本须过凭证正则
);
COMMENT ON TABLE mem.secret_ref IS
  'D23 凭证值永不进库,只存句柄;整表 S2;排除出 v_memory_nons2;远程查询必抛异常';
COMMENT ON COLUMN mem.secret_ref.crypto_tier IS 'D22 已停用加密,本列预留';
CREATE INDEX IF NOT EXISTS idx_secret_ref_issuer  ON mem.secret_ref (issuer);
CREATE INDEX IF NOT EXISTS idx_secret_ref_purpose ON mem.secret_ref (purpose_tag);

-- 6b) 凭证取用审计(§6.9.7);整表 S2;保留=不删(append-only)
CREATE TABLE IF NOT EXISTS mem.cred_access_audit (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  ts                 timestamptz NOT NULL DEFAULT now(),
  refs               text[]      NOT NULL,      -- 本次授权涉及 ref 数组(原子多值)
  intent             text        NOT NULL,      -- 受控枚举(取代自由文本 purpose;例 fill_form)
  plan_hash          text,                      -- §12.4 已批准计划哈希(对 (ref,sink) 取,非对值)
  sink_origin        text,                      -- 执行器上报实际 origin;★ 模型不可写
  outcome            mem.secret_request_outcome NOT NULL,     -- granted|denied|timeout|provider_error|cancelled
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text NULL DEFAULT NULL,
  CONSTRAINT cred_audit_is_s2 CHECK (sensitivity_domain = 'S2')
  -- ★ 无 value 列、无自由文本 purpose;任何返回路径不含高熵字符串(§6.9.7 约束1)
);
COMMENT ON TABLE mem.cred_access_audit IS '凭证取用审计;整表 S2;保留=不删(REVOKE UPDATE/DELETE)';
COMMENT ON COLUMN mem.cred_access_audit.crypto_tier IS 'D22 已停用加密,本列预留';
CREATE INDEX IF NOT EXISTS idx_cred_audit_ts   ON mem.cred_access_audit (ts);
CREATE INDEX IF NOT EXISTS idx_cred_audit_refs ON mem.cred_access_audit USING gin (refs);

-- 6c) Gate 拒绝记录(§6.9.8) — 只存 (类别,时间,会话id);绝不记 body/片段/哈希
--     open_issue 裁定: 其存在性对攻击者有定位价值(§6.9.8)→ 定为 S2、排除远程。
CREATE TABLE IF NOT EXISTS mem.gate_rejection (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  category           mem.cred_pattern_class NOT NULL,   -- 命中类别;★ 不记具体值
  ts                 timestamptz NOT NULL DEFAULT now(),
  session_id         text        NOT NULL,
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  CONSTRAINT gate_rej_is_s2 CHECK (sensitivity_domain = 'S2')
);
COMMENT ON TABLE mem.gate_rejection IS
  '拒绝=不落正文;只 (类别,时间,会话id);不进 pending_review;裁定 S2 排除远程(§6.9.8 定位风险)';
CREATE INDEX IF NOT EXISTS idx_gate_rej_ts      ON mem.gate_rejection (ts);
CREATE INDEX IF NOT EXISTS idx_gate_rej_session ON mem.gate_rejection (session_id);

-- =====================================================================
-- 7) 运维/审计表(非记忆内容;不入视图;远程无读)
--    S2 排除靠结构手段(无 remote GRANT + 不被视图引用)。
--    定级列口径(2026-07-27 核验修正 · D30 一致性):
--      · audit_log 【加】sensitivity_domain —— 它的 object/action/actor 可能承载记忆派生内容,
--        属"可能含内容"的表,须与记忆内容表同样强制显式定级。
--      · metrics_ts 【不加】—— 纯运维数值(显存/磁盘/时长/失败率/花费),结构上不承载记忆内容;
--        强制定级只会给每条指标写入平添无意义摩擦。此为**有意豁免**,非遗漏。
-- =====================================================================

-- 7a) 通用审计日志(§9.1) — 谁·对什么·做了什么·批准链·哈希·回执;保留=不删
CREATE TABLE IF NOT EXISTS mem.audit_log (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  ts             timestamptz NOT NULL DEFAULT now(),
  actor          jsonb,        -- [推断] {user,device,agent} 六元组维度
  object         text,         -- 对什么
  action         text,         -- 做了什么
  approval_chain jsonb,        -- 批准链
  hash           text,         -- §12.4 哈希绑定批准
  receipt        jsonb,        -- 执行回执(封闭枚举+错误码)
  sensitivity_domain mem.sensitivity_domain NOT NULL,   -- D30 一致性:可能承载记忆派生内容
  crypto_tier    text NULL DEFAULT NULL
);
COMMENT ON COLUMN mem.audit_log.crypto_tier IS 'D22 已停用加密,本列预留';
COMMENT ON TABLE mem.audit_log IS
  '§9.1 审计日志;PG 存储;保留=不删(append-only);含凭证串亦全库 grep 不到(§6.9.10①)';
CREATE INDEX IF NOT EXISTS idx_audit_ts ON mem.audit_log (ts);

-- 7b) 指标时序(§9.1) — 保留 1 年(外部 purge/分区,裸机 Windows 无 pg_cron)
CREATE TABLE IF NOT EXISTS mem.metrics_ts (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  ts          timestamptz NOT NULL DEFAULT now(),
  metric_name text        NOT NULL,   -- vram_watermark|disk_watermark|task_duration|failure_rate|api_cost_cumulative
  value       numeric,
  labels      jsonb                   -- 窄表;维度标签
);
COMMENT ON TABLE mem.metrics_ts IS '指标时序;保留 1 年(外部定时 purge / 后续时间分区)';
CREATE INDEX IF NOT EXISTS idx_metrics_name_ts ON mem.metrics_ts (metric_name, ts);

-- 7c) Vigil 观察(backbone §1;§17) — resident-observer 唯一写落点;INSERT-only 提名槽
--     承 D30 全库定级 → 带 sensitivity_domain(open_issue 裁定加列);不含分数列。
CREATE TABLE IF NOT EXISTS mem.vigil_observations (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  claim_class        mem.claim_class NOT NULL,       -- 白名单;决定后果严重性上限
  title              text,        -- 模板锁定文本(应用层强制)
  summary            text CHECK (summary IS NULL OR char_length(summary) <= 80),
  root               text,        -- paths.toml 根变量 {root}
  rel                text,        -- 相对路径 {rel}
  evidence_ref       text,        -- 证据行号/首4KB 引用(默认只渲染到 UI,不进模型上下文)
  source             text        NOT NULL DEFAULT 'model_nominated'
                       CHECK (source = 'model_nominated'),
  pulse_id           text,        -- 单次脉冲=单个会话
  session_id         text,
  observed_at        timestamptz NOT NULL DEFAULT now(),      -- 服务端时间
  status             text        NOT NULL DEFAULT 'pending_handoff',  -- 待交接/已交接/已忽略
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text NULL DEFAULT NULL
);
COMMENT ON TABLE mem.vigil_observations IS
  'INSERT-only 提名槽;不写 L0–L4;无分数列(总分由 Vigil 读不到的确定性公式合成);不含凭证串(§6.9.10⑥)';
COMMENT ON COLUMN mem.vigil_observations.crypto_tier IS 'D22 已停用加密,本列预留';
CREATE INDEX IF NOT EXISTS idx_vigil_status  ON mem.vigil_observations (status);
CREATE INDEX IF NOT EXISTS idx_vigil_session ON mem.vigil_observations (session_id);

-- =====================================================================
-- 8) 交易账本空表族(§7 接口预留;本期只建空表,不实装交易)
--    账本表须定到字段级(§7 唯一法定保存义务);其余为最小骨架 [推断]。
--    均带 D30 定级列;账本进 GC 白名单(只归档不删,应用/运维层)。
-- =====================================================================
CREATE TABLE IF NOT EXISTS mem.idempotent_ledger (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  request_id         text        NOT NULL UNIQUE,   -- 幂等去重键(服务端去重)
  occurred_at        timestamptz NOT NULL DEFAULT now(),
  entry_type         text,        -- [推断] buy|sell|fee|dividend...
  instrument         text,        -- [推断]
  quantity           numeric,     -- [推断]
  amount             numeric,     -- [推断]
  currency           text,        -- [推断]
  loss_pool_category text,        -- 德国亏损抵扣分池(§7 loss pool by category)
  external_ref       text,        -- [推断]
  plan_hash          text,        -- [推断] 已批准计划哈希
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text NULL DEFAULT NULL
);
COMMENT ON TABLE mem.idempotent_ledger IS
  '幂等账本;GC 白名单只归档不删;字段级为 [推断] 草案,投资模块启动时最终化(§7/B4)';
COMMENT ON COLUMN mem.idempotent_ledger.crypto_tier IS 'D22 已停用加密,本列预留';

-- 其余四张空表: 最小骨架 + D30 列 + jsonb payload 占位(字段级待 §7/B4)
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'trade_proposal','approval_ticket','reconciliation','regulatory_snapshot'] LOOP
    EXECUTE format($f$
      CREATE TABLE IF NOT EXISTS mem.%1$I (
        id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        created_at         timestamptz NOT NULL DEFAULT now(),
        payload            jsonb,                             -- [推断] 字段级待 §7/B4
        sensitivity_domain mem.sensitivity_domain NOT NULL,
        crypto_tier        text NULL DEFAULT NULL
      )$f$, t);
    EXECUTE format('COMMENT ON TABLE mem.%I IS %L', t, '§7 接口预留空表;本期不实装;字段级待 B4');
    EXECUTE format('COMMENT ON COLUMN mem.%I.crypto_tier IS %L', t, 'D22 已停用加密,本列预留');
  END LOOP;
END $$;

-- =====================================================================
-- 9) v_memory_nons2 — S2 结构性隔离唯一远程出口(§4.11.4/§6.9.3)
--    语义: 仅暴露记忆内容基表中 sensitivity_domain <> 'S2' 的行(非 S2)。
--    ★ 绝不 JOIN/引用 secret_ref / cred_access_audit / gate_rejection 或任何 S2 表;
--    ★ 只投影安全公共列,绝无 secret_ref/last4/issuer/provider_locator/凭证元数据;
--    ★ 默认排除 L4(l4_procedure 不在此视图内);携带 secret_ref 的实体行因 S2 被行级排除。
--    幂等: DROP + CREATE(见下)。视图属主 mem_rw → 远程经视图读,碰不到基表。
--
--    ★★ security_barrier=true(2026-07-27 核验 FAIL 修正,必须保留):
--    普通视图下 PostgreSQL 可能把调用方谓词【下推到 WHERE sensitivity_domain<>'S2' 之前】求值
--    (低成本函数/操作符先跑),攻击者借错误信息或副作用可反推被排除的 S2 行内容 —— 即项目自己
--    警告的「漏 payload filter 是沉默的」。security_barrier 强制视图自身的过滤先于调用方谓词,
--    关掉这条侧信道。★ 今后任何重建本视图的改动都必须保留此选项。
--
--    ★ 不投影 write_seq(同轮核验:S2 行也消耗全局 write_seq,把它暴露给远程会形成空洞
--      (…5,6,8…),泄露 S2 行的存在性/条数/提交时序)。远程排序改用 asserted_at。
-- =====================================================================
-- DROP 而非 CREATE OR REPLACE:后者不允许变更列清单(改视图列集会报错),
-- 且 DROP 会连带丢掉 GRANT → 因此 roles.sql 必须在 schema.sql 【之后】跑(见 apply 顺序)。
DROP VIEW IF EXISTS mem.v_memory_nons2;
CREATE VIEW mem.v_memory_nons2 WITH (security_barrier = true) AS
  SELECT 'l1_session_summary'::text AS memory_kind, id, summary_text AS content,
         NULL::timestamptz AS event_at, updated_at AS asserted_at,
         NULL::numeric AS confidence, NULL::numeric AS source_confidence,
         NULL::text AS provenance, NULL::text AS origin_device_id,
         NULL::bigint AS superseded_by,
         sensitivity_domain::text AS sensitivity_domain
    FROM mem.l1_session_summary WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'l2_episode', id, body, event_at, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.l2_episode WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'l3_fact', id, statement, NULL::timestamptz, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.l3_fact WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'entity_person', id, label, NULL::timestamptz, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.entity_person WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'entity_event', id, label, event_at, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.entity_event WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'entity_preference', id, label, NULL::timestamptz, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.entity_preference WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'entity_project', id, label, NULL::timestamptz, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.entity_project WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'entity_device', id, label, NULL::timestamptz, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.entity_device WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'entity_place', id, label, NULL::timestamptz, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.entity_place WHERE sensitivity_domain <> 'S2'
  UNION ALL
  SELECT 'entity_thing', id, label, NULL::timestamptz, asserted_at, confidence, source_confidence,
         provenance::text, origin_device_id, superseded_by, sensitivity_domain::text
    FROM mem.entity_thing WHERE sensitivity_domain <> 'S2';

COMMENT ON VIEW mem.v_memory_nons2 IS
  'S2 结构性隔离唯一远程出口;仅非 S2 记忆内容;绝不含 secret_ref/S2 表/S2 行/L4';
-- =====================================================================
-- 结束 · 角色与 GRANT 见 role_and_auth
-- =====================================================================