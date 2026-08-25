# Basis Animated Image Profile 1 Wire Specification

Status: **FIRST RECONCILIATION PUBLISHED — container/pixel/timing contract frozen; hostile-codestream resource contract pending second reconciliation**

Implementation status: **GO_WITH_GUARDS**

Unconditional production replacement: **NO-GO**

This document is the authoritative Profile 1 wire contract after the first pre-release reconciliation described by `JPEG_XL_PROFILE_V1_IMPLEMENTATION_VALIDATION_PLAN.md`. The original benchmark-approved JPEG XL container, pixel, timing, loop, dimension, submitted-pixel, and payload decisions remain frozen. This reconciliation corrects the receiver trust ordering and records specification precision that was already settled by the implementation-validation plan.

Profile 1 has not been enabled on a non-test production network. Therefore this reconciliation completes the undeployed `profileVersion = 1` contract as pre-release errata/clarification rather than creating Profile 2. **No Profile 1 payload may be published to a non-test network before the second reconciliation is published.** If that condition is violated, publication work must stop and the profile-version compatibility decision must be reopened.

## 1. Profile identity and version namespaces

Profile 1 is JPEG XL.

```text
profileVersion = 1
codec = JPEG XL
```

The outer Profile 1 descriptor version is independent of the legacy Basis V2 animation payload version. In particular, `profileVersion = 1` and `BasisBurstAnimationCodec.Version = 2` are separate namespaces and must never be numerically compared.

The selected payload form is the minimal JPEG XL container with ordered `jxlp` split-codestream boxes. A one-frame libjxl result emitted as `jxlc` must be canonicalized into one final-marked `jxlp` before Profile 1 publication. `jxlc` is not valid on the Profile 1 wire.

## 2. Exact container form

The payload begins with this exact JPEG XL container signature:

```text
00 00 00 0c 4a 58 4c 20 0d 0a 87 0a
```

The second box is exactly this 20-byte `ftyp` box:

```text
00 00 00 14 66 74 79 70 6a 78 6c 20
00 00 00 00 6a 78 6c 20
```

Equivalent fields:

```text
box size = 20
box type = "ftyp"
major brand = "jxl "
minor version = 0
compatible brands = ["jxl "]
```

The remaining container consists only of one or more ordered `jxlp` boxes. Exif, XML, JPEG reconstruction, ICC sidecar, application metadata, padding/unknown boxes, raw codestream form, `jxlc`, and trailing bytes are forbidden.

Each `jxlp` content begins with a four-byte big-endian counter:

```text
lower 31 bits = sequence number
high bit = final marker
```

Requirements:

```text
first sequence = 0
sequences are consecutive
sequences are unique
final marker appears exactly on the final jxlp
no box or data follows the final jxlp
```

Concatenating only the codestream portions after the four-byte counters produces the bounded codestream byte span handed to the sandboxed semantic decoder. Stage A proves the container byte spans are complete; it does not prove that the JPEG XL codestream itself is semantically complete.

## 3. Pixel, color, alpha, and orientation contract

Every logical display frame is:

```text
RGBA8
channel order = RGBA
alpha = straight / unassociated
row order = top-to-bottom
orientation = identity
```

Color is exactly RGB / D65 / sRGB primaries / sRGB transfer function with 8-bit integer samples and exponent bits 0.

There are exactly three RGB color channels and exactly one extra channel. The extra channel is alpha at index 0 with:

```text
type = JXL_CHANNEL_ALPHA
bits per sample = 8
exponent bits per sample = 0
dim shift = 0
alpha premultiplied = false
```

Non-alpha extra channels, HDR, floating-point samples, non-sRGB color, nonidentity orientation, and premultiplied alpha are outside Profile 1.

Hidden RGB beneath alpha zero is normative data and must survive the Profile 1 round trip byte-for-byte.

## 4. Timing and loop contract

The canonical Profile 1 timebase representation is exactly:

```text
numerator = 1,000,000
denominator = 1
```

Mathematically equivalent representations such as `2,000,000 / 2` are not canonical Profile 1.

Frame durations are integer microseconds.

```text
minimum frame duration = 33,334 us
maximum base timeline = 300,000,000 us
maximum logical frames = 512
```

