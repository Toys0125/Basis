# Numerel Basis Port and Native Comparison

## Upstream reference

- Repository: `https://codeberg.org/cnlohr/numerel`
- Compared revision: `8676848ae268f3a8eee672413f272ee422521d09`
- Upstream license: MIT, Copyright (c) 2026 cnlohr
- Local comparison clone used during development: `/home/ubuntu/projects/numerel`

The Basis port is implemented in both source trees:

- `Basis Server/BasisNetworkCore/Compression/BasisNumerel.cs`
- `Basis/Packages/com.basis.server/BasisNetworkCore/Compression/BasisNumerel.cs`

The experimental avatar wrapper is also mirrored:

- `Basis Server/BasisNetworkCore/Compression/BasisNumerelArmatureCodec.cs`
- `Basis/Packages/com.basis.server/BasisNetworkCore/Compression/BasisNumerelArmatureCodec.cs`

## Native oracle

The native comparison harness is:

`Basis Server/BasisNumerelBenchmark/NativeReference/numerel_oracle.c`

Build it against the cloned upstream header:

```sh
cc -std=c11 -O2 \
  'Basis Server/BasisNumerelBenchmark/NativeReference/numerel_oracle.c' \
  -I/home/ubuntu/projects/numerel \
  -lm \
  -o /tmp/numerel-oracle

/tmp/numerel-oracle
```

The native harness emits:

- cube-root compression boundary vectors;
- `NumerelGrayScramble` schedules;
- non-looping and looping encode/decode sequences;
- packet-loss vectors using `NumerelApplyDelta`;
- an exhaustive FNV-1a checksum for all signed 12-bit differences.

The expected exhaustive 12-bit checksum is:

```text
b0e90ea47c60370f
```

`BasisNumerelReferenceTests` reproduces the same checksum and known-answer vectors.

## Comparison findings

### Correct upstream behavior

The upstream Gray bit is generated from the transmitter's reconstructed remote estimate:

```c
NumerelToGreyCode(tx_estimate)
```

It is not generated from the original source value. The Basis `Reference` tuning preserves this behavior.

The upstream bitstream is equivalent to:

1. `compBits - 1` zero prefix bits;
2. the signed compressed delta code, including its leading one;
3. one Gray-code estimate bit.

The total scalar length is always `compBits * 2` bits.

### Differences found in the original Basis POC

1. **Cube-root behavior was not wire-compatible.**
   The original C# POC used an integer floor cube root. Upstream uses the literal expression:

   ```c
   ((int)(pow(v, 0.3333333) + 0.4))
   ```

   Revision `8676848` changed this from plain truncation (after briefly trying `+0.5`) to `+0.4` to fix a non-converging differential-compression case. The new mapping is intentionally neither floor nor ordinary nearest rounding: for example `7` and `8` compress to `2`, `26` and `27` to `3`, and `999`/`1000` to `10`.

2. **`NumerelGrayScramble` was missing.**
   The POC advanced Gray bits linearly with `sequence % bits`. The port now uses the upstream alternating high/low schedule.

3. **`NumerelApplyDelta` was missing.**
   Upstream reapplies the last decoded delta once for each missing sample. The port exposes `BasisNumerel.ApplyLastDelta`, and the armature decoder applies it per scalar for every sequence gap.

4. **Decoder state was only partially transactional.**
   Truncated hybrid packets could alter bone-validity or held-pose state even when scalar state was rolled back. Scalar state, pose state, and validity state are now all committed only after the complete packet validates.

5. **Zero bits were not explicitly cleared.**
   The original bit writer assumed a zeroed destination buffer. The port now safely overwrites both zero and one bits.

6. **Reference transmitter state was incorrectly clamped in non-looping mode.**
   The new rounded cube can overshoot the nominal scalar range. Upstream keeps `numerel_tx.remote_estimate` as the raw unsigned reconstructed estimate and clamps only on the receiver side. The Basis reference mode now preserves that transmitter overshoot so native/C# bitstreams remain identical.

## Port modes

`BasisNumerel.Tuning.Reference` is the upstream-compatible mode:

- one Gray bit;
- upstream `((int)(pow(v, 0.3333333) + 0.4))` compression;
- quarter-step output filtering;
- Gray bit sourced from the reconstructed estimate.

The following are Basis experiments and are not upstream wire-compatible:

- deterministic floor cube root;
- deterministic nearest cube root;
- square-root delta compression with signed-square reconstruction (`sqrt+0.4` and nearest-square variants);
- multiple Gray bits;
- output snapping instead of quarter filtering;
- Gray correction sourced from the original value;
- per-bone absolute escapes;
- rotating per-bone refresh records;
- Quaternion-4 armature coding with optional q/-q temporal sign continuity and component precision offsets.

### Quaternion-4 experiment

Upstream's current GUI demo also experiments with treating quaternion x/y/z/w as four independent Numerel numbers and normalizing after decode. `BasisNumerelQuaternion4ArmatureCodec` implements that idea without changing the production Basis payload representation: it decodes each captured smallest-three bone to a unit quaternion, Numerel-codes four components, normalizes the decoded four-vector, and repacks it to the original Basis smallest-three format for apples-to-apples angular-error measurement.

The Basis experiment adds optional temporal sign continuity. Since q and -q represent the same rotation, the encoder negates the current source quaternion when its dot product with the previous source representation is negative. This avoids turning a harmless smallest-three representation sign flip into four large Numerel deltas.

A fixed-16-bit power-curve experiment now keeps the quaternion component domain constant and moves LOD/quality into the nonlinear delta curve instead of changing component BPC. Benchmark modes are power 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, and 5.0. Power 1.0 is an exact delta curve. Fractional powers are deterministic integer mappings (`round(sqrt(k^3))` and `round(sqrt(k^5))`) with 16-bit lookup tables on the common path, avoiding cross-platform floating-point `pow` in the wire mapping. The encoder records actual bits consumed by each of the 204 quaternion scalars so the corpus benchmark can report mean/p50/p95/p99 scalar bit cost and the percentage above 6/8/12 bits.

Benchmark variants include the upstream-style same-BPC mapping, sign-continuous mapping, component precision offsets of -2, -1, +1, and +2 bits relative to each bone's existing Basis BPC, and an adaptive profile that uses +2 bits on coarse <=6-BPC bones and +1 bit on the rest. These are benchmark modes only and are not selected by the live armature protocol.

Initial ARM64 synthetic High-quality results show why precision must be benchmarked rather than assumed: same-BPC Quaternion-4 is about 130.1 framed B/frame but has 4.875-degree idle p95, while +1 reaches about 132.3 B/frame and 0.069-degree idle p95. The adaptive profile is about 133.2 B/frame with 0.079-degree idle p95, 5.03-degree active no-loss p95, and 5.30-degree burst no-loss p95. A fixed 12-bit/component mode is also included to match the upstream author's suggested starting point; its four self-delimiting Numerel codes are concatenated directly with no per-component length or byte alignment. It measured about 136.8 B/frame and 0.148-degree idle p95, 179.1 B/frame and 2.18-degree active no-loss p95, and 185.1 B/frame and 2.71-degree burst no-loss p95. With the experimental square-root Numerel mapping, the same 12-bit Quaternion-4 stream measured roughly 136.0 B/frame / 0.056-degree idle p95, 193.4 B/frame / 0.39-degree active p95, and 203.3 B/frame / 0.47-degree burst p95. Sign continuity is most visible around smallest-three representation flips and materially reduces some catastrophic loss maxima, but Quaternion-4 loss recovery remains worse than the exact V3.1 direction. These numbers are synthetic screening results; the cleaned Windows Humanoid corpus is required before making a design decision.

## Validation

Completed validation:

- 54 focused Numerel, square-root/power-curve, Quaternion-4, Hybrid V2, and V3/V3.1 tests passed;
- native oracle checksum updated to `b0e90ea47c60370f` for revision `8676848`;
- native non-looping bitstream vectors passed;
- native looping bitstream vectors passed;
- native loss and `NumerelApplyDelta` state vectors passed;
- exhaustive signed 12-bit difference checksum passed;
- reused nonzero destination-buffer test passed;
- truncated scalar and armature packet rollback tests passed;
- `BasisNetworkCore` built for `net10.0` and `netstandard2.1`;
- full server suite: 1,300 passed / 3 pre-existing unrelated failures;
- benchmark encode and decode loops allocated zero bytes;
- Numerel and Quaternion-4 loss metrics now score the held/displayed pose on every offered frame, matching V3 rather than omitting dropped or rejected frames.

## Corrected benchmark result

Benchmark configuration:

- 1,200 frames per scenario;
- 20 Hz;
- 51 smallest-three bone rotations;
- absolute position, scale, hips, and end-effector tail;
- synthetic static, idle, active, and burst motion;
- packet loss and packet reordering scenarios;
- late receiver joining at frame 200.

For High-quality idle motion with upstream revision `8676848`:

| Codec | Loss | Framed bytes/frame | Steady p95 angular error | Late join stable under 1 degree |
|---|---:|---:|---:|---:|
| Current keyframe + delta | 0% | 198.8 | exact packed payload | immediate keyframe path |
| Upstream Numerel reference | 0% | 130.4 | 0.105 degrees | 900 ms |
| Upstream Numerel reference | 10% | 130.4 | 0.790 degrees | 2,850 ms |
| Square-root +0.4 experiment | 0% | 131.6 | 0.040 degrees | 900 ms |
| Square-root +0.4 experiment | 10% | 131.6 | 0.951 degrees | 2,100 ms |
| Nearest-square experiment | 10% | 131.6 | 0.432 degrees | 2,900 ms |
| Basis nearest, one Gray bit | 0% | 130.4 | 0.105 degrees | 1,050 ms |

The upstream `+0.4` change materially improves the reference mode compared with revision `ea184345`: idle no-loss p95 falls from about 0.231 degrees to 0.105 degrees in this synthetic corpus. An experimental square-root mapping improves the accuracy/bandwidth trade further: `sqrt(abs(delta))+0.4` with signed-square reconstruction measured about 131.6 B/frame and 0.040-degree idle p95, versus 130.4 B/frame and 0.105 degrees for upstream cube-root Numerel. On High/Active it measured about 149.5 B/frame / 4.84-degree p95 versus 144.9 / 12.88, and on High/Burst about 153.1 / 5.03 versus 146.8 / 13.34. At 10% synthetic loss it also improved average p95 in those scenarios, but still produced large worst-case errors, so it remains a benchmark-only experiment rather than a production recommendation.

CPU on the ARM64 benchmark host remained allocation-free. High/Idle reference cube-root mode measured about 11.0 microseconds encode / 3.3 microseconds decode per frame, while the `sqrt+0.4` experiment measured about 6.7 microseconds encode / 3.5 microseconds decode.

## Important limitations

### Active-motion accuracy

Pure upstream cube-root Numerel is not currently acceptable as the only active-motion armature representation. Revision `8676848` improves convergence, but the latest synthetic High-quality run still measured:

- High/Active, no loss: about 12.88-degree steady p95 for upstream reference mode;
- High/Burst, no loss: about 13.34-degree steady p95 for upstream reference mode.

