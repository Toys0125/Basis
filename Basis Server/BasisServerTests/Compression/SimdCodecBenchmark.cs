using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;
using Xunit.Abstractions;

namespace BasisServerTests.Compression
{
    /// <summary>
    /// Measures each of the three per-sender codec changes against the code it replaced, so the
    /// speedups are numbers rather than claims.
    ///
    /// <para>The "before" implementations are copied verbatim into this file rather than referenced,
    /// because production no longer contains them. That is deliberate: both arms then run on the same
    /// data, in the same process, against the same JIT, and the comparison is a ratio rather than a
    /// number that has to be trusted across machines.</para>
    ///
    /// <para><b>What this does not measure.</b> Only the operation. Whether that operation matters is
    /// a question for the BSR profiler under real load — this codebase has a standing lesson that a
    /// 15x reduction in a hot loop's iteration count measured exactly nothing at the process level,
    /// so a microbenchmark is evidence that a change did what it intended, not that the server got
    /// faster. Deliberately short (~1 s total) so it can live in the suite.</para>
    ///
    /// <para><b>Nothing here asserts a timing.</b> The printed ratios are only trustworthy when this
    /// class has the machine to itself; run it with
    /// <c>--filter FullyQualifiedName~SimdCodecBenchmark</c>. In a full-suite run the arms compete
    /// with every other test for cores and the ratios move by more than the effects being measured.</para>
    /// </summary>
    public class SimdCodecBenchmark
    {
        private readonly ITestOutputHelper _out;
        public SimdCodecBenchmark(ITestOutputHelper output) => _out = output;

        private const int Rounds = 5;

        // ────────────────────────────────────────────────────────────
        //  "Before" implementations, as they stood prior to this work.
        // ────────────────────────────────────────────────────────────

        private static ulong LegacyReadBits(byte[] src, int bitPos, int bitCount)
        {
            int bytePos = bitPos >> 3;
            int bitInByte = bitPos & 7;
            ulong outV = 0;
            int outShift = 0;
            int bitsLeft = bitCount;

            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;
                ulong maskVal = (1UL << take) - 1UL;
                ulong chunk = ((ulong)src[bytePos] >> bitInByte) & maskVal;
                outV |= chunk << outShift;
                outShift += take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }
            return outV;
        }