Loop semantics:

```text
num_loops = 0 -> infinite playback
num_loops > 0 -> total number of playthroughs
```

The base timeline is exactly one logical playthrough. `num_loops` does not multiply or otherwise participate in the base-timeline limit.

No receiver-side timing clamp or rational normalization is permitted. A payload using an out-of-profile duration or alternate timebase is rejected.

## 5. Shared and wire limits

| Limit | Profile 1 value/status |
|---|---:|
| Width | 2,048 |
| Height | 2,048 |
| Canvas pixels | 4,194,304 |
| Logical frames | 512 |
| Submitted canvas pixels | 33,554,432 |
| Encoded payload bytes | 67,108,864 |
| Minimum frame duration | 33,334 us |
| Maximum base timeline | 300,000,000 us |
| Extracted patch pixels | **UNRESOLVED**; previously published value 67,108,864 retained only pending second reconciliation |
| Coded-frame ceiling | **PENDING SECOND RECONCILIATION** |
| Structural `decodeWork` formula/weights/ceiling | **PENDING SECOND RECONCILIATION** |

The published `67,108,864` extracted-patch value is not silently removed by this reconciliation. Its Profile 1 wire status is explicitly unresolved until the canonical Basis patch algorithm establishes the reachable inflation bound. This does not alter the shared legacy/runtime `BasisImagePickupSettings.MaxAnimationDecodedFramePixels` constant.

The coded-frame ceiling and structural `decodeWork` budget are hostile-input interoperability/resource limits. They must be finalized from production-WASM evidence and published together with the final extracted-patch classification in the single second reconciliation before Gates A2, B, C, or D may close.

## 6. Normative submitted-canvas accounting

Profile 1 defines:

```text
submittedCanvasPixels =
    checked_u64(canvasWidth)
    * checked_u64(canvasHeight)
    * checked_u64(logicalFrameCount)
```

`logicalFrameCount` is the number of complete logical display frames presented by one playthrough. This calculation is independent of JPEG XL cropping, references, blending, `jxlp` segmentation, receiver patch rectangles, patch extraction, and compressed payload size.

The result must satisfy:

```text
submittedCanvasPixels <= 33,554,432
```

Arithmetic overflow fails closed and must never wrap into an acceptable value.

Examples:

```text
2048 * 2048 * 8 = 33,554,432 -> accepted boundary
2048 * 2048 * 9 = 37,748,736 -> SharedLimitExceeded
```

This definition is classified by the first reconciliation as a **clarification/erratum of the intended undeployed Profile 1 contract**, not a profile-version change.

## 7. Normative base-timeline accounting

Profile 1 defines:

```text
baseTimelineDurationMicroseconds =
    checked_u64(sum(frameDurationMicroseconds[i]))
```

for exactly one logical playthrough.

The result must satisfy:

```text
baseTimelineDurationMicroseconds <= 300,000,000
```

`num_loops` does not participate. Arithmetic overflow fails closed.

A 300,000,000 us base timeline remains valid for both infinite playback and any valid positive total-playthrough count. A 300,000,001 us base timeline is `SharedLimitExceeded`.

This definition and the exact `1,000,000 / 1` representation are classified by the first reconciliation as **clarifications/errata of the intended undeployed Profile 1 contract**.

## 8. Broad codestream feature policy and structural resource accounting

Profile 1 intentionally keeps the broad pinned-libjxl feature policy. A feature being decodable by pinned libjxl does not by itself make a payload Profile 1; every semantic and resource rule in this specification still applies.

Normative `decodeWork` must be an implementation-independent pure function of codestream-declared or decoder-derived structure that is observable through the pinned libjxl public decoder API in both native-oracle and WASM implementations. Candidate inputs include:

```text
coded-frame count
coded-frame area / crops
reference relationships
blend operations
reference-chain structure/depth
```

`decodeWork` must not depend on wall-clock time, thread count, SIMD, scheduling, private libjxl internals, or implementation-specific progressive-pass state. Progressive/multi-pass amplification that the public API cannot expose is bounded by the WASM sandbox memory/resource/timeout policy instead of an invented parser or private metric.

