# -*- coding: utf-8 -*-
"""registry.toml 的 local_only / agent_allow(D69「Vigil 宠物始终本地」)。跑:python test_local_only_registry.py

★ 本文件的重点在最后一组:**反向全表断言**。
  正向断言(assistant.resident 是本地的)防不住这一族事故的真实形状 ——
  事故永远是「将来给 Vigil 加了新别名忘了写」,而不是「有人把 resident 改成云端」。
  所以下面每一条 fail-closed 用例都构造一份**其余部分完全合法**的 registry,
  只让一个字段出错,确认它确实炸在预期的那一条上。

★★ 断言纪律(DECISIONS.md「二、假断言」):本文件的每条 fail-closed 断言都要求
   load_registry【抛 RegistryError】。若哪天有人把某条检查删掉,对应用例会因为
   「竟然加载成功了」而 FAIL —— 不是恒真的重言式。
"""
import io, sys, os, tempfile, shutil
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
import gateway

p = f = 0
def ck(n, c, x=''):
    global p, f
    if c: p += 1; print(f'  PASS  {n}')
    else: f += 1; print(f'  FAIL  {n} {x}')


# ── 构造「除指定处外完全合法」的 registry ────────────────────────────
_VALID = {
    "a.local":            'egress = false\nlocal_only = false\nagent_allow = ["assistant.main"]\nkind = "chat"\n',
    "assistant.resident": 'egress = false\nlocal_only = true\nagent_allow = ["vigil", "pet"]\nkind = "chat"\n',
}

def write_registry(path, entries):
    body = "".join(f'[aliases."{n}"]\n{b}\n' for n, b in entries.items())
    io.open(path, 'w', encoding='utf-8').write(body)

def expect_refuse(label, entries, must_name=None, reason=None):
    """构造一份 registry,断言 load_registry 拒绝启动(并在消息里点名 + 说对理由)。

    ★ `reason` 不是装饰。第一版没有它,结果「通配 * 必须拒绝」是一条**假断言**:
      "*" 不在 KNOWN_AGENTS 里,所以【未登记 Agent】那条检查会抢先命中,
      消息里照样有别名名字 —— 把专门的通配检查整条删掉,测试依旧全绿。
      现在每条用例都要求消息里出现它**自己那条检查**的理由词。

    ★ 非 RegistryError 的异常一律判 FAIL。把检查删掉后常见的表现是别处 KeyError 崩掉,
      那时既没有 FAIL 行也没有汇总行 —— 看起来像"没跑",不像"红了"。
    """
    tmp = tempfile.mkdtemp()
    try:
        bad = os.path.join(tmp, 'registry.toml')
        write_registry(bad, entries)
        orig = gateway.REGISTRY_PATH
        gateway.REGISTRY_PATH = bad
        try:
            gateway.load_registry()
            ck(label, False, '竟然加载成功了')
        except gateway.RegistryError as e:
            ck(label, True)
            if must_name:
                ck(f'  └ 拒绝时点名了 {must_name}', must_name in str(e), str(e)[:120])
            if reason:
                ck(f'  └ 理由是「{reason}」而不是被别的检查抢先命中',
                   reason in str(e), str(e)[:160])
        except Exception as e:                       # noqa: BLE001 —— 故意兜住一切
            ck(label, False, f'抛的不是 RegistryError 而是 {type(e).__name__}: {e}')
        finally:
            gateway.REGISTRY_PATH = orig
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

def expect_ok(label, entries):
    tmp = tempfile.mkdtemp()
    try:
        good = os.path.join(tmp, 'registry.toml')
        write_registry(good, entries)
        orig = gateway.REGISTRY_PATH
        gateway.REGISTRY_PATH = good
        try:
            gateway.load_registry()
            ck(label, True)
        except gateway.RegistryError as e:
            ck(label, False, f'不该拒绝:{str(e)[:120]}')
        finally:
            gateway.REGISTRY_PATH = orig
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


print('=== 真实 registry:两个新字段齐备且类型正确 ===')
reg = gateway.REGISTRY
miss_lo = [n for n, a in reg.items() if 'local_only' not in a]
miss_aa = [n for n, a in reg.items() if 'agent_allow' not in a]
ck('★ 无别名缺 local_only', not miss_lo, f'{miss_lo}')
ck('★ 无别名缺 agent_allow', not miss_aa, f'{miss_aa}')
ck('★ local_only 全是布尔',
   all(isinstance(a['local_only'], bool) for a in reg.values()))
ck('★ agent_allow 全是数组',
   all(isinstance(a['agent_allow'], list) for a in reg.values()))

print()
print('=== 真实 registry:agent_allow 取值来自封闭表 ===')
allow_union = sorted({g for a in reg.values() for g in a['agent_allow']})
print(f'  出现过的 Agent: {allow_union}')
ck('★ 无未登记 Agent', set(allow_union) <= gateway.KNOWN_AGENTS,
   f'{sorted(set(allow_union) - gateway.KNOWN_AGENTS)}')
