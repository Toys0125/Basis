# JPEG XL Profile 1 — Implementation Validation Plan

Status: **ACTIVE IMPLEMENTATION-VALIDATION PLAN**

Profile 1 container and measured native profile status: **FINAL / FROZEN**

Profile 1 receiver/codestream acceptance contract: **PENDING FORMALIZATION**

Implementation validation status: **GO_WITH_GUARDS**

Unconditional production replacement status: **NO-GO**

# 1. Purpose

JPEG XL / Basis Animated Image Profile 1 has completed native codec and container-format benchmarking.

The selected JPEG XL container form, pixel contract, timing model, and measured native benchmark conclusions are frozen. The complete hostile-receiver acceptance contract is not yet frozen because the codestream resource limits and canonical Basis patch-extraction contract still require formalization.

The next phase is implementation validation across sender canonicalization, strict parsing, coded-frame/decode-work accounting, worker isolation, WASM decoding, Unity runtime integration, deterministic patch conversion, memory lifecycle, synchronization, transport, P2P, relay, and supported platforms.

This phase MUST NOT reopen JPEG XL versus APNG versus WebP selection unless implementation validation proves a genuine correctness defect in the selected Profile 1 design.

Implementation-policy changes do not, by themselves, justify a Profile 1 compatibility change.

---

# 2. Frozen container contract and pending codestream contract

## 2.1 Profile and codec

```
profileVersion:
    1

codec:
    JPEG XL
```

## 2.2 Container

Profile 1 carries a minimal JPEG XL container using ordered `jxlp` codestream boxes.

Container signature:

```
00 00 00 0c 4a 58 4c 20 0d 0a 87 0a
```

Exact `ftyp`:

```
00 00 00 14 66 74 79 70 6a 78 6c 20
00 00 00 00 6a 78 6c 20
```

Equivalent fields:

```
major brand:
    "jxl "

minor version:
    0

compatible brands:
    ["jxl "]
```

Profile 1 publication MUST use `jxlp`.

A one-frame libjxl output using `jxlc` MUST be canonicalized into one final-marked `jxlp` before publication.

`jxlc` is not valid on the Profile 1 wire.

## 2.3 jxlp ordering

Each `jxlp` begins with a four-byte big-endian counter.

```
lower 31 bits:
    sequence number

high bit:
    final marker
```

Requirements:

```
first sequence:
    0

sequence indexes:
    consecutive

sequence indexes:
    unique

final marker:
    present exactly on final jxlp

boxes after final jxlp:
    forbidden
```

Concatenating the payload portions of all ordered `jxlp` boxes MUST produce one complete JPEG XL codestream.

Profile 1 does not permit arbitrary metadata or auxiliary JPEG XL container boxes.

## 2.4 Codestream acceptance policy

Profile 1 intentionally uses a broad JPEG XL codestream compatibility policy rather than a narrow feature whitelist. A receiver MAY accept any JPEG XL codestream feature supported by the pinned libjxl implementation only when every Profile 1 semantic and resource constraint is satisfied.

The broad feature policy does not mean "libjxl accepts it, therefore Profile 1 accepts it." The strict probe and decoder MUST additionally enforce the pixel, color, alpha, orientation, timing, dimensions, logical-frame, coded-frame, decode-work, payload, and memory contracts.

The following are normative:

```
timebase numerator:
    1,000,000

timebase denominator:
    1

coded-frame ceiling:
    MUST be defined before Gate A2 / Gate C

decode-work budget:
    MUST be defined before Gate A2 / Gate C
```

Mathematically equivalent alternate timebases such as `2,000,000 / 2` are not canonical Profile 1.

The coded-frame ceiling and decode-work metric are security/interoperability limits for hostile remote input. `Timeout` is a secondary containment mechanism and MUST NOT substitute for these limits.

The descriptor MUST explicitly document how public-decoder-observable coded-frame structure contributes to the normative decode-work budget, including coded-frame count/area, frame crops, reference relationships, and blend/reference-chain structure. The normative decode-work value MUST be an implementation-independent pure function of codestream-declared or codestream-derived structure that is observable through the pinned libjxl public decoder API in both native and WASM implementations. It MUST NOT depend on wall-clock time, decoder thread count, scheduling, SIMD strategy, implementation-specific early exits, or private/internal libjxl progressive-pass state. Progressive/multi-pass amplification that is not exposed through the pinned public API remains bounded by the WASM sandbox memory/resource/timeout policy rather than being represented by an invented implementation-specific decode-work value. Initial candidate definitions MAY be prepared before WASM exists, but the final weights and ceiling MUST NOT be frozen from native-only evidence. The dedicated WASM receiver benchmark described in Phase 5 calibrates the acceptable ceiling for this structural metric; it does not redefine the metric per implementation. Until that benchmark is complete, the complete receiver acceptance contract is not frozen.

`profileVersion = 1` identifies the JPEG XL Profile 1 transport/profile contract. It is unrelated to `BasisBurstAnimationCodec.Version = 2`, which identifies the legacy V2 animation codec payload. Version values from these namespaces MUST NOT be numerically compared.

Profile 1 has not yet been enabled as a production network format. The first and second Section 2.5 reconciliations are therefore treated as pre-release errata/clarifications that complete the undeployed `profileVersion = 1` contract rather than creating a new profile version, provided no Profile 1 payload is published to a non-test network before the second reconciliation is published. If that precondition is violated, the compatibility/version decision MUST be reopened under Section 28 before further publication.

## 2.5 Relationship to the frozen wire specification

`JPEG_XL_PROFILE_V1_WIRE_SPEC.md` is marked FINAL / FROZEN, but this plan has since identified areas where the frozen specification is stale or internally inconsistent with the intended implementation security model. Implementation MUST NOT proceed through any area where this plan and the frozen wire specification disagree until a published erratum/amendment resolves that disagreement. After publication, the frozen wire specification together with its erratum/amendment is authoritative for Profile 1 wire semantics; this plan remains authoritative for implementation sequencing, validation work, and gate ordering.

Known divergences:

```
receiver lifecycle ordering:
    wire spec section 9 parses JPEG XL frame headers and validates
    timing, dimensions, submitted pixels, and codestream completeness before isolated decode
    this plan requires codestream-semantic work, including determining whether the
    JPEG XL codestream is complete/non-truncated, inside Stage B in the sandbox
    Stage A may validate only the bounded container byte spans and box arithmetic
    Stage B semantic/resource preflight occurs inside WASM, then validated resource
    envelope values return to the host for receiver memory admission before full pixel decode
    the wire spec's pre-decode receiver-memory admission intent is retained, but it MUST NOT
    require unsandboxed codestream parsing
    security-relevant; erratum required

extracted patch pixels:
    wire spec lists 67,108,864 as a frozen wire limit
    this plan has not yet established that value as a demonstrated Profile 1
    wire-semantic requirement
    the first reconciliation MUST record that the published status is unresolved
    and defer final classification until the canonical patch algorithm proves its
    inflation bound
    the final classification is then resolved in the second reconciliation under
    Section 28 rather than silently changed

rejection categories:
    wire spec omits `Cancelled` and lists `PatchLimitExceeded` as a
    probe/conformance classification
    this plan adds `Cancelled` and treats `PatchLimitExceeded` as runtime

sender classification and high-memory admission:
    wire spec keys memory class on RAM only and retains the 64 Mi
    high-memory local admission value
    this plan adds platform classification and removes the inert 64 Mi ceiling
    these are permitted policy changes under Section 28, but the
    specification text is stale and MUST be corrected
```

An implementer following the frozen specification as written today would build an unsandboxed hostile header parser and enforce a patch ceiling this plan directs removing. The erratum is a prerequisite, not documentation cleanup.

### Plan-only normative wire additions requiring specification migration

Some normative Profile 1 semantics are defined in this plan more precisely than in the frozen wire specification. These are not ordinary implementation-policy details and MUST be migrated into the authoritative wire contract before implementations rely on them as interoperability requirements.

Settled additions that are ready for the first specification reconciliation include:

```
submittedCanvasPixels definition:
    checked_u64(canvasWidth) * checked_u64(canvasHeight) * checked_u64(logicalFrameCount)
    computed from complete logical display canvases
    independent of JXL cropping, reference behavior, jxlp segmentation,
    receiver patch rectangles, patch extraction, and compressed payload size

baseTimelineDurationMicroseconds definition:
    checked_u64(sum(frameDurationMicroseconds[i])) for exactly one logical playthrough
    num_loops MUST NOT multiply or otherwise participate in this value

canonical timebase form:
    numerator = 1,000,000
    denominator = 1
    mathematically equivalent alternate rational forms are not canonical
```

