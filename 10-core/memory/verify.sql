-- =====================================================================
-- verify.sql · 记忆库 schema 隔离与约束的【否定用例】
-- §10/§6.8:隔离相关否定用例必须先于功能写,与权限区域同级。
--
-- 跑法(以 ai-mem 经 SSPI):
--   psql -h 127.0.0.1 -U mem_rw -d memory -f verify.sql
-- 每个用例自报 PASS/FAIL。任何 FAIL 都必须在继续之前解决。
--
-- ★ 本文件不是"查查看对不对",而是【试图攻破】:多数用例期望的是被拒绝。
-- =====================================================================
\set ON_ERROR_STOP off
\pset pager off

\echo '======================================================================'
\echo ' A. 结构存在性'
\echo '======================================================================'

SELECT CASE WHEN count(*) >= 24 THEN 'PASS' ELSE 'FAIL' END AS a1_tables,
       count(*) AS actual
  FROM information_schema.tables WHERE table_schema='mem' AND table_type='BASE TABLE';

SELECT CASE WHEN count(*) = 2 THEN 'PASS' ELSE 'FAIL' END AS a2_roles, count(*) AS actual
  FROM pg_roles WHERE rolname IN ('ai_mem_local','ai_mem_remote');

-- ★ 视图必须带 security_barrier(核验 FAIL 修正项;丢了它 = 侧信道回归)
SELECT CASE WHEN reloptions::text LIKE '%security_barrier=true%'
            THEN 'PASS' ELSE 'FAIL — 视图缺 security_barrier,S2 行级过滤可被谓词下推绕过' END
       AS a3_barrier, reloptions
  FROM pg_class WHERE relname='v_memory_nons2' AND relnamespace='mem'::regnamespace;

-- 视图不得投影 write_seq(空洞会泄露 S2 行的存在性/条数/时序)
SELECT CASE WHEN count(*)=0 THEN 'PASS' ELSE 'FAIL — 视图暴露 write_seq' END AS a4_no_writeseq
  FROM information_schema.columns
 WHERE table_schema='mem' AND table_name='v_memory_nons2' AND column_name='write_seq';

\echo '======================================================================'
\echo ' B. 约束:定级强制 / crypto_tier 可空 / write_seq 服务端分配'
\echo '======================================================================'

-- B1 sensitivity_domain 全库无 DEFAULT(有 DEFAULT = 强制定级形同虚设)
SELECT CASE WHEN count(*)=0 THEN 'PASS' ELSE 'FAIL — 这些表给了 DEFAULT' END AS b1_no_default,
       coalesce(string_agg(table_name,','),'') AS offenders
  FROM information_schema.columns
 WHERE table_schema='mem' AND column_name='sensitivity_domain' AND column_default IS NOT NULL;

-- B2 crypto_tier 必须可空(D30:D22 停用加密,不能 NOT NULL 逼填空洞值)
SELECT CASE WHEN count(*)=0 THEN 'PASS' ELSE 'FAIL — crypto_tier 被建成 NOT NULL' END AS b2_crypto_nullable,
       coalesce(string_agg(table_name,','),'') AS offenders
  FROM information_schema.columns
 WHERE table_schema='mem' AND column_name='crypto_tier' AND is_nullable='NO';

-- B3 ★否定用例:不带 sensitivity_domain 的 INSERT 必须失败
\echo '-- B3 期望: ERROR (not-null violation) --'
INSERT INTO mem.l3_fact (statement, provenance) VALUES ('忘了定级的事实','user_typed');

-- B4 ★否定用例:客户端自报 write_seq 必须被服务端覆写(客户端不参与,§4.11.3)
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain, write_seq)
VALUES ('客户端谎报 write_seq','user_typed',1.0,'S0', 999999999);
SELECT CASE WHEN write_seq <> 999999999 THEN 'PASS' ELSE 'FAIL — 客户端自报值被采纳' END AS b4_writeseq_override,
       write_seq
  FROM mem.l3_fact WHERE statement='客户端谎报 write_seq';

-- B5 ★否定用例:自动事实不得 supersede 用户事实(§4.5 铁律)
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain)
VALUES ('用户亲口说的事实','user_typed',1.0,'S0');
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain)
VALUES ('工具推断的事实','tool_result',0.4,'S0');
\echo '-- B5 期望: ERROR (check_violation 自动事实不得 supersede 用户事实) --'
UPDATE mem.l3_fact SET superseded_by =
         (SELECT id FROM mem.l3_fact WHERE statement='工具推断的事实')
 WHERE statement='用户亲口说的事实';

\echo '======================================================================'
\echo ' C. ★★ S2 隔离 — 核心否定用例(核验指出原版缺失,此处补齐)'
\echo '======================================================================'

