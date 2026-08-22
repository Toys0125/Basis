cmake_minimum_required(VERSION 3.22)
project(BasisProfile1BenchmarkEncoder LANGUAGES C CXX)

if(NOT EMSCRIPTEN)
  message(FATAL_ERROR "Basis Profile 1 benchmark encoder must be built with Emscripten")
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

add_executable(profile1_benchmark_encoder profile1_benchmark_encoder.cc)
target_compile_features(profile1_benchmark_encoder PRIVATE cxx_std_17)
target_link_libraries(profile1_benchmark_encoder PRIVATE jxl)
set_target_properties(profile1_benchmark_encoder PROPERTIES SUFFIX ".js")

target_link_options(profile1_benchmark_encoder PRIVATE
  "-sENVIRONMENT=node"
  "-sNODERAWFS=1"
  "-sFORCE_FILESYSTEM=1"
  "-sALLOW_MEMORY_GROWTH=1"
  "-sEXIT_RUNTIME=1"
)
