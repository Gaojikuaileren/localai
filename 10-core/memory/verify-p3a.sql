-- =====================================================================
-- verify-p3a.sql · S0 否定用例:那四条【实测成功过】的攻击必须全部被拒
--
-- 2026-07-28 实测记录(修复前,以 postgres 身份):
--   ① UPDATE 内容列覆盖          → 成功
--   ② 自动事实提权成 user_typed/1.0 → 成功
--   ③ 提权后 supersede 用户事实   → 成功(完整绕过链)
--   ④ superseded_by 改回 NULL 复活 → 成功
-- 本文件逐条复跑,修复后必须【全部 ERROR】。标 `期望: ERROR` 的用例报错才算通过。
-- =====================================================================
\set ON_ERROR_STOP off
\pset pager off

\echo '======================================================================'
\echo ' A. 结构(S0 新增物)'
\echo '======================================================================'
-- 10 张内容表 + quarantine 自己也有该列 = 11(上一版断言写成 10,是断言错不是 schema 错)
SELECT CASE WHEN count(*)=10 THEN 'PASS' ELSE 'FAIL' END AS a1_redacted_at, count(*) AS n
  FROM information_schema.columns
 WHERE table_schema='mem' AND column_name='redacted_at' AND table_name <> 'quarantine';

SELECT CASE WHEN count(*)=1 THEN 'PASS' ELSE 'FAIL' END AS a2_quarantine
  FROM information_schema.tables WHERE table_schema='mem' AND table_name='quarantine';

SELECT CASE WHEN count(*)>=20 THEN 'PASS' ELSE 'FAIL' END AS a3_triggers, count(*) AS n
  FROM information_schema.triggers
 WHERE trigger_schema='mem' AND trigger_name IN ('trg_append_only','trg_supersede_dir');

SELECT CASE WHEN count(*)=1 THEN 'PASS' ELSE 'FAIL' END AS a4_no_bulk
  FROM information_schema.triggers
 WHERE trigger_schema='mem' AND trigger_name='trg_no_bulk_review';

SELECT CASE WHEN count(*)>=2 THEN 'PASS' ELSE 'FAIL' END AS a5_l3_norm_cols, count(*) AS n
  FROM information_schema.columns
 WHERE table_schema='mem' AND table_name='l3_fact'
   AND column_name IN ('subject_norm','predicate_norm');

\echo '======================================================================'
\echo ' B. ★★ 那四条攻击 —— 修复后必须全部被拒'
\echo '======================================================================'
-- 埋两行
INSERT INTO mem.l3_fact (statement, subject_norm, predicate_norm, object,
                         provenance, source_confidence, sensitivity_domain, attestation_kind)
VALUES ('P3A_用户亲口说的','我','妹妹','小雨','user_typed',1.0,'S0','panel_ticket');
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain, attestation_kind)
VALUES ('P3A_工具推断的','tool_result',0.4,'S0','derived');

\echo '-- 攻击① 直接覆盖内容列 —— 期望: ERROR(记忆内容不可覆盖) --'
UPDATE mem.l3_fact SET statement='P3A_已被悄悄改写' WHERE statement='P3A_用户亲口说的';

\echo '-- 攻击② 把自动事实提权成 user_typed/1.0 —— 期望: ERROR --'
UPDATE mem.l3_fact SET source_confidence=1.0, provenance='user_typed' WHERE statement='P3A_工具推断的';

\echo '-- 攻击③ supersede 用户事实(自动事实发起)—— 期望: ERROR(§4.5 铁律) --'
UPDATE mem.l3_fact SET superseded_by=(SELECT id FROM mem.l3_fact WHERE statement='P3A_工具推断的')
 WHERE statement='P3A_用户亲口说的';

\echo '-- 攻击④ 复活:先合法 supersede,再把指针改回 NULL --'
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain, attestation_kind)
VALUES ('P3A_新的用户事实','user_typed',1.0,'S0','panel_ticket');
UPDATE mem.l3_fact SET superseded_by=(SELECT id FROM mem.l3_fact WHERE statement='P3A_新的用户事实')
 WHERE statement='P3A_用户亲口说的';