ck('★ 无别名使用通配 "*"', not any('*' in a['agent_allow'] for a in reg.values()))
ck('★ 无别名的 agent_allow 为空', all(a['agent_allow'] for a in reg.values()))

print()
print('=== 真实 registry:local_only 与 egress 互斥 ===')
both = sorted(n for n, a in reg.items() if a['local_only'] and a['egress'])
ck('★ 无别名同时 local_only=true 与 egress=true', not both, f'{both}')

print()
print('=== 真实 registry:常驻别名的三个性质 ===')
ck(f'★ {gateway.RESIDENT_ALIAS} 存在', gateway.RESIDENT_ALIAS in reg)
res = reg.get(gateway.RESIDENT_ALIAS, {})
ck('★ 常驻别名 egress=false', res.get('egress') is False)
ck('★ 常驻别名 local_only=true', res.get('local_only') is True)
ck('★ 常驻别名无 provider 字段(有 provider = 由外部服务商承接)', 'provider' not in res)

print()
print('=== ★★ 反向全表断言(本文件的重点)===')
resident_capable = sorted(n for n, a in reg.items()
                          if gateway.RESIDENT_AGENTS & set(a['agent_allow']))
print(f'  允许 vigil/pet 的别名: {resident_capable}')
ck(f'★★ 该集合【有且只有】{gateway.RESIDENT_ALIAS}',
   resident_capable == [gateway.RESIDENT_ALIAS], f'{resident_capable}')
ck('★ escalate.cloud 不允许 vigil/pet',
   not (gateway.RESIDENT_AGENTS & set(reg['escalate.cloud']['agent_allow'])))

print()
print('=== fail-closed:缺字段 / 类型错 ===')
expect_ok('基线:两条合法条目应当加载成功', dict(_VALID))
expect_refuse('★ 缺 local_only 必须拒绝启动',
              {**_VALID, "b.forgot": 'egress = false\nagent_allow = ["assistant.main"]\nkind = "chat"\n'},
              must_name='b.forgot', reason='缺少必填的 local_only')
expect_refuse('★ 缺 agent_allow 必须拒绝启动',
              {**_VALID, "c.forgot": 'egress = false\nlocal_only = false\nkind = "chat"\n'},
              must_name='c.forgot', reason='缺少必填的 agent_allow')
expect_refuse('★ local_only 类型错也要拒(字符串 "false" 是真值)',
              {**_VALID, "d.bad": 'egress = false\nlocal_only = "false"\nagent_allow = ["assistant.main"]\nkind = "chat"\n'},
              must_name='d.bad', reason='local_only 必须是布尔值')
expect_refuse('★ agent_allow 类型错也要拒(裸字符串不是数组)',
              {**_VALID, "e.bad": 'egress = false\nlocal_only = false\nagent_allow = "assistant.main"\nkind = "chat"\n'},
              must_name='e.bad', reason='agent_allow 必须是数组')

print()
print('=== fail-closed:agent_allow 的取值 ===')
expect_refuse('★★ 通配 "*" 必须拒绝(通配是 denylist 形状)',
              {**_VALID, "f.wild": 'egress = false\nlocal_only = false\nagent_allow = ["*"]\nkind = "chat"\n'},
              must_name='f.wild', reason='不得使用通配')
expect_refuse('★ 空数组必须拒绝',
              {**_VALID, "g.empty": 'egress = false\nlocal_only = false\nagent_allow = []\nkind = "chat"\n'},
              must_name='g.empty', reason='不得为空数组')
expect_refuse('★ 未登记的 Agent 必须拒绝',
              {**_VALID, "h.unknown": 'egress = false\nlocal_only = false\nagent_allow = ["新来的agent"]\nkind = "chat"\n'},
              must_name='h.unknown', reason='未登记的 Agent')

print()
print('=== fail-closed:互斥与常驻别名 ===')
expect_refuse('★ local_only=true 且 egress=true 必须拒绝',
              {**_VALID, "i.both": 'egress = true\nlocal_only = true\nagent_allow = ["assistant.main"]\nkind = "chat"\n'},
              must_name='i.both', reason='互斥')
expect_refuse('★ 常驻别名缺失必须拒绝(没有它就没有可断言的标的)',
              {"a.local": _VALID["a.local"]},
              must_name='assistant.resident', reason='缺少常驻别名')
expect_refuse('★ 常驻别名带 provider 必须拒绝',
              {**_VALID, "assistant.resident":
               'egress = false\nlocal_only = true\nagent_allow = ["vigil", "pet"]\nkind = "chat"\nprovider = "gemini"\n'},
              must_name='provider', reason='不得有 provider 字段')
expect_refuse('★ 常驻别名被改成 local_only=false 必须拒绝',
              {**_VALID, "assistant.resident":
               'egress = false\nlocal_only = false\nagent_allow = ["vigil", "pet"]\nkind = "chat"\n'},
              must_name='assistant.resident', reason='必须 egress=false 且 local_only=true')

