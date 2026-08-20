# JPEG XL Profile 1 Final Benchmark and Wire Decision

## Executive decision

Freeze Profile 1 as JPEG XL in the minimal JPEG XL container form with ordered `jxlp` boxes. The native wire-format decision is **FINAL / FROZEN**, with guarded implementation validation. JPEG XL is exact on the corrected 139-fixture corpus, the 77-vector conformance suite, and the completed libjxl 0.12 sender rerun. APNG is not a universal Profile 1 alternative because valid canonical durations are not all exactly representable by APNG. WebP remains outside Profile 1. V2 remains the required legacy fallback.

The selected wire submitted-pixel ceiling is **33,554,432 (32 Mi-pixels)**. Local sender admission remains memory-class dependent and is never allowed to override the wire ceiling. A 64 Mi-pixel local workload can be encoder-admissible on a large sender, but it is not a conforming Profile 1 payload.

## Starting evidence

The authoritative prior native benchmark used the frame-fed canonical RGBA8 path, not direct GIF-to-`cjxl` conversion:

- 139/139 encoded, decoded, strict RGBA exact, frame-count exact, frame-timing exact, and loop exact;
- authoritative effort-3 payload median 1.21 MiB, P95 6.79 MiB, maximum 44.70 MiB;
- authoritative JXL/V2 median ratio 0.904;
- WebP was smaller on all 139 fixtures but only 128/139 satisfied the complete Profile 1 equivalence gate;
- APNG was exact on 129/139 real fixtures with the optimized default lane and failed the remaining ten on timing representability;
- incremental JXL encoder memory remained substantial, so local memory policy and wire policy must remain separate.

The previous phase’s recommendation is preserved as the starting benchmark evidence. This report supersedes its unresolved `profileMaximumSubmittedCanvasPixels: null` field.

## Artifact corrections

The new descriptor is [profile-v1-wire.json](profile-v1-wire.json). It removes the ambiguous top-level sender/wire conflation by defining:

- `profileMaximumSubmittedCanvasPixels = 33,554,432`;
- memory-class local admission limits of 8, 32, and 64 Mi-pixels;
- effective Profile 1 sender limits as the minimum of local admission and the 32 Mi-pixel wire ceiling;
- dedicated encoder budget, animation headroom, safe in-process threshold, and worker cap as separate concepts.

The existing patch metrics remain separate: `v2CompatibilityInflation`, `fullCanvasReduction`, and `rectangleInefficiency`. V2 baseline failures remain separate from V3 fallback events through `v2BaselineFailures`, `profile1FallbackToV2Count`, `v2FallbackSucceeded`, and `v2FallbackFailed`.

The wire descriptor now defines `num_loops = 0` as infinite playback and every positive value as total playthroughs. The RGBA contract is explicit: three RGB color channels plus exactly one 8-bit unassociated alpha extra channel at index 0, with no non-alpha extra channels. Color is RGB/D65/sRGB/sRGB, 8-bit, exponent 0, identity orientation.

## Timing audit and payload form

The canonical timing audit covers 6,479 frames across all 139 real fixtures. The canonical minimum is 33,334 µs, the maximum is 5,000,000 µs, no frame is below the Profile 1 minimum, and no fixture exceeds the 300,000,000 µs total-duration limit.

The duration representability sweep covers 43 observed and boundary values. JPEG XL passed 43/43 integer-microsecond timing probes. APNG exactly represented 30/43; the failures include 33,334 µs. The policy is therefore to reject invalid canonical timing during Profile 1 preflight, with no silent wire-timebase rounding.

The first raw animated codestream helper output was rejected as truncated by independent `djxl`. The corrected benchmark encoder emits a minimal container containing only `ftyp` and ordered `jxlp` boxes; its one-frame path now canonicalizes libjxl's `jxlc` result into one final-marked `jxlp`. The exact `ftyp` bytes are `00000014667479706a786c20000000006a786c20`: size 20, major brand `jxl `, minor 0, and exactly one compatible brand `jxl `. A tiny payload is identified by `file` as `JPEG XL container`, begins with the JXL container signature, passes the native exact decoder, and passes both `djxl 0.7.0` and `djxl 0.12.0` in the completed layout audit. Raw codestreams are not accepted by the wire probe.

The current libjxl 0.12 output-mode comparison tested output modes 0, 1, and 2. Modes 0 and 1 emit ordered `jxlp`; mode 2 emits out-of-order-capable `jxlp` with `ftyp` version 1. A rewrapped single-`jxlc` candidate passed native and independent decoding and saved 28–376 bytes on the four layout workloads, but libjxl does not naturally emit one `jxlc` for multi-frame `AddImageFrame` animations. Mode 1 is selected because it is the native maximally-compatible path and avoids a second full-payload rewrite.

