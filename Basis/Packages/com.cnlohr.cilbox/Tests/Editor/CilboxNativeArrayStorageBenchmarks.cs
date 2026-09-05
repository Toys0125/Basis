using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Cilbox.Tests
{
    public class CilboxNativeArrayStorageBenchmarks
    {
        private const int StackSize = 1024;
        private const int Invocations = 1500;
        private const int OperationsPerInvocation = 192;
        private const int Samples = 5;
        private static string validationSummary;
        private static object nativeScratchSink;
        [ThreadStatic] private static StackElement[] singleParameterCache;
        [ThreadStatic] private static bool singleParameterCacheInUse;
        [ThreadStatic] private static object[] nativeSingleArgumentCache;
        [ThreadStatic] private static bool nativeSingleArgumentCacheInUse;

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct NativeStackElement
        {
            [FieldOffset(0)] public StackType Type;
            [FieldOffset(8)] public long Long;
            [FieldOffset(8)] public int Int;
            [FieldOffset(8)] public ulong ULong;
            [FieldOffset(16)] public ulong Handle;

            public void LoadInt(int value)
            {
                Long = value;
                Type = StackType.Int;
            }

            public void LoadHandle(ulong value)
            {
                Handle = value;
                Type = StackType.Object;
            }
        }

        // Once object references are represented by handles, the separate reference
        // word at offset 16 is unnecessary. Every primitive and a 64-bit handle fit
        // in the same payload word, leaving a naturally aligned 16-byte VM value.
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct CompactStackElement
        {
            [FieldOffset(0)] public StackType Type;
            [FieldOffset(8)] public long Long;
            [FieldOffset(8)] public int Int;
            [FieldOffset(8)] public ulong ULong;
            [FieldOffset(8)] public ulong Handle;

            public void LoadInt(int value)
            {
                Long = value;
                Type = StackType.Int;
            }

            public void LoadHandle(ulong value)
            {
                Handle = value;
                Type = StackType.Object;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private readonly struct SampleResult
        {
            public readonly long ElapsedTicks;
            public readonly long AllocatedBytes;
            public readonly int Gen0Collections;
            public readonly int Gen1Collections;
            public readonly int Gen2Collections;
            public readonly long Checksum;

            public SampleResult(long elapsedTicks, long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections, long checksum)
            {
                ElapsedTicks = elapsedTicks;
                AllocatedBytes = allocatedBytes;
                Gen0Collections = gen0Collections;
                Gen1Collections = gen1Collections;
                Gen2Collections = gen2Collections;
                Checksum = checksum;
            }
        }

        public static void RunForValidation()
        {
            validationSummary = string.Empty;
            var benchmarks = new CilboxNativeArrayStorageBenchmarks();
            benchmarks.CompareManagedAndNativeArrayStorage();
            benchmarks.CompareCompactUnmanagedStorageAndHandleStrategies();
            benchmarks.BenchmarkCurrentInterpreterOverhead();
            benchmarks.BenchmarkNativeCallScratchBuffers();
            benchmarks.BenchmarkNativeDelegateDispatch();
            UnityEditor.EditorApplication.quitting += PrintValidationSummary;
        }

        private static void PrintValidationSummary()
        {
            UnityEngine.Debug.Log("CILBOX_NATIVEARRAY_BENCH|FINAL_SUMMARY\n" + validationSummary);
        }

        [Test]
        public unsafe void CompareManagedAndNativeArrayStorage()
        {
            var managedReuse = new StackElement[StackSize];
            var nativeSafe = new NativeArray<NativeStackElement>(StackSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var nativeUnsafe = new NativeArray<NativeStackElement>(StackSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            object[] hostObjects = MakeHostObjects(64);

            try
            {
                // Warm every code path before taking samples so JIT/import one-time work is not measured.
                RunManagedFreshNumeric(8, 32);
                RunManagedReuseNumeric(managedReuse, 8, 32, ClearMode.None);
                RunManagedReuseNumeric(managedReuse, 8, 32, ClearMode.Touched);
                RunManagedReuseNumeric(managedReuse, 8, 32, ClearMode.Full);
                RunNativePersistentSafeNumeric(nativeSafe, 8, 32);
                RunNativePersistentUnsafeNumeric(nativeUnsafe, 8, 32);
                RunNativeTempNumeric(8, 32);
                RunManagedFreshObjects(hostObjects, 8, 32);
                RunManagedReuseObjects(managedReuse, hostObjects, 8, 32, ClearMode.Touched);
                RunManagedReuseObjects(managedReuse, hostObjects, 8, 32, ClearMode.Full);
                RunNativePersistentSafeObjects(nativeSafe, hostObjects, 8, 32);
                RunNativePersistentUnsafeObjects(nativeUnsafe, hostObjects, 8, 32);

                string info =
                    $"CILBOX_NATIVEARRAY_BENCH|INFO|stackSize={StackSize}|invocations={Invocations}|ops={OperationsPerInvocation}|samples={Samples}" +
                    $"|nativeSlotBytes={UnsafeUtility.SizeOf<NativeStackElement>()}|compactSlotBytes={UnsafeUtility.SizeOf<CompactStackElement>()}" +
                    $"|managedObjectOffset={Marshal.OffsetOf(typeof(StackElement), nameof(StackElement.o)).ToInt64()}|pointerBytes={IntPtr.Size}";
                validationSummary += info + "\n";
                UnityEngine.Debug.Log(info);

                long numericChecksum = 0;
                numericChecksum = Measure("numeric-managed-fresh", () => RunManagedFreshNumeric(Invocations, OperationsPerInvocation), numericChecksum);
                numericChecksum = Measure("numeric-managed-reuse-no-clear", () => RunManagedReuseNumeric(managedReuse, Invocations, OperationsPerInvocation, ClearMode.None), numericChecksum);
                numericChecksum = Measure("numeric-managed-reuse-clear-touched", () => RunManagedReuseNumeric(managedReuse, Invocations, OperationsPerInvocation, ClearMode.Touched), numericChecksum);
                numericChecksum = Measure("numeric-managed-reuse-clear-full", () => RunManagedReuseNumeric(managedReuse, Invocations, OperationsPerInvocation, ClearMode.Full), numericChecksum);
                numericChecksum = Measure("numeric-native-persistent-safe", () => RunNativePersistentSafeNumeric(nativeSafe, Invocations, OperationsPerInvocation), numericChecksum);
                numericChecksum = Measure("numeric-native-persistent-unsafe", () => RunNativePersistentUnsafeNumeric(nativeUnsafe, Invocations, OperationsPerInvocation), numericChecksum);
                numericChecksum = Measure("numeric-native-temp", () => RunNativeTempNumeric(Invocations, OperationsPerInvocation), numericChecksum);

                long objectChecksum = 0;
                objectChecksum = Measure("object-managed-fresh", () => RunManagedFreshObjects(hostObjects, Invocations, OperationsPerInvocation), objectChecksum);
                objectChecksum = Measure("object-managed-reuse-clear-touched", () => RunManagedReuseObjects(managedReuse, hostObjects, Invocations, OperationsPerInvocation, ClearMode.Touched), objectChecksum);
                objectChecksum = Measure("object-managed-reuse-clear-full", () => RunManagedReuseObjects(managedReuse, hostObjects, Invocations, OperationsPerInvocation, ClearMode.Full), objectChecksum);
                objectChecksum = Measure("object-native-handle-persistent-safe", () => RunNativePersistentSafeObjects(nativeSafe, hostObjects, Invocations, OperationsPerInvocation), objectChecksum);
                objectChecksum = Measure("object-native-handle-persistent-unsafe", () => RunNativePersistentUnsafeObjects(nativeUnsafe, hostObjects, Invocations, OperationsPerInvocation), objectChecksum);
            }
            finally
            {
                nativeSafe.Dispose();
                nativeUnsafe.Dispose();
            }
        }

        private enum ClearMode
        {
            None,
            Touched,
            Full
        }

        private static long Measure(string name, Func<long> action, long expectedChecksum)
        {
            var results = new SampleResult[Samples];
            for (int sample = 0; sample < Samples; sample++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
                int beforeGen0 = GC.CollectionCount(0);
                int beforeGen1 = GC.CollectionCount(1);
                int beforeGen2 = GC.CollectionCount(2);
                long start = Stopwatch.GetTimestamp();
                long checksum = action();
                long end = Stopwatch.GetTimestamp();
                long afterAllocated = GC.GetAllocatedBytesForCurrentThread();

                results[sample] = new SampleResult(
                    end - start,
                    afterAllocated - beforeAllocated,
                    GC.CollectionCount(0) - beforeGen0,
                    GC.CollectionCount(1) - beforeGen1,
                    GC.CollectionCount(2) - beforeGen2,
                    checksum);
            }

            Array.Sort(results, (a, b) => a.ElapsedTicks.CompareTo(b.ElapsedTicks));
            SampleResult median = results[Samples / 2];
            if (expectedChecksum != 0)
            {
                Assert.AreEqual(expectedChecksum, median.Checksum, $"Checksum mismatch for {name}");
            }

            double milliseconds = median.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            string result =
                $"CILBOX_NATIVEARRAY_BENCH|RESULT|{name}|ms={milliseconds:F4}|allocated={median.AllocatedBytes}" +
                $"|gen0={median.Gen0Collections}|gen1={median.Gen1Collections}|gen2={median.Gen2Collections}|checksum={median.Checksum}";
            validationSummary += result + "\n";
            UnityEngine.Debug.Log(result);
            return median.Checksum;
        }

        private static long RunManagedFreshNumeric(int invocations, int operations)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                var stack = new StackElement[StackSize];
                checksum += RunManagedNumericBody(stack, invocation, operations);
            }
            return checksum;
        }

        private static long RunManagedReuseNumeric(StackElement[] stack, int invocations, int operations, ClearMode clearMode)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                checksum += RunManagedNumericBody(stack, invocation, operations);
                ClearManaged(stack, clearMode, 4);
            }
            return checksum;
        }

        private static long RunManagedNumericBody(StackElement[] stack, int invocation, int operations)
        {
            long checksum = 0;
            int sp = -1;
            for (int op = 0; op < operations; op++)
            {
                stack[++sp].LoadInt(invocation + op + 1);
                stack[++sp].LoadInt((op * 3) + 7);
                int rhs = stack[sp--].i;
                int lhs = stack[sp].i;
                stack[sp].LoadInt(unchecked((lhs * 33) ^ rhs));
                checksum += stack[sp--].i;
            }
            return checksum;
        }

        private static long RunNativePersistentSafeNumeric(NativeArray<NativeStackElement> stack, int invocations, int operations)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                int sp = -1;
                for (int op = 0; op < operations; op++)
                {
                    NativeStackElement lhs = default;
                    lhs.LoadInt(invocation + op + 1);
                    stack[++sp] = lhs;

                    NativeStackElement rhs = default;
                    rhs.LoadInt((op * 3) + 7);
                    stack[++sp] = rhs;

                    rhs = stack[sp--];
                    lhs = stack[sp];
                    lhs.LoadInt(unchecked((lhs.Int * 33) ^ rhs.Int));
                    stack[sp] = lhs;
                    checksum += stack[sp--].Int;
                }
            }
            return checksum;
        }

        private static unsafe long RunNativePersistentUnsafeNumeric(NativeArray<NativeStackElement> stack, int invocations, int operations)
        {
            NativeStackElement* ptr = (NativeStackElement*)NativeArrayUnsafeUtility.GetUnsafePtr(stack);
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                int sp = -1;
                for (int op = 0; op < operations; op++)
                {
                    ptr[++sp].LoadInt(invocation + op + 1);
                    ptr[++sp].LoadInt((op * 3) + 7);
                    int rhs = ptr[sp--].Int;
                    int lhs = ptr[sp].Int;
                    ptr[sp].LoadInt(unchecked((lhs * 33) ^ rhs));
                    checksum += ptr[sp--].Int;
                }
            }
            return checksum;
        }

        private static long RunNativeTempNumeric(int invocations, int operations)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                var stack = new NativeArray<NativeStackElement>(StackSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                try
                {
                    int sp = -1;
                    for (int op = 0; op < operations; op++)
                    {
                        NativeStackElement lhs = default;
                        lhs.LoadInt(invocation + op + 1);
                        stack[++sp] = lhs;

                        NativeStackElement rhs = default;
                        rhs.LoadInt((op * 3) + 7);
                        stack[++sp] = rhs;

                        rhs = stack[sp--];
                        lhs = stack[sp];
                        lhs.LoadInt(unchecked((lhs.Int * 33) ^ rhs.Int));
                        stack[sp] = lhs;
                        checksum += stack[sp--].Int;
                    }
                }
                finally
                {
                    stack.Dispose();
                }
            }
            return checksum;
        }

        private static long RunManagedFreshObjects(object[] hostObjects, int invocations, int operations)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                var stack = new StackElement[StackSize];
                checksum += RunManagedObjectBody(stack, hostObjects, invocation, operations);
            }
            return checksum;
        }

        private static long RunManagedReuseObjects(StackElement[] stack, object[] hostObjects, int invocations, int operations, ClearMode clearMode)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                checksum += RunManagedObjectBody(stack, hostObjects, invocation, operations);
                ClearManaged(stack, clearMode, 2);
            }
            return checksum;
        }

        private static long RunManagedObjectBody(StackElement[] stack, object[] hostObjects, int invocation, int operations)
        {
            long checksum = 0;
            int sp = -1;
            int mask = hostObjects.Length - 1;
            for (int op = 0; op < operations; op++)
            {
                object value = hostObjects[(invocation + op) & mask];
                stack[++sp].LoadObject(value);
                stack[++sp] = stack[sp - 1];
                object rhs = stack[sp--].o;
                object lhs = stack[sp--].o;
                if (ReferenceEquals(lhs, rhs)) checksum++;
            }
            return checksum;
        }

        private static long RunNativePersistentSafeObjects(NativeArray<NativeStackElement> stack, object[] hostObjects, int invocations, int operations)
        {
            long checksum = 0;
            int mask = hostObjects.Length - 1;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                int sp = -1;
                for (int op = 0; op < operations; op++)
                {
                    int objectIndex = (invocation + op) & mask;
                    NativeStackElement value = default;
                    value.LoadHandle((ulong)(objectIndex + 1));
                    stack[++sp] = value;
                    stack[++sp] = stack[sp - 1];
                    ulong rhsHandle = stack[sp--].Handle;
                    ulong lhsHandle = stack[sp--].Handle;
                    object rhs = hostObjects[(int)rhsHandle - 1];
                    object lhs = hostObjects[(int)lhsHandle - 1];
                    if (ReferenceEquals(lhs, rhs)) checksum++;
                }
            }
            return checksum;
        }

        private static unsafe long RunNativePersistentUnsafeObjects(NativeArray<NativeStackElement> stack, object[] hostObjects, int invocations, int operations)
        {
            NativeStackElement* ptr = (NativeStackElement*)NativeArrayUnsafeUtility.GetUnsafePtr(stack);
            long checksum = 0;
            int mask = hostObjects.Length - 1;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                int sp = -1;
                for (int op = 0; op < operations; op++)
                {
                    int objectIndex = (invocation + op) & mask;
                    ptr[++sp].LoadHandle((ulong)(objectIndex + 1));
                    ptr[++sp] = ptr[sp - 1];
                    ulong rhsHandle = ptr[sp--].Handle;
                    ulong lhsHandle = ptr[sp--].Handle;
                    object rhs = hostObjects[(int)rhsHandle - 1];
                    object lhs = hostObjects[(int)lhsHandle - 1];
                    if (ReferenceEquals(lhs, rhs)) checksum++;
                }
            }
            return checksum;
        }

        [Test]
        public unsafe void CompareCompactUnmanagedStorageAndHandleStrategies()
        {
            var compactManagedReuse = new CompactStackElement[StackSize];
            var compactNative = new NativeArray<CompactStackElement>(StackSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            CompactStackElement* rawCompact = (CompactStackElement*)UnsafeUtility.Malloc(
                StackSize * UnsafeUtility.SizeOf<CompactStackElement>(), 16, Allocator.Persistent);
            object[] hostObjects = MakeHostObjects(64);
            object[] handleArena = new object[OperationsPerInvocation];
            var stableHandles = new Dictionary<object, int>(hostObjects.Length, ReferenceComparer.Instance);
            for (int i = 0; i < hostObjects.Length; i++) stableHandles.Add(hostObjects[i], i + 1);

            try
            {
                RunCompactManagedFreshNumeric(8, 32);
                RunCompactManagedReuseNumeric(compactManagedReuse, 8, 32);
                RunCompactNativeUnsafeNumeric(compactNative, 8, 32, 0);
                RunRawCompactNumeric(rawCompact, 8, 32, 0);
                RunCompactNativeUnsafeObjects(compactNative, hostObjects, 8, 32);
                RunCompactHandleArena(compactNative, hostObjects, handleArena, 8, 32, false);
                RunCompactHandleArena(compactNative, hostObjects, handleArena, 8, 32, true);
                RunCompactStableDictionaryHandles(compactNative, hostObjects, stableHandles, 8, 32);

                long numericChecksum = 0;
                numericChecksum = Measure("compact16-managed-fresh", () => RunCompactManagedFreshNumeric(Invocations, OperationsPerInvocation), numericChecksum);
                numericChecksum = Measure("compact16-managed-reuse", () => RunCompactManagedReuseNumeric(compactManagedReuse, Invocations, OperationsPerInvocation), numericChecksum);
                numericChecksum = Measure("compact16-nativearray-unsafe", () => RunCompactNativeUnsafeNumeric(compactNative, Invocations, OperationsPerInvocation, 0), numericChecksum);
                numericChecksum = Measure("compact16-raw-malloc", () => RunRawCompactNumeric(rawCompact, Invocations, OperationsPerInvocation, 0), numericChecksum);
                numericChecksum = Measure("compact16-raw-clear-8-slots", () => RunRawCompactNumeric(rawCompact, Invocations, OperationsPerInvocation, 8), numericChecksum);
                numericChecksum = Measure("compact16-raw-clear-32-slots", () => RunRawCompactNumeric(rawCompact, Invocations, OperationsPerInvocation, 32), numericChecksum);
                numericChecksum = Measure("compact16-raw-clear-1024-slots", () => RunRawCompactNumeric(rawCompact, Invocations, OperationsPerInvocation, StackSize), numericChecksum);

                long objectChecksum = 0;
                objectChecksum = Measure("compact16-object-preassigned-handles", () => RunCompactNativeUnsafeObjects(compactNative, hostObjects, Invocations, OperationsPerInvocation), objectChecksum);
                objectChecksum = Measure("compact16-object-arena-register", () => RunCompactHandleArena(compactNative, hostObjects, handleArena, Invocations, OperationsPerInvocation, false), objectChecksum);
                objectChecksum = Measure("compact16-object-arena-register-clear", () => RunCompactHandleArena(compactNative, hostObjects, handleArena, Invocations, OperationsPerInvocation, true), objectChecksum);
                objectChecksum = Measure("compact16-object-stable-dictionary", () => RunCompactStableDictionaryHandles(compactNative, hostObjects, stableHandles, Invocations, OperationsPerInvocation), objectChecksum);
            }
            finally
            {
                compactNative.Dispose();
                UnsafeUtility.Free(rawCompact, Allocator.Persistent);
            }
        }

        [Test]
        public void BenchmarkCurrentInterpreterOverhead()
        {
            var gameObject = new UnityEngine.GameObject("CilboxInterpreterBenchmark");
            try
            {
                var box = gameObject.AddComponent<CilboxBenchmarkBox>();
                box.timeoutLengthUs = box.MaxTimeoutLengthUs;
                var cls = new CilboxClass { box = box, className = "CilboxInterpreterBenchmark" };
                CilboxMethod tiny = CreateNumericMethod(cls, "Tiny", new byte[] { 0x17, 0x2a }, 1);
                CilboxMethod arithmetic = CreateNumericMethod(cls, "Arithmetic", BuildArithmeticBytecode(OperationsPerInvocation), 2);
                CilboxMethod instanceTiny = CreateInstanceThisMethod(cls, "InstanceTiny");
                var proxy = gameObject.AddComponent<CilboxProxy>();

                var emptyParameters = Array.Empty<StackElement>();
                var ownedStackPool = ArrayPool<StackElement>.Create();
                var ownedParameterPool = ArrayPool<StackElement>.Create();
                var tinyFullStack = new StackElement[StackSize];
                var tinyRightSizedStack = new StackElement[tiny.MaxStackSize + tiny.methodLocals.Length];
                var arithmeticFullStack = new StackElement[StackSize];
                var arithmeticRightSizedStack = new StackElement[arithmetic.MaxStackSize + arithmetic.methodLocals.Length];

                RunCurrentInterpreter(tiny, box, 8);
                RunCurrentInterpreter(arithmetic, box, 8);
                RunBufferedInterpreter(tiny, box, tinyFullStack, emptyParameters, 8, true, false);
                RunBufferedInterpreter(arithmetic, box, arithmeticFullStack, emptyParameters, 8, true, false);
                RunInterpreterEntryExit(box, tiny, 8);
                RunStackAllocations(8, StackSize);
                RunStackAllocations(8, 2);
                RunArrayPoolStackBuffers(ArrayPool<StackElement>.Shared, 8, clearOnRent: true, clearOnReturn: true);
                RunArrayPoolStackBuffers(ArrayPool<StackElement>.Shared, 8, clearOnRent: true, clearOnReturn: false);
                RunArrayPoolStackBuffers(ArrayPool<StackElement>.Shared, 8, clearOnRent: false, clearOnReturn: true);
                RunArrayPoolStackBuffers(ownedStackPool, 8, clearOnRent: false, clearOnReturn: true);
                RunInstanceParameterFresh(instanceTiny, box, tinyFullStack, proxy, 8);
                RunInstanceParameterPool(instanceTiny, box, tinyFullStack, proxy, ownedParameterPool, 8);
                RunInstanceParameterThreadCache(instanceTiny, box, tinyFullStack, proxy, 8);
                RunCurrentInstanceInterpreter(instanceTiny, box, proxy, 8);

                long tinyChecksum = 0;
                tinyChecksum = Measure("interpreter-current-tiny", () => RunCurrentInterpreter(tiny, box, Invocations), tinyChecksum);
                long arithmeticChecksum = 0;
                arithmeticChecksum = Measure("interpreter-current-arithmetic", () => RunCurrentInterpreter(arithmetic, box, Invocations), arithmeticChecksum);

                long bufferedTinyChecksum = 0;
                bufferedTinyChecksum = Measure("interpreter-buffered-tiny-1024", () => RunBufferedInterpreter(tiny, box, tinyFullStack, emptyParameters, Invocations, true, false), bufferedTinyChecksum);
                bufferedTinyChecksum = Measure("interpreter-buffered-tiny-right-sized", () => RunBufferedInterpreter(tiny, box, tinyRightSizedStack, emptyParameters, Invocations, true, false), bufferedTinyChecksum);
                bufferedTinyChecksum = Measure("interpreter-buffered-tiny-1024-full-clear", () => RunBufferedInterpreter(tiny, box, tinyFullStack, emptyParameters, Invocations, true, true), bufferedTinyChecksum);
                bufferedTinyChecksum = Measure("interpreter-buffered-tiny-no-accounting", () => RunBufferedInterpreter(tiny, box, tinyRightSizedStack, emptyParameters, Invocations, false, false), bufferedTinyChecksum);

                long bufferedArithmeticChecksum = 0;
                bufferedArithmeticChecksum = Measure("interpreter-buffered-arithmetic-1024", () => RunBufferedInterpreter(arithmetic, box, arithmeticFullStack, emptyParameters, Invocations, true, false), bufferedArithmeticChecksum);
                bufferedArithmeticChecksum = Measure("interpreter-buffered-arithmetic-right-sized", () => RunBufferedInterpreter(arithmetic, box, arithmeticRightSizedStack, emptyParameters, Invocations, true, false), bufferedArithmeticChecksum);
                bufferedArithmeticChecksum = Measure("interpreter-buffered-arithmetic-1024-full-clear", () => RunBufferedInterpreter(arithmetic, box, arithmeticFullStack, emptyParameters, Invocations, true, true), bufferedArithmeticChecksum);
                bufferedArithmeticChecksum = Measure("interpreter-buffered-arithmetic-no-accounting", () => RunBufferedInterpreter(arithmetic, box, arithmeticRightSizedStack, emptyParameters, Invocations, false, false), bufferedArithmeticChecksum);

                Measure("overhead-interpreter-entry-exit", () => RunInterpreterEntryExit(box, tiny, Invocations), 0);
                Measure("overhead-allocate-stackelement-1024", () => RunStackAllocations(Invocations, StackSize), 0);
                Measure("overhead-allocate-stackelement-2", () => RunStackAllocations(Invocations, 2), 0);
                Measure("overhead-allocate-stackelement-1", () => RunStackAllocations(Invocations, 1), 0);
                Measure("overhead-allocate-stackelement-empty", () => RunStackAllocations(Invocations, 0), 0);
                Measure("overhead-arraypool-shared-1024-clear-rent-clear-return", () => RunArrayPoolStackBuffers(ArrayPool<StackElement>.Shared, Invocations, clearOnRent: true, clearOnReturn: true), 0);
                Measure("overhead-arraypool-shared-1024-clear-rent", () => RunArrayPoolStackBuffers(ArrayPool<StackElement>.Shared, Invocations, clearOnRent: true, clearOnReturn: false), 0);
                Measure("overhead-arraypool-shared-1024-clear-return", () => RunArrayPoolStackBuffers(ArrayPool<StackElement>.Shared, Invocations, clearOnRent: false, clearOnReturn: true), 0);
                Measure("overhead-arraypool-owned-1024-clear-return", () => RunArrayPoolStackBuffers(ownedStackPool, Invocations, clearOnRent: false, clearOnReturn: true), 0);

                long instanceChecksum = 0;
                instanceChecksum = Measure("parameters-instance-fresh-1", () => RunInstanceParameterFresh(instanceTiny, box, tinyFullStack, proxy, Invocations), instanceChecksum);
                instanceChecksum = Measure("parameters-instance-owned-pool-1", () => RunInstanceParameterPool(instanceTiny, box, tinyFullStack, proxy, ownedParameterPool, Invocations), instanceChecksum);
                instanceChecksum = Measure("parameters-instance-thread-cache-1", () => RunInstanceParameterThreadCache(instanceTiny, box, tinyFullStack, proxy, Invocations), instanceChecksum);
                Measure("parameters-static-fresh-empty", () => RunEmptyParameterAllocations(Invocations), 0);
                Measure("parameters-static-array-empty", () => RunEmptyParameterReuse(Invocations), 0);
                Measure("interpreter-current-instance-tiny", () => RunCurrentInstanceInterpreter(instanceTiny, box, proxy, Invocations), 0);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void BenchmarkNativeCallScratchBuffers()
        {
            const int nativeInvocations = Invocations * 10;
            MethodInfo method = typeof(CilboxNativeArrayStorageBenchmarks).GetMethod(nameof(NativeScratchAcceptObject), BindingFlags.Public | BindingFlags.Static);
            object hostObject = new object();

            RunNativeScratchFresh(method, hostObject, 8);
            RunNativeScratchNoByRef(method, hostObject, 8);
            RunNativeScratchThreadCache(method, hostObject, 8);

            long checksum = 0;
            checksum = Measure("nativecall-scratch-fresh-object-and-stackelement-1", () => RunNativeScratchFresh(method, hostObject, nativeInvocations), checksum);
            checksum = Measure("nativecall-scratch-fresh-object-only-1", () => RunNativeScratchNoByRef(method, hostObject, nativeInvocations), checksum);
            checksum = Measure("nativecall-scratch-thread-cache-object-only-1", () => RunNativeScratchThreadCache(method, hostObject, nativeInvocations), checksum);
        }

        [Test]
        public void BenchmarkNativeDelegateDispatch()
        {
            const int directInvocations = 5000000;
            const int reflectionInvocations = 150000;
            byte[] bytes = BitConverter.GetBytes(123.25f);
            MethodInfo method = typeof(BitConverter).GetMethod(nameof(BitConverter.ToSingle), new[] { typeof(byte[]), typeof(int) });
            var typedDelegate = (Func<byte[], int, float>)method.CreateDelegate(typeof(Func<byte[], int, float>));
            object[] reflectionArgs = { bytes, 0 };

            RunBitConverterDirect(bytes, 8);
            RunBitConverterDelegate(typedDelegate, bytes, 8);
            RunBitConverterReflection(method, reflectionArgs, 8);

            long directChecksum = Measure("native-dispatch-bitconverter-direct-5000000", () => RunBitConverterDirect(bytes, directInvocations), 0);
            Measure("native-dispatch-bitconverter-delegate-5000000", () => RunBitConverterDelegate(typedDelegate, bytes, directInvocations), directChecksum);
            Measure("native-dispatch-bitconverter-reflection-150000", () => RunBitConverterReflection(method, reflectionArgs, reflectionInvocations), 0);
        }

        private static long RunBitConverterDirect(byte[] bytes, int invocations)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++) checksum += (int)BitConverter.ToSingle(bytes, 0);
            return checksum;
        }

        private static long RunBitConverterDelegate(Func<byte[], int, float> invoker, byte[] bytes, int invocations)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++) checksum += (int)invoker(bytes, 0);
            return checksum;
        }

        private static long RunBitConverterReflection(MethodInfo method, object[] args, int invocations)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++) checksum += (int)(float)method.Invoke(null, args);
            return checksum;
        }

        public static void NativeScratchAcceptObject(object value)
        {
            nativeScratchSink = value;
        }

        private static long RunNativeScratchFresh(MethodInfo method, object hostObject, int invocations)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                var callpar = new object[1];
                var callparSe = new StackElement[1];
                var se = new StackElement();
                se.Load(hostObject);
                callparSe[0] = se;
                callpar[0] = se.AsObject();
                method.Invoke(null, callpar);
                if (ReferenceEquals(nativeScratchSink, hostObject) && callparSe[0].type == StackType.Object) checksum++;
            }
            return checksum;
        }

        private static long RunNativeScratchNoByRef(MethodInfo method, object hostObject, int invocations)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                var callpar = new object[1];
                var se = new StackElement();
                se.Load(hostObject);
                callpar[0] = se.AsObject();
                method.Invoke(null, callpar);
                if (ReferenceEquals(nativeScratchSink, hostObject)) checksum++;
            }
            return checksum;
        }

        private static long RunNativeScratchThreadCache(MethodInfo method, object hostObject, int invocations)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                bool usingCache = !nativeSingleArgumentCacheInUse;
                object[] callpar = usingCache
                    ? nativeSingleArgumentCache ??= new object[1]
                    : new object[1];
                if (usingCache) nativeSingleArgumentCacheInUse = true;

                try
                {
                    var se = new StackElement();
                    se.Load(hostObject);
                    callpar[0] = se.AsObject();
                    method.Invoke(null, callpar);
                    if (ReferenceEquals(nativeScratchSink, hostObject)) checksum++;
                }
                finally
                {
                    callpar[0] = null;
                    if (usingCache) nativeSingleArgumentCacheInUse = false;
                }
            }
            return checksum;
        }

        private static long RunCompactManagedFreshNumeric(int invocations, int operations)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                var stack = new CompactStackElement[StackSize];
                checksum += RunCompactManagedNumericBody(stack, invocation, operations);
            }
            return checksum;
        }

        private static long RunCompactManagedReuseNumeric(CompactStackElement[] stack, int invocations, int operations)
        {
            long checksum = 0;
            for (int invocation = 0; invocation < invocations; invocation++)
                checksum += RunCompactManagedNumericBody(stack, invocation, operations);
            return checksum;
        }

        private static long RunCompactManagedNumericBody(CompactStackElement[] stack, int invocation, int operations)
        {
            long checksum = 0;
            int sp = -1;
            for (int op = 0; op < operations; op++)
            {
                stack[++sp].LoadInt(invocation + op + 1);
                stack[++sp].LoadInt((op * 3) + 7);
                int rhs = stack[sp--].Int;
                int lhs = stack[sp].Int;
                stack[sp].LoadInt(unchecked((lhs * 33) ^ rhs));
                checksum += stack[sp--].Int;
            }
            return checksum;
        }

        private static unsafe long RunCompactNativeUnsafeNumeric(NativeArray<CompactStackElement> stack, int invocations, int operations, int clearSlots)
        {
            CompactStackElement* ptr = (CompactStackElement*)NativeArrayUnsafeUtility.GetUnsafePtr(stack);
            return RunRawCompactNumeric(ptr, invocations, operations, clearSlots);
        }

        private static unsafe long RunRawCompactNumeric(CompactStackElement* ptr, int invocations, int operations, int clearSlots)
        {
            long checksum = 0;
            int clearBytes = clearSlots * UnsafeUtility.SizeOf<CompactStackElement>();
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                if (clearBytes != 0) UnsafeUtility.MemClear(ptr, clearBytes);
                int sp = -1;
                for (int op = 0; op < operations; op++)
                {
                    ptr[++sp].LoadInt(invocation + op + 1);
                    ptr[++sp].LoadInt((op * 3) + 7);
                    int rhs = ptr[sp--].Int;
                    int lhs = ptr[sp].Int;
                    ptr[sp].LoadInt(unchecked((lhs * 33) ^ rhs));
                    checksum += ptr[sp--].Int;
                }
            }
            return checksum;
        }

        private static unsafe long RunCompactNativeUnsafeObjects(NativeArray<CompactStackElement> stack, object[] hostObjects, int invocations, int operations)
        {
            CompactStackElement* ptr = (CompactStackElement*)NativeArrayUnsafeUtility.GetUnsafePtr(stack);
            long checksum = 0;
            int mask = hostObjects.Length - 1;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                int sp = -1;
                for (int op = 0; op < operations; op++)
                {
                    int objectIndex = (invocation + op) & mask;
                    ptr[++sp].LoadHandle((ulong)(objectIndex + 1));
                    ptr[++sp] = ptr[sp - 1];
                    ulong rhsHandle = ptr[sp--].Handle;
                    ulong lhsHandle = ptr[sp--].Handle;
                    object rhs = hostObjects[(int)rhsHandle - 1];
                    object lhs = hostObjects[(int)lhsHandle - 1];
                    if (ReferenceEquals(lhs, rhs)) checksum++;
                }
            }
            return checksum;
        }

        private static unsafe long RunCompactHandleArena(NativeArray<CompactStackElement> stack, object[] hostObjects, object[] arena, int invocations, int operations, bool clearArena)
        {
            CompactStackElement* ptr = (CompactStackElement*)NativeArrayUnsafeUtility.GetUnsafePtr(stack);
            long checksum = 0;
            int mask = hostObjects.Length - 1;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                int sp = -1;
                int arenaCount = 0;
                for (int op = 0; op < operations; op++)
                {
                    object value = hostObjects[(invocation + op) & mask];
                    arena[arenaCount] = value;
                    ulong handle = (ulong)++arenaCount;
                    ptr[++sp].LoadHandle(handle);
                    ptr[++sp] = ptr[sp - 1];
                    object rhs = arena[(int)ptr[sp--].Handle - 1];
                    object lhs = arena[(int)ptr[sp--].Handle - 1];
                    if (ReferenceEquals(lhs, rhs)) checksum++;
                }
                if (clearArena) Array.Clear(arena, 0, arenaCount);
            }
            return checksum;
        }

        private static unsafe long RunCompactStableDictionaryHandles(NativeArray<CompactStackElement> stack, object[] hostObjects, Dictionary<object, int> handles, int invocations, int operations)
        {
            CompactStackElement* ptr = (CompactStackElement*)NativeArrayUnsafeUtility.GetUnsafePtr(stack);
            long checksum = 0;
            int mask = hostObjects.Length - 1;
            for (int invocation = 0; invocation < invocations; invocation++)
            {
                int sp = -1;
                for (int op = 0; op < operations; op++)
                {
                    object value = hostObjects[(invocation + op) & mask];
                    ulong handle = (ulong)handles[value];
                    ptr[++sp].LoadHandle(handle);
                    ptr[++sp] = ptr[sp - 1];
                    object rhs = hostObjects[(int)ptr[sp--].Handle - 1];
                    object lhs = hostObjects[(int)ptr[sp--].Handle - 1];
                    if (ReferenceEquals(lhs, rhs)) checksum++;
                }
            }
            return checksum;
        }

        private static CilboxMethod CreateNumericMethod(CilboxClass cls, string name, byte[] byteCode, int maxStack)
        {
            var serialized = new SerializedMethod
            {
                methodName = name,
                maxStack = maxStack,
                isVoid = false,
                isStatic = true,
                isCtor = false,
                fullSignature = $"Int32 {name}()",
                body = byteCode,
                locals = Array.Empty<SerializedField>(),
                parameters = Array.Empty<SerializedField>(),
                exceptionHandlers = Array.Empty<SerializedExceptionHandler>()
            };
            var method = new CilboxMethod();
            method.Load(cls, serialized);
            return method;
        }

        private static CilboxMethod CreateInstanceThisMethod(CilboxClass cls, string name)
        {
            var serialized = new SerializedMethod
            {
                methodName = name,
                maxStack = 1,
                isVoid = false,
                isStatic = false,
                isCtor = false,
                fullSignature = $"Int32 {name}()",
                body = new byte[] { 0x02, 0x26, 0x17, 0x2a }, // ldarg.0; pop; ldc.i4.1; ret
                locals = Array.Empty<SerializedField>(),
                parameters = Array.Empty<SerializedField>(),
                exceptionHandlers = Array.Empty<SerializedExceptionHandler>()
            };
            var method = new CilboxMethod();
            method.Load(cls, serialized);
            return method;
        }

        private static byte[] BuildArithmeticBytecode(int operations)
        {
            var code = new byte[operations * 2 + 2];
            int pc = 0;
            code[pc++] = 0x17; // ldc.i4.1
            for (int i = 0; i < operations; i++)
            {
                code[pc++] = 0x18; // ldc.i4.2
                code[pc++] = 0x58; // add
            }
            code[pc] = 0x2a; // ret
            return code;
        }

        private static long RunInstanceParameterFresh(CilboxMethod method, CilboxBenchmarkBox box, StackElement[] stack, CilboxProxy proxy, int invocations)
        {
            box.interpreterAccountingCumulitiveTicks = 0;
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                var parameters = new StackElement[1];
                parameters[0].Load(proxy);
                checksum += Convert.ToInt64(method.InterpretWithBuffersForBenchmark(stack, parameters, true));
            }
            return checksum;
        }

        private static long RunInstanceParameterPool(CilboxMethod method, CilboxBenchmarkBox box, StackElement[] stack, CilboxProxy proxy, ArrayPool<StackElement> pool, int invocations)
        {
            box.interpreterAccountingCumulitiveTicks = 0;
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                StackElement[] parameters = pool.Rent(1);
                try
                {
                    parameters[0].Load(proxy);
                    checksum += Convert.ToInt64(method.InterpretWithBuffersForBenchmark(stack, new ArraySegment<StackElement>(parameters, 0, 1), true));
                }
                finally
                {
                    pool.Return(parameters, clearArray: true);
                }
            }
            return checksum;
        }

        private static long RunInstanceParameterThreadCache(CilboxMethod method, CilboxBenchmarkBox box, StackElement[] stack, CilboxProxy proxy, int invocations)
        {
            box.interpreterAccountingCumulitiveTicks = 0;
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                StackElement[] parameters;
                bool cached = !singleParameterCacheInUse;
                if (cached)
                {
                    singleParameterCacheInUse = true;
                    parameters = singleParameterCache ??= new StackElement[1];
                }
                else
                {
                    parameters = new StackElement[1];
                }

                try
                {
                    parameters[0].Load(proxy);
                    checksum += Convert.ToInt64(method.InterpretWithBuffersForBenchmark(stack, parameters, true));
                }
                finally
                {
                    parameters[0] = default;
                    if (cached) singleParameterCacheInUse = false;
                }
            }
            return checksum;
        }

        private static long RunEmptyParameterAllocations(int invocations)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++) checksum += new StackElement[0].Length + 1;
            return checksum;
        }

        private static long RunEmptyParameterReuse(int invocations)
        {
            long checksum = 0;
            StackElement[] parameters = Array.Empty<StackElement>();
            for (int i = 0; i < invocations; i++) checksum += parameters.Length + 1;
            return checksum;
        }

        private static long RunCurrentInstanceInterpreter(CilboxMethod method, CilboxBenchmarkBox box, CilboxProxy proxy, int invocations)
        {
            box.interpreterAccountingCumulitiveTicks = 0;
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
                checksum += Convert.ToInt64(method.Interpret(proxy, null));
            return checksum;
        }

        private static long RunCurrentInterpreter(CilboxMethod method, CilboxBenchmarkBox box, int invocations)
        {
            box.interpreterAccountingCumulitiveTicks = 0;
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
                checksum += Convert.ToInt64(method.Interpret(null, Array.Empty<object>()));
            return checksum;
        }

        private static long RunBufferedInterpreter(CilboxMethod method, CilboxBenchmarkBox box,
            StackElement[] stack, StackElement[] parameters, int invocations, bool accounting, bool clearFullStack)
        {
            box.interpreterAccountingCumulitiveTicks = 0;
            if (!accounting)
            {
                // InterpretInner independently checks this deadline every 64 instructions.
                // Keep that safety branch from firing while isolating only Entry/Exit cost.
                box.interpreterInstructionsCount = 0;
                box.interpreterAccountingDropDead = long.MaxValue;
            }
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                checksum += Convert.ToInt64(method.InterpretWithBuffersForBenchmark(stack, parameters, accounting));
                if (clearFullStack) Array.Clear(stack, 0, stack.Length);
            }
            return checksum;
        }

        private static long RunInterpreterEntryExit(CilboxBenchmarkBox box, CilboxMethod method, int invocations)
        {
            box.interpreterAccountingCumulitiveTicks = 0;
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                if (!box.InterpreterEntry(method)) continue;
                box.InterpreterExit();
                checksum++;
            }
            return checksum;
        }

        private static long RunStackAllocations(int invocations, int length)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                var stack = new StackElement[length];
                if (length != 0)
                {
                    stack[0].LoadInt(i);
                    checksum += stack[0].i;
                }
                else
                {
                    checksum++;
                }
            }
            return checksum;
        }

        private static long RunArrayPoolStackBuffers(ArrayPool<StackElement> pool, int invocations, bool clearOnRent, bool clearOnReturn)
        {
            long checksum = 0;
            for (int i = 0; i < invocations; i++)
            {
                StackElement[] stack = pool.Rent(StackSize);
                try
                {
                    if (clearOnRent) Array.Clear(stack, 0, StackSize);
                    stack[0].LoadInt(i);
                    checksum += stack[0].i;
                }
                finally
                {
                    pool.Return(stack, clearArray: clearOnReturn);
                }
            }
            return checksum;
        }

        private static void ClearManaged(StackElement[] stack, ClearMode clearMode, int touched)
        {
            switch (clearMode)
            {
                case ClearMode.None:
                    break;
                case ClearMode.Touched:
                    Array.Clear(stack, 0, touched);
                    break;
                case ClearMode.Full:
                    Array.Clear(stack, 0, stack.Length);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(clearMode), clearMode, null);
            }
        }

        private static object[] MakeHostObjects(int count)
        {
            var result = new object[count];
            for (int i = 0; i < count; i++) result[i] = new object();
            return result;
        }
    }

    internal sealed class CilboxBenchmarkBox : global::Cilbox.Cilbox
    {
        public override bool CheckMethodAllowed(out MethodInfo mi, Type declaringType, string name,
            SerializedTypeDescriptor[] parametersIn, SerializedTypeDescriptor[] genericArgumentsIn, string fullSignature)
        {
            mi = null;
            return false;
        }

        public override bool CheckTypeAllowed(string sType) => false;
        public override bool CheckFieldAllowed(string sType, string sFieldName) => false;

        public override bool GetTypeOverride(string sType, out Type t)
        {
            t = null;
            return false;
        }
    }
}
