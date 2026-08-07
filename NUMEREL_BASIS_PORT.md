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
- multiple Gray bits;
- output snapping instead of quarter filtering;
- Gray correction sourced from the original value;
- per-bone absolute escapes;
- rotating per-bone refresh records.

## Validation

Completed validation:

- 33 focused Numerel, Hybrid V2, and V3/V3.1 tests passed;
- native oracle checksum updated to `b0e90ea47c60370f` for revision `8676848`;
- native non-looping bitstream vectors passed;
- native looping bitstream vectors passed;
- native loss and `NumerelApplyDelta` state vectors passed;
- exhaustive signed 12-bit difference checksum passed;
- reused nonzero destination-buffer test passed;
- truncated scalar and armature packet rollback tests passed;
- `BasisNetworkCore` built for `net10.0` and `netstandard2.1`;
- full server suite: 1,287 passed / 3 pre-existing unrelated failures;
- benchmark encode and decode loops allocated zero bytes.

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
| Upstream Numerel reference | 10% | 130.4 | 0.676 degrees | 2,850 ms |
| Basis nearest, one Gray bit | 0% | 130.4 | 0.105 degrees | 1,050 ms |

The upstream `+0.4` change materially improves the reference mode compared with revision `ea184345`: idle no-loss p95 falls from about 0.231 degrees to 0.105 degrees in this synthetic corpus. It does not make pure Numerel suitable as the primary armature codec.

CPU on the ARM64 benchmark host for High/Idle reference mode remained allocation-free and measured roughly 10 microseconds encode / 3.3 microseconds decode per frame in this run.

## Important limitations

### Active-motion accuracy

Pure Numerel is not currently acceptable as the only active-motion armature representation. Revision `8676848` improves convergence, but the latest synthetic High-quality run still measured:

- High/Active, no loss: about 12.88-degree steady p95 for upstream reference mode;
- High/Burst, no loss: about 13.34-degree steady p95 for upstream reference mode.

The upstream repository also added a GUI experiment that represents a quaternion as four independently synchronized components and normalizes the result. This is not a `numerel.h` wire/API change. Basis continues using its existing smallest-three quaternion representation; adopting four Numerel quaternion scalars would require a separate bandwidth/accuracy study.

The first-generation per-bone absolute/refresh hybrid reduces no-loss active-motion error, but its global hold-after-gap policy can keep large moving bones stale for too long. Hybrid V2 replaces that behavior with bounded per-bone prediction age, uses exact temporal deltas for low-BPC fingers/toes, and reserves nearest-cube Numerel for higher-BPC body bones.

With a 12-bone rotating refresh, the synthetic High-quality sanity matrix measured:

| Motion | Current keyframe + delta | Hybrid V2 | Hybrid V2 no-loss p95 | Hybrid V2 10% loss p95 |
|---|---:|---:|---:|---:|
| Idle | 198.8 B/frame | 153.6 B/frame | 0.097 degrees | 0.105 degrees |
| Active | 232.0 B/frame | 190.5 B/frame | 0.384 degrees | 5.027 degrees |
| Burst | 233.7 B/frame | 197.1 B/frame | 0.363 degrees | 9.461 degrees |

These results are a synthetic sanity check only. The Windows Humanoid clip matrix subsequently showed that the existing exact Basis delta codec is substantially smaller than Hybrid V2 on sustained real animation, while V2 still develops large recovery errors under packet loss. Numerel is therefore retained as a reference/experiment rather than the preferred production armature codec.

### Hybrid V3.1 exact distributed recovery

`BasisAvatarDeltaRecoveryV3` keeps the exact Basis dirty-field representation and now adds V3.1 recovery semantics:

- the 57 avatar fields remain deterministically balanced across eight baseline groups;
- healthy steady state defaults to the lower-overhead 12-frame distributed refresh cycle;
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

Latest synthetic High-quality sanity results for V3.1:

| Motion | V3.1 no-loss B/frame | No-loss display p95 | V3.1 10% loss B/frame | 10% display p95 | Late join stable under 1 degree |
|---|---:|---:|---:|---:|---:|
| Idle | 205.0 | 0.000 degrees | 207.9 | 0.000 degrees | ~100 ms |
| Active | 238.8 | 0.000 degrees | 242.2 | ~4.95 degrees | ~50 ms |
| Burst | 239.4 | 0.000 degrees | 242.5 | ~7.11 degrees | ~100 ms |

The request-enabled synthetic benchmark models an immediate reliable request opportunity on subsequent outbound frames; return-path request loss/delay is not yet modeled. Also, burst-window displayed error cannot be repaired retroactively: while packets are physically absent the viewer must still hold/interpolate some older pose. The Windows humanoid rerun should therefore add a **post-burst recovery-time/error** metric in addition to p95 over the burst itself.

V3.1 is not wired into the live avatar protocol yet. The optional repair body needs explicit live framing so it cannot be confused with trailing additional-avatar data, and a production fanout policy must decide whether repair requests produce per-receiver packets or a union repair mask. Required validation remains Windows humanoid V3.1, Mono/IL2CPP, temporary server, relay/P2P, reconnect, sequence-reset, capability negotiation, and request-loss testing.

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
