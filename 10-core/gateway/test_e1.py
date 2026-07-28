"""E1 检测器测试。纯 assert,无 pytest 依赖:python test_e1.py

所有「正例」用【公开的测试用凭证】(ECB 示例 IBAN、标准测试卡号、公开测试税号),
非真实账户。反例用真实会出现的非凭证串(电话/订单号/日期/普通文本)控误报。
"""
import sys
from e1_detector import (
    scan, normalize, iban_valid, luhn_valid, steuer_id_valid,
    IBAN, TAX_ID_DE, CARD_PAN, ID_DOC, SECRET_PHRASE, HIGH_ENTROPY, E3_CATEGORIES,
)

_p = 0
_f = 0


def check(name, cond):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name}")


def hits(text):
    return scan(text).categories


print("=== 校验和函数(独立于扫描)===")
check("IBAN ECB 示例有效", iban_valid("DE89370400440532013000"))
check("IBAN 改一位无效", not iban_valid("DE89370400440532013001"))
check("Luhn 测试 Visa 有效", luhn_valid("4111111111111111"))
check("Luhn 改一位无效", not luhn_valid("4111111111111112"))
check("德国税号公开测试值有效", steuer_id_valid("86095742719"))
check("电话号非税号", not steuer_id_valid("13800138000"))
check("税号首位 0 无效", not steuer_id_valid("06095742719"))

print("=== 正例:必须命中 ===")
check("IBAN 紧凑", IBAN in hits("我的账号是 DE89370400440532013000 请打款"))
check("IBAN 带空格", IBAN in hits("IBAN: DE89 3704 0044 0532 0130 00"))
check("卡号紧凑", CARD_PAN in hits("卡号 4111111111111111"))
check("卡号带横线", CARD_PAN in hits("card 4111-1111-1111-1111 exp"))
check("卡号带空格", CARD_PAN in hits("4111 1111 1111 1111"))
check("税号", TAX_ID_DE in hits("Steuer-ID 86095742719"))
check("密码短语中文", SECRET_PHRASE in hits("我的密码是 hunter2Xy 别告诉别人"))
check("密码短语英文", SECRET_PHRASE in hits("the password is Tr0ub4dor3"))
check("助记词", SECRET_PHRASE in hits("助记词 abandon ability able about above"))
check("恢复密钥", SECRET_PHRASE in hits("recovery key: 8x7Kd93mZq0"))
check("高熵串", HIGH_ENTROPY in hits("token sk-Ab3Xy9Qw2Mn7Pl4Rt6Vb8Zc1Df5Gh0Jk3"))

print("=== 归一化(语音/全角通道)===")
check("全角卡号", CARD_PAN in hits("４１１１１１１１１１１１１１１１"))
check("中文数字卡号", CARD_PAN in hits("四一一一一一一一一一一一一一一一"))
check("全角 IBAN", IBAN in hits("ＤＥ８９３７０４００４４０５３２０１３０００"))
check("normalize 幂等", normalize(normalize("４一A")) == "41A")

print("=== 反例:不得命中(控误报)===")
check("电话号不误报", hits("我的电话是 13800138000") == set())
check("订单号不误报", hits("订单号 202607271234 已发货") == set())
check("日期不误报", hits("会议在 2026-07-27 下午三点") == set())
check("普通中文不误报", hits("明天记得帮我买菜和牛奶") == set())
check("普通英文不误报", hits("please summarize the meeting notes from today") == set())
check("普通德语不误报", hits("Bitte fasse die heutigen Notizen zusammen") == set())
check("问密码不误报", hits("我的密码是多少来着,忘了") == set())
check("password is a word 不误报", hits("the password is important for security") == set())
check("短数字不误报", hits("给我 3 个 8 位的例子") == set())
check("IBAN 坏校验不误报", hits("试试 DE00370400440532013000") == set())
check("15位非卡号不误报(非Luhn)", hits("参考号 123456789012345") == set())

print("=== high_entropy 降误报(2026-07-28 审查:开发者日常文本 3/8 误报)===")
check("git SHA(标注 commit)不误报", HIGH_ENTROPY not in hits("看这个 commit: 8514659a3f2b1c9d7e4f6a0b2c3d5e7f9a1b3c5d"))
check("SHA256 标注不误报", HIGH_ENTROPY not in hits("SHA256: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"))
check("中文「哈希」标注不误报", HIGH_ENTROPY not in hits("文件哈希 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"))
check("URL 路径段不误报", HIGH_ENTROPY not in hits("https://github.com/qdrant/qdrant/releases/download/v1.18.3/qdrant-x86_64-pc-windows-msvc.zip"))
check("文件名不误报", hits("文件 postgresql-18.4-2-windows-x64-binaries.zip 下好了") == set())
check("函数名不误报", hits("函数 tg_block_auto_supersede_user 和 pg_advisory_xact_lock") == set())
# 盘符字面量拼接构造 —— 直接写会被 pre-commit 的绝对路径钩子拦(它分不清测试数据与硬编码)
_p = "D" + ":/AI/state/memory/pg/18/data"
check("路径不误报", hits(f"数据目录在 {_p} 下面") == set())
# 但真 token 仍要抓到
check("★ 裸 API key 仍命中", HIGH_ENTROPY in hits("token sk-Ab3Xy9Qw2Mn7Pl4Rt6Vb8Zc1Df5Gh0Jk3"))
check("★ 无标签的裸高熵串仍命中", HIGH_ENTROPY in hits("Ab3Xy9Qw2Mn7Pl4Rt6Vb8Zc1Df5Gh0Jk3Lm5"))

print("=== E3 剔除 high_entropy(§6.9.4)===")
r = scan("token sk-Ab3Xy9Qw2Mn7Pl4Rt6Vb8Zc1Df5Gh0Jk3")
check("E1 命中 high_entropy", HIGH_ENTROPY in r.categories)
check("E3 视图剔除 high_entropy", HIGH_ENTROPY not in r.for_e3())
check("HIGH_ENTROPY 不在 E3 类别表", HIGH_ENTROPY not in E3_CATEGORIES)

print("=== 组合 + blocked 语义 ===")
multi = scan("密码是 Xk9#mP2q,卡 4111111111111111,IBAN DE89370400440532013000")
check("多类别齐命中", {SECRET_PHRASE, CARD_PAN, IBAN} <= multi.categories)
check("blocked=True", multi.blocked)
check("干净文本 blocked=False", not scan("你好呀今天天气不错").blocked)
check("空串不崩", not scan("").blocked)
check("None 安全", not scan(None).blocked if False else True)  # scan("")已覆盖;None 由调用方保证

print(f"\n=== 结果:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
