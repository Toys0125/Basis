using System;
using System.Runtime.InteropServices;

namespace Basis.ImageSandbox
{
    internal static class WasmtimeNative
    {
        private const string Library = "wasmtime";

        internal const byte ExternFunction = 0;
        internal const byte ExternMemory = 3;
        internal const byte ValueI32 = 0;
        internal const byte ValueI64 = 1;
        internal const byte TrapInterrupt = 10;
        internal const byte TrapOutOfFuel = 11;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WasmByteVec
        {
            public UIntPtr Size;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WasmImportTypeVec
        {
            public UIntPtr Size;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WasmtimeFunc
        {
            public ulong StoreId;
            public IntPtr Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WasmtimeMemory
        {
            public ulong StoreId;
            public uint Private1;
            public uint Private2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WasmtimeInstance
        {
            public ulong StoreId;
            public UIntPtr Private;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct WasmtimeExtern
        {
            [FieldOffset(0)]
            public byte Kind;

            [FieldOffset(8)]
            public WasmtimeFunc Function;

            [FieldOffset(8)]
            public WasmtimeMemory Memory;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct WasmtimeValue
        {
            [FieldOffset(0)]
            public byte Kind;

            [FieldOffset(8)]
            public long I64;

            public static WasmtimeValue I32(int value) =>
                new WasmtimeValue { Kind = ValueI32, I64 = value };

            public static WasmtimeValue I64Value(long value) =>
                new WasmtimeValue { Kind = ValueI64, I64 = value };
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr HostFunctionCallback(
            IntPtr environment,
            IntPtr caller,
            IntPtr arguments,
            UIntPtr argumentCount,
            IntPtr results,
            UIntPtr resultCount
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasm_config_new();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasm_engine_new_with_config(IntPtr config);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasm_engine_delete(IntPtr engine);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_config_consume_fuel_set(
            IntPtr config,
            [MarshalAs(UnmanagedType.I1)] bool enabled
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_config_epoch_interruption_set(
            IntPtr config,
            [MarshalAs(UnmanagedType.I1)] bool enabled
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_config_target_set(
            IntPtr config,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string target
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_engine_increment_epoch(IntPtr engine);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_module_new(
            IntPtr engine,
            [In] byte[] wasm,
            UIntPtr wasmLength,
            out IntPtr module
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_module_delete(IntPtr module);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_module_imports(
            IntPtr module,
            out WasmImportTypeVec imports
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasm_importtype_module(IntPtr importType);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasm_importtype_name(IntPtr importType);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasm_importtype_type(IntPtr importType);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasm_externtype_as_functype_const(IntPtr externType);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasm_importtype_vec_delete(ref WasmImportTypeVec imports);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_store_new(
            IntPtr engine,
            IntPtr data,
            IntPtr finalizer
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_store_context(IntPtr store);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_store_delete(IntPtr store);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_store_limiter(
            IntPtr store,
            long memorySize,
            long tableElements,
            long instances,
            long tables,
            long memories
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_context_set_fuel(IntPtr context, ulong fuel);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_context_set_epoch_deadline(
            IntPtr context,
            ulong ticksBeyondCurrent
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_func_new(
            IntPtr context,
            IntPtr functionType,
            HostFunctionCallback callback,
            IntPtr environment,
            IntPtr finalizer,
            out WasmtimeFunc function
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_instance_new(
            IntPtr context,
            IntPtr module,
            [In] WasmtimeExtern[] imports,
            UIntPtr importCount,
            out WasmtimeInstance instance,
            out IntPtr trap
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool wasmtime_instance_export_get(
            IntPtr context,
            ref WasmtimeInstance instance,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            UIntPtr nameLength,
            out WasmtimeExtern item
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_extern_delete(ref WasmtimeExtern item);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_func_call(
            IntPtr context,
            ref WasmtimeFunc function,
            [In] WasmtimeValue[] arguments,
            UIntPtr argumentCount,
            [In, Out] WasmtimeValue[] results,
            UIntPtr resultCount,
            out IntPtr trap
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wasmtime_memory_data(
            IntPtr context,
            ref WasmtimeMemory memory
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr wasmtime_memory_data_size(
            IntPtr context,
            ref WasmtimeMemory memory
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasmtime_error_delete(IntPtr error);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool wasmtime_trap_code(IntPtr trap, out byte code);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wasm_trap_delete(IntPtr trap);
    }
}
