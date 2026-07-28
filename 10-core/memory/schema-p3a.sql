-- =====================================================================
-- schema-p3a.sql · P3a 阶段 S0:DB 最终化(幂等,可重复应用)
-- 在 schema.sql / roles.sql 之后应用。依据 D33 三项裁决。
--
-- ★★ 本文件存在的首要原因:2026-07-28 实测发现写路径有一条完整绕过链。
--    四条攻击【全部成功】(以 postgres 身份实测):
--      ① UPDATE mem.l3_fact SET statement='...'        → 直接覆盖,「不覆盖」铁律形同虚设
--      ② UPDATE ... SET source_confidence=1.0, provenance='user_typed'  → 自动事实提权
--      ③ 提权后再 supersede 用户事实                    → §4.5 铁律被绕过
--      ④ UPDATE ... SET superseded_by=NULL             → 悄悄复活已被取代的旧事实
--    根因:原触发器 tg_block_auto_supersede_user 只在 superseded_by 变化时才检查,
--    且读的是【当前】的 provenance/source_confidence —— 可以先篡改再 supersede。
--    内容列的 UPDATE 根本不进它的判断分支。
--
--    修法:把记忆内容表做成【只可追加】—— 除 supersede 指针与少数运维列外,
--    任何列的 UPDATE 一律 RAISE。这样「冲突不覆盖」不再是约定,而是数据库拒绝。
-- =====================================================================

SET client_min_messages = warning;
SET search_path = mem, public;

-- =====================================================================
-- 1) append-only 守卫:内容与元数据列不可变
-- =====================================================================
CREATE OR REPLACE FUNCTION mem.tg_append_only() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  col   text;
  oldv  text;
  newv  text;
  -- 允许被 UPDATE 的列(白名单):
  --   superseded_by —— 冲突处理的唯一合法写(且下方另有方向约束)
  --   redacted_at   —— D33② tombstone 删除
  --   review_status / reviewed_at —— 仅 pending_review 用
  allowed text[] := ARRAY['superseded_by','redacted_at','review_status','reviewed_at'];
BEGIN
  FOR col IN
    SELECT a.attname FROM pg_attribute a
     WHERE a.attrelid = TG_RELID AND a.attnum > 0 AND NOT a.attisdropped
  LOOP
    IF col = ANY(allowed) THEN CONTINUE; END IF;
    EXECUTE format('SELECT ($1).%I::text, ($2).%I::text', col, col)
      INTO oldv, newv USING OLD, NEW;
    IF oldv IS NOT DISTINCT FROM newv THEN CONTINUE; END IF;

    -- ★ vector_point_id 是【索引指针】,不是记忆内容 —— 但也不是随便可写:
    --     NULL → 值    建索引        ✓
    --     值   → NULL  下架(D33② tombstone 删完向量后清指针) ✓
    --     值   → 另一个值            ✗ 「重指」
    --   为什么禁重指:这个指针唯一的用途就是删除时定位向量点。指错了,
    --   tombstone 会删掉别人的点、而本行的向量【留在库里继续被检索命中】——
    --   正文已删、向量还在,是最难发现的一类删除失败(§4.5 / D33②)。
    IF col = 'vector_point_id' THEN
      IF oldv IS NULL OR newv IS NULL THEN CONTINUE; END IF;
      RAISE EXCEPTION
        '向量指针不得重指(%.% 列 %):% → %。'
        '该指针是 tombstone 删除时定位向量点的唯一依据,改指会让删除删错点 ——'
        '正文没了、向量还在库里继续被检索命中。要换点请先置 NULL 再设新值。',
        TG_TABLE_SCHEMA, TG_TABLE_NAME, col, oldv, newv
        USING ERRCODE = 'check_violation';
    END IF;

    RAISE EXCEPTION
      '记忆内容不可覆盖(§4.5):表 %.% 的列 % 试图从 % 改为 %。'
      '冲突处理必须【新增一行并把旧行 superseded_by 指向它】,不是改写。'
      '删除请用 redacted_at(D33②:tombstone + 隔离区)。',
      TG_TABLE_SCHEMA, TG_TABLE_NAME, col,
      coalesce(left(oldv, 40), 'NULL'), coalesce(left(newv, 40), 'NULL')
      USING ERRCODE = 'check_violation';
  END LOOP;
  RETURN NEW;
END $$;

-- =====================================================================
-- 2) supersede 指针的方向约束:只能 NULL → 非 NULL,且不得自指
--    ④「悄悄复活」正是靠 非NULL → NULL 做到的。
-- =====================================================================
CREATE OR REPLACE FUNCTION mem.tg_supersede_direction() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.superseded_by IS NOT NULL AND NEW.superseded_by IS NULL THEN
    RAISE EXCEPTION
      '不得复活已被取代的事实(%.% id=%):superseded_by 只能 NULL→非NULL,'
      '不能反向 —— 否则可悄悄让旧值重新生效,而历史看不出发生过什么。',
      TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.id
      USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.superseded_by IS NOT NULL AND NEW.superseded_by IS DISTINCT FROM OLD.superseded_by THEN
    RAISE EXCEPTION
      '不得改写已存在的 supersede 指针(%.% id=%):它是历史链的一环。',
      TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.id
      USING ERRCODE = 'check_violation';
  END IF;
  IF NEW.superseded_by IS NOT NULL AND NEW.superseded_by = NEW.id THEN
    RAISE EXCEPTION '自指的 superseded_by(%.% id=%)会让历史链成环。',
      TG_TABLE_SCHEMA, TG_TABLE_NAME, NEW.id
      USING ERRCODE = 'check_violation';
  END IF;

  -- ★★ redacted_at 同样只能单向(NULL → 非NULL)。
  --   「悄悄撤销删除」与「悄悄复活旧值」是同一个失效模式的两副面孔:
  --   用户删掉的东西被一条 UPDATE 弄回检索结果里,而历史上看不出发生过什么。
  --   D33② 规定的撤销路径是【从隔离区新增一行】(append-only),不是清标志位 ——
  --   那样至少留下了「它曾被删除、又被重新写入」这条可审计的痕迹。
  IF OLD.redacted_at IS NOT NULL AND NEW.redacted_at IS NULL THEN
    RAISE EXCEPTION
      '不得撤销 tombstone(%.% id=%):redacted_at 只能 NULL→非NULL。'
      '要恢复请从隔离区【新增一行】(D33②),这样历史里留得下痕迹;'
      '清标志位会让"删过又回来了"完全不可见。',
      TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.id
      USING ERRCODE = 'check_violation';
  END IF;
  IF OLD.redacted_at IS NOT NULL AND NEW.redacted_at IS DISTINCT FROM OLD.redacted_at THEN
    RAISE EXCEPTION
      '不得改写已存在的 redacted_at(%.% id=%):删除时刻是审计事实。',
      TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.id
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

