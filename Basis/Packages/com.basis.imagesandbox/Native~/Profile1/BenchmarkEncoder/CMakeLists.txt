cmake_minimum_required(VERSION 3.22)
project(BasisProfile1BenchmarkEncoder LANGUAGES C CXX)

if(NOT DEFINED LIBJXL_SOURCE_DIR)
  message(FATAL_ERROR "Set LIBJXL_SOURCE_DIR to pinned libjxl v0.12.0 source")
endif()

set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TOOLS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_BENCHMARK OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_DEVTOOLS OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

add_subdirectory("${LIBJXL_SOURCE_DIR}" libjxl EXCLUDE_FROM_ALL)

add_executable(profile1_benchmark_encoder profile1_benchmark_encoder.cc)
target_compile_features(profile1_benchmark_encoder PRIVATE cxx_std_17)
target_link_libraries(profile1_benchmark_encoder PRIVATE jxl)
