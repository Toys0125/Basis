# JPEG XL V3 Profile Benchmark — Phase 1 Final Decision

Generated 2026-08-11 from the authoritative native benchmark datasets. This remains benchmark-only; WASM, transport, platform, and production packaging work is deferred.

## Executive decision

JPEG XL remains the preferred Profile 1 transport. The native wire result is `FINAL / FROZEN`, with `GO_WITH_GUARDS` implementation validation and `NO-GO` for unconditional production replacement. JPEG XL is the only lane that is exact on all 139 real fixtures. APNG is a useful exact candidate for the 129/139 real fixtures whose durations are representable in APNG's 16-bit timebase, but it is not a universal Profile 1 replacement. WebP remains outside Profile 1.

## Starting evidence

The authoritative corrected-container JXL corpus is `results/raw/profile1-jxl-v1-real-corpus.jsonl`. It reports 139/139 strict exact, median payload 1,274,195 bytes, P95 7,116,484 bytes, maximum 46,875,776 bytes, median encode 303.0 ms, and P95 encode 1,443.1 ms. Its median JXL/V2 ratio remains approximately 0.904. The older duplicate summaries are not used for final numeric claims.

The existing correctness baseline remains 139/139 encoded, decoded, strict RGBA exact, frame-count exact, timing exact, and loop exact. The five V2 issues remain baseline failures (`v2BaselineFailures`), not V3 fallback events.

## Artifact corrections

The recommendation removes the ambiguous `maximumSubmittedCanvasPixels` field. Wire and sender limits are separate: `profileMaximumSubmittedCanvasPixels` is 32 Mi-pixels, `defaultSenderMaximumSubmittedCanvasPixels` is 32 Mi-pixels, and local sender limits are 8 Mi, 32 Mi, and 64 Mi by memory class before the wire clamp. Dedicated encoder budget, local admission limit, safe in-process threshold, and worker cap are separate fields. `num_loops = 0` means infinite playback and positive values mean total playthroughs. RGBA is three RGB color channels plus one 8-bit unassociated alpha extra channel, with no non-alpha extras. Patch metrics remain `v2CompatibilityInflation`, `fullCanvasReduction`, and `rectangleInefficiency`; V2 terminology remains `v2BaselineFailures`, `profile1FallbackToV2Count`, `v2FallbackSucceeded`, and `v2FallbackFailed`.

## Worker overhead and isolation

The 66-case small/median/large/synthetic overhead run is in `results/raw/jxl-worker-overhead.jsonl`. The empty-process startup baseline was 0.64 ms median and 1.11 ms P95. The measured worker-overhead P95 was 5.58 ms; this is negligible beside medium/large encode times. The parent survived and cleanup completed for all 66 cases. The selected policy is worker isolation for every native JXL encode, with an explicit memory cap. The worker decision tree is: over local encoder budget → do not start JXL and evaluate APNG/V2; within budget but above the safe in-process threshold → worker; within both → worker remains the selected uniform policy.

The current libjxl 0.12 sender run contains 62 cases across effort 1/3/5, buffering -1/0/1/2, output modes 0/1/2, submitted-pixel bands 8/16/32/64 Mi, and threads 2/4/8. All 59 completed encodes were strict exact. A 64-Mi identical mode-1 case measured 316.8 MiB peak RSS delta at effort 3/4 threads; a 2048² 32-Mi reference measured 573.5 MiB. A 256-MiB cap terminated that 64-Mi case, while 768 MiB and 1.5 GiB completed. The dedicated-cap edge audit also records 8-Mi broad/noisy cases under 256/320 MiB caps. Evidence: `results/raw/profile1-libjxl-v012-memory.jsonl`, `results/raw/profile1-libjxl-v012-memory-cap.jsonl`.

The container layout audit has 12 libjxl 0.12 samples. Native modes 0 and 1 emit ordered `jxlp`; mode 2 emits version-1 `jxlp`. Rewrapped single-`jxlc` candidates passed both native and independent decoding and saved 28–376 bytes, but require a second full-payload rewrite. Profile 1 freezes native ordered `jxlp`: signature first, exact 20-byte `ftyp` second (`jxl ` / minor 0 / compatible `jxl `), four-byte big-endian counters, consecutive lower-31-bit indexes starting at zero, final high bit only on the last box, and no box after it. The one-frame sender path canonicalizes `jxlc` to one final-marked `jxlp`. The receiver order is payload bytes/cap, container/profile, frame headers, dimensions/frame/timing, submitted pixels, memory reservation, decode, extraction, patch cap during extraction, then activation.