-- =====================================================================
-- 3) D33② tombstone:内容表加 redacted_at(删除 = 标记 + 正文进隔离区)
-- =====================================================================
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'l1_session_summary','l2_episode','l3_fact',
    'entity_person','entity_event','entity_preference','entity_project',
    'entity_device','entity_place','entity_thing'] LOOP
    EXECUTE format('ALTER TABLE mem.%I ADD COLUMN IF NOT EXISTS redacted_at timestamptz', t);
    EXECUTE format('COMMENT ON COLUMN mem.%I.redacted_at IS %L', t,
      'D33②:tombstone 删除标记。置位后检索不再命中,正文搬进 mem.quarantine,历史链保留');
  END LOOP;
END $$;

-- 隔离区:被删除记忆的正文落点(与 §12.4「永不 delete 只移隔离区」同一套语义)
CREATE TABLE IF NOT EXISTS mem.quarantine (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  src_table          text        NOT NULL,
  src_id             bigint      NOT NULL,
  payload            jsonb       NOT NULL,   -- 被删条目的完整快照(误删可救)
  redacted_at        timestamptz NOT NULL DEFAULT now(),
  expires_at         timestamptz NOT NULL,   -- 到期后才真正消失(§8.4 quarantine_days)
  reason             text,
  sensitivity_domain mem.sensitivity_domain NOT NULL,
  crypto_tier        text NULL DEFAULT NULL
);
COMMENT ON TABLE mem.quarantine IS
  'D33②:删除 = tombstone + 正文进此表。记忆是不可重建数据,物理 DELETE 一旦误操作无法挽回';
CREATE INDEX IF NOT EXISTS idx_quarantine_expires ON mem.quarantine (expires_at);
CREATE INDEX IF NOT EXISTS idx_quarantine_src     ON mem.quarantine (src_table, src_id);

-- =====================================================================
-- 4) D33① 亲属事实的规范表示:l3_fact 三元组为唯一远程可达形态
--    → subject/predicate/object 必须可被高效精确查询(「我妹妹叫什么」走这条)
-- =====================================================================
-- 规范化列:查询前把词形折叠到这里(妹/小妹→妹妹),与检索词表共用同一份归一化
ALTER TABLE mem.l3_fact ADD COLUMN IF NOT EXISTS subject_norm   text;
ALTER TABLE mem.l3_fact ADD COLUMN IF NOT EXISTS predicate_norm text;
COMMENT ON COLUMN mem.l3_fact.predicate_norm IS
  'D33①:归一化后的谓词(称谓族折叠)。结构化轨按 (subject_norm, predicate_norm) 精确查';
-- 只对【当前有效】的事实建索引:被 supersede 或被删的不参与检索
CREATE INDEX IF NOT EXISTS idx_l3_lookup ON mem.l3_fact (subject_norm, predicate_norm)
  WHERE superseded_by IS NULL AND redacted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_l3_pred   ON mem.l3_fact (predicate_norm)
  WHERE superseded_by IS NULL AND redacted_at IS NULL;

-- entity_person 双写侧的归一化显示名(本地索引用)
ALTER TABLE mem.entity_person ADD COLUMN IF NOT EXISTS label_norm text;
CREATE INDEX IF NOT EXISTS idx_person_label ON mem.entity_person (label_norm)
  WHERE superseded_by IS NULL AND redacted_at IS NULL;

