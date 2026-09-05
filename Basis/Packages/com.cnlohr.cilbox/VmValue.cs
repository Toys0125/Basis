using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Cilbox
{
    /// <summary>
    /// Compact interpreter-only value. Primitive data stays inline; managed references are
    /// represented by a 32-bit handle into the current top-level interpreter call's arena.
    /// The public/serialized StackElement representation remains unchanged.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct VmValue
    {
        [FieldOffset(0)] public StackType type;
        [FieldOffset(8)] public bool b;
        [FieldOffset(8)] public float f;
        [FieldOffset(8)] public double d;
        [FieldOffset(8)] public long l;
        [FieldOffset(8)] public ulong e;

        public readonly int i => (int)l;
        public readonly uint u => (uint)e;
        public readonly nint n => (nint)l;

        // Managed-object handle uses the high 32 bits. Address/native-handle index uses low 32.
        public object o
        {
            readonly get => VmValueArena.Resolve((uint)(e >> 32));
            set
            {
                uint handle = VmValueArena.Register(value);
                e = ((ulong)handle << 32) | (e & 0xffffffffUL);
            }
        }

        public VmValue Load(object value)
        {
            StackElement tmp = StackElement.LoadAsStatic(value);
            this = FromStackElement(tmp);
            return this;
        }

        public static VmValue LoadAsStatic(object value)
        {
            VmValue ret = default;
            return ret.Load(value);
        }

        public VmValue LoadBool(bool value) { l = value ? 1 : 0; type = StackType.Boolean; return this; }
        public VmValue LoadObject(object value) { e = (ulong)VmValueArena.Register(value) << 32; type = StackType.Object; return this; }
        public VmValue LoadSByte(sbyte value) { l = value; type = StackType.Sbyte; return this; }
        public VmValue LoadByte(uint value) { e = value; type = StackType.Byte; return this; }
        public VmValue LoadShort(short value) { l = value; type = StackType.Short; return this; }
        public VmValue LoadUshort(ushort value) { e = value; type = StackType.Ushort; return this; }
        public VmValue LoadInt(int value) { l = value; type = StackType.Int; return this; }
        public VmValue LoadNint(nint value) { l = value; type = StackType.Int; return this; }
        public VmValue LoadUint(uint value) { e = value; type = StackType.Uint; return this; }
        public VmValue LoadLong(long value) { l = value; type = StackType.Long; return this; }
        public VmValue LoadUlong(ulong value) { e = value; type = StackType.Ulong; return this; }
        public VmValue LoadFloat(float value) { l = 0; f = value; type = StackType.Float; return this; }
        public VmValue LoadDouble(double value) { d = value; type = StackType.Double; return this; }
        public VmValue LoadUlongType(ulong value, StackType stackType) { e = value; type = stackType; return this; }
        public VmValue LoadLongType(long value, StackType stackType) { l = value; type = stackType; return this; }

        public readonly Type GetInnerType()
        {
            return type == StackType.Object ? o.GetType() : StackElement.TypeFromStackType[(int)type];
        }

        public void Unbox(object value, StackType stackType)
        {
            StackElement tmp = default;
            tmp.Unbox(value, stackType);
            this = FromStackElement(tmp);
        }

        public readonly object AsObject(Cilbox box = null)
        {
            return type switch
            {
                StackType.Sbyte => (sbyte)i,
                StackType.Byte => (byte)i,
                StackType.Short => (short)i,
                StackType.Ushort => (ushort)i,
                StackType.Int => i,
                StackType.Uint => u,
                StackType.Long => l,
                StackType.Ulong => e,
                StackType.Float => f,
                StackType.Double => d,
                StackType.Boolean => b,
                StackType.Address => DereferenceAddress(),
                StackType.NativeHandle => DereferenceNativeHandle(box),
                _ => o
            };
        }

        public readonly object CoerceToObject(Type targetType)
        {
            return ToStackElement().CoerceToObject(targetType);
        }

        public readonly object DereferenceAddress()
        {
            object target = o;
            int index = i;
            if (target is VmValue[] vmValues)
                return vmValues[index].AsObject();
            if (target is StackElement[] stackElements)
                return stackElements[index].AsObject();
            return ((Array)target).GetValue(index);
        }

        public readonly object DereferenceNativeHandle(Cilbox box)
        {
            return box.metadatas[u].nativeField.GetValue(o);
        }

        public void DereferenceLoadAddress(object overwrite)
        {
            object target = o;
            int index = i;
            if (target is VmValue[] vmValues)
                vmValues[index].Load(overwrite);
            else if (target is StackElement[] stackElements)
                stackElements[index].Load(overwrite);
            else
                ((Array)target).SetValue(overwrite, index);
        }

        public readonly void DereferenceLoadNativeHandle(Cilbox box, object overwrite)
        {
            box.metadatas[u].nativeField.SetValue(o, overwrite);
        }

        public static VmValue CreateAddressReference(Array array, uint index)
        {
            VmValue ret = default;
            ret.type = StackType.Address;
            uint handle = VmValueArena.Register(array);
            ret.e = ((ulong)handle << 32) | index;
            return ret;
        }

        public static VmValue CreateNativeHandleReference(object target, uint index)
        {
            VmValue ret = default;
            ret.type = StackType.NativeHandle;
            uint handle = VmValueArena.Register(target);
            ret.e = ((ulong)handle << 32) | index;
            return ret;
        }

        public static VmValue ResolveToVmValue(VmValue value)
        {
            while (value.type == StackType.Address)
            {
                object target = value.o;
                int index = value.i;
                if (target is VmValue[] vmValues)
                    value = vmValues[index];
                else if (target is StackElement[] stackElements)
                    value = FromStackElement(stackElements[index]);
                else
                    value = LoadAsStatic(((Array)target).GetValue(index));
            }
            return value;
        }

        public readonly StackElement ToStackElement()
        {
            StackElement ret = default;
            switch (type)
            {
                case StackType.Boolean: ret.LoadBool(b); break;
                case StackType.Sbyte: ret.LoadSByte((sbyte)i); break;
                case StackType.Byte: ret.LoadByte((byte)u); break;
                case StackType.Short: ret.LoadShort((short)i); break;
                case StackType.Ushort: ret.LoadUshort((ushort)u); break;
                case StackType.Int: ret.LoadInt(i); break;
                case StackType.Uint: ret.LoadUint(u); break;
                case StackType.Long: ret.LoadLong(l); break;
                case StackType.Ulong: ret.LoadUlong(e); break;
                case StackType.Float: ret.LoadFloat(f); break;
                case StackType.Double: ret.LoadDouble(d); break;
                case StackType.Object: ret.LoadObject(o); break;
                case StackType.Address: ret = StackElement.CreateAddressReference((Array)o, u); break;
                case StackType.NativeHandle: ret = StackElement.CreateNativeHandleReference(o, u); break;
                default: ret.type = type; break;
            }
            return ret;
        }

        public static VmValue FromStackElement(StackElement value)
        {
            VmValue ret = default;
            ret.type = value.type;
            switch (value.type)
            {
                case StackType.Boolean:
                case StackType.Sbyte:
                case StackType.Short:
                case StackType.Int:
                case StackType.Long:
                    ret.l = value.l;
                    break;
                case StackType.Byte:
                case StackType.Ushort:
                case StackType.Uint:
                case StackType.Ulong:
                    ret.e = value.e;
                    break;
                case StackType.Float:
                    ret.l = 0;
                    ret.f = value.f;
                    break;
                case StackType.Double:
                    ret.d = value.d;
                    break;
                case StackType.Object:
                    ret.LoadObject(value.o);
                    break;
                case StackType.Address:
                    ret = CreateAddressReference((Array)value.o, value.u);
                    break;
                case StackType.NativeHandle:
                    ret = CreateNativeHandleReference(value.o, value.u);
                    break;
            }
            return ret;
        }

        public static implicit operator StackElement(VmValue value) => value.ToStackElement();
        public static implicit operator VmValue(StackElement value) => FromStackElement(value);
    }

    /// <summary>
    /// Per-thread, per-top-level-call managed-reference arena for VmValue handles.
    /// Reuses its backing array between calls so the common non-reentrant path is allocation-free.
    /// </summary>
    internal sealed class VmValueArena
    {
        private const int InitialCapacity = 1024;

        [ThreadStatic] private static VmValueArena current;
        [ThreadStatic] private static VmValueArena root;
        [ThreadStatic] private static Stack<VmValueArena> reentrantPool;

        private object[] objects;
        private int count;
        private object lastObject;
        private uint lastHandle;

        internal static Scope Enter()
        {
            VmValueArena previous = current;
            VmValueArena arena;
            if (previous == null)
            {
                arena = root ??= new VmValueArena();
            }
            else
            {
                var pool = reentrantPool ??= new Stack<VmValueArena>(2);
                arena = pool.Count > 0 ? pool.Pop() : new VmValueArena();
            }

            arena.Reset();
            current = arena;
            return new Scope(previous, arena);
        }

        internal static uint Register(object value)
        {
            if (value == null) return 0;
            VmValueArena arena = current ?? throw new InvalidOperationException("VmValue managed reference used outside an interpreter arena.");

            if (ReferenceEquals(value, arena.lastObject))
                return arena.lastHandle;

            int next = arena.count + 1;
            if (next >= arena.objects.Length)
                arena.Grow();

            arena.objects[next] = value;
            arena.count = next;
            arena.lastObject = value;
            arena.lastHandle = (uint)next;
            return (uint)next;
        }

        internal static object Resolve(uint handle)
        {
            if (handle == 0) return null;
            VmValueArena arena = current ?? throw new InvalidOperationException("VmValue managed reference used outside an interpreter arena.");
            return arena.objects[handle];
        }

        private VmValueArena()
        {
            objects = ArrayPool<object>.Shared.Rent(InitialCapacity);
        }

        private void Grow()
        {
            object[] larger = ArrayPool<object>.Shared.Rent(objects.Length * 2);
            Array.Copy(objects, larger, objects.Length);
            Array.Clear(objects, 0, count + 1);
            ArrayPool<object>.Shared.Return(objects, clearArray: false);
            objects = larger;
        }

        private void Reset()
        {
            if (count > 0)
                Array.Clear(objects, 1, count);
            count = 0;
            lastObject = null;
            lastHandle = 0;
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly VmValueArena previous;
            private readonly VmValueArena arena;

            internal Scope(VmValueArena previous, VmValueArena arena)
            {
                this.previous = previous;
                this.arena = arena;
            }

            public void Dispose()
            {
                arena.Reset();
                current = previous;
                if (previous != null)
                    (reentrantPool ??= new Stack<VmValueArena>(2)).Push(arena);
            }
        }
    }
}
