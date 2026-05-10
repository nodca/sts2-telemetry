#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOD_MANIFEST="${ROOT}/src/Sts2Telemetry/Sts2Telemetry.json"
VALIDATOR="${ROOT}/scripts/validate-mod-release-assets.sh"

die() {
    echo "upload-github-mod-release: $*" >&2
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

require_command gh
require_command python3

VERSION="${VERSION:-$(json_value "${MOD_MANIFEST}" version)}"
TAG="${STS2_RELEASE_TAG:-v${VERSION}}"
RELEASE_DIR="${STS2_RELEASE_OUTPUT_DIR:-${ROOT}/artifacts/mod-release/${VERSION}}"
REPO="${STS2_GITHUB_REPO:-}"

[[ -d "${RELEASE_DIR}" ]] || die "missing release directory: ${RELEASE_DIR}; run scripts/package-mod-release.sh first"
[[ -f "${RELEASE_DIR}/latest.json" ]] || die "missing ${RELEASE_DIR}/latest.json"
"${VALIDATOR}" "${RELEASE_DIR}"

if [[ -z "${REPO}" ]]; then
    REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null || true)"
fi
[[ -n "${REPO}" ]] || die "set STS2_GITHUB_REPO=owner/repo"

release_args=(--repo "${REPO}")
assets=("${RELEASE_DIR}/latest.json")
while IFS= read -r -d '' asset; do
    assets+=("${asset}")
done < <(find "${RELEASE_DIR}" -maxdepth 1 -type f -name "sts2-telemetry-${VERSION}-*.zip" -print0 | sort -z)

[[ ${#assets[@]} -gt 1 ]] || die "no package zip assets found in ${RELEASE_DIR}"

if gh release view "${TAG}" "${release_args[@]}" >/dev/null 2>&1; then
    gh release upload "${TAG}" "${assets[@]}" "${release_args[@]}" --clobber
else
    gh release create "${TAG}" "${assets[@]}" "${release_args[@]}" \
        --title "STS2 Telemetry ${VERSION}" \
        --notes "${STS2_RELEASE_NOTES:-STS2 Telemetry ${VERSION}}"
fi

echo "Uploaded ${#assets[@]} asset(s) to ${REPO} ${TAG}"