The frozen wire specification already states a 1,000,000-ticks-per-second timebase, but it does not currently state the canonical numerator/denominator representation or reject equivalent alternate rational forms. The reconciliation MUST explicitly classify that stricter representation rule, and the submitted-canvas/base-timeline definitions, as either clarifications/errata of the intended Profile 1 contract or genuine semantic changes requiring a profile-version decision under Section 28. Do not silently promote plan-only normative text into the frozen wire contract.

The coded-frame ceiling and decode-work metric/ceiling are also normative wire/resource semantics, but they are not ready for the first reconciliation because Phase 5 intentionally leaves them provisional until the production WASM receiver benchmark is complete. The extracted-patch-pixel classification is likewise finalized only after the canonical patch algorithm proves its inflation bound. After those results exist, the authoritative wire specification MUST receive one second reconciliation update containing both the finalized coded-frame/decode-work semantics and the final extracted-patch status before Gate A2, Gate B, Gate C, or Gate D can close against those values. Keeping these items in one second reconciliation is intentional: it publishes one completed hostile-receiver contract rather than introducing separate 2a/2b specification ceremonies. Under the pre-release condition in Section 2.4, this reconciliation is an erratum/clarification completing undeployed Profile 1 rather than a profile-version bump.

A reconciliation is considered published only when the Profile 1 implementation owner lands the version-controlled wire-spec erratum/amendment in `Research/BasisImageSandbox/Profile1/JPEG_XL_PROFILE_V1_WIRE_SPEC.md` and updates the matching `profile-v1-wire.json` descriptor in the same repository change. Discussion, benchmark notes, or plan text alone do not constitute publication.

---

# 3. Frozen pixel contract

Decoded logical display frames MUST be:

```
RGBA8

RGB channels:
    3

alpha extra channels:
    1

alpha channel type:
    JXL_CHANNEL_ALPHA

alpha bits:
    8

alpha exponent bits:
    0

alpha dim shift:
    0

alpha association:
    straight / unassociated

non-alpha extra channels:
    forbidden
```

Color contract:

```
color space:
    RGB

white point:
    D65

primaries:
    sRGB

transfer:
    sRGB

bits:
    8

exponent bits:
    0
```

Orientation:

```
identity
```

Hidden RGB beneath fully transparent pixels MUST survive Profile 1 round-trip byte-for-byte.

Visible-RGBA equivalence alone is insufficient.

---

# 4. Frozen timing and loop contract

```
timebase numerator / denominator:
    1,000,000 / 1 exactly

minimum frame duration:
    33,334 microseconds

maximum base timeline:
    300,000,000 microseconds

maximum logical frames:
    512
```

Loop semantics:

```
num_loops = 0:
    infinite playback

num_loops > 0:
    total playthrough count
```

The base timeline is one playthrough only.

The base timeline limit MUST NOT be multiplied by `num_loops`.

---

# 5. Frozen wire limits

```
maximum width:
    2048

maximum height:
    2048

maximum canvas pixels:
    4,194,304

maximum logical frames:
    512

maximum submitted canvas pixels:
    33,554,432
    (32 Mi-pixels)

maximum extracted patch pixels:
    currently published as 67,108,864 in the frozen wire specification
    Profile 1 wire status unresolved pending the second Section 2.5 reconciliation
    final classification derived from the canonical Basis runtime patch algorithm

maximum encoded payload:
    67,108,864 bytes
    (64 MiB)
```

Width, height, canvas pixels, logical frames, submitted canvas pixels, and encoded payload are established Profile 1 limits. The currently published 67,108,864 extracted-patch value remains compatibility-sensitive and unresolved until the second Section 2.5 reconciliation; this section does not silently delete it.

Extracted patches are a deterministic Basis runtime representation derived after complete logical RGBA8 canvas decode. The canonical extraction algorithm and its proven maximum inflation factor MUST be specified before Gate D. If the proven algorithm cannot exceed submitted canvas pixels, the second reconciliation SHOULD remove the historical 64 Mi-pixel value from the Profile 1 wire contract as an explicit pre-release erratum. This specification action MUST NOT automatically lower the shared legacy/runtime `BasisImagePickupSettings.MaxAnimationDecodedFramePixels` constant.

Platform-specific and sender-specific implementation limits MAY be stricter.

They MUST NOT redefine the Profile 1 receiver semantics.

---

# 6. Normative submitted-canvas accounting

Profile 1 defines:

```
submittedCanvasPixels =
    checked_u64(canvasWidth)
    * checked_u64(canvasHeight)
    * checked_u64(logicalFrameCount)
```

`logicalFrameCount` means the number of canonical logical display frames presented by the animation stream.

The calculation MUST use complete logical display canvases.

It MUST NOT depend on:

```
JPEG XL frame cropping
JPEG XL reference-frame behavior
codec-internal optimizations
jxlp segmentation
receiver patch rectangles
patch extraction behavior
compressed payload size
```

The resulting value MUST satisfy:

```
submittedCanvasPixels <= 33,554,432
```

Required boundary tests:

```
2048 * 2048 * 8
= 33,554,432
PASS
```

```
2048 * 2048 * 9
= 37,748,736
SharedLimitExceeded
```

Also test checked arithmetic overflow independently.

Integer overflow MUST fail closed and MUST NOT wrap into an apparently acceptable value.

---

# 7. Normative base-timeline accounting

Profile 1 defines:

```
baseTimelineDurationMicroseconds =
    checked_u64(sum(frameDurationMicroseconds[i]))
```

This is exactly one playthrough of all logical frames.

Require:

```
baseTimelineDurationMicroseconds <= 300,000,000
```

`num_loops` MUST NOT participate in this calculation.

Required tests:

```
300,000,000 us
num_loops = 0
PASS
```

```
300,000,000 us
num_loops = 3
PASS
```

```
300,000,001 us
num_loops = 1
SharedLimitExceeded
```

Also test accumulation overflow.

Overflow MUST fail closed.

These definitions are specification-precision additions to the frozen Profile 1 contract. They do not reopen codec benchmarking.

---

# 8. Local sender admission policy

Profile 1 permits implementations to apply stricter sender-side admission than the wire ceiling.

This classification governs JPEG XL sender/encoder admission only. It does not redefine receiver residency budgets or `CalculateAnimationMemoryLimits`. Quest/Android/mobile bypass this classifier entirely during the initial implementation because JPEG XL publication is disabled there and locally authored animation uses V2.

Desktop sender classification is:

```
desktop RAM unknown / unreported / <= 4 GiB:
    smallest / low-memory class

desktop > 4 GiB and <= 8 GiB:
    middle class

desktop > 8 GiB:
    high-memory class
```

Any receiver-side classifier that uses an unknown/unreported RAM value MUST also fail toward the smallest supported memory class rather than falling through to the largest budgets; changing that existing shared receiver behavior requires the V2 regression coverage described in Section 16.

Current desktop preflight ceilings:

```
smallest / low-memory class:
    <= 8 Mi submitted pixels

middle class:
    <= 32 Mi submitted pixels

high-memory class:
    <= 32 Mi submitted pixels for Profile 1 publication
```

The 8 Mi value is a preflight ceiling, not a guarantee that every <=8 Mi workload can complete JPEG XL encoding inside the 256 MiB worker/dedicated-encoder budget. Geometry/content-aware memory prediction MAY reject a workload below the ceiling before starting JXL. A worker-cap termination below the ceiling MUST fail closed and may use V2 fallback where V2 is otherwise permitted.

The previously documented 64 Mi local value for the high-memory class is not an effective Profile 1 publication limit and is removed from the active policy to avoid an inert second ceiling.

No Profile 1 publication may exceed 32 Mi submitted pixels.

Quest/Android and other mobile/portable platforms MUST NOT perform JPEG XL encoding/publication during the initial implementation-validation phase. They MUST receive and decode conforming Profile 1 JPEG XL through the production WASM decoder. Locally authored mobile animations use V2.

Receiver compatibility is symmetric even though sender capability is not: any Profile 1 JPEG XL payload that a supported desktop client is permitted to publish MUST be decodable and viewable on every supported Profile 1 receiver platform, including Quest/Android. A payload that is otherwise within all Profile 1 wire limits MUST NOT be routinely rejected on Quest/Android solely because the current mobile decoded-frame or resident-animation budget is smaller than the wire profile.

