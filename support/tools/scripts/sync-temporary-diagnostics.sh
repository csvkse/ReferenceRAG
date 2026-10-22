#!/usr/bin/env bash
# ==============================================================================
# ReferenceRAG - TemporaryDiagnostics Latest Release Auto-Sync Script (Plan B)
# ==============================================================================
set -euo pipefail

REPO="csvkse/TemporaryDiagnostics"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../../.." && pwd)"
PACKAGES_DIR="${ROOT_DIR}/packages"
PROPS_FILE="${ROOT_DIR}/Directory.Packages.props"
TOKEN="${GITHUB_TOKEN:-}"
FORCE=false
OFFLINE_FALLBACK=true

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      REPO="$2"
      shift 2
      ;;
    --packages-dir)
      PACKAGES_DIR="$2"
      shift 2
      ;;
    --props-file)
      PROPS_FILE="$2"
      shift 2
      ;;
    --token)
      TOKEN="$2"
      shift 2
      ;;
    --force)
      FORCE=true
      shift
      ;;
    --no-fallback)
      OFFLINE_FALLBACK=false
      shift
      ;;
    *)
      echo "[Warn] Unknown argument: $1"
      shift
      ;;
  esac
done

mkdir -p "${PACKAGES_DIR}"

echo "=========================================================="
echo "  ReferenceRAG - TemporaryDiagnostics Auto-Sync Tool"
echo "=========================================================="
echo "Target Repo:   ${REPO}"
echo "Packages Dir:  ${PACKAGES_DIR}"
echo "Props File:    ${PROPS_FILE}"

# 1. Read current version from Directory.Packages.props
CURRENT_VERSION="0.0.0"
if [[ -f "${PROPS_FILE}" ]]; then
  DETECTED_VER=$(grep -E 'PackageVersion.*Include="TemporaryDiagnostics\.AspNetCore"' "${PROPS_FILE}" | sed -E 's/.*Version="([^"]+)".*/\1/' || true)
  if [[ -n "${DETECTED_VER}" ]]; then
    CURRENT_VERSION="${DETECTED_VER}"
  fi
fi
echo "Current Version: ${CURRENT_VERSION}"

# 2. Query GitHub Releases API
API_URL="https://api.github.com/repos/${REPO}/releases/latest"
AUTH_HEADER=()
if [[ -n "${TOKEN}" ]]; then
  AUTH_HEADER=(-H "Authorization: Bearer ${TOKEN}")
  echo "Using GitHub Authorization Token (Rate Limit safe)"
fi

RELEASE_JSON=""
if ! RELEASE_JSON=$(curl -sSL --max-time 15 "${AUTH_HEADER[@]}" -H "User-Agent: ReferenceRAG-AutoSync-Tool" -H "Accept: application/vnd.github.v3+json" "${API_URL}"); then
  echo "[Warning] Failed to fetch latest release from ${API_URL}"
  if [[ "${OFFLINE_FALLBACK}" == "true" ]]; then
    EXISTING_COUNT=$(find "${PACKAGES_DIR}" -maxdepth 1 -name "TemporaryDiagnostics.*.nupkg" | wc -l || true)
    if [[ "${EXISTING_COUNT}" -gt 0 ]]; then
      echo "[Fallback] Using ${EXISTING_COUNT} existing local packages in ${PACKAGES_DIR}"
      exit 0
    else
      echo "[Error] No local fallback packages available!"
      exit 1
    fi
  else
    exit 1
  fi
fi

# Check for API error response (e.g. rate limit or not found)
if echo "${RELEASE_JSON}" | grep -q '"message":'; then
  API_MSG=$(echo "${RELEASE_JSON}" | grep -E '"message":' | head -n 1)
  if echo "${RELEASE_JSON}" | grep -q 'API rate limit exceeded'; then
    echo "[Warning] GitHub API rate limit exceeded: ${API_MSG}"
  else
    echo "[Warning] GitHub API message: ${API_MSG}"
  fi
  if [[ "${OFFLINE_FALLBACK}" == "true" ]]; then
    echo "[Fallback] Retaining existing packages in ${PACKAGES_DIR}"
    exit 0
  fi
fi

# 3. Extract remote tag and assets
REMOTE_TAG=""
if command -v jq &> /dev/null; then
  REMOTE_TAG=$(echo "${RELEASE_JSON}" | jq -r '.tag_name // empty')
else
  REMOTE_TAG=$(echo "${RELEASE_JSON}" | grep -E '"tag_name":' | head -n 1 | sed -E 's/.*"tag_name": "([^"]+)".*/\1/')
fi

if [[ -z "${REMOTE_TAG}" || "${REMOTE_TAG}" == "null" ]]; then
  echo "[Warning] No valid tag_name found in release response"
  exit 0
