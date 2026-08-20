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

From the repository root, run:

```bash
Basis/Packages/com.basis.imagesandbox/Native~/Profile1/build-profile1-wasm.sh
```

The script clones the pinned libjxl revision into a temporary build cache, builds with the pinned multi-architecture Emscripten container, verifies the exact decoder SHA-256, and writes:

```text
Basis/Packages/com.basis.imagesandbox/Runtime/Resources/BasisImageSandbox/profile1_decoder.bytes
```

The runtime fails closed if that generated resource is absent. Do not update the libjxl/Emscripten/decoder hash pins without rerunning the native/WASM differential and receiver benchmark suites required by the Profile 1 implementation-validation plan.

The generated module has no WASI, filesystem, networking, clock, or random imports. Its only host import is the validated Emscripten memory-growth notification callback; Wasmtime linear-memory, fuel, and epoch-interruption limits remain host-controlled.