print()
print('=== ★★★ fail-closed:反向全表断言(「将来忘了写」的那条)===')
expect_refuse('★★★ 新增一条允许 vigil 的【云端】别名 → 必须拒绝启动',
              {**_VALID, "vigil.cloud":
               'egress = true\nlocal_only = false\nagent_allow = ["vigil"]\nkind = "chat_cloud"\n'},
              must_name='vigil.cloud', reason='反向全表断言失败')
expect_refuse('★★★ 新增一条允许 vigil 的【本地但非 local_only】别名 → 同样必须拒绝',
              {**_VALID, "vigil.sneaky":
               'egress = false\nlocal_only = false\nagent_allow = ["vigil"]\nkind = "chat"\n'},
              must_name='vigil.sneaky', reason='反向全表断言失败')
expect_refuse('★★★ 给 pet 单独开一条别名 → 必须拒绝(pet 与 vigil 同族)',
              {**_VALID, "pet.extra":
               'egress = false\nlocal_only = true\nagent_allow = ["pet"]\nkind = "chat"\n'},
              must_name='pet.extra', reason='反向全表断言失败')

print()
print('=== 隔离账户(D69 / D72)===')
ck('★ ai-vigil 在 LOCAL_DENY_ACCOUNTS(Vigil 结构上连不上网关)',
   'ai-vigil' in gateway.LOCAL_DENY_ACCOUNTS)
ck('★ ai-ctl 在 LOCAL_DENY_ACCOUNTS(层二不得反向调 chat 网关)',
   'ai-ctl' in gateway.LOCAL_DENY_ACCOUNTS)
ck('★★ ai-op 在 LOCAL_DENY_ACCOUNTS(外部 AI 宿主账户)',
   'ai-op' in gateway.LOCAL_DENY_ACCOUNTS)
ck('  既有的两个仍在', {'ai-asset', 'ai-exec'} <= gateway.LOCAL_DENY_ACCOUNTS)

# ★★ 这条是上面那条的【理由】,单独断言出来 —— 不是重复:
#    classify_caller 的兜底是「解析不到 → trusted-local」,即**新账户默认落在放行侧**,
#    而 trusted-local 是唯一含 S2 读与 E1 解除权的档位。
#    所以「新增一个 ai-* 服务账户」与「把它登记进 LOCAL_DENY_ACCOUNTS」必须同时发生。
#    下面穷举:凡是项目里已知的隔离服务账户,一个都不能漏。
_KNOWN_SERVICE_ACCOUNTS = {'ai-asset', 'ai-exec', 'ai-mem', 'ai-vigil', 'ai-ctl', 'ai-op'}
_should_deny = _KNOWN_SERVICE_ACCOUNTS - {'ai-mem'}      # ai-mem 是记忆平面自己,必须放行
_missing = sorted(_should_deny - gateway.LOCAL_DENY_ACCOUNTS)
ck('★★ 除 ai-mem 外的全部隔离服务账户都已登记(新增账户默认落放行侧,漏一个=发最高档)',
   not _missing, f'漏登记:{_missing}')
ck('  ai-mem 刻意【不】在拒绝名单里(它是记忆平面自身)',
   'ai-mem' not in gateway.LOCAL_DENY_ACCOUNTS)

print()
print('=== E1 解除权:新档位不得默认获得 ===')
ck('★ ext-operator 不在 E1_OVERRIDE_ALLOWED_TIERS',
   'ext-operator' not in gateway.E1_OVERRIDE_ALLOWED_TIERS)
ck('★ resident-observer 不在 E1_OVERRIDE_ALLOWED_TIERS',
   'resident-observer' not in gateway.E1_OVERRIDE_ALLOWED_TIERS)

print()
print('=== 路由分级:两个新档位不得成为任何路由的准入级 ===')
# ★ 决议包原本写「不加入 E1_OVERRIDE、不进 ROUTE_TIERS(已落地)」——
#   复核指出那个「已落地」只覆盖了 _ALLOWED_CALLERS 一条,另两条零断言。
#   E1_OVERRIDE 的断言在上面;ROUTE_TIERS 的补在这里。
_route_classes = set(gateway.ROUTE_TIERS.values())
print(f'  现有路由分级: {sorted(_route_classes)}')
ck('★ ext-operator 不是任何路由的准入级',
   'ext-operator' not in _route_classes)
ck('★ resident-observer 不是任何路由的准入级',
   'resident-observer' not in _route_classes)
# ★ 这条才是有牙齿的那条:分级词表是封闭的。新增一个分级必须是**故意**的,
#   而不是某次「给新档位开条路」时顺手打出来的第三个词。
ck('★★ 路由分级词表封闭(新增分级必须同时改这条断言)',
   _route_classes == {'public-minimal', 'authenticated'}, f'{sorted(_route_classes)}')
ck('  每条路由都已归类(unclassified_routes 为空)',
   not gateway.unclassified_routes(), f'{gateway.unclassified_routes()}')

print(f'\n=== local_only / agent_allow 检查:{p} PASS · {f} FAIL ===')
sys.exit(1 if f else 0)