Evidence: `results/raw/profile1-timing-audit.jsonl`, `results/raw/profile1-duration-representability.jsonl`, and `results/raw/profile1-payload-form-audit.jsonl`.

The receiver validation order is frozen as: payload bytes and cap; container/profile metadata; frame headers; dimensions/frame/timing; submitted canvas pixels; memory reservation; decode; rectangle extraction; extracted-patch cap during extraction; active-image construction.

## Worker overhead and isolation

The worker overhead corpus contains 66 cases spanning small, median, large, and synthetic workloads. Empty-worker startup was 0.64 ms median and 1.11 ms P95; routine worker overhead P95 was 5.58 ms. Parent survival and cleanup passed across the measured failure paths.

Decision: all native JPEG XL encodes use a worker. A workload over the dedicated local encoder budget does not start JXL. A workload within that budget is isolated in a memory-limited worker; the worker is not an admission bypass.

Final fields: `encoderIsolation = worker-all-jxl`, `safeInProcessMaximumSubmittedCanvasPixels = 0`, and the default worker cap is 768 MiB, with per-memory-class caps in the descriptor. The current libjxl 0.12 run added 62 sender cases; all 59 completed encodes were strict exact. The 256-MiB cap terminated before a valid result, while 768 MiB and 1.5 GiB completed. This confirms that worker isolation protects the parent but does not turn an over-budget case into an admissible one.

## Dedicated encoder-budget policy

The selected allocation is 50% of the animation working-set budget. The tested candidate shares were 25%, 40%, 50%, 60%, and 75%, with resident animation state, multiple resident animations, compositor/decode reservations, and import buffers represented in the benchmark datasets.

| Memory class | Animation working-set budget | Dedicated encoder budget | Local sender admission | Effective Profile 1 limit |
|---|---:|---:|---:|---:|
| 4 GiB or less | 512 MiB | 256 MiB | 8 Mi-pixels | 8 Mi-pixels |
| 8 GiB | 1.5 GiB | 768 MiB | 32 Mi-pixels | 32 Mi-pixels |
| Above 8 GiB | 3 GiB | 1.5 GiB | 64 Mi-pixels | 32 Mi-pixels |

The >8 GiB class’s 64 Mi-pixel local limit is intentionally reduced to 32 Mi-pixels for Profile 1 wire admission. The remaining local capacity can support a future profile or another transport; it cannot be claimed by Profile 1.

The 0.12 mode-1 memory observations were 92.8, 124.2, 188.7, and 316.8 MiB peak RSS delta for 8, 16, 32, and 64 Mi submitted pixels on 1024² identical workloads. Geometry/content still matters: a 2048², 32-Mi-pixel workload measured 573.5 MiB, and a 2048², 128-Mi-pixel reference measured 763.5 MiB. The class limits therefore remain guarded and content/geometry-aware rather than being raised solely from the favorable identical-frame slope.

Evidence: `results/raw/profile1-libjxl-v012-memory.jsonl`, `results/summary/profile1-libjxl-v012-memory.csv`.

## Wire ceiling and independent decoder validation

The candidate matrix covered 15 exact-pixel workloads at 16, 32, 48, and 64 Mi-pixels, including 1024² and 2048² canvas/frame combinations, identical frames, sparse/broad motion, and noisy content. All successfully encoded cases decoded exactly and stayed within the modeled 512 MiB receiver envelope with 20% headroom. Noisy cases that crossed 64 MiB were rejected by the hard output cap, including at lower submitted-pixel counts; this is expected and is not a reason to raise the payload cap.

The selected 32 Mi-pixel wire ceiling is the conservative receiver-profile boundary. It leaves room for native decoder state and the active decoded animation working set while retaining the 64 MiB payload and 64 Mi-pixel extracted-patch caps. The independent decoder audit tested 16, 32, and 64 Mi-pixel representative container payloads: `djxl` accepted 3/3 and the native exact oracle passed 3/3. The new layout audit adds 12 libjxl 0.12 samples; every native `jxlp` sample and every rewrapped `jxlc` candidate was accepted by both exact oracles.

The corrected container-form real-corpus rerun is stronger evidence than the previous raw-helper run: 139/139 encoded, decoded, strict RGBA exact, frame-count exact, frame-timing exact, and loop exact. Its payload median is 1,274,195 bytes, P95 7,116,484 bytes, and maximum 46,875,776 bytes. Encode median is 303.0 ms, P95 1,443.1 ms, maximum 5,150.5 ms.

Evidence: `results/raw/profile1-wire-ceiling.jsonl`, `results/summary/profile1-wire-ceiling-summary.json`, `results/raw/profile1-independent-decoder-audit.jsonl`, `results/raw/profile1-container-layout.jsonl`, and `results/raw/profile1-jxl-v1-real-corpus.jsonl`.

## Wire freeze verification