The final formula, weights, coded-frame ceiling, and `decodeWork` ceiling remain intentionally unpublished until the production WASM receiver benchmark has calibrated them. The broad feature policy must not be narrowed merely to simplify accounting.

A provisional desktop production-WASM candidate has now been measured and is preserved at `results/summary/profile1-wasm-resource-candidate-desktop-20260825.md`. It defines coded frames as public regular `JXL_DEC_FRAME` layers observed with coalescing disabled and exercises plain coded-frame amplification through 8,192 layers plus reference chains through depth 4,096. Candidate `decodeWork` version 1 tracks the large desktop stress fixtures closely enough for continued calibration, including the exact 33,554,432 submitted-pixel boundary and the combined maximum-pixel/reference workload. This evidence is **measurement-only**: no coded-frame or `decodeWork` ceiling is normative or enforced from it. Quest/Android on-device production-WASM measurements remain required before final values can enter the second reconciliation.

## 9. Local sender policy

Sender admission is implementation policy below the wire ceiling; it does not redefine receiver compatibility or decoded-residency budgets.

Initial JPEG XL publication support:

```text
Windows/Linux desktop -> Profile 1 publication supported after all validation gates
Quest/Android/mobile -> receive Profile 1 through WASM; locally authored animation publishes through V2; no JXL encode
```

Desktop sender memory classes:

| Desktop class | Profile 1 submitted-pixel preflight ceiling | Dedicated encoder budget | Worker cap |
|---|---:|---:|---:|
| unknown/unreported or <=4 GiB | 8 Mi-pixels | 256 MiB | 256 MiB |
| >4 GiB and <=8 GiB | 32 Mi-pixels | 768 MiB | 768 MiB |
| >8 GiB | 32 Mi-pixels | 1.5 GiB | 1.5 GiB |

Unknown or unreported RAM fails into the smallest class. The historical 64 Mi-pixel high-memory local admission value is not an active Profile 1 publication ceiling and is removed from current policy because Profile 1 itself ends at 32 Mi-pixels.

All enabled native JPEG XL encoding occurs outside the Unity parent process in an isolated worker. The measured encoder estimate remains an admission input:

```text
1.20 * (76,546,048
        + submittedPixels * 16.72
        + (threads - 1) * 1,247,805)
```

Worker isolation is containment, not an admission bypass. A workload predicted over its dedicated budget is not started as JPEG XL. Quest/Android bypass this encoder classifier entirely during the initial implementation.

The active effort policy remains effort 1 for latency-first/low-memory use, effort 3 default, effort 5 explicit extended/asynchronous mode, normal maximum 8 threads, and efforts 7/9 outside ordinary interactive use.

These sender/platform corrections are classified as **implementation-policy clarification**, not wire-format changes.

## 10. Receiver trust model

Remote/network JPEG XL codestream semantics are **WASM/sandbox only**.

Native libjxl decode is trusted-local/editor/oracle only. It must never process remote Profile 1 codestream semantics in production.

Receiver validation is divided into Stage A outside the sandbox and Stage B inside the production sandbox.

### 10.1 Stage A — allocation-light container preflight

Stage A may validate only:

```text
profile version
declared/reassembled payload length
64 MiB payload cap
exact JPEG XL container signature
exact ftyp bytes
ordered jxlp box byte spans
big-endian counters
sequence start/consecutiveness/uniqueness
final marker
no box/data after final jxlp
forbidden/non-jxlp boxes
complete bounded concatenated codestream byte span
no trailing bytes
```

Stage A must not:

```text
parse JPEG XL frame headers
interpret codestream dimensions or timing
interpret frame/reference/blend semantics
invoke native libjxl against remote bytes
decide semantic codestream completeness/truncation
```

A box whose declared byte span runs beyond the payload is a Stage A `Malformed` failure. A container-complete codestream that is semantically truncated is a Stage B `Malformed` failure.

### 10.2 Stage B — sandboxed semantic/resource preflight

The first Stage B part runs inside the bounded pinned-libjxl WASM sandbox and validates:

```text
semantic codestream completeness
width / height / canvas pixels
logical frame count
exact 1,000,000 / 1 timebase
frame durations
base timeline
loop semantics
pixel/color/alpha/orientation contract
submittedCanvasPixels
coded-frame accounting
structural decodeWork
all other validated bounded output/resource-envelope values needed by the host
```