        private static void LegacyWriteBits(byte[] dst, int bitPos, ulong value, int bitCount)
        {
            int bytePos = bitPos >> 3;
            int bitInByte = bitPos & 7;
            int bitsLeft = bitCount;

            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;
                byte chunk = (byte)(value & ((1UL << take) - 1UL));
                dst[bytePos] = (byte)(dst[bytePos] | (chunk << bitInByte));
                value >>= take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }
        }

        private static uint LegacyRescaleQuant(uint qSrc, int bSrc, int bDst)
        {
            if (bSrc == bDst) return qSrc;
            if (bDst <= 0) return 0;
            ulong maxSrc = ((ulong)1 << bSrc) - 1UL;
            ulong maxDst = ((ulong)1 << bDst) - 1UL;
            ulong num = (ulong)qSrc * maxDst + (maxSrc >> 1);
            return (uint)(num / maxSrc);
        }

        // ────────────────────────────────────────────────────────────

        private static double MedianNsPerOp(Func<long> run, long opsPerRun)
        {
            // Run the complete measurement body once before the clock starts. Besides basic JIT
            // startup this gives tiered compilation enough traffic to promote the hot methods.
            long warmup = run();
            if (warmup == long.MinValue) throw new InvalidOperationException();

            var samples = new double[Rounds];
            for (int r = 0; r < Rounds; r++)
            {
                var sw = Stopwatch.StartNew();
                long sink = run();
                sw.Stop();
                // Keep the result live so nothing above is dead-code eliminated.
                if (sink == long.MinValue) throw new InvalidOperationException();
                samples[r] = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / opsPerRun;
            }
            Array.Sort(samples);
            return samples[Rounds / 2];
        }

        private void Report(string label, double beforeNs, double afterNs)
        {
            _out.WriteLine($"  {label,-34} {beforeNs,8:F2} ns -> {afterNs,8:F2} ns   {beforeNs / afterNs,5:F2}x");
        }

        private void ReportCandidate(string label, double baselineNs, double candidateNs)
        {
            double ratio = candidateNs / baselineNs;
            double change = (ratio - 1.0) * 100.0;
            string changeText = $"{change:+0.00;-0.00;0.00}%";
            _out.WriteLine($"  {label,-38} baseline {baselineNs,8:F2} ns  candidate {candidateNs,8:F2} ns  ratio {ratio,6:F3}x  change {changeText,8}");
        }

        [Fact]
        public void BitCodecIsFasterThanTheByteWalkItReplaced()
        {
            const int Iterations = 200_000;

            var layout = BasisAvatarChannelMap.For(BasisAvatarBitPacking.BitQuality.High);
            var channels = layout.Channels;
            var payload = new byte[layout.PayloadBytes];
            new Random(11).NextBytes(payload);
            int n = channels.Length;

            _out.WriteLine($"[SIMD] {BasisSimdCapabilities.Describe()}");
            _out.WriteLine($"Bit field access, High layout: {n} channels x {Iterations} iterations");

            double before = MedianNsPerOp(() =>
            {
                ulong acc = 0;
                for (int it = 0; it < Iterations; it++)
                    for (int c = 0; c < n; c++)
                        acc += LegacyReadBits(payload, channels[c].BitOffset, channels[c].Width);
                return (long)acc;
            }, (long)Iterations * n);

            double after = MedianNsPerOp(() =>
            {
                ulong acc = 0;
                for (int it = 0; it < Iterations; it++)
                    for (int c = 0; c < n; c++)
                        acc += BasisBitCodec.Read(payload, channels[c].BitOffset, channels[c].Width);
                return (long)acc;
            }, (long)Iterations * n);

            Report("read a channel", before, after);

            var scratchLegacy = new byte[layout.PayloadBytes];
            var scratchNew = new byte[layout.PayloadBytes];

            double writeBefore = MedianNsPerOp(() =>
            {
                for (int it = 0; it < Iterations; it++)
                {
                    Array.Clear(scratchLegacy, 0, scratchLegacy.Length);
                    for (int c = 0; c < n; c++)
                        LegacyWriteBits(scratchLegacy, channels[c].BitOffset, (ulong)c, channels[c].Width);
                }
                return scratchLegacy[0];
            }, (long)Iterations * n);

            double writeAfter = MedianNsPerOp(() =>
            {
                for (int it = 0; it < Iterations; it++)
                {
                    Array.Clear(scratchNew, 0, scratchNew.Length);
                    for (int c = 0; c < n; c++)
                        BasisBitCodec.Or(scratchNew, channels[c].BitOffset, (ulong)c, channels[c].Width);
                }
                return scratchNew[0];
            }, (long)Iterations * n);

            Report("write a channel (incl. clear)", writeBefore, writeAfter);

            // Correctness, not speed. Timing is NOT asserted anywhere in this class: the suite runs
            // its tests against a machine that is also running the rest of the suite, and a ratio
            // measured under that contention swings either way — an earlier version of this assert
            // went red on a 0.92x reading of a change that measures 1.18x when it has the box to
            // itself. A benchmark that can fail on load is a flaky test, and this codebase already
            // pays for enough of those. Read the printed ratios; run the class alone to trust them.
            Assert.Equal(
                LegacyReadBits(payload, channels[0].BitOffset, channels[0].Width),
                BasisBitCodec.Read(payload, channels[0].BitOffset, channels[0].Width));
        }

        [Fact]
        public void DirtyMaskPrefilterSkipsTheUnchangedFields()
        {
            const int Iterations = 200_000;

            var layout = BasisAvatarChannelMap.For(BasisAvatarBitPacking.BitQuality.High);
            int payloadBytes = layout.PayloadBytes;
            int fieldCount = layout.FieldCount;

            var keyframe = new byte[payloadBytes];
            new Random(23).NextBytes(keyframe);

            _out.WriteLine($"[SIMD] {BasisSimdCapabilities.Describe()}");
            _out.WriteLine($"Dirty-mask scan, High layout: {fieldCount} fields, {payloadBytes} B payload");

            // Three points on the curve the prefilter actually lives on: a still player (the case
            // delta compression exists for), light motion, and a full-body move where the prefilter
            // can only add cost.
            foreach (int movedFields in new[] { 0, 3, fieldCount })
            {
                var current = (byte[])keyframe.Clone();
                var rng = new Random(31 + movedFields);
                for (int f = 0; f < movedFields; f++)
                {
                    int c = layout.FieldChannelStart(f);
                    if (c >= layout.FieldChannelEnd(f)) continue;
                    var ch = layout.Channels[c];
                    uint v = (uint)rng.Next() & ch.Mask;
                    BasisBitCodec.Replace(current, ch.BitOffset, v ^ 1u, ch.Width);
                }

                double before = MedianNsPerOp(() =>
                {
                    long dirty = 0;
                    for (int it = 0; it < Iterations; it++)
                    {
                        for (int f = 0; f < fieldCount; f++)
                        {
                            for (int c = layout.FieldChannelStart(f); c < layout.FieldChannelEnd(f); c++)
                            {
                                var ch = layout.Channels[c];
                                if (BasisBitCodec.Read(current, ch.BitOffset, ch.Width)
                                    != BasisBitCodec.Read(keyframe, ch.BitOffset, ch.Width))
                                {
                                    dirty++;
                                    break;
                                }
                            }
                        }
                    }
                    return dirty;
                }, Iterations);

                double after = MedianNsPerOp(() =>
                {
                    long dirty = 0;
                    for (int it = 0; it < Iterations; it++)
                    {
                        ulong words = BasisPayloadDiff.WordDiffMask(current, keyframe, payloadBytes);
                        for (int f = 0; f < fieldCount; f++)
                        {
                            if ((words & layout.FieldWordMask[f]) == 0) continue;
                            for (int c = layout.FieldChannelStart(f); c < layout.FieldChannelEnd(f); c++)
                            {
                                var ch = layout.Channels[c];
                                if (BasisBitCodec.Read(current, ch.BitOffset, ch.Width)
                                    != BasisBitCodec.Read(keyframe, ch.BitOffset, ch.Width))
                                {
                                    dirty++;
                                    break;
                                }
                            }
                        }
                    }
                    return dirty;
                }, Iterations);

                string shape = movedFields == 0 ? "still" : movedFields == fieldCount ? "everything moved" : $"{movedFields} fields moved";
                Report($"whole-payload scan ({shape})", before, after);
            }
        }

        [Fact]
        public void ReciprocalRescaleIsFasterThanTheDivide()
        {
            const int Iterations = 2_000_000;

            // The pairs the repacker actually runs: High 12-bit components down to each lower tier.
            var pairs = new (int src, int dst)[] { (12, 8), (12, 6), (12, 5), (5, 4), (13, 9), (13, 7) };

            _out.WriteLine($"Quantized rescale, {pairs.Length} width pairs x {Iterations} iterations");

            double before = MedianNsPerOp(() =>
            {
                uint acc = 0;
                for (int it = 0; it < Iterations; it++)
                {
                    var p = pairs[it % pairs.Length];
                    acc += LegacyRescaleQuant((uint)(it & ((1 << p.src) - 1)), p.src, p.dst);
                }
                return acc;
            }, Iterations);

            double after = MedianNsPerOp(() =>
            {
                uint acc = 0;
                for (int it = 0; it < Iterations; it++)
                {
                    var p = pairs[it % pairs.Length];
                    acc += QuantRescaleTable.Rescale((uint)(it & ((1 << p.src) - 1)), p.src, p.dst);
                }
                return acc;
            }, Iterations);

            Report("rescale one component", before, after);

            // Same reasoning as above: assert the result, print the ratio.
            foreach (var p in pairs)
            {
                Assert.Equal(LegacyRescaleQuant(1u, p.src, p.dst), QuantRescaleTable.Rescale(1u, p.src, p.dst));
            }
        }

        private enum DiffPattern
        {
            Unchanged,
            OneWord,
            Sparse,
            Quarter,
            Half,
            All,
            Clustered,
            Beginning,
            End
        }

        private static (byte[] current, byte[] baseline) MakeDiffPair(int length, DiffPattern pattern)
        {
            var baseline = new byte[length];
            new Random(1000 + length * 17 + (int)pattern).NextBytes(baseline);
            var current = (byte[])baseline.Clone();
            int wordCount = (length + 7) >> 3;
            if (wordCount == 0) return (current, baseline);

            void DirtyWord(int word)
            {
                int byteIndex = Math.Min(word * 8, length - 1);
                current[byteIndex] ^= 0x5A;
            }

            switch (pattern)
            {
                case DiffPattern.Unchanged:
                    break;
                case DiffPattern.OneWord:
                    DirtyWord(wordCount / 2);
                    break;
                case DiffPattern.Sparse:
                    for (int word = 0; word < wordCount; word += 4) DirtyWord(word);
                    break;
                case DiffPattern.Quarter:
                    for (int word = 0; word < wordCount; word++)
                        if ((word & 3) == 1) DirtyWord(word);
                    break;
                case DiffPattern.Half:
                    for (int word = 0; word < wordCount; word += 2) DirtyWord(word);
                    break;
                case DiffPattern.All:
                    for (int word = 0; word < wordCount; word++) DirtyWord(word);
                    break;
                case DiffPattern.Clustered:
                {
                    int count = Math.Max(1, wordCount / 3);
                    int start = Math.Max(0, (wordCount - count) / 2);
                    for (int word = start; word < start + count; word++) DirtyWord(word);
                    break;
                }
                case DiffPattern.Beginning:
                    DirtyWord(0);
                    break;
                case DiffPattern.End:
                    current[length - 1] ^= 0xA5;
                    break;
            }

            return (current, baseline);
        }

        [Fact]
        public void WordDiffMaskCandidatesAreCorrectAndBenchmarked()
        {
            var rng = new Random(9127);
            for (int trial = 0; trial < 10_000; trial++)
            {
                int length = rng.Next(0, 201);
                var current = new byte[length];
                var baseline = new byte[length];
                rng.NextBytes(current);
                current.CopyTo(baseline, 0);
                int flips = rng.Next(0, Math.Max(1, length / 4 + 1));
                for (int f = 0; f < flips && length != 0; f++)
                    current[rng.Next(length)] ^= (byte)(1 << rng.Next(8));

                ulong oracle = SimdCandidateKernels.ScalarWordDiffMask(current, baseline, length);
                Assert.Equal(oracle, BasisPayloadDiff.WordDiffMask(current, baseline, length));
                if (Avx2.IsSupported)
                {
                    Assert.Equal(oracle, SimdCandidateKernels.Avx2WordDiffMask(current, baseline, length));
                    Assert.Equal(oracle, SimdCandidateKernels.Avx2WordDiffMaskBranchless(current, baseline, length));
                    Assert.Equal(oracle, SimdCandidateKernels.HybridVectorAvx2WordDiffMask(current, baseline, length));
                }
                if (Avx2.IsSupported && Bmi2.IsSupported)
                    Assert.Equal(oracle, SimdCandidateKernels.Avx2WordDiffMaskPext(current, baseline, length));
            }

            _out.WriteLine($"[SIMD] {BasisSimdCapabilities.Describe()}");
            _out.WriteLine("Candidate 1: WordDiffMask; timings are ns per complete payload mask");

            const int iterations = 250_000;
            int[] realisticLengths = { 90, 128, 175 };
            int[] boundaryLengths = { 31, 32, 33, 63, 64, 65, 127, 129, 176 };
            DiffPattern[] allPatterns =
            {
                DiffPattern.Unchanged, DiffPattern.OneWord, DiffPattern.Sparse,
                DiffPattern.Quarter, DiffPattern.Half, DiffPattern.All,
                DiffPattern.Clustered, DiffPattern.Beginning, DiffPattern.End
            };
            DiffPattern[] boundaryPatterns =
            {
                DiffPattern.Unchanged, DiffPattern.OneWord, DiffPattern.All, DiffPattern.End
            };

            void RunScenario(int length, DiffPattern pattern)
            {
                var pair = MakeDiffPair(length, pattern);
                double currentNs = MedianNsPerOp(() =>
                {
                    ulong acc = 0;
                    for (int i = 0; i < iterations; i++)
                        acc ^= BasisPayloadDiff.WordDiffMask(pair.current, pair.baseline, length);
                    return (long)acc;
                }, iterations);

                double scalarNs = MedianNsPerOp(() =>
                {
                    ulong acc = 0;
                    for (int i = 0; i < iterations; i++)
                        acc ^= SimdCandidateKernels.ScalarWordDiffMask(pair.current, pair.baseline, length);
                    return (long)acc;
                }, iterations);

                _out.WriteLine($"WordDiffMask / {length} B / {pattern}: current {currentNs:F2} ns");
                ReportCandidate("scalar oracle", currentNs, scalarNs);

                if (Avx2.IsSupported)
                {
                    double avx2Ns = MedianNsPerOp(() =>
                    {
                        ulong acc = 0;
                        for (int i = 0; i < iterations; i++)
                            acc ^= SimdCandidateKernels.Avx2WordDiffMask(pair.current, pair.baseline, length);
                        return (long)acc;
                    }, iterations);
                    ReportCandidate("AVX2 movemask", currentNs, avx2Ns);

                    double branchlessNs = MedianNsPerOp(() =>
                    {
                        ulong acc = 0;
                        for (int i = 0; i < iterations; i++)
                            acc ^= SimdCandidateKernels.Avx2WordDiffMaskBranchless(pair.current, pair.baseline, length);
                        return (long)acc;
                    }, iterations);
                    ReportCandidate("AVX2 branchless collapse", currentNs, branchlessNs);

                    double hybridNs = MedianNsPerOp(() =>
                    {
                        ulong acc = 0;
                        for (int i = 0; i < iterations; i++)
                            acc ^= SimdCandidateKernels.HybridVectorAvx2WordDiffMask(pair.current, pair.baseline, length);
                        return (long)acc;
                    }, iterations);
                    ReportCandidate("Vector clean + AVX2 refine", currentNs, hybridNs);
                }

                if (Avx2.IsSupported && Bmi2.IsSupported)
                {
                    double pextNs = MedianNsPerOp(() =>
                    {
                        ulong acc = 0;
                        for (int i = 0; i < iterations; i++)
                            acc ^= SimdCandidateKernels.Avx2WordDiffMaskPext(pair.current, pair.baseline, length);
                        return (long)acc;
                    }, iterations);
                    ReportCandidate("AVX2 + BMI2 PEXT", currentNs, pextNs);
                }
            }

            foreach (int length in realisticLengths)
                foreach (DiffPattern pattern in allPatterns)
                    RunScenario(length, pattern);

            foreach (int length in boundaryLengths)
                foreach (DiffPattern pattern in boundaryPatterns)
                    RunScenario(length, pattern);
        }

        [Fact]
        public void BatchedBodyRescaleCandidatesAreCorrectAndBenchmarked()
        {
            var source = new uint[27];
            var scalar = new uint[27];
            var portable = new uint[27];
            var avx2 = new uint[27];

            foreach (int destinationBits in new[] { 8, 6, 5 })
            {
                for (int start = 0; start < 4096; start += 27)
                {
                    for (int i = 0; i < 27; i++) source[i] = (uint)Math.Min(4095, start + i);
                    SimdCandidateKernels.Rescale27Scalar(source, scalar, destinationBits);
                    SimdCandidateKernels.Rescale27PortableVector(source, portable, destinationBits);
                    Assert.Equal(scalar, portable);
                    if (Avx2.IsSupported)
                    {
                        SimdCandidateKernels.Rescale27Avx2(source, avx2, destinationBits);
                        Assert.Equal(scalar, avx2);
                    }
                }
            }

            var rng = new Random(6112);
            for (int i = 0; i < source.Length; i++) source[i] = (uint)rng.Next(4096);

            _out.WriteLine($"[SIMD] {BasisSimdCapabilities.Describe()}");
            _out.WriteLine("Candidate 2 isolated kernel: ns per 27-value 12-bit body rescale");
            const int kernelIterations = 500_000;
            foreach (int destinationBits in new[] { 8, 6, 5 })
            {
                double scalarNs = MedianNsPerOp(() =>
                {
                    long sink = 0;
                    for (int it = 0; it < kernelIterations; it++)
                    {
                        SimdCandidateKernels.Rescale27Scalar(source, scalar, destinationBits);
                        sink += scalar[it % 27];
                    }
                    return sink;
                }, kernelIterations);

                double portableNs = MedianNsPerOp(() =>
                {
                    long sink = 0;
                    for (int it = 0; it < kernelIterations; it++)
                    {
                        SimdCandidateKernels.Rescale27PortableVector(source, portable, destinationBits);
                        sink += portable[it % 27];
                    }
                    return sink;
                }, kernelIterations);

                _out.WriteLine($"Rescale27 / 12->{destinationBits}: scalar {scalarNs:F2} ns");
                ReportCandidate("portable Vector<uint>", scalarNs, portableNs);

                if (Avx2.IsSupported)
                {
                    double avx2Ns = MedianNsPerOp(() =>
                    {
                        long sink = 0;
                        for (int it = 0; it < kernelIterations; it++)
                        {
                            SimdCandidateKernels.Rescale27Avx2(source, avx2, destinationBits);
                            sink += avx2[it % 27];
                        }
                        return sink;
                    }, kernelIterations);
                    ReportCandidate("explicit AVX2", scalarNs, avx2Ns);
                }
            }

            // Full operation correctness: the benchmark-only repacker must stay byte-identical to
            // production before its timing is meaningful.
            for (int trial = 0; trial < 100; trial++)
            {
                byte[] highArray = DeltaTestSupport.MakeRealisticPayload(BasisAvatarBitPacking.BitQuality.High, rng);
                var high = new SerializableBasis.LocalAvatarSyncMessage
                {
                    array = highArray,
                    DataQualityLevel = (byte)BasisAvatarBitPacking.BitQuality.High
                };
                var expected = AvatarQualityRepacker.BuildAllLowerFromHigh(high);

                var med = new SerializableBasis.LocalAvatarSyncMessage();
                var low = new SerializableBasis.LocalAvatarSyncMessage();
                var vlow = new SerializableBasis.LocalAvatarSyncMessage();
                SimdCandidateKernels.BuildAllLowerFromHighIntoBatchedBody(
                    high, ref med, ref low, ref vlow, SimdCandidateKernels.BodyRescaleKernel.PortableVector);
                Assert.Equal(expected.medium.array, med.array);
                Assert.Equal(expected.low.array, low.array);
                Assert.Equal(expected.veryLow.array, vlow.array);

                if (Avx2.IsSupported)
                {
                    med = new SerializableBasis.LocalAvatarSyncMessage();
                    low = new SerializableBasis.LocalAvatarSyncMessage();
                    vlow = new SerializableBasis.LocalAvatarSyncMessage();
                    SimdCandidateKernels.BuildAllLowerFromHighIntoBatchedBody(
                        high, ref med, ref low, ref vlow, SimdCandidateKernels.BodyRescaleKernel.Avx2);
                    Assert.Equal(expected.medium.array, med.array);
                    Assert.Equal(expected.low.array, low.array);
                    Assert.Equal(expected.veryLow.array, vlow.array);
                }
            }

            byte[] benchmarkHighArray = DeltaTestSupport.MakeRealisticPayload(BasisAvatarBitPacking.BitQuality.High, rng);
            var benchmarkHigh = new SerializableBasis.LocalAvatarSyncMessage
            {
                array = benchmarkHighArray,
                DataQualityLevel = (byte)BasisAvatarBitPacking.BitQuality.High
            };
            var baselineMed = new SerializableBasis.LocalAvatarSyncMessage();
            var baselineLow = new SerializableBasis.LocalAvatarSyncMessage();
            var baselineVlow = new SerializableBasis.LocalAvatarSyncMessage();
            var portableMed = new SerializableBasis.LocalAvatarSyncMessage();
            var portableLow = new SerializableBasis.LocalAvatarSyncMessage();
            var portableVlow = new SerializableBasis.LocalAvatarSyncMessage();

            // Allocate destination buffers before timing, matching the steady-state server path.
            AvatarQualityRepacker.BuildAllLowerFromHighInto(
                benchmarkHigh, ref baselineMed, ref baselineLow, ref baselineVlow);
            SimdCandidateKernels.BuildAllLowerFromHighIntoBatchedBody(
                benchmarkHigh, ref portableMed, ref portableLow, ref portableVlow,
                SimdCandidateKernels.BodyRescaleKernel.PortableVector);

            const int repackIterations = 100_000;
            double baselineRepackNs = MedianNsPerOp(() =>
            {
                long sink = 0;
                for (int it = 0; it < repackIterations; it++)
                {
                    AvatarQualityRepacker.BuildAllLowerFromHighInto(
                        benchmarkHigh, ref baselineMed, ref baselineLow, ref baselineVlow);
                    sink += baselineMed.array[it % baselineMed.array.Length];
                }
                return sink;
            }, repackIterations);

            double portableRepackNs = MedianNsPerOp(() =>
            {
                long sink = 0;
                for (int it = 0; it < repackIterations; it++)
                {
                    SimdCandidateKernels.BuildAllLowerFromHighIntoBatchedBody(
                        benchmarkHigh, ref portableMed, ref portableLow, ref portableVlow,
                        SimdCandidateKernels.BodyRescaleKernel.PortableVector);
                    sink += portableMed.array[it % portableMed.array.Length];
                }
                return sink;
            }, repackIterations);

            _out.WriteLine($"Full AvatarQualityRepacker: scalar production {baselineRepackNs:F2} ns");
            ReportCandidate("batched portable body", baselineRepackNs, portableRepackNs);

            if (Avx2.IsSupported)
            {
                var avxMed = new SerializableBasis.LocalAvatarSyncMessage();
                var avxLow = new SerializableBasis.LocalAvatarSyncMessage();
                var avxVlow = new SerializableBasis.LocalAvatarSyncMessage();
                SimdCandidateKernels.BuildAllLowerFromHighIntoBatchedBody(
                    benchmarkHigh, ref avxMed, ref avxLow, ref avxVlow,
                    SimdCandidateKernels.BodyRescaleKernel.Avx2);

                double avxRepackNs = MedianNsPerOp(() =>
                {
                    long sink = 0;
                    for (int it = 0; it < repackIterations; it++)
                    {
                        SimdCandidateKernels.BuildAllLowerFromHighIntoBatchedBody(
                            benchmarkHigh, ref avxMed, ref avxLow, ref avxVlow,
                            SimdCandidateKernels.BodyRescaleKernel.Avx2);
                        sink += avxMed.array[it % avxMed.array.Length];
                    }
                    return sink;
                }, repackIterations);
                ReportCandidate("batched explicit AVX2 body", baselineRepackNs, avxRepackNs);
            }
        }

        /// <summary>
        /// A server whose vector paths are running scalar would still pass every correctness test
        /// while quietly giving up the width. Worth one line in the output so a benchmark run says so.
        /// </summary>
        [Fact]
        public void ReportsTheVectorWidthInUse()
        {
            _out.WriteLine($"DOTNET_PreferredVectorBitWidth = {Environment.GetEnvironmentVariable("DOTNET_PreferredVectorBitWidth") ?? "<unset>"}");
            _out.WriteLine($"Vector.IsHardwareAccelerated   = {Vector.IsHardwareAccelerated}");
            _out.WriteLine($"Vector<byte>.Count             = {Vector<byte>.Count}");
            _out.WriteLine($"Vector512.IsHardwareAccelerated = {Vector512.IsHardwareAccelerated}");
            _out.WriteLine($"BasisSimdCapabilities          = {BasisSimdCapabilities.Describe()}");
            _out.WriteLine($"Avx2.IsSupported               = {Avx2.IsSupported}");
            _out.WriteLine($"Avx512F.IsSupported            = {Avx512F.IsSupported}");
            _out.WriteLine($"Bmi2.IsSupported               = {Bmi2.IsSupported}");
            _out.WriteLine($"Sse42.IsSupported              = {Sse42.IsSupported}");
            _out.WriteLine($"AdvSimd.IsSupported            = {AdvSimd.IsSupported}");
            _out.WriteLine($"Crc32.IsSupported              = {Crc32.IsSupported}");
            Assert.True(BasisSimdCapabilities.VectorByteWidth >= 1);
        }
    }
}
