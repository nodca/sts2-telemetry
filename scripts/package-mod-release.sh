#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOD_PROJECT="${ROOT}/src/Sts2Telemetry/Sts2Telemetry.csproj"
UPDATER_PROJECT="${ROOT}/src/Sts2Telemetry.Updater/Sts2Telemetry.Updater.csproj"
MOD_MANIFEST="${ROOT}/src/Sts2Telemetry/Sts2Telemetry.json"

die() {
    echo "package-mod-release: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "$1 is required"
}

json_value() {
    python3 - "$1" "$2" <<'PY'
import json
import sys

path, key = sys.argv[1], sys.argv[2]
with open(path, "r", encoding="utf-8") as handle:
    value = json.load(handle)
print(value[key])
PY
}

require_command dotnet
require_command python3

VERSION="${VERSION:-$(json_value "${MOD_MANIFEST}" version)}"
[[ -n "${VERSION}" ]] || die "VERSION is empty"

CONFIGURATION="${CONFIGURATION:-Release}"
PLATFORMS="${STS2_RELEASE_PLATFORMS:-linux-x64 win-x64}"
OUTPUT_DIR="${STS2_RELEASE_OUTPUT_DIR:-${ROOT}/artifacts/mod-release/${VERSION}}"
BASE_URL="${STS2_RELEASE_BASE_URL:-https://example.invalid/releases/mod}"
MIN_SUPPORTED_VERSION="${STS2_RELEASE_MIN_SUPPORTED_VERSION:-0.0.0}"
RELEASE_NOTES="${STS2_RELEASE_NOTES:-STS2 Telemetry ${VERSION}}"
REQUIRES_CONFIRMATION="${STS2_RELEASE_REQUIRES_CONFIRMATION:-false}"
SELF_CONTAINED="${STS2_UPDATER_SELF_CONTAINED:-true}"

case "${REQUIRES_CONFIRMATION}" in
    true|false) ;;
    *) die "STS2_RELEASE_REQUIRES_CONFIRMATION must be true or false" ;;
esac

case "${SELF_CONTAINED}" in
    true|false) ;;
    *) die "STS2_UPDATER_SELF_CONTAINED must be true or false" ;;
esac

mkdir -p "${OUTPUT_DIR}"

dotnet build "${MOD_PROJECT}" -c "${CONFIGURATION}"

python3 - "${ROOT}" "${VERSION}" "${OUTPUT_DIR}" "${BASE_URL}" "${MIN_SUPPORTED_VERSION}" "${RELEASE_NOTES}" "${REQUIRES_CONFIRMATION}" "${MOD_PROJECT}" "${UPDATER_PROJECT}" "${CONFIGURATION}" "${SELF_CONTAINED}" ${PLATFORMS} <<'PY'
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

root = Path(sys.argv[1])
version = sys.argv[2]
output_dir = Path(sys.argv[3])
base_url = sys.argv[4].rstrip("/")
min_supported_version = sys.argv[5]
release_notes = sys.argv[6]
requires_confirmation = sys.argv[7] == "true"
mod_project = Path(sys.argv[8])
updater_project = Path(sys.argv[9])
configuration = sys.argv[10]
self_contained = sys.argv[11]
platforms = sys.argv[12:]

mod_output = mod_project.parent / "bin" / configuration / "net9.0"
required_mod_files = [
    mod_output / "Sts2Telemetry.dll",
    mod_output / "Sts2Telemetry.json",
]
for path in required_mod_files:
    if not path.exists():
        raise SystemExit(f"missing build output: {path}")

artifacts = []
output_dir.mkdir(parents=True, exist_ok=True)

for platform in platforms:
    publish_args = [
        "dotnet",
        "publish",
        str(updater_project),
        "-c",
        configuration,
        "-r",
        platform,
        "--self-contained",
        self_contained,
        "-p:PublishSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
    ]
    if self_contained == "true":
        publish_args.append("-p:EnableCompressionInSingleFile=true")
    subprocess.run(publish_args, check=True, cwd=root)

    helper_name = "Sts2Telemetry.Updater.exe" if platform.startswith("win-") else "Sts2Telemetry.Updater"
    helper_path = updater_project.parent / "bin" / configuration / "net9.0" / platform / "publish" / helper_name
    if not helper_path.exists():
        raise SystemExit(f"missing updater helper for {platform}: {helper_path}")

    package_name = f"sts2-telemetry-{version}-{platform}.zip"
    package_path = output_dir / package_name
    with zipfile.ZipFile(package_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.write(mod_output / "Sts2Telemetry.dll", "Sts2Telemetry.dll")
        archive.write(mod_output / "Sts2Telemetry.json", "Sts2Telemetry.json")
        archive.write(helper_path, helper_name)
        pdb = mod_output / "Sts2Telemetry.pdb"
        if pdb.exists():
            archive.write(pdb, "Sts2Telemetry.pdb")

    digest = hashlib.sha256(package_path.read_bytes()).hexdigest()
    artifacts.append({
        "platform": platform,
        "kind": "mod_package",
        "url": f"{base_url}/{package_name}",
        "sha256": digest,
        "size_bytes": package_path.stat().st_size,
        "file_name": package_name,
    })

manifest = {
    "schema_version": "sts2.telemetry.mod_release.v1",
    "latest_version": version,
    "min_supported_version": min_supported_version,
    "release_notes": release_notes,
    "requires_confirmation": requires_confirmation,
    "artifacts": artifacts,
}

manifest_path = output_dir / "latest.json"
manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")

print(f"Wrote {manifest_path}")
for artifact in artifacts:
    print(f"Wrote {output_dir / artifact['file_name']} sha256={artifact['sha256']}")
PY