-- =====================================================================
-- 5) D33③ 远程写入:置信度封顶 0.6 + 一律 pending
--    attestation_kind 记录「这条 1.0 是怎么来的」—— 没有它,分级表退化成注释
-- =====================================================================
DO $$ BEGIN
  CREATE TYPE mem.attestation_kind AS ENUM
    ('panel_ticket',    -- 工作站面板逐条确认(唯一能产生 1.0 的路径)
     'device_signed',   -- 设备密钥验签(远程,封顶 0.6)
     'assistant_infer', -- 助理从对话流推断(≤0.6)
     'derived');        -- tool_result / rag_chunk / web_content(≤0.4)
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'l2_episode','l3_fact','pending_review',
    'entity_person','entity_event','entity_preference','entity_project',
    'entity_device','entity_place','entity_thing'] LOOP
    EXECUTE format('ALTER TABLE mem.%I ADD COLUMN IF NOT EXISTS attestation_kind mem.attestation_kind', t);
    -- ★ 只有 panel_ticket 能配 1.0。这条把 §4.4.2 的分级表从注释变成数据库拒绝。
    --
    -- ★★ 必须用 IS NOT DISTINCT FROM,不能用 = (2026-07-28 实测发现):
    --    SQL 是三值逻辑,`NULL = 'panel_ticket'` 求值为 NULL,而 CHECK 只拒绝 FALSE ——
    --    NULL 一律放行。于是「1.0 必须有票据背书」对【不填 attestation_kind】的写入
    --    完全无效,实测 1.0 + NULL 直接插入成功。这与之前 REVOKE...FROM ai_mem_remote
    --    撤的是不存在的授权是同一类错误:看着做了防护,实际没有,而且不报错。
    EXECUTE format($c$
      ALTER TABLE mem.%1$I DROP CONSTRAINT IF EXISTS %1$s_conf_needs_attestation;
      ALTER TABLE mem.%1$I ADD CONSTRAINT %1$s_conf_needs_attestation CHECK (
        source_confidence IS NULL
        OR source_confidence < 1.0
        OR attestation_kind IS NOT DISTINCT FROM 'panel_ticket'::mem.attestation_kind)
    $c$, t);
  END LOOP;
END $$;

-- =====================================================================
-- 5b) §4.5 铁律守卫升级:把「面板批准」纳入人类权威
--
-- schema.sql 里的 mem.is_user_fact 已修正为只看 provenance(原先要求
-- source_confidence ≥ 1.0,与 §4.4.2「1.0 必须有票据」冲突,导致铁律形同虚设)。
-- 这里再补一件 schema.sql 做不到的事:attestation_kind 列是本文件 §5 才加的,
-- 所以「面板批准的条目也算人类权威」只能在这一层实现。
--
-- ★ 为什么 panel_ticket 要算权威:它的含义就是【用户在记忆面板上逐条点了确认】。
--   用户在面板上纠正自己说过的话,是纠错的正当路径;若连它都挡,
--   用户就【永远无法修改自己的记忆】,只能不断追加相互矛盾的条目。
-- ★ 反过来,provenance=tool_result 且无票据的条目仍然一律挡住 —— 那是管线自作主张。
-- =====================================================================
CREATE OR REPLACE FUNCTION mem.tg_block_auto_supersede_user() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  np      mem.provenance;
  nsc     numeric;
  natt    mem.attestation_kind;
  old_auth boolean;
  new_auth boolean;
BEGIN
  IF NEW.superseded_by IS NULL
     OR OLD.superseded_by IS NOT DISTINCT FROM NEW.superseded_by THEN
    RETURN NEW;
  END IF;

  EXECUTE format(
    'SELECT provenance, source_confidence, attestation_kind FROM %I.%I WHERE id = $1',
    TG_TABLE_SCHEMA, TG_TABLE_NAME)
    INTO np, nsc, natt USING NEW.superseded_by;

  -- OLD = 被取代的既有行;NEW.superseded_by 指向取代它的新行
  old_auth := mem.is_user_fact(OLD.provenance, OLD.source_confidence)
              OR OLD.attestation_kind IS NOT DISTINCT FROM 'panel_ticket'::mem.attestation_kind;
  new_auth := mem.is_user_fact(np, nsc)
              OR natt IS NOT DISTINCT FROM 'panel_ticket'::mem.attestation_kind;

  IF old_auth AND NOT new_auth THEN
    RAISE EXCEPTION
      '自动事实(id=%,来源=%)不得 supersede 用户事实(id=%,来源=%) — §4.5 铁律。'
      '要纠正用户说过的话,正当路径是【用户再说一次】或【在记忆面板上逐条确认】'
      '(attestation_kind=panel_ticket),而不是让管线自行改写。',
      NEW.superseded_by, np, OLD.id, OLD.provenance
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

-- =====================================================================
-- 5c) ★★ 把 derived 封顶约束从 denylist 翻成 allowlist(2026-07-28 审查后)
--
-- 原写法:`provenance NOT IN ('tool_result','web_content','rag_chunk') OR ...`
-- 缺陷:**将来任何新增枚举值都让 NOT IN 为真 ⇒ 整条 CHECK 恒真 ⇒ 完全不受约束**。
--   于是"加了 provenance 忘了同步加约束"这个疏漏,后果是【默认自由】而非默认受限,
--   而且悄无声息。外联通道(WhatsApp/Signal/Discord 之类)若新增一个 provenance,
--   它会**直接绕过 0.4 封顶与强制 pending**,拿到与用户直述同级的待遇。
-- 改法:正面列举"什么算用户直述",其余一律封顶 —— 新枚举默认落在受限一侧。
--
-- ★ 本文件 §5 的 attestation CHECK 也是同一类教训(NULL 绕过三值逻辑),
--   两次都指向同一条规矩:**约束要写成拒绝优先,不能写成放行优先**。
--
-- 注:schema.sql 里的内联定义已同步改写(供全新安装);此处的 ALTER 供既有库升级 ——
--   CREATE TABLE IF NOT EXISTS 不会重建已存在表的约束。
-- =====================================================================
-- ★ 清理旧约束必须按【定义形状】,不能按名字猜(2026-07-28 实测踩到):
--   历史命名不一致 —— l2_episode 用短名 `l2_derived_conf_cap`、l3_fact 用 `l3_...`,
--   而 entity_* 用的是全表名前缀 `entity_person_derived_conf_cap`。
--   按 `<表名>_derived_conf_cap` 去 DROP IF EXISTS,在 l2/l3 上根本没命中,
--   结果是**新旧两条并存**。安全上没破(两条 CHECK 是 AND,严的那条仍生效),
--   但旧的那条会给以后读代码的人错误示范 —— 而它示范的正是刚被判定有害的 denylist 形状。
DO $$
DECLARE t text; c record;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'l2_episode','l3_fact',
    'entity_person','entity_event','entity_preference','entity_project',
    'entity_device','entity_place','entity_thing'] LOOP

    FOR c IN SELECT conname FROM pg_constraint
              WHERE conrelid = format('mem.%I', t)::regclass AND contype = 'c'
                AND pg_get_constraintdef(oid) LIKE '%provenance%'
                AND pg_get_constraintdef(oid) LIKE '%source_confidence <= 0.4%'
    LOOP
      EXECUTE format('ALTER TABLE mem.%I DROP CONSTRAINT %I', t, c.conname);
    END LOOP;

    EXECUTE format($c$
      ALTER TABLE mem.%1$I ADD CONSTRAINT %1$s_derived_conf_cap CHECK (
        provenance IN ('user_typed','user_voice_asr')
        OR source_confidence IS NULL
        OR source_confidence <= 0.4)
    $c$, t);
  END LOOP;
