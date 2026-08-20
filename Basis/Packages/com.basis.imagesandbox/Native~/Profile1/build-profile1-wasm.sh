#!/usr/bin/env bash
set -euo pipefail

LIBJXL_TAG="v0.12.0"
LIBJXL_COMMIT="a7a9c787341cf703dede03c2009fa460cae5e5df"
EMSCRIPTEN_IMAGE="emscripten/emsdk:4.0.23"
EXPECTED_SHA256="b644482523b6ee3cf639fcfcf57e6974f857fa1cdd7528403f08ccc5eec8a37d"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_PATH="${1:-${SCRIPT_DIR}/../../Runtime/Resources/BasisImageSandbox/profile1_decoder.bytes}"
CACHE_DIR="${BASIS_PROFILE1_BUILD_CACHE:-${TMPDIR:-/tmp}/basis-profile1-wasm-build}"
LIBJXL_DIR="${CACHE_DIR}/libjxl"
BUILD_DIR="${CACHE_DIR}/build"

mkdir -p "${CACHE_DIR}" "$(dirname -- "${OUTPUT_PATH}")"

if [[ ! -d "${LIBJXL_DIR}/.git" ]]; then
  git clone --depth 1 --branch "${LIBJXL_TAG}" --recurse-submodules --shallow-submodules \
    https://github.com/libjxl/libjxl.git "${LIBJXL_DIR}"
fi

actual_commit="$(git -C "${LIBJXL_DIR}" rev-parse HEAD)"
if [[ "${actual_commit}" != "${LIBJXL_COMMIT}" ]]; then
  echo "Pinned libjxl mismatch: expected ${LIBJXL_COMMIT}, got ${actual_commit}" >&2
  exit 1
fi

git -C "${LIBJXL_DIR}" submodule update --init --recursive --depth 1
rm -rf "${BUILD_DIR}"
mkdir -p "${BUILD_DIR}"

repo_mount="${SCRIPT_DIR}"
libjxl_mount="${LIBJXL_DIR}"
build_mount="${BUILD_DIR}"

docker_command=(docker)
if ! docker info >/dev/null 2>&1; then
  if sudo -n docker info >/dev/null 2>&1; then
    docker_command=(sudo docker)
  else
    echo "Docker is required to build the pinned Profile 1 WASM decoder." >&2
    exit 1
  fi
fi

"${docker_command[@]}" run --rm \
  -v "${repo_mount}:/profile1:ro" \
  -v "${libjxl_mount}:/libjxl" \
  -v "${build_mount}:/build" \
  "${EMSCRIPTEN_IMAGE}" \
  bash -lc '
    emcmake cmake -S /profile1 -B /build \
      -DCMAKE_BUILD_TYPE=Release \
      -DLIBJXL_SOURCE_DIR=/libjxl
    cmake --build /build --target profile1_decoder --parallel
  '

cp "${BUILD_DIR}/profile1_decoder.wasm" "${OUTPUT_PATH}"
actual_sha256="$(sha256sum "${OUTPUT_PATH}" | awk '{print $1}')"
if [[ "${actual_sha256}" != "${EXPECTED_SHA256}" ]]; then
  echo "Profile 1 WASM SHA-256 changed." >&2
  echo "expected: ${EXPECTED_SHA256}" >&2
  echo "actual:   ${actual_sha256}" >&2
  echo "Do not update the pin without rerunning native/WASM differential and receiver benchmarks." >&2
  exit 1
fi

echo "Built ${OUTPUT_PATH}"
echo "SHA-256 ${actual_sha256}"