Earlier benchmark experiments at 48, 64, 96, or 128 Mi submitted pixels remain historical native-memory measurements only.

---

# 9. Frozen benchmark evidence

Preserve the completed native benchmark artifacts unchanged as testing/benchmarking evidence. They are not production/runtime dependencies.

Recorded Phase 1 benchmark results, pending repository restoration and re-verification of the referenced datasets:

```
JPEG XL real corpus:
    139/139 exact

Final Profile 1 conformance:
    77/77
    17 positive
    60 negative

Final container matrix:
    9/9

Frozen sender smoke:
    139/139

Independent djxl acceptance:
    139/139 smoke
```

Native container and codec-selection benchmarking is complete.

Current decision:

```
Profile 1 container and measured native profile:
    FINAL / FROZEN

receiver/codestream acceptance contract:
    PENDING FORMALIZATION

Implementation validation:
    GO_WITH_GUARDS

Unconditional production replacement:
    NO-GO
```

Do not reinterpret historical benchmark experiments as permission to modify the selected container/pixel/timing constants. The remaining codestream resource limits and patch-conversion rules must be formalized before the receiver contract is called fully frozen.

---

# 10. Compatibility decisions

Keep:

```
Profile 1:
    JPEG XL

APNG:
    future restricted-timing profile candidate

WebP:
    outside Profile 1

V2:
    legacy fallback for eligible local sender failures and the mobile/Quest encode lane
    retained as an interoperable source format for later V2 -> JPEG XL save/export conversion
```

APNG is outside Profile 1 because valid Profile 1 frame durations such as:

```
33,334 microseconds
```

cannot always be represented exactly using APNG's 16-bit delay numerator/denominator representation.

Do not run another broad JPEG XL/APNG/WebP comparison.

V2 compatibility MUST include a deterministic semantic transcode path for future save/export workflows. The V2 payload is decoded into the canonical complete logical RGBA8 display timeline and re-encoded; it is never byte/container rewrapped. V2 patch rectangles, Blend/Disposal state, and codec-internal layout do not survive as JPEG XL semantics.

Section 23 is the single normative definition of that conversion path, its encode admission policy, its round-trip verification requirement, its container form, and its Profile 1 eligibility classification. This section records only the compatibility decision.

---

# 11. Phase 1 — two-stage Profile 1 validation

Profile 1 validation is split into two explicit trust stages. Remote payloads MUST NOT be parsed as JPEG XL codestreams outside the production WASM sandbox.

## 11.1 Stage A — allocation-light container preflight

Stage A runs on the correctly reassembled payload before any allocation or memory admission whose size scales with untrusted codestream-declared values. It MAY run outside the WASM sandbox because it does not interpret JPEG XL frame/codestream semantics.

Stage A MUST validate at minimum:

```
profile version
payload declared length
64 MiB payload limit
JXL container signature
exact ftyp bytes
ordered jxlp boxes
big-endian jxlp counters
first sequence = 0
consecutive sequences
unique sequences
final marker
final marker only on final jxlp
no boxes after final jxlp
no metadata boxes
no jxlc on wire
complete bounded concatenated codestream byte span
no trailing bytes
```

Stage A is authoritative for container canonicality and for determining whether a payload may proceed to sandboxed codestream validation. It MUST NOT parse hostile JPEG XL frame headers or use native libjxl on remote payloads.

## 11.2 Stage B — sandboxed codestream/profile validation

Stage B runs inside the production WASM/sandboxed decoder under bounded sandbox memory/resource policy. It MUST validate at minimum:

```
exact timebase numerator = 1,000,000
exact timebase denominator = 1
coded-frame ceiling
decode-work budget

pixel contract
color contract
alpha contract
orientation

frame durations
base timeline
loop semantics

width
height
canvas pixels
logical frame count
submittedCanvasPixels
complete/non-truncated JPEG XL codestream
validated output/resource envelope required for subsequent host receiver-memory admission
```

Stage B has two ordered parts for remote input. First, the bounded WASM sandbox performs codestream-semantic/resource preflight and returns only validated dimensions, frame/timing values, submitted-pixel accounting, coded-frame/decode-work accounting, and other bounded resource-envelope data to the host. Before any full pixel-output decode or host allocation that scales with those validated values, the host MUST perform receiver memory/output-residency admission against current aggregate pressure. A denial at this boundary is `MemoryAdmissionDenied`. Only after host admission succeeds may the sandbox perform the full bounded pixel decode. The sandbox does not decide host aggregate residency policy, and host admission MUST NOT require unsandboxed codestream parsing.

Direct reuse and post-encode publication validation use the same Stage A + Stage B semantic contract, but trusted local/native oracle implementations MAY exercise Stage B only on trusted local/test data.

Profile 1 intentionally accepts a broad set of libjxl-decodable codestream features, but only inside these semantic and resource limits.

A codestream that ends before libjxl can establish a complete valid JPEG XL codestream is `Malformed`. Stage A may detect only container-span truncation; semantic codestream truncation/completeness is a Stage B responsibility and MUST be classified consistently by native/WASM conformance implementations.

A decodable JPEG XL file is not necessarily Profile 1.

## Stable rejection categories

All relevant implementations should normalize failures into stable categories by layer:

Wire/profile validation:

```
Malformed
UnsupportedProfile
SharedLimitExceeded
PayloadLimitExceeded
```

Runtime/resource handling:

```
PatchLimitExceeded
Timeout
Cancelled
MemoryAdmissionDenied
```

`PatchLimitExceeded` is runtime patch-conversion/resource accounting only unless the finalized canonical patch algorithm proves a wire-reachable Profile 1 condition that requires a portable conformance vector.

Internal diagnostic details MAY be attached separately.

Do not make external behavior depend on unstable libjxl/WASM/native error strings.

---

# 12. Phase 2 — canonical sender pipeline

The publication pipeline MUST be:

```
source decoder
    ->
canonical Basis animation stream
    ->
complete logical RGBA8 display canvases
    ->
Profile 1 preflight
    ->
worker-isolated JXL encode
    ->
Profile 1 container canonicalization
    ->
local Stage A + Stage B Profile 1 validation
    ->
exact local decode validation
    ->
publish
```

Publication is prohibited until every validation step succeeds.

The sender MUST NOT publish:

```
raw arbitrary libjxl container output
jxlc output
noncanonical ftyp
unexpected metadata
unsupported color state
unsupported alpha state
out-of-profile timing
over-limit payloads
partially validated output
```

The complete logical RGBA8 canvases are the normative source representation for submitted-pixel accounting.

---

# 13. Phase 3 — worker-isolated native encoder

All native JPEG XL encoding MUST remain outside the main Unity/runtime process in an isolated worker on platforms where JPEG XL encoding is enabled.

Quest/Android/mobile publication is V2-only during this phase. The mobile receiver still uses the production WASM JPEG XL decoder.

Do not retain the earlier threshold-only isolation recommendation for Profile 1 production validation.

Current encoder policy:

```
effort 1:
    latency-first
    low-memory
    normally 2 threads

effort 3:
    default
    normally 4 threads

effort 5:
    extended / asynchronous mode

normal maximum threads:
    8

effort 7:
    excluded from ordinary interactive use

effort 9:
    excluded from ordinary interactive use
```

These settings are implementation policy, not wire semantics.

## Worker validation matrix

Test:

```
memory-cap termination
timeout
explicit cancellation
parent cancellation
worker crash
malformed worker result
truncated output
output > 64 MiB
output crossing cap during encode
temporary-file cleanup
worker-process cleanup
no orphan worker
no partial publication
no partial activation
repeated worker failures
<= 8 Mi worst-case geometry/content under the 256 MiB low-memory worker cap, with predictor denial or clean V2 fallback treated as expected behavior rather than a containment failure
```

The parent process MUST survive worker memory-limit termination.

Cleanup MUST be deterministic.

A dead worker MUST NOT leave a partially valid-looking payload available for publication.

---

# 14. Phase 4 — sender preflight and JXL/V2 selection

Use inexpensive sender preflight to avoid obviously unsuitable JXL work.

On Quest/Android/mobile, skip JPEG XL sender preflight/encoding and publish locally authored animation through V2 only.

The predictor is implementation policy.

It MUST NOT redefine the hard Profile 1 limits.

Use V2 when JXL:

```
fails memory admission
exceeds local submitted-pixel policy
is predicted to exceed the 64 MiB payload cap
misses the active encoding time budget
worker fails
worker times out
worker is cancelled
post-encode Profile 1 probe fails
exact local decode validation fails
does not provide sufficient predicted benefit
```

V2 remains a fallback lane, not an unconditional guarantee.

Animations whose canonical submitted canvas pixels exceed the 32 Mi Profile 1 limit are ineligible for JPEG XL Profile 1, but MAY continue through the existing V2 publication path when they satisfy V2's own limits. This is normal codec eligibility/selection and MUST NOT be counted as a JPEG XL encode or fallback failure. V2 fallback applies separately to otherwise Profile-1-eligible animations whose JPEG XL sender path is unavailable or fails cleanly.

Track independently:

```
v2BaselineFailures
profile1FallbackToV2Count
v2FallbackSucceeded
v2FallbackFailed
```

A pre-existing V2 failure MUST NOT be reported as a JPEG XL fallback failure.

---

# 15. Phase 5 — production WASM decoder

Implement the production decoder against the Profile 1 descriptor and the provisional receiver resource contract.

Network-supplied Profile 1 payloads MUST be decoded only through the production WASM/sandboxed decoder. Native decode MUST NOT process remote payloads in production.

## WASM receiver benchmark checkpoint

Once the production WASM decoder is functional, run a new receiver-side benchmark before freezing receiver resource limits or proceeding on assumptions derived only from native decoding. This benchmark is a required implementation-validation phase, not a repeat of the earlier codec-selection benchmark.

Measure at minimum:

```
production WASM decode latency
peak WASM memory
retained memory after decode/release
input/payload memory
logical RGBA output memory
runtime patch-conversion memory
maximum conforming 32 Mi submitted-pixel payload
near-64 MiB encoded payloads
cold and warm decode
repeated decode/release
multiple simultaneous animations
Quest/Android on-device behavior
Windows/Linux behavior
malformed and adversarial codestream behavior
coded-frame amplification
reference-chain behavior
progressive/multi-pass behavior
candidate decode-work accounting
cancellation and timeout behavior
```

Use the pinned libjxl revision for the WASM implementation and the trusted native oracle. The benchmark MUST produce machine-readable results and a short decision report.

The following remain provisional until this WASM benchmark is complete:

```
coded-frame ceiling
decode-work structural formula candidate and calibrated ceiling
receiver memory reservations
Quest/mobile decoded-residency policy
single-animation and aggregate decoded-pixel budgets
decode concurrency
WASM timeout values
Gate G quantitative thresholds
need for streaming/staged decode or representation changes
```

Do not prematurely raise shared V2/JXL memory constants or redesign the residency model solely from native estimates. Use the WASM benchmark to decide whether a simple budget adjustment is sufficient or whether a structural change such as streaming/staging is required.

The decoder MUST NOT define its own looser interpretation of the profile.

## Native/WASM differential oracle

For every accepted fixture, compare native and WASM decoded logical canvases exactly:

```
width
height
logical frame count
frame durations
base timeline
loop count
all complete logical RGBA canvas bytes
hidden RGB under alpha=0
coded-frame accounting
decode-work accounting
```

Patch rectangles are NOT part of the native/WASM codec differential. They are derived later by one deterministic Basis runtime patch-conversion algorithm and are tested separately in Gate D.

Also compare rejection classification for invalid fixtures, under the Gate C scope: exact category equality is required for the payload-deterministic wire/profile categories, while runtime/resource outcomes need only prove that both paths fail closed without partial activation.

Run the differential suite against repo-contained implementation-validation test fixtures, including representative files derived from the prior benchmark corpus where licensing/storage permits:

```
positive Profile 1 encode/decode fixtures
frozen portable wire-conformance vectors
payload-limit boundaries
submitted-pixel boundaries
timeline boundaries
logical-frame boundaries
coded-frame boundaries
decode-work boundaries
dimension boundaries
malformed-container corpus
malformed-codestream corpus
memory-abuse corpus
unit-level arithmetic overflow vectors
```

The historical 139-fixture benchmark corpus remains benchmark evidence and is not a production dependency. The next phase MUST provide dedicated checked-in test files sufficient to exercise encoder/decoder exactness and the receiver contract.

A native/WASM mismatch is an implementation defect.

Do not change Profile 1 merely to make mismatching implementations agree unless the frozen wire contract itself is demonstrated to be incorrect.

---

# 16. Phase 6 — WASM memory and lifecycle

Measure separately:

```
module initialization memory
decoder working memory
input payload storage
decoded-frame storage
compositor storage
patch-extraction storage
peak total memory
retained memory after release
```

Required scenarios:

```
cold decode
warm decode
sequential independent decodes
repeated decode/release
repeated malformed payload
repeated failure
cancellation during decode
malformed payload
32 Mi submitted-pixel boundary
maximum conforming Profile 1 payload on the low-memory/mobile receiver class
near-64 MiB encoded payload
multiple active animations
```

Verify that failed and cancelled decodes release memory.

For the maximum conforming Profile 1 payload on the low-memory/mobile class, isolated single-animation viewability is mandatory: with no competing animation residency from the same sender, `MemoryAdmissionDenied` is NOT an acceptable steady-state compatibility result when the payload otherwise satisfies the Profile 1 wire contract. The current mobile decoded-residency constants are therefore provisional for Profile 1 until the Phase 5 WASM receiver benchmark measures the real production path. Measure WASM peak memory, retained decoded/runtime memory, canonical patch representation size, and successful activation. First determine whether the existing shared decoded-residency budget is already sufficient. Do not split the residency architecture preemptively.

The current low-memory/mobile decoded-residency ceiling is 16 Mi pixels. Profile 1 permits up to 32 Mi submitted canvas pixels, and a canonical patch algorithm with a proven inflation factor <= 1.0 would permit a worst-case upper bound as high as 32 Mi patch pixels. This does not prove that a conforming payload requiring 32 Mi resident decoded pixels actually exists, so the Phase 5 WASM benchmark plus the finalized canonical patch conversion MUST determine the maximum reachable single-animation resident requirement. If that reachable requirement is <= 16 Mi, keep the existing shared budget unchanged. If it exceeds 16 Mi, first test the minimum justified increase to the existing shared budget and run V2 Quest/mobile residency regressions. A split Profile-1-specific single-animation/aggregate budget is required only if the minimum shared increase is unsafe, causes unacceptable V2 aggregate-residency pressure, or otherwise cannot preserve the required isolated-viewability guarantee. If a safe budget adjustment is not possible, use representation/streaming/staging changes rather than defining a smaller Quest receive profile.

Current remote payload-backed animation handling already separates persistent attachment from active decoded residency. Reloadable remote payloads are attached through the aggregate canvas/payload path, while active decoded residency uses the existing distance/visibility-aware reclaim path (`TryMakeRoomForDecodedPixels` / `TrimDecodedPixelBudget`) and may release farther reloadable decoded animations under memory pressure. The plan MUST preserve that distinction:

```
isolated single-animation compatibility:
    any one conforming Profile 1 payload must be able to become viewable

active decoded-residency pressure:
    reclaim/evict farther reloadable decoded animations before denying
    an individually compatible candidate where the existing policy allows it

persistent aggregate payload/canvas pressure:
    may still deny additional competing animations once non-reclaimable
    aggregate limits are genuinely exhausted
```

Aggregate-pressure denial is therefore a separate lifecycle/policy outcome, not evidence that the single-animation Profile 1 receive envelope has failed.

Because the decoded-residency budgets are shared with the existing V2 path, any change to those shared constants MUST include V2 regression testing on Quest/mobile, including aggregate residency/memory-pressure behavior rather than only successful decode/activation.

Measure retained memory after repeated use rather than only one-shot peak memory.

---

# 17. Phase 7 — Unity / BasisAnimatedImageData integration

Define one deterministic canonical Basis patch-extraction/conversion algorithm from complete logical RGBA8 display canvases, then validate exact conversion into `BasisAnimatedImageData`.

Required behavior:

```
deterministic patch rectangles for identical logical canvases
exact RGBA patch bytes
hidden RGB preserved
explicit Blend / Disposal mapping for JXL-sourced frames
correct frame durations
correct loop behavior
correct canvas dimensions
correct active-native byte accounting
runtime patch-memory enforcement
receiver-memory admission
no activation before complete validation
```