END $$;

-- pending_review 的形状不同:它额外强制 status='pending'(必须停在待审队列里)
DO $$
DECLARE c record;
BEGIN
  FOR c IN SELECT conname FROM pg_constraint
            WHERE conrelid = 'mem.pending_review'::regclass AND contype = 'c'
              AND pg_get_constraintdef(oid) LIKE '%provenance%'
              AND pg_get_constraintdef(oid) LIKE '%pending%'
  LOOP
    EXECUTE format('ALTER TABLE mem.pending_review DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;
ALTER TABLE mem.pending_review ADD CONSTRAINT pr_derived_forced_pending CHECK (
  provenance IN ('user_typed','user_voice_asr')
  OR (status = 'pending'
      AND (source_confidence IS NULL OR source_confidence <= 0.4)));

-- =====================================================================
-- 6) 绑触发器(不可覆盖的记忆内容表)
--
-- ★★ 本清单【不含 l1_session_summary】,这是有意的(2026-07-28 实测后修正):
--    L1 是滚动压缩的会话摘要 —— 它按设计【就要被反复改写】,且只存续数天、
--    可由对话重算。把 append-only 绑上去,这张表直接不可用:任何一次滚动压缩
--    都会被拒,而拒绝时给出的建议「新增一行并把旧行 superseded_by 指向它」
--    在这张表上根本无法执行 —— 它没有 superseded_by 列。
--    §4.5「记忆内容不可覆盖」管的是【会被当作事实检索出去】的 L2/L3/实体图谱,
--    不是工作缓冲。
--
-- ★ 本清单的每张表都必须同时具备 superseded_by 与 redacted_at:
--   两个触发器分别静态读了这两列,绑到缺列的表上会在【触发时】报
--   "record OLD has no field" —— 响亮,但要等到有人真去 UPDATE 才暴露。
-- =====================================================================
DO $$
DECLARE t text;
BEGIN
  -- 清掉此前误绑在 L1 上的两个触发器(旧版本 schema-p3a 绑过)
  DROP TRIGGER IF EXISTS trg_append_only   ON mem.l1_session_summary;
  DROP TRIGGER IF EXISTS trg_supersede_dir ON mem.l1_session_summary;

  FOREACH t IN ARRAY ARRAY[
    'l2_episode','l3_fact',
    'entity_person','entity_event','entity_preference','entity_project',
    'entity_device','entity_place','entity_thing'] LOOP
    EXECUTE format('DROP TRIGGER IF EXISTS trg_append_only ON mem.%I', t);
    EXECUTE format('CREATE TRIGGER trg_append_only BEFORE UPDATE ON mem.%I
                    FOR EACH ROW EXECUTE FUNCTION mem.tg_append_only()', t);
    EXECUTE format('DROP TRIGGER IF EXISTS trg_supersede_dir ON mem.%I', t);
    EXECUTE format('CREATE TRIGGER trg_supersede_dir BEFORE UPDATE ON mem.%I
                    FOR EACH ROW EXECUTE FUNCTION mem.tg_supersede_direction()', t);
  END LOOP;
END $$;

-- =====================================================================
-- 7) pending_review:禁止批量确认(§4.4.2「逐条确认,禁批量」)
--    ★ 这是少数能在 DB 层真封死的写路径规则:一条语句改多行直接报错。
--    批量确认是投毒的放大器 —— 攻击者塞 50 条,用户一次「全选」就全进库。
-- =====================================================================
ALTER TABLE mem.pending_review ADD COLUMN IF NOT EXISTS review_status text;
ALTER TABLE mem.pending_review ADD COLUMN IF NOT EXISTS reviewed_at   timestamptz;

CREATE OR REPLACE FUNCTION mem.tg_no_bulk_review() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE n bigint;
BEGIN
  SELECT count(*) INTO n FROM new_table;
  IF n > 1 THEN
    RAISE EXCEPTION
      '禁止批量确认(§4.4.2):本条语句要改 % 行 pending_review。'
      '必须逐条确认 —— 批量确认是投毒的放大器,攻击者塞满队列后一次「全选」就全进库,'
      '分级在确认这一步被人工旁路。', n
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS trg_no_bulk_review ON mem.pending_review;
CREATE TRIGGER trg_no_bulk_review
  AFTER UPDATE ON mem.pending_review
  REFERENCING NEW TABLE AS new_table
  FOR EACH STATEMENT EXECUTE FUNCTION mem.tg_no_bulk_review();

-- =====================================================================
-- 7b) 编码指纹登记(S2 向量轨)
--     向量只有在【同一套编码参数下产生】时才可比。改了模型/前缀/维度之后,
--     老向量与新查询就不在同一个空间里 —— 检索**不报错,只悄悄变差**。
--     故把参数存这里,启动时双向比对,不一致即拒绝启动(§12.3 禁止静默降级)。
-- =====================================================================
CREATE TABLE IF NOT EXISTS mem.vector_space (
  space_id   text PRIMARY KEY,
  digest     text        NOT NULL,
  params     jsonb       NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE mem.vector_space IS
  '编码指纹。digest 变了 ⇒ 全部向量必须重建,否则检索静默劣化';

-- L2 情节:向量点 id 与正文的关联(载荷不存正文,故需要这条回指)
ALTER TABLE mem.l2_episode ADD COLUMN IF NOT EXISTS vector_point_id bigint;
CREATE INDEX IF NOT EXISTS idx_l2_active ON mem.l2_episode (id)
  WHERE superseded_by IS NULL AND redacted_at IS NULL;

-- =====================================================================
-- 8) 权限:隔离区与新列跟随既有角色二分
-- =====================================================================
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='ai_mem_local') THEN
    GRANT SELECT, INSERT, UPDATE ON mem.quarantine TO ai_mem_local;
    GRANT SELECT, INSERT, UPDATE ON mem.vector_space TO ai_mem_local;
    REVOKE ALL ON mem.vector_space FROM ai_mem_remote;
    -- 隔离区可能含 S2 正文 → 远程绝不可读(与 secret_ref 同等对待)
    REVOKE ALL ON mem.quarantine FROM ai_mem_remote;

    -- ★★ 触发器守卫函数必须显式授 EXECUTE 给 ai_mem_local(2026-07-28 实测修正)。
    --   roles.sql 里 `REVOKE ALL ON ALL FUNCTIONS IN SCHEMA mem FROM PUBLIC` 之后,
    --   这些函数对 ai_mem_local 就没有 EXECUTE 了。
    --   ★ 我此前在 roles.sql 注释里断言「PG 在 CREATE TRIGGER 时检查 EXECUTE,
    --     触发时不再检查」—— **实测证明这是错的**:supersede 时报
    --     `42501 permission denied for function mem.tg_block_auto_supersede_user`。
    --     (INSERT 没报错是因为 tg_set_write_seq 是 SECURITY DEFINER,以属主身份跑。)
    --   授 EXECUTE 是安全的:它们 RETURNS trigger,脱离触发器上下文直接调用会失败。
    GRANT EXECUTE ON FUNCTION mem.tg_append_only()               TO ai_mem_local;
    GRANT EXECUTE ON FUNCTION mem.tg_supersede_direction()       TO ai_mem_local;
    GRANT EXECUTE ON FUNCTION mem.tg_no_bulk_review()            TO ai_mem_local;
    GRANT EXECUTE ON FUNCTION mem.tg_block_auto_supersede_user() TO ai_mem_local;
    GRANT EXECUTE ON FUNCTION mem.is_user_fact(mem.provenance, numeric) TO ai_mem_local;
  END IF;