Only validated bounded resource-envelope values cross back to the host.

### 10.3 Host memory admission

After sandbox semantic/resource preflight and **before** full pixel-output decode or host allocations that scale with decoded output, the host evaluates current receiver residency and aggregate pressure.

Failure at this boundary is:

```text
MemoryAdmissionDenied
```

Host admission must not parse the JPEG XL codestream outside the sandbox. The sandbox does not decide global host aggregate residency policy.

### 10.4 Full Stage B pixel decode

Only after host memory admission succeeds may the bounded WASM sandbox perform full pixel decode. The complete logical RGBA8 canvases then cross the canonical Basis patch-conversion boundary. No animation becomes visible or replaces prior active state until all required validation and conversion succeeds atomically.

The previous specification order that parsed remote JPEG XL frame headers outside the sandbox is superseded by this section. This correction is classified as a **security erratum of the undeployed Profile 1 receiver contract**.

## 11. Stable rejection classifications

Wire/profile validation categories:

```text
Malformed
UnsupportedProfile
SharedLimitExceeded
PayloadLimitExceeded
```

Runtime/resource categories:

```text
PatchLimitExceeded
Timeout
Cancelled
MemoryAdmissionDenied
```

`PatchLimitExceeded` is runtime patch-conversion/resource accounting unless the second reconciliation proves a portable wire-reachable condition requiring otherwise.

External behavior must not depend on unstable native/WASM/libjxl diagnostic strings. Detailed internal diagnostics may accompany the stable category.

Adding `Cancelled` and separating runtime/resource outcomes from portable wire/profile outcomes is classified as a **rejection-layering clarification**.

## 12. Direct `.jxl` reuse

Direct byte reuse is allowed only for a file already conforming exactly to Profile 1.

Required path:

```text
direct .jxl
    -> Stage A
    -> Stage B semantic/resource preflight
    -> normal isolated host receiver admission
    -> full sandbox decode/activation validation
    -> publish
```

Section 9 encoder-memory admission does not apply because no JPEG XL encoding occurs. Direct reuse receives no special or larger receive envelope. The local publishing client must itself be able to admit and activate the payload through the normal isolated single-animation receiver path.

A generic libjxl-valid `.jxl` file is insufficient. Out-of-profile files must be rejected for direct reuse rather than silently normalized.

## 13. Receiver residency and legacy V2 separation

A conforming Profile 1 payload publishable by a supported desktop sender must remain viewable on every supported receiver, including the minimum supported Quest/Android class, when tested as an isolated single animation.

The current shared low-memory/mobile decoded-residency budget is approximately 16 Mi-pixels. That value is provisional for Profile 1 compatibility until production WASM decode plus canonical Basis patch conversion measures the maximum reachable resident requirement.

Implementation order is:

1. measure the production path;
2. keep the existing shared budget if the reachable requirement is <=16 Mi-pixels;
3. otherwise test the minimum justified shared increase with V2 Quest/mobile regression coverage;
4. split Profile-1-specific single-animation/aggregate policy only if the safe shared architecture cannot satisfy the isolated receive guarantee;
5. use streaming/staging/representation changes instead of silently shrinking the Quest Profile 1 wire envelope if a safe budget adjustment is impossible.

The Profile 1 extracted-patch decision does not by itself alter `BasisImagePickupSettings.MaxAnimationDecodedFramePixels`, which remains shared by V2/GIF/runtime code.

## 14. Canonical patch conversion

The runtime conversion boundary is the sequence of complete logical RGBA8 canvases, not JPEG XL coded-frame geometry and not V2 patch geometry.

The canonical Basis implementation uses one full-canvas `Source` / `None` patch for each validated logical frame. Therefore:

```text
extractedPatchPixels =
    canvasWidth * canvasHeight * logicalFrameCount
    = submittedCanvasPixels

maximum inflation factor = 1.0
```

This algorithm is deterministic, preserves hidden RGB, preserves exact frame durations and loop semantics, and cannot exceed the 32 Mi-pixel submitted-canvas ceiling for a conforming Profile 1 payload.

