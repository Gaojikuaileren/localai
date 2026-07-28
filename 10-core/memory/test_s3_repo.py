# -*- coding: utf-8 -*-
"""repo 层 S3 API 的活库冒烟。用 PYTHONPATH 指向 10-core/memory 运行。"""
import sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
import repo
from repo import PendingWrite
from tainted import seal, unseal_for_client, CallerTier

p = f = 0
def ck(n, c, x=''):
    global p, f
    if c:
        p += 1; print(f'  PASS  {n}')
    else:
        f += 1; print(f'  FAIL  {n} {x}')

def clean():
    """★ 用 postgres 身份清理 —— ai_mem_local **故意没有 DELETE 权限**(§12.4 永不 delete)。
    第一次跑冒烟时就是被这条挡住的,而那说明授权是对的、测试写错了。"""
    import os
    old = os.environ.get('LOCALAI_PG_USER')
    os.environ['LOCALAI_PG_USER'] = 'postgres'
    c = repo.connect()
    with c.cursor() as cur:
        cur.execute("DELETE FROM mem.pending_review WHERE session_id='s3smoke'")
        cur.execute("DELETE FROM mem.write_ticket   WHERE session_id='s3smoke'")
        cur.execute("DELETE FROM mem.circuit_breaker WHERE name='smoke'")
        cur.execute("DELETE FROM mem.gate_rejection WHERE session_id='s3smoke'")
    c.commit(); c.close()
    if old is None:
        os.environ.pop('LOCALAI_PG_USER', None)
    else:
        os.environ['LOCALAI_PG_USER'] = old

clean()
conn = repo.connect()

print('=== 授权面(narrow by design)===')
# ★ 真的去试,不是写一句 True。§12.4「永不 delete 只移隔离区」要在授权上成立,
#   而不是靠调用方自觉不写 DELETE。
import psycopg as _pg
for tbl in ('write_ticket', 'pending_review', 'gate_rejection'):
    try:
        with conn.cursor() as cur:
            cur.execute(f"DELETE FROM mem.{tbl} WHERE 1=0")   # 连空集删除都不该被允许
        conn.rollback()
        ck(f'★ ai_mem_local 对 {tbl} 无 DELETE 权限', False, '竟然允许 DELETE')
    except _pg.errors.InsufficientPrivilege:
        conn.rollback(); ck(f'★ ai_mem_local 对 {tbl} 无 DELETE 权限', True)
    except _pg.Error as ex:
        conn.rollback(); ck(f'★ ai_mem_local 对 {tbl} 无 DELETE 权限', False, type(ex).__name__)

print('=== 队列 ===')
base = repo.count_pending(conn)
pid = repo.insert_pending(conn, PendingWrite(
    body=seal('S3SMOKE 从网页读到的东西', sensitivity='S0', source='web_content'),
    provenance='web_content', source_confidence=0.4, sensitivity_domain='S0',
    session_id='s3smoke'))
conn.commit()
ck('入队', isinstance(pid, int))
ck('熔断计数 +1', repo.count_pending(conn) == base + 1)

row = repo.get_pending(conn, pid)
ck('读回且正文密封', row is not None and not isinstance(row.body, str))
ck('正文正确', 'S3SMOKE' in unseal_for_client(row.body, caller=CallerTier.TRUSTED_LOCAL))
ck('有 TTL', row.expires_at is not None)
ck('有溯源', 'created_at' in row.trace())
ck('出现在待审列表里', any(r.id == pid for r in repo.list_pending(conn)))

print('=== 哈希回绑(防「面板看到 A、确认进库 B」)===')
try:
    repo.set_pending_status(conn, pid, 'approved', expect_sha256='wrong_hash')
    ck('★ 哈希对不上必须拒', False, '竟然放行')
except repo.RepoError:
    conn.rollback(); ck('★ 哈希对不上必须拒', True)

repo.set_pending_status(conn, pid, 'rejected', expect_sha256=row.candidate_sha256)
conn.commit()
ck('哈希对上则可转终态', repo.get_pending(conn, pid).status == 'rejected')
ck('终态不再占熔断额度', repo.count_pending(conn) == base)

print('=== 票据:原子消费,不可双花 ===')
t = repo.issue_ticket(conn, session_id='s3smoke', candidate_text='hello'); conn.commit()
ck('签发', bool(t))
ck('首次消费成功', repo.consume_ticket(conn, t, session_id='s3smoke', candidate_text='hello'))
conn.commit()
ck('★ 二次消费失败(不可双花)',
   not repo.consume_ticket(conn, t, session_id='s3smoke', candidate_text='hello'))
t2 = repo.issue_ticket(conn, session_id='s3smoke', candidate_text='a'); conn.commit()
ck('★ 换文本消费失败(票据绑候选)',
   not repo.consume_ticket(conn, t2, session_id='s3smoke', candidate_text='b'))
ck('★ 换会话消费失败(票据绑会话)',
   not repo.consume_ticket(conn, t2, session_id='别的会话', candidate_text='a'))
conn.commit()

print('=== 熔断:落库,重启不复位 ===')
ck('初始未跳', not repo.breaker_tripped(conn, 'smoke'))
repo.trip_breaker(conn, 'smoke', '积压超限'); conn.commit()
ck('跳闸后为真', repo.breaker_tripped(conn, 'smoke'))
conn.close()
conn = repo.connect()          # ★ 换连接 = 模拟重启
ck('★ 换连接后仍为跳闸态(不是进程内存态)', repo.breaker_tripped(conn, 'smoke'))
repo.clear_breaker(conn, 'smoke', cleared_by='panel_ticket'); conn.commit()
ck('恢复后为假', not repo.breaker_tripped(conn, 'smoke'))
try:
    repo.clear_breaker(conn, 'smoke', cleared_by='panel_ticket')
    ck('重复恢复应拒', False, '竟然放行')
except repo.RepoError:
    conn.rollback(); ck('重复恢复应拒', True)

print('=== E3 拒绝落库 ===')
repo.log_gate_rejection(conn, ['iban', 'card_pan'], 's3smoke'); conn.commit()
with conn.cursor() as cur:
    cur.execute("""SELECT count(*), bool_and(sensitivity_domain='S2')
                     FROM mem.gate_rejection WHERE session_id='s3smoke'""")
    n, all_s2 = cur.fetchone()
ck('★ 落库且每类一行', n == 2, f'{n}')
ck('★ 标记为 S2', bool(all_s2))

conn.close()
clean()
print(f'\n=== repo S3 冒烟:{p} PASS · {f} FAIL ===')
sys.exit(1 if f else 0)
