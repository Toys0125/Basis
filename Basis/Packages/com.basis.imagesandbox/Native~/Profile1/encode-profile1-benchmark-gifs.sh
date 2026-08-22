#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <input-directory> <output-directory>" >&2
  exit 2
fi

LIBJXL_TAG="v0.12.0"
LIBJXL_COMMIT="a7a9c787341cf703dede03c2009fa460cae5e5df"
EMSCRIPTEN_IMAGE="emscripten/emsdk:4.0.23"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ENCODER_DIR="${SCRIPT_DIR}/BenchmarkEncoder"
CACHE_DIR="${BASIS_PROFILE1_BUILD_CACHE:-${TMPDIR:-/tmp}/basis-profile1-wasm-build}"
LIBJXL_DIR="${CACHE_DIR}/libjxl"
BUILD_DIR="${CACHE_DIR}/benchmark-encoder-native"
INPUT_DIR="$(cd -- "$1" && pwd)"
mkdir -p "$2"
OUTPUT_DIR="$(cd -- "$2" && pwd)"
mkdir -p "${CACHE_DIR}" "${BUILD_DIR}"

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

docker_command=(docker)
if ! docker info >/dev/null 2>&1; then
  if sudo -n docker info >/dev/null 2>&1; then
    docker_command=(sudo docker)
  else
    echo "Docker is required for GIF benchmark conversion." >&2
    exit 1
  fi
fi

"${docker_command[@]}" run --rm \
  -v "${ENCODER_DIR}:/encoder:ro" \
  -v "${LIBJXL_DIR}:/libjxl:ro" \
  -v "${BUILD_DIR}:/build" \
  -v "${INPUT_DIR}:/input:ro" \
  -v "${OUTPUT_DIR}:/output" \
  "${EMSCRIPTEN_IMAGE}" \
  bash -lc 'CC=gcc CXX=g++ cmake -S /encoder -B /build -DCMAKE_BUILD_TYPE=Release -DLIBJXL_SOURCE_DIR=/libjxl && cmake --build /build --target profile1_benchmark_encoder --parallel && /build/profile1_benchmark_encoder /input /output'