The final wire gate is complete:

- exact `ftyp` bytes and brand policy: PASS;
- exact `jxlp` four-byte big-endian counter, lower-31-bit sequence, final high bit, and EOF rule: PASS;
- signature → `ftyp` → `jxlp[0..N final]` ordering with `jxlc`, metadata, padding, and trailing data rejected: PASS;
- loop semantics for 0/1/2/3 total playthroughs: PASS;
- exact alpha contract, including hidden transparent RGB: PASS;
- exact RGB/D65/sRGB/sRGB color contract and negative gates: PASS;
- expanded conformance: PASS, 77/77 (17 positive, 60 negative);
- libjxl 0.12 container matrix, including one-frame normalization: PASS, 9/9;
- current frozen sender smoke: PASS, 139/139, with independent `djxl` acceptance 139/139.

The required final datasets are `results/raw/profile1-ftyp-conformance.jsonl`, `results/raw/profile1-jxlp-index-conformance.jsonl`, `results/raw/profile1-loop-conformance.jsonl`, `results/raw/profile1-alpha-conformance.jsonl`, `results/raw/profile1-color-conformance.jsonl`, `results/raw/profile1-container-final.jsonl`, `results/raw/profile1-wire-smoke-139.jsonl`, and `results/raw/profile1-conformance-final.jsonl`.

**PROFILE 1 WIRE FORMAT = FROZEN.** The native benchmark is ready for deferred implementation validation; it is not an unconditional production replacement.

## Near-cap payload predictor

The expanded near-cap dataset contains 50 fixtures across noisy, correlated, photographic, alpha, pixel-art, scrolling, and mixed content. The hybrid conservative predictor produced zero false acceptances for outputs above 64 MiB. False rejections are reported separately in the predictor summary for below 56 MiB, 56–60 MiB, and 60–64 MiB bands.

The predictor is advisory. The encoded-output cap remains authoritative, and output crossing 64 MiB is aborted with no finalized payload.

## JXL-versus-V2 selection predictor

The selection dataset evaluates percentage saving, absolute saving, combined saving, net latency, and payload-limit rescue rules at 0.5, 1, 2, 3.75, and 10 MiB/s.

The implementable default is:

```text
if estimated JXL memory exceeds the dedicated budget: use V2
else if V2 is valid and predicted JXL saving is below both 10% and 512 KiB: use V2
else if predicted JXL cannot fit the payload cap: use V2
else select JXL effort 3
if effort 3 is predicted to miss 3 seconds and effort 1 fits: select effort 1
if explicit extended mode and effort 5 fits: select effort 5
if no exact candidate passes: use V2
```

The predictor does not select APNG in Profile 1. A future restricted-timing profile may add an APNG lane after a separate exact-timebase gate.

## Interactive-budget fallout

Effort 3 completed 134/139 fixtures under three seconds. Effort 1 completed 139/139. The current policy is predictive effort selection rather than encoding effort 3 and then retrying effort 1, because duplicate encoding spends the very CPU budget the policy is intended to protect.

The extended 10-second budget remains available for explicit asynchronous effort-5 work. V2 is selected when the preflight predicts that no allowed JXL effort can meet the active budget and payload gates.

## Submitted-limit usability

The following frame counts are the maximum under each submitted-pixel limit, capped at the 512-frame Profile 1 frame limit.

| Limit | 2048×2048 | 1536×1536 | 1024×1024 | 768×768 | 512×512 | 256×256 |
|---:|---:|---:|---:|---:|---:|---:|
| 8 Mi | 2 | 3 | 8 | 14 | 32 | 128 |
| 16 Mi | 4 | 7 | 16 | 28 | 64 | 256 |
| 24 Mi | 6 | 10 | 24 | 42 | 96 | 384 |
| 32 Mi | 8 | 14 | 32 | 56 | 128 | 512 |
| 48 Mi | 12 | 21 | 48 | 85 | 192 | 512 |
| 64 Mi | 16 | 28 | 64 | 113 | 256 | 512 |
| 96 Mi | 24 | 42 | 96 | 170 | 384 | 512 |
| 128 Mi | 32 | 56 | 128 | 227 | 512 | 512 |

The 48, 64, 96, and 128 Mi-pixel rows are usability references and local/future-profile planning values. Profile 1’s wire row is 32 Mi-pixels.

## APNG implementation and exactness

APNG used the same canonical complete RGBA8 display-frame sequence as JPEG XL and WebP. The benchmark covered full-canvas and optimized cropped-rectangle lanes, zlib levels 1/6/9, disposal/blend playback, duplicate-state merging, hidden transparent RGB, irregular durations, finite/infinite loops, and alpha edge values.

The synthetic APNG corpus passed 162/162 eligibility rows. That does not override the real-corpus timing result: 33334 µs cannot always be represented exactly by APNG’s 16-bit delay fields.

