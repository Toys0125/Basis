# Basis Image Sandbox

`com.basis.imagesandbox` hosts untrusted image-codec work outside the Unity/native trust boundary.

## JPEG XL Profile 1 pins

The initial Profile 1 decoder is pinned to:

- libjxl `v0.12.0`, commit `a7a9c787341cf703dede03c2009fa460cae5e5df`
- Emscripten `4.0.23`
- Wasmtime `44.0.0`
- MediaSandbox native-runtime package commit `f200256a2e56c1c5229a07e5530faa4a6b1ab325`
- decoder WASM SHA-256 `b644482523b6ee3cf639fcfcf57e6974f857fa1cdd7528403f08ccc5eec8a37d`

Remote/network JPEG XL codestream semantics must be processed only through this WASM path. Native libjxl is reserved for trusted-local/editor/oracle use.

## Build the decoder asset

In the Unity editor, use `Basis/Debug/JPEG XL Profile 1/Build Test Decoder`. The menu runs the native PowerShell build on Windows and the bash build on other editor platforms.

From the repository root, the same builds can be run manually:

```powershell
# Windows PowerShell
Basis/Packages/com.basis.imagesandbox/Native~/Profile1/build-profile1-wasm.ps1
```

```bash
# Linux/macOS
Basis/Packages/com.basis.imagesandbox/Native~/Profile1/build-profile1-wasm.sh
```

Docker must be installed and running; on Windows this means Docker Desktop with Linux containers. The scripts clone the pinned libjxl revision into a temporary build cache, build with the pinned multi-architecture Emscripten container, verify the exact decoder SHA-256, and write:

```text
Basis/Packages/com.basis.imagesandbox/Runtime/Resources/BasisImageSandbox/profile1_decoder.bytes
```

The runtime fails closed if that generated resource is absent. Do not update the libjxl/Emscripten/decoder hash pins without rerunning the native/WASM differential and receiver benchmark suites required by the Profile 1 implementation-validation plan.

The generated module has no WASI, filesystem, networking, clock, or random imports. Its only host import is the validated Emscripten memory-growth notification callback; Wasmtime linear-memory, fuel, and epoch-interruption limits remain host-controlled.

## Benchmarking

After building the decoder, use `Basis/Debug/JPEG XL Profile 1/Build Editor Native Codec` once on the editor host, then open `Basis/Debug/JPEG XL Profile 1/Benchmark`. The editor-native codec follows the same package pattern as Basis Media Player: CMake source lives under `Native~/`, the resulting shared library is copied into `Plugins/Editor/<platform>/`, and Unity's importer is explicitly configured so it is never included in player builds. Windows builds use the host Visual Studio/CMake toolchain; Linux/macOS use the host CMake toolchain. No Docker or Emscripten process is involved in normal GIF fixture preparation.

Select a directory containing `.jxl` and/or `.gif` fixtures, choose warmup/measured iteration counts, a concurrency sweep such as `1,2,4`, and a fuel sweep such as `1000000000,4000000000,16000000000`, then run the benchmark. Before timing begins, the editor accepts raw JPEG XL codestreams or standard `jxlc`/fragmented `jxlp` containers, extracts the codestream without decoding or re-encoding it, and wraps it into the exact canonical Profile 1 single-`jxlp` container. GIF fixtures are resolved through a persistent content-addressed cache under the benchmark output directory. Cache misses are decoded with Basis's existing GIF decoder, composed into their display-frame timeline, and losslessly encoded by the pinned trusted-local native libjxl editor plugin before timing begins; unchanged GIFs reuse the cached Profile 1 JXL on later runs. Already-canonical JPEG XL fixtures are used unchanged. The original format/size and prepared size are recorded in the results, and all preparation is excluded from timing. The benchmark itself still exercises the production WASM/Wasmtime decoder path, which is also the only JPEG XL codestream parser used by player/runtime receive paths.

Wasmtime out-of-fuel traps are reported separately from epoch/wall-clock timeouts. The benchmark records the actual fuel consumed by Stage B preflight and full decode calls and reports aggregate fuel means/maxima for each configured fuel/concurrency point. Exact WASM linear-memory high-water marks remain unavailable until the runtime exposes them directly; the benchmark does not infer or fabricate those values.