END $$;

-- =====================================================================
-- =====================================================================
--  S3 · 完整记忆闸(分级 · E3 · 队列 · 熔断 · 冲突)
--  2026-07-28 · 规格提取后落地。四条裁定见 §14 P3a S3 与本文件各节注释。
-- =====================================================================
-- =====================================================================

-- =====================================================================
-- S3-1) ★★ 修回归:supersede 守卫要【同时】看来源与档位
--
-- 2026-07-28 早些时候把 is_user_fact 从
--     provenance IN (user_typed, user_voice_asr) AND coalesce(sc,0) >= 1.0
-- 改成只看 provenance。那个修复救活了 §4.5 铁律(此前 sc>=1.0 的要求让
-- 「用户事实」几乎是空集,自动来源可以随意覆盖用户的话),
-- **但同时打开了另一扇门**:原定义里 `sc >= 1.0` 顺带挡住了
-- 「低档用户事实 supersede 高档用户事实」。
--
-- 后果:一条 (provenance=user_typed, sc=0.6, attestation_kind='assistant_infer')
-- 的行可以 supersede 一条面板逐条确认过的 (1.0, 'panel_ticket') 条目 ——
-- 而 §4.4.2 分级表第三行明写「助理从对话流推断 ❌ 不可自动 supersede」。
-- 那个 ❌ 此前零强制。
--
-- ★ 教训:权威不是一个维度。「谁说的」(provenance)与「多确定」(档位)
--   是正交的两件事,守卫必须同时看。只看其一,另一半必然漏。
-- =====================================================================
CREATE OR REPLACE FUNCTION mem.authority_rank(
    p mem.provenance, sc numeric, att mem.attestation_kind)
  RETURNS int LANGUAGE sql IMMUTABLE AS $$
  -- 权威序:数字越大越权威。只用于「能否 supersede」的比较,不用于检索排序。
  --   3 面板逐条确认(人在场,逐条看过)          —— §4.4.2 第一档
  --   2 用户直述(打字/语音),但无票据背书        —— §4.4.2 第三档
  --   1 设备签名的远程写入(D33③ 封顶 0.6)      —— 目前无生产者
  --   0 派生(tool_result / rag_chunk / web_content)—— §4.4.2 第四档
  SELECT CASE
    WHEN att IS NOT DISTINCT FROM 'panel_ticket'::mem.attestation_kind
         AND p IN ('user_typed','user_voice_asr')            THEN 3
    WHEN p IN ('user_typed','user_voice_asr')                THEN 2
    WHEN att IS NOT DISTINCT FROM 'device_signed'::mem.attestation_kind THEN 1
    ELSE 0
  END;