This evidence makes the extracted-patch classification ready for the second reconciliation, but the historical `67,108,864` published wire value remains formally unresolved until that single second reconciliation also publishes the final coded-frame and `decodeWork` contract.

## 15. Synchronization and restoration semantics

Playback derives from the existing Basis synchronized network epoch. Positive elapsed 100 ns ticks are converted to microseconds by floor division by 10.

Backward synchronized-clock corrections must not move playback backward. Each animation/publication keeps a monotonic target-playback watermark keyed by its publication identity and `PlaybackEpochUtcTicks`.

```text
new publication epoch -> reset watermark
same epoch backward correction -> clamp to prior target watermark
same epoch forward correction -> advance target immediately
eviction/restoration -> retain watermark
```

The watermark tracks epoch-derived target playback time, not the compositor frame that happened to finish rendering. Different peers with different correction histories may temporarily disagree and must converge once corrected synchronized time passes the older watermark.

Restoration is bounded convergence under the global compositor transition/pixel budgets; arbitrary same-Unity-frame reconstruction is not required. Restoration must not restart from frame zero unless the current epoch selects frame zero.

## 16. Native decoder policy

Native libjxl decode is permitted only for trusted local/editor/oracle use. Normal editor validation should exercise the production WASM path by default once that implementation is packaged; a native editor oracle must be behind an explicit editor-only configuration gate.

The production WASM decoder, trusted native oracle, and encoder must use the same pinned libjxl revision. Upgrading libjxl requires rerunning the portable conformance and native/WASM differential suites.

Encoder byte output need not be deterministic. Decoded semantics and structural accounting must be exact.

## 17. Portable conformance ownership

Stage A negative vectors include at least:

```text
bad signature
bad ftyp size/brand/version/compatible brands
missing ftyp
metadata or unknown boxes
jxlc
jxlp start/duplicate/skip/reorder errors
multiple/missing final markers
box after final jxlp
truncated jxlp counter/container span
declared-length mismatch
payload >64 MiB
```

Stage B negative vectors include at least:

```text
container-complete but semantically truncated JPEG XL codestream
zero/oversized dimensions
canvas limit
logical frame count
submitted-pixel limit
minimum duration
base-timeline limit
wrong timebase numerator/denominator
coded-frame ceiling
decodeWork ceiling
pixel/color/alpha/orientation violations
```

The generic fixture name `truncated codestream` is insufficient because container-span truncation and semantic codestream truncation belong to different trust stages.

## 18. Historical benchmark evidence

The completed native benchmark recorded the following historical results:

```text
JPEG XL real corpus: 139/139 exact
native conformance: 77/77
container matrix: 9/9
frozen sender smoke: 139/139
```

The historical `profile-v1-phase1-final-recommendation.json` preservation entry is present in this repository. The raw historical Phase 1 datasets referenced by the reports remain **unavailable/unrecovered**. Those claims are retained as historical evidence only; the implementation must not recreate or invent missing benchmark outputs. Missing historical raw evidence is not a production dependency and does not block new repo-contained implementation/conformance fixtures.

## 19. Reconciliation state and remaining gate

First reconciliation classifications:

```text
Stage A / Stage B trust ordering -> security erratum
Stage B ownership of semantic codestream completeness -> security erratum
submittedCanvasPixels exact definition -> clarification/erratum
baseTimelineDurationMicroseconds exact definition -> clarification/erratum
canonical 1,000,000 / 1 timebase representation -> clarification/erratum
rejection layering including Cancelled -> clarification
Windows/Linux desktop vs Quest/Android sender policy -> implementation-policy clarification
unknown/unreported memory -> fail to smallest class
historical high-memory 64 Mi local Profile 1 admission -> removed as inert policy ceiling
maximumExtractedPatchPixels 67,108,864 -> UNRESOLVED, retained pending second reconciliation
profileVersion -> remains 1 under pre-release errata policy
```

The required single second reconciliation must publish together:

```text
final coded-frame semantics and ceiling
final structural decodeWork formula/weights/ceiling
final extracted-patch classification
```

Until that second reconciliation is published, Gates A2, B, C, and D remain open and Profile 1 must not be published to a non-test network.
