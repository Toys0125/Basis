using System;
using System.Diagnostics;
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
            new CilboxNativeArrayStorageBenchmarks().CompareManagedAndNativeArrayStorage();
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

                UnityEngine.Debug.Log(
                    $"CILBOX_NATIVEARRAY_BENCH|INFO|stackSize={StackSize}|invocations={Invocations}|ops={OperationsPerInvocation}|samples={Samples}" +
                    $"|nativeSlotBytes={UnsafeUtility.SizeOf<NativeStackElement>()}|managedObjectOffset={Marshal.OffsetOf(typeof(StackElement), nameof(StackElement.o)).ToInt64()}|pointerBytes={IntPtr.Size}");

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
            UnityEngine.Debug.Log(
                $"CILBOX_NATIVEARRAY_BENCH|RESULT|{name}|ms={milliseconds:F4}|allocated={median.AllocatedBytes}" +
                $"|gen0={median.Gen0Collections}|gen1={median.Gen1Collections}|gen2={median.Gen2Collections}|checksum={median.Checksum}");
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
}