$$;
COMMENT ON FUNCTION mem.authority_rank(mem.provenance, numeric, mem.attestation_kind) IS
  '§4.4.2 权威序。★ 同时看来源与档位 —— 只看其一必漏(2026-07-28 实测)';

-- ★ 注意 panel_ticket 那一档【必须同时要求 provenance ∈ 用户直述】:
--   否则 (tool_result, 0.4, panel_ticket) 这种组合会拿到最高权威 ——
--   而 verify.sql 的 B5c 用例正是这么构造的,它本身就演示了这个洞。
--   下面 S3-2 的 CHECK 从写入侧堵死该组合;此处是读侧的第二道。

CREATE OR REPLACE FUNCTION mem.tg_block_auto_supersede_user() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  np      mem.provenance;
  nsc     numeric;
  natt    mem.attestation_kind;
  old_rank int;
  new_rank int;
BEGIN
  IF NEW.superseded_by IS NULL
     OR OLD.superseded_by IS NOT DISTINCT FROM NEW.superseded_by THEN
    RETURN NEW;
  END IF;

  EXECUTE format(
    'SELECT provenance, source_confidence, attestation_kind FROM %I.%I WHERE id = $1',
    TG_TABLE_SCHEMA, TG_TABLE_NAME)
    INTO np, nsc, natt USING NEW.superseded_by;

  old_rank := mem.authority_rank(OLD.provenance, OLD.source_confidence, OLD.attestation_kind);
  new_rank := mem.authority_rank(np, nsc, natt);

  -- ★ 判据是【不得降权覆盖】,不是「自动 vs 人类」。后者只是它的一个特例。
  IF new_rank < old_rank THEN
    RAISE EXCEPTION
      '不得以更低权威覆盖更高权威(%.% id=%):旧行权威=%(来源 %),新行权威=%(来源 %)。'
      '§4.4.2 分级表:面板逐条确认(3) > 用户直述(2) > 设备签名(1) > 派生(0)。'
      '要纠正一条面板确认过的记忆,正当路径是【再次在面板上逐条确认】,'
      '而不是让一条推断出来的候选把它盖掉。',
      TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.id,
      old_rank, OLD.provenance, new_rank, np
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

-- =====================================================================
-- S3-2) attestation_kind 与 provenance 的一致性(写入侧堵死伪造权威)
--
-- 今天可以合法插入 (provenance='tool_result', attestation_kind='panel_ticket')。
-- repo.insert_fact 接受 FactWrite 里任意 attestation_kind,而 S3 即将新增的
-- 「面板确认 → 写库」路径正好会走这条缝。
-- =====================================================================
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'l2_episode','l3_fact','pending_review',
    'entity_person','entity_event','entity_preference','entity_project',
    'entity_device','entity_place','entity_thing'] LOOP
    EXECUTE format($c$
      ALTER TABLE mem.%1$I DROP CONSTRAINT IF EXISTS %1$s_panel_ticket_needs_user;
      ALTER TABLE mem.%1$I ADD CONSTRAINT %1$s_panel_ticket_needs_user CHECK (
        attestation_kind IS DISTINCT FROM 'panel_ticket'::mem.attestation_kind
        OR provenance IN ('user_typed','user_voice_asr'))
    $c$, t);
  END LOOP;
END $$;

-- =====================================================================
-- S3-3) ★★ 队列状态机:一个状态列,不是两个
--
-- 此前 pending_review 有两套状态列:
--   status         —— schema.sql 建,被 pr_derived_forced_pending 引用,有索引,**但无取值 CHECK**
--   review_status  —— schema-p3a 后加,批准流程实际改的是它
-- 后果:status 永远停在 'pending' ⇒ CHECK 恒真 ⇒
-- 「派生候选必须停在待审队列」这条约束**结构上不可能被触发**。
-- 这比漏了约束更毒 —— 它看起来在。
--
-- 裁定:**保留 status,废弃 review_status**(status 被 CHECK 与索引引用,动它代价大)。
-- =====================================================================
-- 把历史上写进 review_status 的值迁到 status(幂等)
UPDATE mem.pending_review
   SET status = review_status
 WHERE review_status IS NOT NULL
   AND review_status <> status
   AND status = 'pending';

ALTER TABLE mem.pending_review DROP CONSTRAINT IF EXISTS pr_status_enum;
ALTER TABLE mem.pending_review ADD CONSTRAINT pr_status_enum CHECK (
  status IN ('pending','approved','rejected','expired'));

COMMENT ON COLUMN mem.pending_review.review_status IS
  '★ 已废弃(S3):状态机唯一的列是 status。保留本列仅为不破坏历史行,新代码一律不读不写';

-- 终态不可再转出 —— 否则「已拒绝」可被悄悄改回 pending 再确认一次
CREATE OR REPLACE FUNCTION mem.tg_pending_terminal() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status <> 'pending' AND NEW.status IS DISTINCT FROM OLD.status THEN
    RAISE EXCEPTION
      '待审条目已处于终态 %(id=%),不得再转出。'
      '若要重新提交,请【新建一条候选】—— 复用旧条目会让审计里看不出它被处理过几次。',
      OLD.status, OLD.id
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_pending_terminal ON mem.pending_review;
CREATE TRIGGER trg_pending_terminal BEFORE UPDATE ON mem.pending_review
  FOR EACH ROW EXECUTE FUNCTION mem.tg_pending_terminal();

