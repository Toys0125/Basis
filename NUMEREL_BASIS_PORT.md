# Numerel Basis Port and Native Comparison

## Upstream reference

- Repository: `https://codeberg.org/cnlohr/numerel`
- Compared revision: `ea184345c109ef1915b1dfe6603d5b188bca8e4e`
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
56c42c8cc4f31f27
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
   pow(v, 0.3333333)
   ```

   This differs at exact cubes and nearby boundaries. For example, upstream maps `8` to compressed value `1`, not `2`; `27` to `2`, not `3`; and `512` to `7`, not `8`.

2. **`NumerelGrayScramble` was missing.**
   The POC advanced Gray bits linearly with `sequence % bits`. The port now uses the upstream alternating high/low schedule.

3. **`NumerelApplyDelta` was missing.**
   Upstream reapplies the last decoded delta once for each missing sample. The port exposes `BasisNumerel.ApplyLastDelta`, and the armature decoder applies it per scalar for every sequence gap.

4. **Decoder state was only partially transactional.**
   Truncated hybrid packets could alter bone-validity or held-pose state even when scalar state was rolled back. Scalar state, pose state, and validity state are now all committed only after the complete packet validates.

5. **Zero bits were not explicitly cleared.**
   The original bit writer assumed a zeroed destination buffer. The port now safely overwrites both zero and one bits.

## Port modes

`BasisNumerel.Tuning.Reference` is the upstream-compatible mode:

- one Gray bit;
- upstream `pow(v, 0.3333333)` compression;
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

- 16 focused Numerel and armature tests passed;
- native non-looping bitstream vectors passed;
- native looping bitstream vectors passed;
- native loss and `NumerelApplyDelta` state vectors passed;
- exhaustive signed 12-bit difference checksum passed;
- reused nonzero destination-buffer test passed;
- truncated scalar and armature packet rollback tests passed;
- `BasisNetworkCore` built for `net10.0` and `netstandard2.1`;
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

For High-quality idle motion:

| Codec | Loss | Framed bytes/frame | 20 Hz bandwidth | Steady p95 angular error | Late join stable under 1 degree |
|---|---:|---:|---:|---:|---:|
| Current keyframe + delta | 0% | 198.8 | 3,976 B/s | lossless relative to packed payload | immediate keyframe path |
| Upstream Numerel reference | 0% | 130.63 | 2,612.6 B/s | 0.231 degrees | 900 ms |
| Upstream Numerel reference | 10% | 130.63 | 2,612.6 B/s | 0.673 degrees | 1,400 ms |
| Basis nearest, one Gray bit | 0% | 130.38 | 2,607.6 B/s | 0.105 degrees | 1,050 ms |
| Basis two-Gray-bit POC | 10% | 149.64 | 2,992.8 B/s | 0.319 degrees | 1,000 ms |

The upstream reference reduced High/Idle framed bandwidth by about 34.3% versus the current synthetic keyframe-plus-delta baseline.

CPU on the ARM64 benchmark host for High/Idle reference mode:

- encode: approximately 9.69 microseconds per frame;
- decode: approximately 3.18 microseconds per frame;
- encode allocations: 0 bytes;
- decode allocations: 0 bytes.

## Important limitations

### Active-motion accuracy

Pure Numerel is not currently acceptable as the only active-motion armature representation. In this synthetic benchmark, High-quality active and burst motion produced large angular error because cube-root delta reconstruction is intentionally lossy:

- High/Active, no loss: 17.44-degree steady p95 for upstream reference mode;
- High/Burst, no loss: 25.82-degree steady p95 for upstream reference mode.

The per-bone absolute/refresh hybrid reduces no-loss active-motion error, but its current loss concealment policy can hold stale bones for too long after a gap. It remains an experiment and must not replace the production keyframe/delta protocol yet.

### Cross-platform determinism

The upstream-compatible mode intentionally preserves the literal `pow(v, 0.3333333)` expression. The native oracle and C# port match exhaustively for all signed 12-bit differences on the Linux ARM64 development host, but `pow` is supplied by the platform math runtime. Before enabling this as a network wire format, the same known-answer vectors must pass on every supported sender platform, including Windows, Linux, macOS, Android, Mono, IL2CPP, and the dedicated server runtime. If any platform differs at a boundary, the production protocol should use a fixed lookup table or a precisely specified integer mapping instead of platform `pow`.

## Integration status

The verified scalar port and experimental armature codec are available to both server and Unity client source trees. They are not yet selected by the live Basis network protocol. Production integration still requires:

- explicit protocol or capability negotiation;
- per-avatar stream lifecycle and reset rules;
- sequence-gap handling shared by server, normal client, and P2P paths;
- a production decision on absolute refresh/recovery behavior;
- real server/client load and visual-motion testing;
- a network-version transition or backward-compatible mode bit.