## APNG real-corpus result

The optimized default APNG lane was timeline exact and hidden-transparent-RGB exact on 129/139 real fixtures. Its payload median was 0.71 MiB, P95 2.83 MiB, maximum 24.52 MiB, and median encode time 111.2 ms. APNG is smaller and faster on many fixtures, but correctness is a hard gate; it is not universally exact under the Profile 1 timing contract.

APNG remains a future restricted-timing profile candidate. It is not selected as a Profile 1 fallback because an inexact duration cannot be silently rounded.

## APNG memory and payload-cap result

The existing APNG memory sweep covers 8, 16, 32, 48, 64, and 128 Mi submitted pixels for full-canvas and optimized construction. The optimized lane remains bounded by the materialized input/frame history measured by that benchmark, rather than showing JPEG XL's retained whole-animation encoder state. The near-cap lane applies the same 64 MiB hard output cap and removes incomplete output on abort. These results are policy evidence only; they do not reopen APNG for Profile 1 because the exact timing gate is mandatory.

Evidence: `results/raw/apng-memory-sweep.jsonl`, `results/raw/apng-near-cap.jsonl`, and `results/raw/apng-cancellation-stress.jsonl`.

## V2/JXL/WebP/APNG comparison

| Codec | Profile 1 correctness | Payload | Encode/decode behavior | Decision |
|---|---|---|---|---|
| JPEG XL | 139/139 exact on corrected container rerun | Strong; median 1.27 MiB | Worker-isolated, predictable, independently accepted | Profile 1 primary |
| APNG | 129/139 real exact; 162/162 synthetic | Often smallest | Fast and modest memory, but timing-limited | Future restricted profile |
| WebP | 128/139 required exact | Smaller than JXL on all 139 | Fails timeline/profile equivalence | Outside Profile 1 |
| V2 | Existing legacy exact path where supported | Baseline | Lowest migration risk | Required fallback |

Transfer break-even results remain rate-dependent: JXL’s compression benefit is more valuable on slow links, while effort-3 CPU often loses on fast links. Format selection must therefore use the preflight benefit rule instead of treating smallest payload as universally lowest latency.

## Final codec choice

Profile 1 uses JPEG XL. The fallback order is:

1. JPEG XL when memory, timing, predictor, payload, and active-budget gates pass;
2. V2 when JPEG XL is denied, predicted to miss the budget/cap, or fails cleanly at runtime.

APNG and WebP are not Profile 1 codecs. APNG remains a future exact-only restricted-timing candidate; WebP remains a future profile candidate.

## Final sender admission policy

The wire ceiling is 32 Mi submitted canvas pixels. Local memory-class admission is 8/32/64 Mi-pixels, with effective Profile 1 admission clamped to 8/32/32 Mi-pixels. One encoder receives 50% of the animation working-set budget. Estimated memory above that dedicated budget skips JXL before encoding. All JXL work runs in the worker under the class-specific cap.

The sender must not activate a partial output. On cancellation, timeout, memory-cap termination, worker crash, malformed result, or payload-cap crossing, the worker is removed, temporary input/output is deleted, and V2 fallback is attempted when valid.

## Final effort and thread policy

Effort 3 with four threads is the default. Effort 1 with two threads is latency-first/low-memory. Effort 5 with up to eight threads is extended asynchronous mode. Efforts 7 and 9 are excluded from ordinary interactive import.

## Final cancellation result

The prior stress corpus completed 100/100 JXL cancellation cycles, 100/100 APNG cancellation cycles, and 100/100 mixed selection cycles with no finalized partial payload, stale temporary output, orphan worker, or unbounded retained memory observed. The new conformance runner adds malformed, cap, timing, alpha/color, exact-container, and memory-admission negative gates; 77/77 vectors pass.

## Profile recommendation

```text
Native Profile 1 wire format: FINAL / FROZEN
Implementation validation: GO_WITH_GUARDS
Unconditional production replacement: NO-GO until production decoder/transport validation
```

The native benchmark phase is complete enough to begin the deferred WASM and implementation-validation work, subject to preserving this wire descriptor and its negative gates. The current descriptor is [profile-v1-wire.json](profile-v1-wire.json). The historical `profile-v1-phase1-final-recommendation.json` could not be recovered; the repository entry records that absence rather than reconstructing its results. The final conformance result is 77/77, comprising 17 positive and 60 negative vectors; the frozen sender smoke is 139/139.

## Remaining deferred work

Not implemented or tested in this benchmark phase:

- production WASM decoding;
- network transport and throughput validation;
- P2P/relay behavior;
- Quest/Android behavior;
- eviction and restoration;
- network epoch synchronization;
- production packaging.

These are implementation-validation and product-integration tasks, not reasons to change the measured native wire decision.