-- =====================================================================
-- S3-4) ★★ 队列行的 TOCTOU:候选内容不可在确认前被改
--
-- 场景:面板上显示「候选 A 将取代事实 X」,你点确认;确认前的一瞬间
-- candidate_body 被改成 B、supersedes_ref 被改成 Y。
-- tg_no_bulk_review 只数行数、不看列;pending_review 又不在 append-only 的绑定清单里。
-- gate.py 的票据之所以绑 candidate_sha256,防的正是这一手 —— 但那道防护还没接到队列上。
-- =====================================================================
ALTER TABLE mem.pending_review ADD COLUMN IF NOT EXISTS candidate_sha256 text;
COMMENT ON COLUMN mem.pending_review.candidate_sha256 IS
  '入队时算的候选哈希。确认请求必须回传它 —— 对不上即拒(防「面板看到 A、确认进库 B」)';

CREATE OR REPLACE FUNCTION mem.tg_pending_immutable() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  -- ★ 只有 status(状态机)可改,其余一律冻结。用**正面列举冻结列**而不是
  --   「除了 status 都不能改」—— 后者在加新列时默认自由,与本项目 allowlist 的
  --   一贯取向相反。这里刻意逐列写出,加列时会被 code review 看见。
  IF NEW.candidate_body     IS DISTINCT FROM OLD.candidate_body
     OR NEW.provenance      IS DISTINCT FROM OLD.provenance
     OR NEW.supersedes_ref  IS DISTINCT FROM OLD.supersedes_ref
     OR NEW.source_confidence  IS DISTINCT FROM OLD.source_confidence
     OR NEW.sensitivity_domain IS DISTINCT FROM OLD.sensitivity_domain
     OR NEW.candidate_sha256 IS DISTINCT FROM OLD.candidate_sha256
     OR NEW.origin_device_id IS DISTINCT FROM OLD.origin_device_id
     OR NEW.write_seq       IS DISTINCT FROM OLD.write_seq
     -- 溯源三件套:改了它们,这条候选就说不清是谁在什么时候提的
     OR NEW.session_id      IS DISTINCT FROM OLD.session_id
     OR NEW.asserted_at     IS DISTINCT FROM OLD.asserted_at
     OR NEW.created_at      IS DISTINCT FROM OLD.created_at
     -- TTL 在入队时定死:可延长 = 可让一条候选永久占着熔断额度;
     -- 可缩短 = 可把别人的候选逼到过期。两个方向都不许。
     OR NEW.expires_at      IS DISTINCT FROM OLD.expires_at THEN
    RAISE EXCEPTION
      '待审候选一经入队即冻结(id=%):不得修改正文/来源/取代目标/定级。'
      '否则「你在面板上看到的」与「确认后进库的」可以是两样东西。'
      '要改请拒绝本条并新建候选。', OLD.id
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_pending_immutable ON mem.pending_review;
CREATE TRIGGER trg_pending_immutable BEFORE UPDATE ON mem.pending_review
  FOR EACH ROW EXECUTE FUNCTION mem.tg_pending_immutable();

-- =====================================================================
-- S3-5) 队列过期(裁定:过期进隔离区,不静默删除)
--
-- 方案书未规定 pending_review 的 TTL。与熔断叠加会死锁:
--   队列塞满 50 条 → 熔断暂停接受候选 → 若条目永不过期且人不逐条处理
--   → **无出口的永久降级**,而攻击者只需塞满队列即达成 DoS。
-- =====================================================================
ALTER TABLE mem.pending_review
  ADD COLUMN IF NOT EXISTS expires_at timestamptz;
COMMENT ON COLUMN mem.pending_review.expires_at IS
  'S3 裁定:待审候选有 TTL。到期由 GC 转 expired 并把正文搬进 mem.quarantine —— '
  '不静默删除(§12.4 永不 delete 只移隔离区),也不放它永久占着熔断额度';
CREATE INDEX IF NOT EXISTS idx_pr_expires ON mem.pending_review (expires_at)
  WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_pr_pending ON mem.pending_review (id)
  WHERE status = 'pending';

-- =====================================================================
-- S3-6) 熔断状态:必须持久化,否则「重启即复位」使熔断无效
-- =====================================================================
CREATE TABLE IF NOT EXISTS mem.circuit_breaker (
  name        text PRIMARY KEY,
  tripped_at  timestamptz,
  reason      text,
  -- 恢复也走面板票据(与逐条确认同级)—— 规格里「显式恢复」是循环引用,此处裁定
  cleared_at  timestamptz,
  cleared_by  text
);
COMMENT ON TABLE mem.circuit_breaker IS
  'S3 熔断状态。★ 必须落库:进程内存态的熔断「重启即复位」,等于没有';

-- =====================================================================
-- S3-7) 票据移进 PG,用 UPDATE...RETURNING 原子消费
--
-- 进程内存态的票据存在两个问题:与 pending_review.id 无绑定;
-- 多进程/重启后失效。原子消费同时挡住并发双花。
-- =====================================================================
CREATE TABLE IF NOT EXISTS mem.write_ticket (
  ticket_id        text PRIMARY KEY,
  session_id       text        NOT NULL,
  candidate_sha256 text        NOT NULL,
  pending_id       bigint      REFERENCES mem.pending_review(id),
  issued_at        timestamptz NOT NULL DEFAULT now(),
  expires_at       timestamptz NOT NULL,
  consumed_at      timestamptz
);
CREATE INDEX IF NOT EXISTS idx_ticket_live ON mem.write_ticket (expires_at)
  WHERE consumed_at IS NULL;
COMMENT ON TABLE mem.write_ticket IS
  '§4.4.2 面板票据 —— 1.0 的唯一来源。消费必须是 UPDATE...RETURNING(原子),'
  '否则并发两次确认可以双花同一张票';

