#!/usr/bin/env bash
set -euo pipefail

die() {
    echo "validate-mod-release-assets: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "$1 is required"
}

require_command python3

RELEASE_DIR="${1:-}"
[[ -n "${RELEASE_DIR}" ]] || die "usage: scripts/validate-mod-release-assets.sh <release-dir>"
[[ -d "${RELEASE_DIR}" ]] || die "missing release directory: ${RELEASE_DIR}"
[[ -f "${RELEASE_DIR}/latest.json" ]] || die "missing ${RELEASE_DIR}/latest.json"

python3 - "${RELEASE_DIR}" <<'PY'
import hashlib
import json
import sys
import zipfile
from pathlib import Path
from urllib.parse import urlparse

release_dir = Path(sys.argv[1])
manifest_path = release_dir / "latest.json"

try:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
except json.JSONDecodeError as exc:
    raise SystemExit(f"invalid latest.json: {exc}") from exc

if manifest.get("schema_version") != "sts2.telemetry.mod_release.v1":
    raise SystemExit("latest.json schema_version must be sts2.telemetry.mod_release.v1")

latest_version = manifest.get("latest_version")
if not isinstance(latest_version, str) or not latest_version:
    raise SystemExit("latest.json latest_version must be a non-empty string")

if not isinstance(manifest.get("min_supported_version"), str) or not manifest["min_supported_version"]:
    raise SystemExit("latest.json min_supported_version must be a non-empty string")

if not isinstance(manifest.get("release_notes"), str):
    raise SystemExit("latest.json release_notes must be a string")

if not isinstance(manifest.get("requires_confirmation"), bool):
    raise SystemExit("latest.json requires_confirmation must be a boolean")

artifacts = manifest.get("artifacts")
if not isinstance(artifacts, list) or not artifacts:
    raise SystemExit("latest.json artifacts must be a non-empty array")

seen_names = set()
for index, artifact in enumerate(artifacts):
    prefix = f"artifact[{index}]"
    if not isinstance(artifact, dict):
        raise SystemExit(f"{prefix} must be an object")

    platform = artifact.get("platform")
    if not isinstance(platform, str) or not platform:
        raise SystemExit(f"{prefix}.platform must be a non-empty string")

    if artifact.get("kind") != "mod_package":
        raise SystemExit(f"{prefix}.kind must be mod_package")

    file_name = artifact.get("file_name")
    if not isinstance(file_name, str) or not file_name:
        raise SystemExit(f"{prefix}.file_name must be a non-empty string")
    if "/" in file_name or "\\" in file_name or file_name.startswith("."):
        raise SystemExit(f"{prefix}.file_name must be a plain package file name")
    if file_name in seen_names:
        raise SystemExit(f"duplicate artifact file_name: {file_name}")
    seen_names.add(file_name)

    expected_prefix = f"sts2-telemetry-{latest_version}-"
    if not file_name.startswith(expected_prefix) or not file_name.endswith(".zip"):
        raise SystemExit(f"{prefix}.file_name does not match {expected_prefix}*.zip: {file_name}")

    url = artifact.get("url")
    if not isinstance(url, str) or not url:
        raise SystemExit(f"{prefix}.url must be a non-empty string")
    url_file_name = Path(urlparse(url).path).name
    if url_file_name != file_name:
        raise SystemExit(f"{prefix}.url path must end with {file_name}")

    expected_hash = artifact.get("sha256")
    if not isinstance(expected_hash, str) or len(expected_hash) != 64:
        raise SystemExit(f"{prefix}.sha256 must be a 64-character hex string")
    try:
        int(expected_hash, 16)
    except ValueError as exc:
        raise SystemExit(f"{prefix}.sha256 must be lowercase hexadecimal") from exc
    if expected_hash != expected_hash.lower():
        raise SystemExit(f"{prefix}.sha256 must be lowercase hexadecimal")

    expected_size = artifact.get("size_bytes")
    if not isinstance(expected_size, int) or expected_size <= 0:
        raise SystemExit(f"{prefix}.size_bytes must be a positive integer")

    package_path = release_dir / file_name
    if not package_path.is_file():
        raise SystemExit(f"missing package asset: {package_path}")

    actual_size = package_path.stat().st_size
    if actual_size != expected_size:
        raise SystemExit(f"{file_name} size mismatch: manifest={expected_size} actual={actual_size}")

    actual_hash = hashlib.sha256(package_path.read_bytes()).hexdigest()
    if actual_hash != expected_hash:
        raise SystemExit(f"{file_name} sha256 mismatch: manifest={expected_hash} actual={actual_hash}")

    helper_name = "Sts2Telemetry.Updater.exe" if platform.startswith("win-") else "Sts2Telemetry.Updater"
    required_entries = {"Sts2Telemetry.dll", "Sts2Telemetry.json", helper_name}
    with zipfile.ZipFile(package_path, "r") as archive:
        entry_names = set(archive.namelist())
        missing = sorted(required_entries - entry_names)
        if missing:
            raise SystemExit(f"{file_name} missing package entries: {', '.join(missing)}")

print(f"Validated {len(artifacts)} release artifact(s) in {release_dir}")
PY