fi

REMOTE_VERSION="${REMOTE_TAG#v}"
REMOTE_VERSION="${REMOTE_VERSION#V}"
echo "Remote Latest Version: ${REMOTE_VERSION} (Tag: ${REMOTE_TAG})"

# Extract nupkg download URLs and file names
declare -a ASSET_NAMES=()
declare -a ASSET_URLS=()

if command -v jq &> /dev/null; then
  while IFS= read -r line; do
    if [[ -n "${line}" ]]; then
      name=$(echo "${line}" | jq -r '.name')
      url=$(echo "${line}" | jq -r '.browser_download_url')
      if [[ "${name}" == *.nupkg ]]; then
        ASSET_NAMES+=("${name}")
        ASSET_URLS+=("${url}")
      fi
    fi
  done < <(echo "${RELEASE_JSON}" | jq -c '.assets[]?')
else
  # Grep fallback parser
  TEMP_ASSETS=$(echo "${RELEASE_JSON}" | grep -B 2 -A 5 '"browser_download_url":' || true)
  # Extract names
  while IFS= read -r n; do
    if [[ "${n}" == *.nupkg ]]; then
      ASSET_NAMES+=("${n}")
    fi
  done < <(echo "${RELEASE_JSON}" | grep -E '"name": "[^"]+\.nupkg"' | sed -E 's/.*"name": "([^"]+)".*/\1/')

  # Extract urls
  while IFS= read -r u; do
    if [[ "${u}" == *.nupkg ]]; then
      ASSET_URLS+=("${u}")
    fi
  done < <(echo "${RELEASE_JSON}" | grep -E '"browser_download_url": "[^"]+\.nupkg"' | sed -E 's/.*"browser_download_url": "([^"]+)".*/\1/')
fi

ASSET_COUNT=${#ASSET_NAMES[@]}
if [[ ${ASSET_COUNT} -eq 0 ]]; then
  echo "[Warning] No .nupkg assets found in remote release ${REMOTE_TAG}"
  exit 0
fi

echo "Found ${ASSET_COUNT} .nupkg package assets in release."

# 4. Idempotency Check
ALL_EXIST=true
for asset_name in "${ASSET_NAMES[@]}"; do
  target_file="${PACKAGES_DIR}/${asset_name}"
  if [[ ! -s "${target_file}" ]]; then
    ALL_EXIST=false
    break
  fi
done

if [[ "${FORCE}" != "true" && "${CURRENT_VERSION}" == "${REMOTE_VERSION}" && "${ALL_EXIST}" == "true" ]]; then
  echo "[Idempotent] Local packages are already up to date (${REMOTE_VERSION}), skipping download."
  exit 0
fi

echo "Synchronizing to latest version ${REMOTE_VERSION}..."

# 5. Download assets to temp directory
TMP_DIR="${PACKAGES_DIR}/.download_tmp"
rm -rf "${TMP_DIR}"
mkdir -p "${TMP_DIR}"

for i in "${!ASSET_NAMES[@]}"; do
  name="${ASSET_NAMES[$i]}"
  url="${ASSET_URLS[$i]}"
  echo "Downloading ${name}..."
  curl -sSL --max-time 60 "${AUTH_HEADER[@]}" -H "User-Agent: ReferenceRAG-AutoSync-Tool" -o "${TMP_DIR}/${name}" "${url}"
  if [[ ! -s "${TMP_DIR}/${name}" ]]; then
    echo "[Error] Download failed or empty file for ${name}"
    rm -rf "${TMP_DIR}"
    exit 1
  fi
done

# 6. Clean older packages and replace with new
find "${PACKAGES_DIR}" -maxdepth 1 -name "TemporaryDiagnostics.*.nupkg" ! -name "*${REMOTE_VERSION}.nupkg" -exec rm -f {} +
for name in "${ASSET_NAMES[@]}"; do
  mv -f "${TMP_DIR}/${name}" "${PACKAGES_DIR}/${name}"
done
rm -rf "${TMP_DIR}"

# 7. Update Directory.Packages.props
if [[ -f "${PROPS_FILE}" ]]; then
  echo "Updating ${PROPS_FILE} with version ${REMOTE_VERSION}..."
  sed -i -E "s/(<PackageVersion[[:space:]]+Include=\"TemporaryDiagnostics\.[^\"]+\"[[:space:]]+Version=\")[^\"]+(\"[[:space:]]*\/>)/\1${REMOTE_VERSION}\2/g" "${PROPS_FILE}"
  echo "Successfully updated Directory.Packages.props to ${REMOTE_VERSION}."
fi

echo "=========================================================="
echo "  TemporaryDiagnostics synced successfully: v${REMOTE_VERSION}"
echo "=========================================================="
