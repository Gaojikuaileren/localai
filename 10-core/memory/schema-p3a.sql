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
    IF oldv IS DISTINCT FROM newv THEN
      RAISE EXCEPTION
        '记忆内容不可覆盖(§4.5):表 %.% 的列 % 试图从 % 改为 %。'
        '冲突处理必须【新增一行并把旧行 superseded_by 指向它】,不是改写。'
        '删除请用 redacted_at(D33②:tombstone + 隔离区)。',
        TG_TABLE_SCHEMA, TG_TABLE_NAME, col,
        coalesce(left(oldv, 40), 'NULL'), coalesce(left(newv, 40), 'NULL')
        USING ERRCODE = 'check_violation';
    END IF;
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
-- 6) 绑触发器(记忆内容表 + pending_review)
-- =====================================================================
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'l1_session_summary','l2_episode','l3_fact',
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
-- 8) 权限:隔离区与新列跟随既有角色二分
-- =====================================================================
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='ai_mem_local') THEN
    GRANT SELECT, INSERT, UPDATE ON mem.quarantine TO ai_mem_local;
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