-- =====================================================================
-- S3-8) 冲突检测的索引支撑
--    裁定:同 subject_norm + predicate_norm 的【活跃行】即冲突候选(结构判据,不用 LLM)。
--    语义冲突留给面板上的人 —— 机器只负责把「这条要取代谁」摆到你面前。
-- =====================================================================
CREATE INDEX IF NOT EXISTS idx_l3_conflict
  ON mem.l3_fact (subject_norm, predicate_norm)
  WHERE superseded_by IS NULL AND redacted_at IS NULL;

-- =====================================================================
-- S3-9) 授权
-- =====================================================================
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ai_mem_local') THEN
    GRANT SELECT, INSERT, UPDATE ON mem.pending_review TO ai_mem_local;
    GRANT SELECT, INSERT, UPDATE ON mem.circuit_breaker TO ai_mem_local;
    GRANT SELECT, INSERT, UPDATE ON mem.write_ticket    TO ai_mem_local;
    GRANT INSERT, SELECT          ON mem.gate_rejection TO ai_mem_local;
    GRANT USAGE ON ALL SEQUENCES IN SCHEMA mem TO ai_mem_local;
    GRANT EXECUTE ON FUNCTION mem.authority_rank(
      mem.provenance, numeric, mem.attestation_kind) TO ai_mem_local;
    GRANT EXECUTE ON FUNCTION mem.tg_pending_terminal()  TO ai_mem_local;
    GRANT EXECUTE ON FUNCTION mem.tg_pending_immutable() TO ai_mem_local;
  END IF;
  -- ★★ 撤掉 pending_review 与 circuit_breaker 的 DELETE(2026-07-28 实测发现)
  --
  -- roles.sql 有一句 `GRANT ... DELETE ON ALL TABLES IN SCHEMA mem`,并在注释里
  -- 写着「pending_review 保留全 DML 供确认/清理」—— 那句写在 S3 定下队列语义**之前**。
  -- S3 之后:过期候选转 expired + 正文进隔离区(§12.4 永不 delete),终态不可转出。
  -- ⇒ **不存在任何需要 DELETE 队列行的正当流程**。
  --
  -- 而删一行队列记录,恰好抹掉「某人提交过这条候选」这个证据 ——
  -- 正是刷爆队列的人被发现之后最想做的动作。
  -- circuit_breaker 同理:删掉那一行等于绕过「显式恢复」这条规矩。
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ai_mem_local') THEN
    REVOKE DELETE ON mem.pending_review, mem.circuit_breaker,
                     mem.write_ticket FROM ai_mem_local;
  END IF;

  -- ★ 远程角色对这四张表一个权限都不给(默认即无,此处显式撤以防将来 GRANT 泛化)
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ai_mem_remote') THEN
    REVOKE ALL ON mem.pending_review, mem.circuit_breaker,
                  mem.write_ticket, mem.gate_rejection FROM ai_mem_remote;
  END IF;
END $$;

-- =====================================================================
-- S3-10) ★ 修:pr_derived_forced_pending 把派生候选【永久】锁在 pending
--
-- 原约束:  provenance IN (user_*) OR (status='pending' AND sc<=0.4)
-- 它想说的是「派生候选**入队时**必须停在待审」,
-- 写成的却是「派生候选**永远**是 pending」—— 于是它们无法被批准、也无法被拒绝。
--
-- 后果是一个死锁,而且正是本次裁定要避免的那个:
--   队列被派生候选填满 → 熔断 → 但这些候选**转不到终态** → 队列永远清不空
--   → 熔断永远无法恢复。攻击者塞满队列即达成永久 DoS。
--
-- ★ 教训:CHECK 是**行级**的,它在 INSERT 与 UPDATE 上同样生效 ——
--   想表达「入库时的初始状态」必须用 BEFORE INSERT 触发器,
--   把它写进 CHECK 等于顺手禁掉了一切后续状态转移。
-- =====================================================================
ALTER TABLE mem.pending_review DROP CONSTRAINT IF EXISTS pr_derived_forced_pending;
-- ★ 新名也要先 DROP —— 否则第二次应用本文件会因「已存在」而失败,
--   而本文件第一行就承诺「幂等,可重复应用」。2026-07-28 实测踩到:
--   改完之后只跑了一遍,没重跑幂等验证。**改幂等脚本必须重验幂等,不能只在首次验。**
ALTER TABLE mem.pending_review DROP CONSTRAINT IF EXISTS pr_derived_conf_cap;

-- CHECK 只保留【与状态无关】的那一半:派生来源的置信度封顶
ALTER TABLE mem.pending_review ADD CONSTRAINT pr_derived_conf_cap CHECK (
  provenance IN ('user_typed','user_voice_asr')
  OR source_confidence IS NULL
  OR source_confidence <= 0.4);

-- 「必须停在待审」改由 BEFORE INSERT 强制 —— 只管入队那一刻
CREATE OR REPLACE FUNCTION mem.tg_pending_initial_state() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.provenance NOT IN ('user_typed','user_voice_asr')
     AND NEW.status IS DISTINCT FROM 'pending' THEN
    RAISE EXCEPTION
      '派生来源(%)的候选必须以 pending 入队,不得直接以 % 入库 —— '
      '§4.4.2 第四档:它们要停下来等人逐条确认,而不是自己给自己盖章。',
      NEW.provenance, NEW.status
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_pending_initial_state ON mem.pending_review;
CREATE TRIGGER trg_pending_initial_state BEFORE INSERT ON mem.pending_review
  FOR EACH ROW EXECUTE FUNCTION mem.tg_pending_initial_state();

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ai_mem_local') THEN
    GRANT EXECUTE ON FUNCTION mem.tg_pending_initial_state() TO ai_mem_local;
  END IF;
END $$;
