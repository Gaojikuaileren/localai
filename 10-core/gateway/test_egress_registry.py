# -*- coding: utf-8 -*-
"""registry.toml 的 egress 字段:每个别名都有、类型正确、缺字段 fail-closed。"""
import io, sys, tempfile, os, shutil
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
import gateway

p = f = 0
def ck(n, c, x=''):
    global p, f
    if c: p += 1; print(f'  PASS  {n}')
    else: f += 1; print(f'  FAIL  {n} {x}')

print('=== 每个别名都显式声明 egress ===')
reg = gateway.REGISTRY
ck(f'注册表加载成功({len(reg)} 个别名)', len(reg) >= 9)
missing = [n for n, a in reg.items() if 'egress' not in a]
ck('★ 无别名缺 egress', not missing, f'{missing}')
nonbool = [n for n, a in reg.items() if not isinstance(a.get('egress'), bool)]
ck('★ egress 全是布尔', not nonbool, f'{nonbool}')

print()
print('=== 分类是否合理 ===')
local = sorted(n for n, a in reg.items() if not a['egress'])
cloud = sorted(n for n, a in reg.items() if a['egress'])
print(f'  本地(egress=false): {local}')
print(f'  出境(egress=true) : {cloud}')
ck('★ escalate.cloud 标为出境', reg['escalate.cloud']['egress'] is True)
ck('★ assistant.fast 标为本地', reg['assistant.fast']['egress'] is False)
ck('★ image.concept(nano-banana)标为出境', reg['image.concept']['egress'] is True)

print()
print('=== backend_of:未知别名必须按【出境】处理 ===')
b = gateway.backend_of('assistant.fast')
ck('已知本地别名 → egress=False', b.egress is False)
b2 = gateway.backend_of('escalate.cloud')
ck('已知云端别名 → egress=True', b2.egress is True)
b3 = gateway.backend_of('打错的别名xyz')
ck('★★ 未知别名 → egress=True(拼错不得变成静默出境路径)', b3.egress is True)

print()
print('=== 缺 egress 必须拒绝启动(fail-closed)===')
tmp = tempfile.mkdtemp()
try:
    bad = os.path.join(tmp, 'registry.toml')
    io.open(bad, 'w', encoding='utf-8').write(
        '[aliases."x.local"]\negress = false\nkind = "chat"\n'
        '[aliases."y.forgot"]\nkind = "chat"\n')          # ← 忘了写 egress
    orig = gateway.REGISTRY_PATH
    gateway.REGISTRY_PATH = bad
    try:
        gateway.load_registry()
        ck('★★ 缺 egress 的别名必须拒绝启动', False, '竟然加载成功了')
    except gateway.RegistryError as e:
        ck('★★ 缺 egress 的别名必须拒绝启动', True)
        ck('拒绝时点名了是哪个别名', 'y.forgot' in str(e), str(e)[:80])
    # 类型错也要拒
    io.open(bad, 'w', encoding='utf-8').write('[aliases."z"]\negress = "false"\nkind = "chat"\n')
    try:
        gateway.load_registry()
        ck('★ egress 类型错也要拒(字符串 "false" 是真值)', False, '竟然通过')
    except gateway.RegistryError:
        ck('★ egress 类型错也要拒(字符串 "false" 是真值)', True)
    gateway.REGISTRY_PATH = orig
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print(f'\n=== egress 检查:{p} PASS · {f} FAIL ===')
sys.exit(1 if f else 0)
