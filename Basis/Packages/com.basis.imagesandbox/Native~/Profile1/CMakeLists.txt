cmake_minimum_required(VERSION 3.22)
project(BasisProfile1Decoder LANGUAGES C CXX)

if(NOT EMSCRIPTEN)
  message(FATAL_ERROR "Basis Profile 1 decoder must be built with Emscripten")
endif()

if(NOT DEFINED LIBJXL_SOURCE_DIR)
  message(FATAL_ERROR "Set LIBJXL_SOURCE_DIR to pinned libjxl v0.12.0 source")
endif()

set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TOOLS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_BENCHMARK OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_DEVTOOLS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_WASM_THREADS OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

add_subdirectory("${LIBJXL_SOURCE_DIR}" libjxl EXCLUDE_FROM_ALL)

add_executable(profile1_decoder profile1_decoder.cc)
target_compile_features(profile1_decoder PRIVATE cxx_std_17)
target_link_libraries(profile1_decoder PRIVATE jxl_dec)
set_target_properties(profile1_decoder PROPERTIES SUFFIX ".wasm")

target_link_options(profile1_decoder PRIVATE
  "-sSTANDALONE_WASM=1"
  "-sFILESYSTEM=0"
  "-sALLOW_MEMORY_GROWTH=1"
  "-sINITIAL_MEMORY=33554432"
  "-sMAXIMUM_MEMORY=2147483648"
  "-sERROR_ON_UNDEFINED_SYMBOLS=1"
  "-Wl,--no-entry"
  "-Wl,--export=p1_abi_version"
  "-Wl,--export=p1_result_u64_count"
  "-Wl,--export=p1_alloc"
  "-Wl,--export=p1_free"
  "-Wl,--export=p1_preflight"
  "-Wl,--export=p1_preflight_logical_headers"
  "-Wl,--export=p1_preflight_structural_headers"
  "-Wl,--export=p1_preflight_headers"
  "-Wl,--export=p1_preflight_validate"
  "-Wl,--export=p1_decode_open"
  "-Wl,--export=p1_decode_next"
  "-Wl,--export=p1_decode_close"
)