The canonical algorithm MUST define its maximum patch-pixel inflation factor relative to submitted logical canvas pixels. That evidence determines the final Profile 1 extracted-patch classification in the second Section 2.5 reconciliation. If the proven factor is <= 1.0, extracted patch pixels cannot exceed the 32 Mi submitted-pixel ceiling and the historical 64 Mi value MUST be removed from the Profile 1 wire contract. This is a specification action only: `BasisImagePickupSettings.MaxAnimationDecodedFramePixels` is currently shared by legacy V2 validation, GIF patch accumulation/import, `BasisAnimatedImageData` runtime admission, and working-set estimation, so it MUST NOT be lowered or removed as a side effect of changing Profile 1 wire semantics. Any Profile-1-specific runtime safety bound should use distinct policy/accounting unless a separate compatibility review intentionally changes the shared V2/GIF behavior.

The active animation MUST NOT become visible or replace the existing state until the payload has passed all required validation.

---

# 18. Phase 8 — eviction and restoration

When an animation is evicted, retain:

```
full canonical JXL payload
required poster / preview state
timing metadata required to restore state
backward-correction clamp watermark keyed by animation/publication identity and PlaybackEpochUtcTicks
profile metadata
```

Release:

```
decoded patch data
compositor state
decoder scratch memory
temporary decode buffers
other recreatable native/WASM state
```

Restoration MUST:

```
reacquire memory admission
decode the retained payload
revalidate as necessary
rebuild patches
resolve current animation state
seek toward the correct logical display state
converge to that display state within the bounded global compositor transition/pixel budgets
resume without timing drift once caught up
```

Test repeated eviction/restoration cycles.

Aggregate sender-pressure policy MUST be explicit and distinct from isolated single-animation compatibility. When decoded-residency pressure is caused by multiple animations from one sender, the runtime SHOULD first evict or defer reloadable decoded state according to the existing distance/visibility policy before denying activation of a candidate that would otherwise be individually compatible. Persistent payload/canvas aggregate limits MAY still deny additional animations after reclaimable decoded state is exhausted; such aggregate-pressure denial is not the same as failure of the Profile 1 single-animation receive envelope.

A restore failure MUST leave the animation in a valid non-partially-activated state.

---

# 19. Phase 9 — epoch synchronization

Use the existing Basis network epoch as the synchronization source.

For positive elapsed network time, convert 100 ns ticks to microseconds using floor division by 10. Sub-microsecond remainder is discarded.

Playback state MUST NOT move backward because of a backward synchronized-clock correction. Backward corrections are clamped to a monotonic playback-time watermark keyed by animation/publication identity and `PlaybackEpochUtcTicks`. A new publication epoch resets the watermark; a watermark from an older epoch MUST NOT clamp a legitimately restarted animation. The watermark records the epoch-derived target playback time, not merely the last compositor state that happened to finish rendering. Forward corrections resolve immediately to the state selected by the corrected epoch unless a later implementation-validation decision explicitly defines a different monotonic correction policy.

Validate:

```
initial join
late join
finite loops
infinite loops
one completed loop
multiple completed loops
long-running playback
eviction
restoration
reconnect
route change
backward clock correction followed by eviction/restoration versus a continuously running peer
```

For one client, eviction/restoration at the same epoch MUST preserve the monotonic watermark and converge to the same logical frame/display state that its continuously running path would reach, subject to the existing global compositor transition/pixel budgets. Same-frame instantaneous reconstruction is not required when the compositor budget requires catch-up across multiple Unity frames.

Different clients that observed the same epoch and the same synchronized-clock correction history MUST resolve to the same logical playback target. Clients that did not observe the same backward correction history MAY temporarily differ because the monotonic clamp is intentionally history-dependent; they MUST converge once corrected synchronized time passes the older client's watermark.

Finite animations MUST agree on terminal state after convergence.

Infinite animations MUST agree on modulo-timeline state after convergence.

Restoration MUST NOT restart the animation from frame zero unless frame zero is the state selected by the current epoch.

---

# 20. Phase 10 — transport validation

Do not add a content hash to Profile 1.

Continue using the existing transport integrity model:

```
GUID
declared length
chunk count
chunk index
duplicate handling
exact reassembly length
```

Validate:

```
missing chunk
duplicate identical chunk
duplicate conflicting chunk
out-of-order delivery
invalid chunk index
invalid chunk count
declared-length mismatch
truncation
extra data
payload over 64 MiB
interrupted transfer
sender disconnect
late completion
```

The strict Profile 1 payload probe runs only on a correctly reassembled transport payload.

Because Profile 1 intentionally adds no content hash, successful transport reassembly plus Stage A container preflight and Stage B sandboxed codestream validation form the payload acceptance boundary. Stage A MUST run before any allocation or memory admission whose size scales with untrusted codestream-declared values. Stage B then runs only inside the bounded WASM sandbox and MAY perform the codestream/header work required for timing, coded-frame, decode-work, pixel/color/alpha, and output validation.

Transport validity does not imply Profile 1 validity.

Profile 1 validity does not bypass transport checks.

Malformed, over-limit, repeated invalid, and abusive Profile 1 submissions MUST feed the existing per-sender abuse/rate accounting rather than only producing logs.

---

# 21. Phase 11 — relay and direct P2P validation

The exact same Profile 1 payload MUST reconstruct through:

```
relay / server path
direct P2P path
```

Compare final payload bytes, not merely decoded visual output.

Test:

```
relay-only transfer
P2P-only transfer
relay -> P2P route transition
P2P -> relay route transition
reconnect
interruption
duplicate delivery
sender disconnect
receiver disconnect
late completion
transfer restart
multiple receivers using different routes
```

No route may reinterpret, normalize, or re-encode the payload.

---

# 22. Phase 12 — platform validation

Required platforms:

```
Windows
Linux
Quest / Android
```

Validate especially:

```
WASM decoder exactness
decoder peak memory
retained memory
worker behavior where desktop encoder is available
cancellation
eviction
restoration
epoch synchronization
thermal behavior
multiple simultaneous animations
near-limit payloads
near-limit submitted-pixel animations
hostile/malformed JXL rejection and abuse accounting
```

Quest/Android MUST validate Profile 1 receive/decode through WASM. Quest/Android JPEG XL encoding is out of scope for the initial production path; locally authored animation uses V2.

A conforming payload publishable by a supported desktop sender MUST remain viewable on Quest/Android. Platform-specific receiver policy MAY reduce concurrency, cache residency, or other implementation behavior, but MUST NOT reduce the accepted Profile 1 wire envelope. In particular, a maximum conforming Profile 1 payload MUST be successfully decoded and activated on the minimum supported Quest/Android receiver class in an isolated single-animation test with no competing same-sender animation residency.

Quantitative Gate G thresholds for peak memory, retained-after-release memory, decode latency, and supported concurrent animations MUST come from the dedicated Phase 5 WASM receiver benchmark and follow-up on-device measurements. They are frozen only after those measurements exist and before Gate G formally begins.

Platform-specific aggregate/concurrency limits MAY be stricter, but isolated per-animation receiver admission MUST still allow any one conforming Profile 1 payload to become viewable. Aggregate-pressure denial of additional same-sender animations is permitted after reclaimable decoded residency has been evicted/deferred and applicable persistent aggregate limits are still exceeded; that case MUST be tested and classified separately from isolated single-animation compatibility.

Examples of limits that MAY be lower on constrained platforms include:

```
aggregate decoded-animation residency across multiple animations
simultaneous active-animation count
decode concurrency
worker concurrency where an encoder exists
cache residency / retention
sender admission
```

Those aggregate/concurrency limits MUST NOT reduce the single-animation Profile 1 receive envelope or change Profile 1 wire semantics.

---

# 23. Phase 13 — direct `.jxl` import

Direct byte reuse is allowed only when the imported file passes Stage A container preflight and Stage B sandboxed Profile 1 validation unchanged. Section 8 encoder/preflight memory-class ceilings do not apply to direct reuse because no JPEG XL encode occurs. Direct reuse remains subject to the same Profile 1 receiver compatibility guarantee as any other Profile 1 publication: before publication, the local client MUST be able to admit and activate the candidate through the normal isolated single-animation receiver path. No separate direct-import receive envelope exists.

Directly reused files MUST already contain:

```
exact Profile 1 container signature
exact Profile 1 ftyp
ordered Profile 1 jxlp boxes
canonical sequence numbering
canonical final marker
no forbidden boxes
no jxlc
canonical timing
canonical loop semantics
canonical RGB color contract
canonical straight alpha contract
identity orientation
all wire limits satisfied
```

A JPEG XL file being valid according to libjxl is not sufficient.

Out-of-profile `.jxl` files MUST be rejected for direct reuse.

A separate future normalization/import path MAY:

```
decode an out-of-profile JXL
prove its animation is exactly representable
convert it to canonical logical RGBA8 canvases
encode a new Profile 1 payload
validate it
```

That is not direct reuse.

## V2 -> JPEG XL save/export conversion

Future animation-save/export support MUST be able to start from a retained or received V2 payload without requiring the original GIF/source file.

The required conversion path is:

```
V2 payload
    -> validate V2 header and structural bounds
    -> compute checked_u64 submitted canvas pixels from CanvasWidth * CanvasHeight * FrameCount
    -> pre-reconstruction export working-set admission
    -> decode/reconstruct the exact canonical logical RGBA8 display-frame timeline
    -> preserve exact durations and loop semantics
    -> JPEG XL encode in the isolated worker
    -> bounded verification decode/compare of exact logical output
    -> save resulting `.jxl`
```

### Export encode admission

Export encoding is not bounded by the Profile 1 wire ceilings, because this section permits exporting animations that exceed them. The V2 limits are far weaker than Profile 1: `BasisBurstAnimationCodec` validates canvas pixels and frame count independently and caps only the sum of patch areas, so a legal V2 animation may reach `2048 * 2048 * 512 = 2,147,483,648` submitted canvas pixels, roughly 64x the 32 Mi Profile 1 publication ceiling.

The Section 8 ceilings are Profile 1 publication limits and are NOT the export admission limit. Export admission is bounded by measured local memory, not by the wire profile. Unlike the publication pipeline in Section 12, the export path cannot safely construct complete logical canvases before admission because it lacks Profile 1's 32 Mi submitted-pixel ceiling. The validated V2 header already provides `CanvasWidth`, `CanvasHeight`, and `FrameCount`, so submitted-canvas accounting MUST use `checked_u64(CanvasWidth) * checked_u64(CanvasHeight) * checked_u64(FrameCount)` before any full-timeline reconstruction. Arithmetic failure at this admission boundary MUST fail closed as `MemoryAdmissionDenied`, not escape as an unhandled integer-overflow exception.

Export admission MUST bound the total overlapping working set, not only the encoder worker. At minimum it MUST account for the concurrently live portions of:

```
retained/received compressed V2 payload
LZ4-decompressed V2 body / decode input storage
V2 decoded patch-pixel pool
V2 reconstruction compositor canvases, including the additional Previous-disposal canvas when required
reconstructed source logical RGBA timeline
isolated encoder working set
encoded output / transport-to-verification buffers
verification decoder working set
verification logical RGBA output needed for exact comparison
```

The reconstructed logical timeline MUST NOT be specified or implemented as one mandatory contiguous `Color32`/`NativeArray` allocation; the maximum legal V2 logical timeline exceeds signed 32-bit element counts. Segmented, staged, or otherwise non-contiguous reconstruction is permitted as long as exact canonical logical-frame semantics are preserved and admission models the actual maximum overlapping allocations.

The implementation MAY reduce overlap through streaming/staging, but its admission model MUST match the actual maximum concurrently live allocation pattern. The existing measured encoder estimator remains one component of this total model; it is not by itself the export-memory bound.

```
pre-reconstruction admission:
    occurs immediately after validated V2 header parsing
    uses checked_u64(CanvasWidth) * checked_u64(CanvasHeight) * checked_u64(FrameCount)
    includes the V2-side decode/reconstruction allocations that remain live during reconstruction
    rejects before allocating/materializing the logical timeline

worker isolation:
    same as publication
    no export encode runs in the parent process

dedicated encoder budget:
    same per-memory-class encoder budget as publication

total export working-set budget:
    measured/calibrated per supported memory class
    includes timeline reconstruction + encoder + verification overlap

over-budget workload:
    MemoryAdmissionDenied
    refused before timeline reconstruction or encode starts
```

An export that cannot satisfy the pre-reconstruction or total working-set admission MUST fail as `MemoryAdmissionDenied`. It MUST NOT allocate the complete logical timeline first, start the encoder and rely on the worker cap, or begin a verification decode that exceeds the admitted total-working-set model.

### Round-trip verification

Conversion tests and the shipped export path MUST compare the V2-decoded canonical timeline against the JPEG XL round-trip for dimensions, logical frame count, durations, loop semantics, complete RGBA bytes, and hidden RGB under alpha zero.

The Profile 1 RGBA8 path is lossless, so a mismatch is an implementation defect rather than a quality tradeoff. A mismatch MUST fail the export. A file that failed round-trip verification MUST NOT be written, retained, or presented to the user as an exact conversion.

### Container form and eligibility classification

If the canonical decoded animation is Profile 1 eligible, the saved JPEG XL SHOULD use the canonical Profile 1 container and MUST pass the normal Profile 1 validation path before being marked reusable as Profile 1.

If the V2 animation exceeds Profile 1 wire limits, the save/export feature MAY still produce a non-Profile-1 local JPEG XL file. Such a file is a local export artifact, not a Profile 1 transport payload, and direct Profile 1 reuse remains prohibited.

A non-eligible export MUST NOT be labeled, advertised, or routed as a conforming Profile 1 transport payload unless it independently satisfies the complete Profile 1 contract. Its local JPEG XL container form is an implementation/export-policy choice; it MAY use default libjxl output or another valid local JXL container representation. If it happens to resemble the canonical Profile 1 container shape, direct Profile 1 reuse is still prohibited unless Stage A and Stage B both succeed.

The conversion MUST NOT inherit V2 patch geometry as JPEG XL semantics. The canonical complete display-frame timeline is the interchange boundary.

On platforms where JPEG XL encoding is intentionally disabled, including the initial Quest/Android path, retaining/receiving V2 remains supported; V2 -> JPEG XL save/export conversion is performed only where a supported JPEG XL encoder is available unless a later mobile encoder policy is validated.

### Gate scope

Save/export conversion is not required for the initial production decision in Section 27. When it is enabled, the Gate B worker-containment requirements apply to both Profile-1-eligible and non-Profile-1 export branches: worker failures are contained, failed output is never written/published as valid output, and no partial artifact is exposed. Gate B's Profile 1 canonicalization/probe/exact-local-profile-decode requirements apply only to the Profile-1-eligible export branch. Both branches additionally require export encode admission and exact fail-closed round-trip verification, and neither may ship ahead of those applicable checks.

---

# 24. Editor/native decode policy

Where editor-native decode support is retained, gate it explicitly with the Unity editor conditional and a configuration flag.

Native decode is a trusted-local/editor/oracle facility only. It MUST NOT be enabled as a production path for remote/network-supplied payloads.

Default editor validation should exercise the production WASM path so normal development does not silently bypass the production decoder.

Native decode remains useful as an oracle and diagnostic implementation.

Native and WASM decoding MUST conform to the same Profile 1 semantic/resource contract.

The encoder, native oracle, and WASM decoder MUST use the same pinned libjxl version/commit. Any libjxl upgrade requires rerunning the portable conformance suite and Gate C differential suite before adoption.

Decoder results MUST be deterministic across supported thread-count choices. Encoder output bytes are not required to be deterministic; tests MUST compare decoded semantics/profile conformance rather than golden encoded byte streams.

---

# 25. Required negative and fault testing

## 25.1 Frozen portable wire-conformance vectors

The portable wire-conformance suite MUST identify which trust stage owns each negative vector.

Stage A container/transport vectors should contain explicit failures for at least:

```
bad signature
bad ftyp size
bad ftyp major brand
bad ftyp minor version
extra compatible brand
missing ftyp
metadata box
jxlc
jxlp sequence starts nonzero
jxlp duplicate sequence
jxlp skipped sequence
jxlp reordered sequence
multiple final markers
missing final marker
box after final jxlp
truncated jxlp counter
truncated jxlp/container byte span
declared-length mismatch
payload > 64 MiB
```

Stage B codestream/profile vectors should contain explicit failures for at least:

```
container-complete but semantically truncated/incomplete JPEG XL codestream
zero dimensions
width > 2048
height > 2048
canvas > 4,194,304 pixels
logical frame count > 512
submitted pixels > 33,554,432
duration < 33,334 us
base timeline > 300,000,000 us
wrong timebase numerator
wrong timebase denominator
coded-frame ceiling exceeded
decode-work budget exceeded
wrong color space
wrong primaries
wrong white point
wrong transfer function
wrong bit depth
wrong orientation
missing alpha
associated/premultiplied alpha
wrong alpha depth
wrong alpha exponent
wrong alpha dim shift
non-alpha extra channel
```

A vector named only `truncated codestream` is insufficient because container-span truncation is Stage A while semantic JPEG XL completeness/truncation is Stage B.

The `duration < 33,334 us` wire vector is sourced from hostile/direct-JXL/synthetic input. The canonical GIF source path clamps durations before Profile 1 sender preflight and therefore cannot construct this invalid sender case.

Two-sided boundary tests MUST cover `limit - 1`, `limit`, and `limit + 1` for submitted pixels and base timeline duration.

Arithmetic-overflow tests for submitted-pixel multiplication and timeline accumulation are unit-level parser/accounting tests using not-yet-validated declared values; they are not required to be valid Profile 1 wire fixtures after width/height/frame-count validation.

Patch-pixel overflow is tested in the runtime patch-conversion suite only if the finalized canonical extraction algorithm proves such a state reachable.

## 25.2 Runtime fault-injection suite

Runtime fault injection is implementation-specific and evolves separately from the frozen portable wire corpus. It MUST include:

```
receiver memory denied
worker timeout
decoder timeout
explicit cancellation
parent cancellation
worker crash
decoder crash
malformed worker result
worker memory-cap termination
temporary-file cleanup failure simulation
no partial activation
```

Runtime results use stable categories including `Timeout`, `Cancelled`, and `MemoryAdmissionDenied` where applicable.

The suite should assert both:

```
rejection
stable rejection category
```

where a public category is defined.

Structure-aware fuzzing of the strict probe and WASM decoder is permitted and encouraged as follow-up hardening, but it is not an initial production gate under this plan.

---

# 26. Validation gates

## Gate A1 — container preflight

A1 may close before the production WASM decoder exists, but only after the first Section 2.5 wire-spec reconciliation has been published and is authoritative for all Stage A semantics. PASS only when:

```
all Stage A positive container vectors accepted
all Stage A negative container vectors rejected correctly
payload/declared-length arithmetic uses checked operations
container canonicality enforced
no hostile JPEG XL codestream parsing occurs outside the sandbox
```

## Gate A2 — sandboxed codestream/profile validation

A2 closes only after the Phase 5 WASM receiver benchmark has finalized the receiver resource contract and the second Section 2.5 wire-spec reconciliation has been published and is authoritative for the finalized coded-frame/decode-work contract. PASS only when:

```
all portable Stage B positive conformance vectors accepted
all portable Stage B negative vectors rejected correctly
coded-frame and decode-work limits finalized and enforced
submitted-pixel and timeline arithmetic uses checked operations
pixel/color/alpha/orientation/timing semantics enforced
```

Gate A is complete only when both A1 and A2 pass.

## Gate B — sender

Gate B MUST NOT close until the second Section 2.5 wire-spec reconciliation has been published. The first reconciliation must already be authoritative for sender/platform policy, including the Quest/Android V2-only publication rule, and the second reconciliation must make the Stage B coded-frame/decode-work contract final before the sender's post-encode Stage A + Stage B probe can count as a Gate B PASS. This avoids closing Gate B against a provisional receiver contract and later requiring a separate re-verification ceremony. PASS only when:

```
all admitted desktop implementation-validation fixtures produce canonical Profile 1 or take an explicitly expected clean fallback path
<=8 Mi low-memory worst-case cases either pass prediction/worker limits or fall back cleanly without parent-process failure
local probe passes
exact local decode passes
failed output is never published
worker failures are contained
Quest/Android local publication remains V2-only
```

## Gate C — WASM differential

Gate C MUST NOT close until the second Section 2.5 wire-spec reconciliation has been published and is authoritative for the finalized coded-frame/decode-work contract. PASS only when:

```
native == WASM
```

for every normative decoded-canvas field, RGBA byte, deterministic wire/profile rejection category, coded-frame accounting value, and decode-work accounting value across the portable validation suite. Exact rejection-category equality is required only for payload-deterministic wire/profile categories (`Malformed`, `UnsupportedProfile`, `SharedLimitExceeded`, `PayloadLimitExceeded`). Runtime/resource outcomes such as `Timeout`, `Cancelled`, `MemoryAdmissionDenied`, and runtime-only `PatchLimitExceeded` need not match category-for-category between native and WASM; those cases MUST instead prove that both paths fail closed, do not partially activate, and respect their own bounded resource policy.

## Gate D — Unity runtime

Gate D MUST NOT close until the second Section 2.5 wire-spec reconciliation has published the final extracted-patch classification using the canonical patch algorithm's proven inflation bound. PASS only when:

```
canonical patch algorithm is fully specified
canonical patch conversion is deterministic and exact
timing is exact
loops are exact
hidden RGB remains exact
memory limits are enforced
activation is atomic
```

## Gate E — lifecycle

PASS only when:

```
eviction releases recreatable state
restoration rebuilds exact state and converges within bounded compositor budgets
epoch synchronization remains correct
new publication epochs reset the backward-correction watermark
same-client eviction/restoration preserves monotonic playback state
no timing drift is introduced after catch-up
```

## Gate F — network

PASS only when:

```
relay reconstruction is byte-exact
P2P reconstruction is byte-exact
route changes do not corrupt state
transport failures fail closed
```

## Gate G — platforms

PASS only after:

```
Windows
Linux
Quest / Android
```

complete the required production validation matrix. Quantitative memory, retained-memory, decode-latency, and concurrency thresholds are measured first, frozen before Gate G begins, and then used as falsifiable PASS/FAIL criteria.

Quest/Android Gate G covers WASM receive/decode, lifecycle, synchronization, hostile-input handling, memory, latency, concurrency, aggregate pressure, and thermal behavior. It does not require JPEG XL encoding. Gate G MUST demonstrate successful viewing of a maximum conforming Profile 1 payload on the minimum supported Quest/Android receiver class in an isolated single-animation case; `MemoryAdmissionDenied` in that isolated case is a Gate G failure. Separate aggregate-pressure scenarios MUST demonstrate correct eviction/defer behavior and well-defined denial once persistent aggregate limits are genuinely exhausted; denial of an additional competing animation in that aggregate case is not itself a failure of the single-animation Profile 1 receive envelope.

---

# 27. Production decision criteria

The implementation may advance beyond `GO_WITH_GUARDS` only when the complete production path demonstrates:

```
strict Profile 1 validation
worker containment
WASM/native exactness
bounded decoder memory
bounded active animation memory
isolated single-animation Profile 1 receive guarantee on every supported platform
defined and tested aggregate-pressure eviction/denial behavior
correct Unity patch conversion
correct eviction/restoration
correct epoch synchronization
transport robustness
relay/P2P equivalence
supported-platform stability
reliable V2 fallback accounting
```

Encoder speed or compression ratio alone is insufficient.

The current state remains:

```
Profile 1 container and measured native profile:
    FINAL / FROZEN

receiver/codestream acceptance contract:
    PENDING FORMALIZATION

Implementation validation:
    GO_WITH_GUARDS

Unconditional production replacement:
    NO-GO
```

until those implementation gates are satisfied.

---

# 28. Wire changes versus policy changes

Section 2.5 records the known divergences between this plan and `JPEG_XL_PROFILE_V1_WIRE_SPEC.md` and requires explicit published reconciliations before implementation gates close. That list is normative there and MUST NOT be restated here.

Because Profile 1 has not yet been enabled for production network publication, the selected versioning policy is to keep `profileVersion = 1` and treat both reconciliations as pre-release errata/clarifications completing the undeployed contract. This exception applies only while no Profile 1 payload has been published to a non-test network before the second reconciliation. If publication occurs earlier, compatibility review MUST determine whether the unfinished semantics instead require a new profile version.

The second reconciliation intentionally remains one publication containing both finalized coded-frame/decode-work semantics and final extracted-patch classification. Gates A2/C may therefore wait for patch-algorithm evidence even though they do not consume the patch classification directly; that serialization is accepted to keep one authoritative completed hostile-receiver contract rather than adding reconciliation 2a/2b.