SELECT CASE WHEN superseded_by IS NOT NULL THEN 'PASS — 合法 supersede 应当成功'
            ELSE 'FAIL — 合法路径被误伤' END AS b4a_legit_supersede
  FROM mem.l3_fact WHERE statement='P3A_用户亲口说的';
\echo '-- 攻击④ 现在把它改回 NULL —— 期望: ERROR(不得复活) --'
UPDATE mem.l3_fact SET superseded_by=NULL WHERE statement='P3A_用户亲口说的';

\echo '-- 攻击⑤ 自指 —— 期望: ERROR(历史链成环) --'
UPDATE mem.l3_fact SET superseded_by=id WHERE statement='P3A_新的用户事实';

\echo '======================================================================'
\echo ' C. D33③ 置信度 1.0 必须有 panel_ticket 背书'
\echo '======================================================================'
\echo '-- 期望: ERROR(1.0 但 attestation_kind=device_signed)--'
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain, attestation_kind)
VALUES ('P3A_手机想直写1.0','user_typed',1.0,'S0','device_signed');
\echo '-- 期望: ERROR(1.0 但 attestation_kind 为 NULL)--'
-- ★ 这条上一版只发 INSERT 没断言结果,于是它【真的插进去了】却没人发现:
--   SQL 三值逻辑下 NULL = 'panel_ticket' 是 NULL,CHECK 只拒 FALSE → NULL 放行。
--   已改用 IS NOT DISTINCT FROM。断言必须显式,否则「期望 ERROR」只是期望。
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain)
VALUES ('P3A_无背书的1.0','user_typed',1.0,'S0');
SELECT CASE WHEN count(*)=0 THEN 'PASS — 已被拒'
            ELSE 'FAIL — ★ NULL 绕过了 CHECK' END AS c2_null_attestation
  FROM mem.l3_fact WHERE statement='P3A_无背书的1.0';
-- 0.6 的远程候选应当可以
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain, attestation_kind)
VALUES ('P3A_手机候选0.6','user_typed',0.6,'S0','device_signed');
SELECT CASE WHEN count(*)=1 THEN 'PASS' ELSE 'FAIL' END AS c3_remote_06
  FROM mem.l3_fact WHERE statement='P3A_手机候选0.6';

\echo '======================================================================'
\echo ' D. 禁止批量确认(§4.4.2)'
\echo '======================================================================'
INSERT INTO mem.pending_review (candidate_body, provenance, sensitivity_domain)
VALUES ('{"t":"P3A_c1"}','user_typed','S0'), ('{"t":"P3A_c2"}','user_typed','S0');
\echo '-- 期望: ERROR(一条语句改 2 行)--'
UPDATE mem.pending_review SET review_status='approved'
 WHERE candidate_body->>'t' LIKE 'P3A_c%';
-- 逐条改应当可以
UPDATE mem.pending_review SET review_status='approved'
 WHERE candidate_body->>'t' = 'P3A_c1';
SELECT CASE WHEN count(*)=1 THEN 'PASS' ELSE 'FAIL' END AS d2_single_ok
  FROM mem.pending_review WHERE candidate_body->>'t'='P3A_c1' AND review_status='approved';

\echo '======================================================================'
\echo ' E. tombstone 删除(D33②)是允许的'
\echo '======================================================================'
UPDATE mem.l3_fact SET redacted_at=now() WHERE statement='P3A_手机候选0.6';
SELECT CASE WHEN count(*)=1 THEN 'PASS' ELSE 'FAIL' END AS e1_tombstone_ok
  FROM mem.l3_fact WHERE statement='P3A_手机候选0.6' AND redacted_at IS NOT NULL;

\echo '======================================================================'
\echo ' F. 清理'
\echo '======================================================================'
DELETE FROM mem.pending_review WHERE candidate_body->>'t' LIKE 'P3A_%';
DELETE FROM mem.l3_fact WHERE statement LIKE 'P3A_%';
SELECT CASE WHEN count(*)=0 THEN 'PASS — 已清' ELSE 'WARN — 有残留' END AS f1_cleanup, count(*) AS n
  FROM mem.l3_fact WHERE statement LIKE 'P3A_%';

\echo '======================================================================'
\echo ' 判读:标 PASS 的要 PASS;标「期望: ERROR」的必须真的报错(报错=防护生效)'
\echo '======================================================================'
