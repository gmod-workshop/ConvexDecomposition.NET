cmake_minimum_required(VERSION 3.24)

# Static CRT on Windows so consumers need no VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
cmake_policy(SET CMP0091 NEW)
# Some of CoACD's fetched dependencies declare ancient minimum versions.
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)

project(coacd_native LANGUAGES C CXX)

set(COACD_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/coacd" CACHE PATH
    "Path to the upstream CoACD checkout (git submodule)")
if(NOT EXISTS "${COACD_SOURCE_DIR}/public/coacd.cpp")
  message(FATAL_ERROR "CoACD sources not found at ${COACD_SOURCE_DIR}; "
                      "run `git submodule update --init --recursive`.")
endif()

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

# ---------------------------------------------------------------------------
# CoACD (static), with its preprocess dependencies (OpenVDB, TBB, Boost,
# spdlog) fetched by its own CMake. OpenMP is disabled in favour of CoACD's
# std::thread fallback, so the library carries no vcomp140.dll / libgomp
# runtime dependency.
# ---------------------------------------------------------------------------

set(WITH_3RD_PARTY_LIBS ON CACHE BOOL "" FORCE)
set(WITH_OPENMP OFF CACHE BOOL "" FORCE)
set(WITH_STD_THREADS ON CACHE BOOL "" FORCE)
set(OPENVDB_CORE_SHARED OFF CACHE BOOL "" FORCE)
set(TBB_TEST OFF CACHE BOOL "" FORCE)

add_subdirectory("${COACD_SOURCE_DIR}" coacd EXCLUDE_FROM_ALL)

# ---------------------------------------------------------------------------
# coacd_native shared library
# ---------------------------------------------------------------------------

add_library(coacd_native SHARED coacd_capi.cpp)
set_target_properties(coacd_native PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED ON
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN ON
  POSITION_INDEPENDENT_CODE ON)
target_include_directories(coacd_native PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}"
  "${COACD_SOURCE_DIR}/public")
target_link_libraries(coacd_native PRIVATE coacd)

if(MSVC)
  target_compile_options(coacd_native PRIVATE /W3 /utf-8)
elseif(APPLE)
  target_link_options(coacd_native PRIVATE -Wl,-dead_strip)
else()
  # Self-contained: no dependency on the consumer's libstdc++ version, and
  # none of the statically linked dependencies' symbols are exported.
  target_link_options(coacd_native PRIVATE
    -static-libstdc++ -static-libgcc
    -Wl,--exclude-libs,ALL -Wl,--no-undefined -Wl,--as-needed)
endif()

install(TARGETS coacd_native
  RUNTIME DESTINATION .
  LIBRARY DESTINATION .)