## Dedicated encoder-budget policy

The selected allocation is 50% of the animation working-set budget, with the remaining 50% reserved for runtime headroom. The tested candidates were 25%, 40%, 50%, 60%, and 75% with one resident animation, multiple resident animations, compositor/decode reservation, and import buffers represented in the derived dataset. The resulting local limits are 8 Mi submitted pixels for the ≤4 GiB class, 32 Mi for the 8 GiB class, and 64 Mi above 8 GiB. These are sender limits only; they do not claim a wire-profile maximum.

## Near-cap payload predictor

The expanded corpus contains 50 measured fixtures across the requested cap bands. The hard 64 MiB encoded-output cap remains authoritative and must abort/delete partial output. Predictor summary:

- sampled-zlib: false accepts 5, false rejects 0 (below 56: 0; 56–60: 0; 60–64: 0)
- content-class-adjusted: false accepts 5, false rejects 9 (below 56: 4; 56–60: 5; 60–64: 0)
- changed-pixel-entropy: false accepts 0, false rejects 22 (below 56: 7; 56–60: 10; 60–64: 5)
- bounded-trial: false accepts 0, false rejects 14 (below 56: 4; 56–60: 5; 60–64: 5)
- hybrid-conservative: false accepts 0, false rejects 26 (below 56: 11; 56–60: 10; 60–64: 5)

The hybrid conservative predictor is advisory and has zero false acceptances above 64 MiB in this corpus, with the reported false-rejection tradeoff near the cap. It must never replace the hard output cap.

## JXL-versus-V2 selection predictor

The predictor dataset evaluates percentage, absolute, combined, net-latency, and profile-rescue rules at 0.5, 1, 2, 3.75, and 10 MiB/s. The implementable rule is combined benefit: use JXL when predicted saving is at least 10% and 512 KiB, or when V2 exceeds the cap and JXL is predicted to fit. Otherwise use V2, except for a separately validated APNG rescue. The complete per-fixture results are in `results/raw/jxl-v2-selection-predictor.jsonl` and its CSV summary.

## Interactive-budget fallout

Effort 3 completed within the 3-second interactive budget for 134/139 fixtures (96.4%). Effort 1 completed within budget for 139/139. Five effort-3 imports exceeded the budget. Predictive effort selection is preferred; retrying after an over-budget effort-3 encode duplicates work and is not worthwhile as a default. Extended mode uses a 10-second budget and effort 5.

## Submitted-limit usability

Frame counts below are capped at the global 512-frame limit:

| Limit | 2048² | 1536² | 1024² | 768² | 512² | 256² |
|---:|---:|---:|---:|---:|---:|---:|
| 8 Mi | 2 | 3 | 8 | 14 | 32 | 128 |
| 16 Mi | 4 | 7 | 16 | 28 | 64 | 256 |
| 24 Mi | 6 | 10 | 24 | 42 | 96 | 384 |
| 32 Mi | 8 | 14 | 32 | 56 | 128 | 512 |
| 48 Mi | 12 | 21 | 48 | 85 | 192 | 512 |
| 64 Mi | 16 | 28 | 64 | 113 | 256 | 512 |
| 96 Mi | 24 | 42 | 96 | 170 | 384 | 512 |
| 128 Mi | 32 | 56 | 128 | 227 | 512 | 512 |

## APNG implementation and exactness

APNG was encoded from the same coalesced complete RGBA8 display-frame streams as JXL/WebP. The benchmark includes a full-canvas baseline, cropped source-rectangle optimized/Basis-derived construction, zlib levels 1/6/9, APNG disposal/blend decode through Pillow, duration/loop/timeline oracle checks, hidden transparent RGB, and synthetic edge cases. All 162/162 synthetic rows passed the Profile 1 eligibility gate.

## APNG real-corpus result

The full-canvas and optimized default lanes each ran all 139 real fixtures. Optimized default was timeline-exact on 129/139, hidden-transparent-RGB exact on 129/139, and Profile 1 eligible on 129/139. The 10 failures are timing-inexact because durations such as 33,333 µs cannot always be represented exactly by APNG's 16-bit numerator/denominator timebase. No silent rounding is accepted. The optimized default payload median is 0.71 MiB, P95 2.83 MiB, maximum 24.52 MiB, median encode 111.2 ms. APNG is therefore exact-but-timebase-limited, not universally Profile 1 eligible.

## APNG memory and payload-cap result

