cmake_minimum_required(VERSION 3.22)
project(BasisProfile1EditorNative LANGUAGES C CXX)

if(NOT DEFINED LIBJXL_SOURCE_DIR)
  message(FATAL_ERROR "Set LIBJXL_SOURCE_DIR to pinned libjxl v0.12.0 source")
endif()

set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TOOLS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_BENCHMARK OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_DEVTOOLS OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

add_subdirectory("${LIBJXL_SOURCE_DIR}" libjxl EXCLUDE_FROM_ALL)

add_library(basis_profile1_editor SHARED profile1_benchmark_encoder.cc)
target_compile_features(basis_profile1_editor PRIVATE cxx_std_17)
target_link_libraries(basis_profile1_editor PRIVATE jxl)
set_target_properties(basis_profile1_editor PROPERTIES
  OUTPUT_NAME "basis_profile1_editor"
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
)
