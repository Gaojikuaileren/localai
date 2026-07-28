"""P3b S3 · 读 S2 成员表(.NET identity 服务写的 store.json),按证书指纹反查 LAN_DEVICE。

只读。store 缺失 / 损坏 / 指纹未知 / 证书或设备非 active → 一律 **fail-closed**(返回 None = 无 LAN 访问)。
主体只来自成员表;客户端自报的 device_id / tier 一律不采信(§7.1)。
"""
import json
import tomllib
from pathlib import Path

PATHS_TOML = Path(__file__).resolve().parents[2] / "config" / "paths.toml"


def _store_path() -> Path:
    with open(PATHS_TOML, "rb") as f:
        return Path(tomllib.load(f)["state"]["identity"]) / "store.json"


def load_store() -> dict:
    with open(_store_path(), encoding="utf-8") as f:
        return json.load(f)


def active_device(cert_sha256: str):
    """指纹 → {device_id, generation},当且仅当 证书 active 且 设备 active。否则 None(fail-closed)。

    store.json 由 .NET System.Text.Json 写出,键为 PascalCase;指纹为大写 HEX(Convert.ToHexString)。
    """
    if not cert_sha256:
        return None
    try:
        s = load_store()
    except Exception:
        return None  # 无 store / 损坏 → 无 LAN 访问(fail-closed)

    fp = cert_sha256.upper()
    certs = s.get("Certs") or []
    devices = s.get("Devices") or []
    cert = next((c for c in certs if str(c.get("CertSha256", "")).upper() == fp), None)
    if not cert or cert.get("Status") != "active":
        return None
    dev = next((d for d in devices if d.get("DeviceId") == cert.get("DeviceId")), None)
    if not dev or dev.get("Status") != "active":
        return None
    return {"device_id": dev.get("DeviceId"), "generation": s.get("IdentityGeneration", 0)}