-- 埋一行 S2 记忆内容(不是整表 S2,是混表里的行级 S2 —— 最容易漏的那种)
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain)
VALUES ('S2机密内容_KANARIE_7Q4X','user_typed',1.0,'S2')
ON CONFLICT DO NOTHING;
-- 再埋一行非 S2 作对照(证明视图确实在工作,不是把什么都挡了)
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain)
VALUES ('普通内容_KANARIE_OK','user_typed',1.0,'S0')
ON CONFLICT DO NOTHING;
-- 一条 S2 凭证句柄
INSERT INTO mem.secret_ref (ref, value_kind, issuer, last4, sensitivity_domain)
VALUES ('bank.de.testkonto.iban','string','TestBank','9999','S2')
ON CONFLICT DO NOTHING;

-- C1 视图自身必须滤掉 S2 行(以属主身份看)
SELECT CASE WHEN count(*)=0 THEN 'PASS' ELSE 'FAIL — S2 行出现在视图里' END AS c1_view_filters_s2
  FROM mem.v_memory_nons2 WHERE content LIKE '%KANARIE_7Q4X%';
SELECT CASE WHEN count(*)=1 THEN 'PASS' ELSE 'FAIL — 非 S2 行反被挡了' END AS c1b_view_shows_nons2
  FROM mem.v_memory_nons2 WHERE content LIKE '%KANARIE_OK%';

\echo '-- 以下切到 ai_mem_remote 身份(远程角色)--'
SET ROLE ai_mem_remote;
SELECT current_user AS now_acting_as;

-- C2 ★否定用例:远程读基表 → 必须 permission denied(响亮失败)
\echo '-- C2 期望: ERROR permission denied for table l3_fact --'
SELECT count(*) FROM mem.l3_fact;

-- C3 ★否定用例:远程读 secret_ref → 必须 permission denied(§6.9.10 否定用例⑦)
\echo '-- C3 期望: ERROR permission denied for table secret_ref --'
SELECT count(*) FROM mem.secret_ref;

-- C4 ★否定用例:远程读凭证审计 / Gate 拒绝表 → 必须 permission denied
\echo '-- C4 期望: ERROR permission denied --'
SELECT count(*) FROM mem.cred_access_audit;
\echo '-- C4b 期望: ERROR permission denied --'
SELECT count(*) FROM mem.gate_rejection;

-- C5 ★否定用例:远程读 L4 指针表 → 必须 permission denied(远程默认排除 L4)
\echo '-- C5 期望: ERROR permission denied for table l4_procedure --'
SELECT count(*) FROM mem.l4_procedure;

-- C6 ★★ 最关键:远程【经视图】也绝不能看到那行 S2 内容
SELECT CASE WHEN count(*)=0 THEN 'PASS'
            ELSE 'FAIL — 远程经视图读到了 S2 行!' END AS c6_remote_cannot_see_s2
  FROM mem.v_memory_nons2 WHERE content LIKE '%KANARIE_7Q4X%';

-- C7 对照:远程经视图应当能读到非 S2 行(否则是把全部挡了,不算隔离)
SELECT CASE WHEN count(*)=1 THEN 'PASS'
            ELSE 'FAIL — 远程连非 S2 都读不到,视图/授权坏了' END AS c7_remote_sees_nons2
  FROM mem.v_memory_nons2 WHERE content LIKE '%KANARIE_OK%';

-- C8 ★否定用例:远程调用 write_seq 分配函数 → 必须 permission denied
--    (原版只 REVOKE FROM ai_mem_remote,没撤 PUBLIC,等于没撤)
\echo '-- C8 期望: ERROR permission denied for function next_write_seq --'
SELECT mem.next_write_seq();

-- C9 ★否定用例:远程写入 → 必须 permission denied
\echo '-- C9 期望: ERROR permission denied for table l3_fact --'
INSERT INTO mem.l3_fact (statement, provenance, sensitivity_domain)
VALUES ('远程试图写入','user_typed','S0');

RESET ROLE;
SELECT current_user AS back_to;

\echo '======================================================================'
\echo ' D. 清理测试数据'
\echo '======================================================================'
DELETE FROM mem.secret_ref WHERE ref='bank.de.testkonto.iban';
UPDATE mem.l3_fact SET superseded_by=NULL WHERE statement LIKE '%KANARIE%' OR statement LIKE '%事实%' OR statement LIKE '%谎报%';
DELETE FROM mem.l3_fact
 WHERE statement LIKE '%KANARIE%' OR statement IN
       ('用户亲口说的事实','工具推断的事实','客户端谎报 write_seq');
SELECT CASE WHEN count(*)=0 THEN 'PASS — 测试数据已清' ELSE 'WARN — 有残留' END AS d1_cleanup,
       count(*) AS leftover
  FROM mem.l3_fact WHERE statement LIKE '%KANARIE%';

\echo '======================================================================'
\echo ' 判读:A/B/C 段凡标 PASS 即通过;标 -- 期望: ERROR -- 的用例'
\echo ' 必须真的报错(报错=隔离生效)。若某条"期望 ERROR"却成功执行,即为隔离失效。'
\echo '======================================================================'
