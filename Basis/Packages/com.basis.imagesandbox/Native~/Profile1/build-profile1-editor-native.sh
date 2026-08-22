#!/usr/bin/env bash
set -euo pipefail

LIBJXL_COMMIT="a7a9c787341cf703dede03c2009fa460cae5e5df"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../../../.." && pwd)"
ENCODER_DIR="${SCRIPT_DIR}/BenchmarkEncoder"
CACHE_ROOT="${BASIS_PROFILE1_BUILD_CACHE:-${TMPDIR:-/tmp}/basis-profile1-native-build}"
LIBJXL_DIR="${CACHE_ROOT}/libjxl"
HOST_OS="$(uname -s)"
HOST_ARCH="$(uname -m)"

case "${HOST_OS}:${HOST_ARCH}" in
  Linux:x86_64)
    RID="linux-x86_64"
    OUTPUT_DIR="${PROJECT_ROOT}/Packages/com.basis.imagesandbox/Plugins/Editor/Linux/x86_64"
    OUTPUT_NAME="libbasis_profile1_editor.so"
    ;;
  Linux:aarch64|Linux:arm64)
    RID="linux-arm64"
    OUTPUT_DIR="${PROJECT_ROOT}/Packages/com.basis.imagesandbox/Plugins/Editor/Linux/ARM64"
    OUTPUT_NAME="libbasis_profile1_editor.so"
    ;;
  Darwin:x86_64)
    RID="macos-x86_64"
    OUTPUT_DIR="${PROJECT_ROOT}/Packages/com.basis.imagesandbox/Plugins/Editor/macOS/x86_64"
    OUTPUT_NAME="libbasis_profile1_editor.dylib"
    ;;
  Darwin:arm64)
    RID="macos-arm64"
    OUTPUT_DIR="${PROJECT_ROOT}/Packages/com.basis.imagesandbox/Plugins/Editor/macOS/arm64"
    OUTPUT_NAME="libbasis_profile1_editor.dylib"
    ;;
  *)
    echo "Unsupported editor-native Profile 1 host: ${HOST_OS} ${HOST_ARCH}" >&2
    exit 2
    ;;
esac

BUILD_DIR="${CACHE_ROOT}/editor-native-${RID}"
mkdir -p "${CACHE_ROOT}"
if [[ ! -d "${LIBJXL_DIR}/.git" ]]; then
  git clone https://github.com/libjxl/libjxl.git "${LIBJXL_DIR}"
fi
git -C "${LIBJXL_DIR}" fetch --tags --force origin
git -C "${LIBJXL_DIR}" checkout --force "${LIBJXL_COMMIT}"
git -C "${LIBJXL_DIR}" submodule sync --recursive
if ! git -C "${LIBJXL_DIR}" submodule update --init --recursive --force; then
  git -C "${LIBJXL_DIR}" submodule deinit --force --all
  git -C "${LIBJXL_DIR}" submodule update --init --recursive --force
fi

cmake -S "${ENCODER_DIR}" -B "${BUILD_DIR}" \
  -DCMAKE_BUILD_TYPE=Release \
  -DLIBJXL_SOURCE_DIR="${LIBJXL_DIR}"
cmake --build "${BUILD_DIR}" --config Release --target basis_profile1_editor --parallel

BUILT="${BUILD_DIR}/${OUTPUT_NAME}"
if [[ ! -f "${BUILT}" ]]; then
  BUILT="${BUILD_DIR}/Release/${OUTPUT_NAME}"
fi
if [[ ! -f "${BUILT}" ]]; then
  echo "Native build succeeded but ${OUTPUT_NAME} was not produced." >&2
  exit 3
fi
mkdir -p "${OUTPUT_DIR}"
cp -f "${BUILT}" "${OUTPUT_DIR}/${OUTPUT_NAME}"
echo "Built editor-only Profile 1 native codec: ${OUTPUT_DIR}/${OUTPUT_NAME}"