One of those divergences is governed by this section's versioning rules. The extracted-patch-pixel discrepancy is compatibility-sensitive: a value currently published as a frozen wire limit MUST NOT be silently deleted. Either justify it as a mistaken runtime-limit inclusion through an explicit compatibility erratum, or retain and version it according to the rules above.

The following MAY change without a Profile 1 version bump:

```
encoder effort
encoder thread count
worker implementation
worker launch policy
system memory classes
local sender admission below wire limit
compressibility predictor
timeouts
JXL/V2 selection heuristics
caching
decode concurrency
eviction thresholds
thermal policy
```

The following require explicit compatibility review and will generally require a new profile version:

```
container syntax
ftyp requirements
jxlp semantics
timing representation
loop semantics
pixel format
color semantics
alpha semantics
orientation semantics
wire-limit changes
meaning of submittedCanvasPixels
meaning of baseTimelineDurationMicroseconds
changes to the broad accepted JPEG XL feature policy or normative coded-frame/decode-work accounting
```

Do not change frozen wire semantics to work around an implementation-performance problem.

---

# 29. Explicit non-goals

This implementation-validation phase does NOT include:

```
another JPEG XL/APNG/WebP shootout
changing Profile 1 to APNG
changing Profile 1 to WebP
raising frozen wire limits due to desktop hardware
changing shared Profile 1 receiver semantics solely due to Quest hardware
adding a content hash
inventing a new benchmark architecture
replacing the existing transport protocol
removing V2 compatibility
```

Any codec research beyond Profile 1 should be tracked independently as a future profile investigation.

---

# 30. Artifact preservation and test-data layout

Completed benchmark evidence is testing/benchmarking material, not a production/runtime dependency.

Use this repository layout for Profile 1 research evidence:

```
Research/BasisImageSandbox/Profile1/
    JPEG_XL_PROFILE_V1_WIRE_SPEC.md
    JPEG_XL_V3_PROFILE_BENCHMARK_PHASE1_FINAL_DECISION.md
    JPEG_XL_PROFILE_V1_FINAL_BENCHMARK.md
    JPEG_XL_PROFILE_V1_IMPLEMENTATION_VALIDATION_PLAN.md
    profile-v1-wire.json
    profile-v1-phase1-final-recommendation.json
    conformance/
    results/raw/
    results/summary/
```

Fix benchmark-document links to be repository-relative. Do not retain machine-specific absolute paths.

Preserve the Phase 1 raw/summary benchmark results needed to substantiate historical conclusions when those artifacts are recoverable. If a referenced historical dataset cannot be recovered, record it explicitly as unavailable/unrecovered rather than reconstructing or inventing it. Missing historical benchmark evidence does not block Gates A/C or production implementation because the historical corpus is non-load-bearing; the dedicated repo-contained implementation/conformance fixtures defined below are the gate-bearing test inputs. The historical benchmark corpus is used only for research/testing/benchmarking and MUST NOT be required by the shipped client.

The implementation phase MUST add dedicated repo-contained test files/fixtures that exercise Profile 1 encode/decode, portable conformance, resource boundaries, and WASM/native differential behavior. These test fixtures are the runnable basis for Gates A and C; the production runtime does not depend on benchmark data.

Earlier experimental memory limits and policy recommendations should remain documented as historical measurements.

The implementation-validation plan is the active forward-looking document.

---

# 31. Immediate implementation order

Recommended execution order:

```
1. Move/preserve recoverable Profile 1 research artifacts under Research/BasisImageSandbox/Profile1 and fix repo-relative links. Record any referenced historical Phase 1 artifact that cannot be recovered as unavailable; do not make historical-corpus recovery a gate blocker.
2. Add the missing profile-v1-phase1-final-recommendation.json preservation entry and runnable implementation test fixtures.
3. The Profile 1 implementation owner performs the first Section 2.5 reconciliation by landing the version-controlled erratum/amendment in `Research/BasisImageSandbox/Profile1/JPEG_XL_PROFILE_V1_WIRE_SPEC.md` together with the matching `profile-v1-wire.json` update. Resolve the Stage A/Stage B security ordering including Stage B ownership of semantic codestream completeness/truncation, rejection-category layering, current platform/sender-policy classification, and the settled plan-only normative additions for submittedCanvasPixels, baseTimelineDurationMicroseconds, and the canonical 1,000,000/1 timebase form. For extracted patch pixels, record that the published 64 Mi wire-limit status is not yet demonstrated and defer final classification until the canonical patch algorithm proves its inflation bound. For each compatibility-sensitive settled item, explicitly classify it as an erratum/clarification or require a profile-version decision under Section 28 before implementation relies on it.
4. Update `profile-v1-wire.json` fields affected by the first published reconciliation, explicitly including `validationOrder`, layered `rejectionClassifications` including `Cancelled`, the unresolved `maximumExtractedPatchPixels` status, mobile/platform sender classification, unknown/default-memory-class behavior, exact timebase representation, version-namespace note, submitted/base-timeline definitions, stale 64 Mi sender-admission entries, and any duplicate/stray worker-memory-cap field that conflicts with the authoritative worker policy.
5. Define provisional coded-frame/decode-work accounting candidates and construct the adversarial receiver benchmark corpus; do not freeze the final numbers from native-only evidence.
6. Add checked submittedCanvasPixels and baseTimelineDurationMicroseconds calculations with unit-level overflow tests.
7. Implement the allocation-light container preflight and the sandbox-facing Profile 1 validation interface.
8. Implement the production WASM decoder, then run the dedicated WASM receiver benchmark on desktop and an early on-device Quest/Android feasibility pass using representative and maximum conforming Profile 1 payloads. Measure decode latency, peak/retained memory, adversarial coded-frame/reference/progressive cases, runtime representation size, and activation success.
9. Use the WASM benchmark evidence to finalize the implementation-independent coded-frame/decode-work accounting and calibrated ceilings, receiver memory policy, Quest/mobile residency requirements, timeout/concurrency values, and whether simple budget changes or structural streaming/staging changes are required.
10. Add/run the portable positive/negative/boundary conformance suite and native/WASM decoded-canvas differential harness against the finalized candidate receiver contract, while Gate A2/C remain open pending publication of the second reconciliation.
11. Define and test the deterministic canonical Basis patch-extraction algorithm, Blend/Disposal mapping, and proven inflation bound.
12. The Profile 1 implementation owner performs the single second Section 2.5 reconciliation by landing the finalized coded-frame/decode-work semantics and the final extracted-patch classification in `Research/BasisImageSandbox/Profile1/JPEG_XL_PROFILE_V1_WIRE_SPEC.md` together with the matching `profile-v1-wire.json` update. Under the selected pre-release policy this completes `profileVersion = 1` by erratum/clarification, provided no non-test Profile 1 publication has occurred. Gate A2, Gate B, Gate C, and Gate D remain open until this reconciliation is published.
13. Implement sender canonicalization for desktop JXL publication.
14. Move all enabled native JXL encoding behind worker isolation; keep Quest/Android sender V2-only.
15. Add post-encode Stage A + Stage B validation plus the exact trusted-local decode gate.
16. Implement explicit JXL -> V2 fallback accounting for otherwise eligible sender failures.
17. Integrate decoded output with BasisAnimatedImageData and validate patch/active-memory accounting.
18. Implement eviction/restoration, including aggregate sender-pressure eviction/defer behavior distinct from isolated single-animation admission.
19. Validate epoch-based restoration state including floor tick conversion and backward-clock clamp.
20. Validate chunk transport and abuse accounting.
21. Validate relay/P2P equivalence.
22. Measure platform memory/latency/concurrency thresholds, freeze Gate G numbers, then run the Windows/Linux/Quest-Android matrix including isolated single-animation and aggregate-pressure cases.
23. Implement and test V2 -> canonical logical timeline -> JPEG XL save/export conversion, including V2-header pre-reconstruction admission, calibrated total overlapping working-set accounting, isolated encoder containment, `MemoryAdmissionDenied` fail-closed behavior, exact round-trip validation, non-Profile-1 container form, and Profile 1 eligibility classification.
24. Enable direct `.jxl` reuse only through Stage A container preflight plus Stage B sandboxed Profile 1 validation.
25. Re-evaluate production readiness against the validation gates.
```

The next engineering work begins by making the evidence/test layout reproducible, reconciling the frozen wire spec through explicit errata, and completing the hostile-receiver codestream resource contract, then implementing Stage A container preflight, Stage B WASM validation, and the WASM differential path before substantial sender work.
