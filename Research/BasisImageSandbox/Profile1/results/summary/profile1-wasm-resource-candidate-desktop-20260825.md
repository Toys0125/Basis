# Profile 1 production-WASM resource candidate — desktop checkpoint

Status: **PROVISIONAL DESKTOP EVIDENCE — not a second wire reconciliation and not a production ceiling**

Source commit: `24a04fcfb804672cbf9cfa5a973d16cf4d28d0c0`

This checkpoint continues Phase 5 / immediate-order items 5, 8, and 9 of `JPEG_XL_PROFILE_V1_IMPLEMENTATION_VALIDATION_PLAN.md`. It measures a public-libjxl-observable coded-frame definition and a provisional structural `decodeWork` candidate in the actual Wasmtime decoder path on the x86 Unity-Server. The implementation does **not** enforce a coded-frame or `decodeWork` ceiling from this evidence.

## Candidate accounting

Coded frames are the public regular `JXL_DEC_FRAME` layers observed with decoder coalescing disabled. This is available through the pinned libjxl public API in both native and WASM builds and does not depend on private progressive-pass state.

Candidate `decodeWork` version 1 is:

```text
5 * submittedCanvasPixels
+ 5 * publicRegularLayerPixels
+ 2048 * publicRegularLayerCount
+ 64 * croppedLayerCount
+ ceil(croppedLayerPixels / 4)
+ 128 * referenceReadEdges
+ ceil(referenceReadPixels / 2)
+ 512 * savedReferenceCount
+ 64 * blendOperationCount
+ ceil(blendOperationPixels / 2)
+ 4 * maximumReferenceChainDepth
+ 5 * previewPixels
```

All arithmetic is checked unsigned 64-bit arithmetic. The score uses only structure observable through the pinned public decoder API. It is independent of wall-clock time, thread count, SIMD choice, scheduling, and private libjxl progressive state.

## Environment

```text
Linux 6.12 / Ubuntu 22.04 x86-64
Intel Xeon E5-2620 v3
24 logical processors
Unity 6000.5.8f1
libjxl 0.12.0 @ a7a9c787341cf703dede03c2009fa460cae5e5df
Emscripten 4.0.23
Wasmtime 44.0.0
WASM linear-memory limit: 256 MiB
fuel limit: 96,000,000,000
concurrency: 1
```

The benchmark decoder SHA was `17a1e564d3540fde7ffefbf0693332179f2a2d13c32986f89ae7b47185209b57`. It was built on Unity-Server with the pinned toolchain but not through the final Docker reproducibility path, so it is benchmark evidence rather than the authoritative production SHA pin.

## Stress results

| Fixture | Coded frames | Max ref depth | `decodeWork` | Decode fuel | Fuel / work |
|---|---:|---:|---:|---:|---:|
| coded frames | 512 | 1 | 1,474,372 | 240,836,420 | 163.348 |
| coded frames | 1,024 | 1 | 2,948,932 | 481,011,981 | 163.114 |
| coded frames | 2,048 | 1 | 5,898,052 | 960,479,666 | 162.847 |
| coded frames | 4,096 | 1 | 11,796,292 | 1,923,268,244 | 163.040 |
| coded frames | 8,192 | 1 | 23,592,772 | 3,848,612,444 | 163.127 |
| reference chain | 512 | 512 | 1,607,232 | 262,934,002 | 163.594 |
| reference chain | 1,024 | 1,024 | 3,214,912 | 524,976,151 | 163.294 |
| reference chain | 2,048 | 2,048 | 6,430,272 | 1,049,168,699 | 163.161 |
| reference chain | 4,096 | 4,096 | 12,860,992 | 2,098,488,465 | 163.167 |

The exact 33,554,432 submitted-pixel boundary scored `336,592,900` work units and consumed `55,208,408,941` full-decode fuel, or about `164.021` fuel/work.

The combined maximum-pixel structural fixture scored `428,560,008` work units and consumed `72,053,436,269` full-decode fuel, or about `168.129` fuel/work. It combines:

```text
33,554,432 submitted pixels
640 public regular layers
511 cropped layers
638 reference reads
468 saved references
638 blend operations
maximum reference-chain depth 130
```

The decoder remained at the 32 MiB initial WASM linear-memory allocation for that fixture.

## Interpretation

For the large adversarial cases that matter to receiver admission, the candidate tracks full-decode fuel closely enough to continue calibration: roughly `162.8` to `168.2` fuel per work unit across plain coded-frame amplification, deep reference chains, the exact submitted-pixel boundary, and the combined pixel/structure workload.

Small one-frame fixtures show larger ratios because fixed decoder/module work dominates a very small structural score. Their absolute resource use is small and they do not determine a hostile-input ceiling.

The desktop evidence demonstrates that the candidate is useful for the next validation phase. It does **not** establish a final wire ceiling. In particular:

- no final coded-frame ceiling is selected; plain coded-frame amplification has been tested through 8,192 public regular layers;
- no final `decodeWork` ceiling is selected; the largest observed candidate value in this checkpoint is 428,560,008;
- Quest/Android on-device production-WASM measurements are still required by the plan before the resource contract can be finalized;
- limit-1 / limit / limit+1 portable conformance vectors must be generated only after the final ceilings are selected;
- the second wire reconciliation remains pending and Gates A2/B/C/D remain open.

The matching machine-readable summary is `profile1-wasm-resource-candidate-desktop-20260825.json`.