The APNG memory sweep covers 8/16/32/48/64/128 Mi submitted pixels and full/optimized construction. The optimized lane's measured encoder delta is roughly bounded by the current source/frame materialization plus the current working frame; the benchmark reports source-materialization and encoder deltas separately. Because the benchmark input is a complete RGBA stream, those input bytes must not be mistaken for a production streaming-memory guarantee. APNG near-cap cases use the same 64 MiB hard cap and delete incomplete output on abort; 64 cap abort rows were recorded. Full details are in `results/raw/apng-memory-sweep.jsonl` and `results/raw/apng-near-cap.jsonl`.

## V2/JXL/WebP/APNG comparison

| Codec | Profile-eligible fixtures | Median payload | P95 payload | Maximum payload | Median encode ms |
|---|---:|---:|---:|---:|---:|
| v2 | 139 | 1.13 MiB | 6.83 MiB | 59.76 MiB | 25.8993 |
| jxl | 139 | 1.21 MiB | 6.79 MiB | 44.7 MiB | 310.642058000667 |
| webp | 128 | 0.34 MiB | 1.79 MiB | 28.44 MiB | -1 |
| apng | 129 | 0.71 MiB | 2.83 MiB | 24.52 MiB | 111.24561999167781 |

JXL is the correctness hard gate. WebP's method-3 real-corpus result is 128/139 timeline exact and 135/139 hidden-RGB exact, but only 128/139 meets all required fields. APNG is smaller/faster than JXL on many real fixtures but fails the universal timebase gate. The full pairwise payload and transfer-rate break-even data is in `results/raw/codec-four-way-comparison.jsonl`.

## Final codec choice

Use JPEG XL as the Profile 1 primary. APNG is not selectable in Profile 1; retain it only as a future restricted-timing candidate. Use V2 as the required final fallback. Do not select WebP for Profile 1.

## Final sender admission policy

The wire submitted-pixel maximum is 32 Mi submitted canvas pixels. Local admission is 8 Mi / 32 Mi / 64 Mi submitted pixels for ≤4 GiB / 8 GiB / >8 GiB classes, clamped to 32 Mi for Profile 1. Dedicated encoder budgets are 256 MiB / 768 MiB / 1.5 GiB; the other half remains runtime headroom. Any estimated encoder memory above the dedicated budget skips JXL before encoding. All JXL work uses the worker because the safe in-process threshold is zero.

## Final effort and thread policy

Default JXL effort is 3 with 4 threads. Effort 1 with 2 threads is latency-first/low-memory. Effort 5 with 4 threads is extended high-compression. Eight threads is the normal maximum; effort 7/9 remain excluded from ordinary interactive imports.

## Final cancellation result

The existing 100-cycle JXL stress run completed cleanly for 100/100 cycles. APNG cancellation completed cleanly for 100/100 cycles, and the mixed JXL/APNG selection stress completed cleanly for 100/100 cycles, with no output or temporary-directory residue. The native codec lanes themselves have no observed orphan/partial-output leak in these runs.

## Wire freeze verification

The final wire-format gate passed all required checks:

- exact `ftyp` bytes and compatible-brand policy: PASS;
- exact `jxlp` byte order, sequence mask, final marker, and ordering: PASS;
- signature → `ftyp` → `jxlp[0..N final]` → EOF with `jxlc`, metadata, padding, and trailing data rejected: PASS;
- loop semantics 0/1/2/3: PASS;
- alpha and color contracts: PASS;
- expanded conformance: PASS, 77/77 (17 positive, 60 negative);
- libjxl 0.12 container matrix, including one-frame normalization: PASS, 9/9;
- current frozen encoder smoke: PASS, 139/139, independent `djxl` accepted 139/139.

**PROFILE 1 WIRE FORMAT = FROZEN.** Native benchmark status is ready for deferred implementation validation. Implementation status remains `GO_WITH_GUARDS`; unconditional production replacement remains `NO-GO`.

## Profile recommendation

Native wire format: `FINAL / FROZEN`. Implementation validation: `GO_WITH_GUARDS`. Unconditional production replacement remains `NO-GO` until decoder integration, worker integration, sender fallback behavior, and the deferred platform/transport work are completed. The machine-readable recommendations are `profile-v1-wire.json` and `profile-v1-phase1-final-recommendation.json`. The final conformance suite passes 77/77 vectors (17 positive, 60 negative), and the frozen sender smoke passes 139/139.

## Remaining deferred work

WASM decoding, network transport, P2P/relay, Quest/Android, eviction/restoration, epoch synchronization, and production packaging were intentionally not implemented or tested in this benchmark phase.