The square-root experiment materially improves those no-loss figures to about 4.84 degrees Active and 5.03 degrees Burst, at roughly 3.2% and 4.4% more framed bandwidth respectively. It still develops large displayed-pose errors under loss (about 12.9-degree Active and 22.3-degree Burst p95 at the benchmark's 10% loss/reorder scenario), so the improvement does not replace V3's exact local-recovery direction.

The upstream repository also added a GUI experiment that represents a quaternion as four independently synchronized components and normalizes the result. This is not a `numerel.h` wire/API change. Basis continues using its existing smallest-three quaternion representation; adopting four Numerel quaternion scalars would require a separate bandwidth/accuracy study.

The first-generation per-bone absolute/refresh hybrid reduces no-loss active-motion error, but its global hold-after-gap policy can keep large moving bones stale for too long. Hybrid V2 replaces that behavior with bounded per-bone prediction age, uses exact temporal deltas for low-BPC fingers/toes, and reserves nearest-cube Numerel for higher-BPC body bones.

With a 12-bone rotating refresh, the synthetic High-quality sanity matrix measured:

| Motion | Current keyframe + delta | Hybrid V2 | Hybrid V2 no-loss p95 | Hybrid V2 10% loss p95 |
|---|---:|---:|---:|---:|
| Idle | 198.8 B/frame | 153.6 B/frame | 0.097 degrees | 0.105 degrees |
| Active | 232.0 B/frame | 190.5 B/frame | 0.384 degrees | 5.027 degrees |
| Burst | 233.7 B/frame | 197.1 B/frame | 0.363 degrees | 9.461 degrees |

These results are a synthetic sanity check only. The Windows Humanoid clip matrix subsequently showed that the existing exact Basis delta codec is substantially smaller than Hybrid V2 on sustained real animation, while V2 still develops large recovery errors under packet loss. Numerel is therefore retained as a reference/experiment rather than the preferred production armature codec.

### Hybrid V3.2 exact distributed recovery

`BasisAvatarDeltaRecoveryV3` keeps the exact Basis dirty-field representation and layers targeted recovery over rotating baseline groups:

- the 57 avatar fields remain deterministically balanced across eight baseline groups;
- V3.2 defaults to an 8-frame refresh cycle plus up to four targeted repair groups per frame; V3.1's cycle12/r4 mode remains available as the lower-overhead comparison;
- a coordinated sequence-zero stream start can bootstrap all eight groups, guaranteeing byte-exact output from frame zero after a matching codec reset;
- missing scheduled refreshes invalidate only the affected groups;
- invalid groups still apply dirty absolute fields immediately; only omitted fields remain held;
- `Decoder.MissingGroupMask` exposes the exact eight-bit set that needs baseline repair;
- a sender can append a second ordinary Basis delta body containing up to four requested baseline groups per frame;
- the repair body contains the sender's existing baseline and does **not** mutate the shared sender baseline, so a lost repair cannot desynchronize other receivers;
- a late join requesting all missing groups can therefore rebuild baseline state in at most two delivered repair packets with the default four-groups-per-frame cap;
- scheduled refresh remains a fallback when recovery requests or repair packets are lost;
- malformed/partial repair groups, stale packets, and truncation are rejected transactionally.

The implicit sequence schedule still requires coordinated sender/receiver lifecycle reset semantics. A sequence reset without a codec reset is ambiguous.

Latest synthetic High-quality sanity results for the V3.2 cycle8/r4 default:

| Motion | V3.2 no-loss B/frame | No-loss display p95 | V3.2 10% loss B/frame | 10% display p95 | Late join stable under 1 degree |
|---|---:|---:|---:|---:|---:|
| Idle | 198.0 | 0.000 degrees | 202.8 | 0.000 degrees | ~200 ms |
| Active | 238.5 | 0.000 degrees | 243.5 | ~4.96 degrees | ~50 ms |
| Burst | 239.3 | 0.000 degrees | 243.4 | ~8.31 degrees | immediate in this synthetic case |

The cycle8/r4 default was selected because the cleaned Windows Humanoid V3.1 report showed the old cycle8 schedule was dramatically stronger against isolated/periodic loss, while targeted repair approximately halved the previous Burst5/Burst10 recovery error. A cycle8/10/12 × repair2/repair4 synthetic sweep found repair4 adds negligible healthy traffic and improves convergence/max-error behavior, making cycle8/r4 the best next real-corpus candidate.

The request-enabled synthetic benchmark models an immediate reliable request opportunity on subsequent outbound frames; return-path request loss/delay is not yet modeled. Also, burst-window displayed error cannot be repaired retroactively: while packets are physically absent the viewer must still hold/interpolate some older pose. The Windows humanoid rerun should therefore add a **post-burst recovery-time/error** metric in addition to p95 over the burst itself.

V3.2 is not wired into the live avatar protocol yet. The optional repair body needs explicit live framing so it cannot be confused with trailing additional-avatar data, and a production fanout policy must decide whether repair requests produce per-receiver packets or a union repair mask. Required validation remains Windows humanoid V3.1, Mono/IL2CPP, temporary server, relay/P2P, reconnect, sequence-reset, capability negotiation, and request-loss testing.

### Cross-platform determinism

The upstream-compatible mode intentionally preserves the literal `((int)(pow(v, 0.3333333) + 0.4))` expression. The native oracle and C# port match exhaustively for all signed 12-bit differences on the Linux ARM64 development host, but `pow` is supplied by the platform math runtime. Before enabling this as a network wire format, the same known-answer vectors must pass on every supported sender platform, including Windows, Linux, macOS, Android, Mono, IL2CPP, and the dedicated server runtime. If any platform differs at a boundary, the production protocol should use a fixed lookup table or a precisely specified integer mapping instead of platform `pow`.

## Integration status

The verified scalar port, experimental Numerel armature codec, and exact V3 distributed-recovery codec are available to both server and Unity client source trees. They are not yet selected by the live Basis network protocol. Production integration still requires:

- explicit protocol or capability negotiation;
- per-avatar stream lifecycle and reset rules;
- sequence-gap handling shared by server, normal client, and P2P paths;
- a production decision on absolute refresh/recovery behavior;
- real server/client load and visual-motion testing;
- a network-version transition or backward-compatible mode bit.
