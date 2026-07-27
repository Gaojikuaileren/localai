-- 1) 角色创建(幂等 DO 守卫)。SSPI 登录角色需 LOGIN、无口令。
-- 注: CREATE ROLE 需 superuser 或 CREATEROLE → 若 mem_rw 无 CREATEROLE,本段以 postgres 运行。
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='ai_mem_local') THEN
    CREATE ROLE ai_mem_local LOGIN;
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='ai_mem_remote') THEN
    CREATE ROLE ai_mem_remote LOGIN;
  END IF;
END $$;

-- 2) schema 可见性: 两角色都需 USAGE(远程也要,才能经视图读);先清 PUBLIC。
REVOKE ALL ON SCHEMA mem FROM PUBLIC;
GRANT USAGE ON SCHEMA mem TO ai_mem_local, ai_mem_remote;

-- 3) ai_mem_local: 基表全权(SELECT/INSERT/UPDATE/DELETE)+ 序列 + write_seq 分配函数。
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA mem TO ai_mem_local;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA mem TO ai_mem_local;

-- 3b) ★ 先把函数的隐式 PUBLIC EXECUTE 清掉,再定向授权(2026-07-27 核验修正)。
--     PostgreSQL 新建函数默认 GRANT EXECUTE TO PUBLIC —— 只 REVOKE ... FROM ai_mem_remote
--     根本无效(它撤的是并不存在的直接授权,PUBLIC 那条还在),远程仍可调用
--     SECURITY DEFINER 的 next_write_seq() 空耗全局序列。必须从 PUBLIC 撤。
--     (触发器函数不受影响:PG 在 CREATE TRIGGER 时检查 EXECUTE,触发时不再检查。)
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA mem FROM PUBLIC;
GRANT EXECUTE ON FUNCTION mem.next_write_seq() TO ai_mem_local;

-- 4) append-only 落实「保留=不删」: 即便对 ai_mem_local 也 REVOKE UPDATE/DELETE
--    (凭证审计 cross_cutting 明确要求;audit_log/cred_access_audit/vigil/gate/ledger 不删)。
REVOKE UPDATE, DELETE ON mem.audit_log, mem.cred_access_audit, mem.vigil_observations, mem.gate_rejection, mem.idempotent_ledger FROM ai_mem_local;
-- secret_ref / l4_procedure 允许 UPDATE,不允许 DELETE。
REVOKE DELETE ON mem.secret_ref, mem.l4_procedure FROM ai_mem_local;
-- (metrics_ts 保留 DELETE 供 1 年 purge;pending_review 保留全 DML 供确认/清理。)

-- 5) ai_mem_remote 锁死: 先 REVOKE 一切基表/序列权限(含误挂在视图上的),再只授视图 SELECT。
--    顺序关键: REVOKE ALL(含视图)必须在 GRANT 视图之前。
REVOKE ALL ON ALL TABLES IN SCHEMA mem FROM ai_mem_remote;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA mem FROM ai_mem_remote;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA mem FROM ai_mem_remote;   -- 真正的闸门是上面 3b 的 FROM PUBLIC

-- 6) 显式再 REVOKE 三张 S2 表(冗余但可审计: 证明远程碰不到 secret_ref/凭证审计/拒绝表)。
REVOKE ALL ON mem.secret_ref, mem.cred_access_audit, mem.gate_rejection FROM ai_mem_remote;

-- 7) ai_mem_remote 唯一权限: SELECT v_memory_nons2。碰不到基表、更碰不到 secret_ref。
GRANT SELECT ON mem.v_memory_nons2 TO ai_mem_remote;

-- 8) 默认权限: 今后 mem_rw 在 schema mem 新建的对象自动只授 local,绝不漏给 remote/PUBLIC。
ALTER DEFAULT PRIVILEGES FOR ROLE mem_rw IN SCHEMA mem GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ai_mem_local;
ALTER DEFAULT PRIVILEGES FOR ROLE mem_rw IN SCHEMA mem GRANT USAGE, SELECT ON SEQUENCES TO ai_mem_local;
ALTER DEFAULT PRIVILEGES FOR ROLE mem_rw IN SCHEMA mem REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE mem_rw IN SCHEMA mem REVOKE ALL ON FUNCTIONS FROM PUBLIC;
-- 说明: 默认不向 ai_mem_remote 授任何表 → 新建表永不自动泄露给远程;远程新可读项须显式并入 v_memory_nons2。

-- =====================================================================
-- ★ 应用顺序(必须): schema.sql  →  roles.sql
--   schema.sql 里 v_memory_nons2 用 DROP+CREATE(要改列清单),会连带丢掉 GRANT;
--   roles.sql 在其后重新 GRANT SELECT 给 ai_mem_remote。顺序颠倒 = 远程读不到视图。
--
-- ★ 已知残留(PostgreSQL 固有,非本设计缺陷):ai_mem_remote 仍可读 pg_catalog /
--   information_schema,因而能【看到表名】(secret_ref 等)并经 pg_class.reltuples 估算行数。
--   它读不到任何一行内容,但"存在性"瞒不住。PG 无法在不破坏基本功能的前提下屏蔽目录。
--   真正的边界在网关:远程会话根本不该拿到裸 SQL 通道(远程只经受限端点访问)。
--   —— 此项已知并接受,记录在此以免将来误以为已屏蔽。
-- =====================================================================