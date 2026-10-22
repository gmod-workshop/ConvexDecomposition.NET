cmake_minimum_required(VERSION 3.24)

# Static CRT on Windows so consumers need no VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
cmake_policy(SET CMP0091 NEW)

project(vhacd_native LANGUAGES CXX)

set(VHACD_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/vhacd" CACHE PATH
    "Path to the upstream V-HACD checkout (git submodule)")
if(NOT EXISTS "${VHACD_SOURCE_DIR}/include/VHACD.h")
  message(FATAL_ERROR "V-HACD sources not found at ${VHACD_SOURCE_DIR}; "
                      "run `git submodule update --init --recursive`.")
endif()

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

find_package(Threads REQUIRED)

# V-HACD is header-only; the shim compiles its implementation.
add_library(vhacd_native SHARED vhacd_capi.cpp)
set_target_properties(vhacd_native PROPERTIES
  CXX_STANDARD 17
  CXX_STANDARD_REQUIRED ON
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN ON
  POSITION_INDEPENDENT_CODE ON)
target_include_directories(vhacd_native PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}"
  "${VHACD_SOURCE_DIR}/include")
target_link_libraries(vhacd_native PRIVATE Threads::Threads)

if(MSVC)
  # V-HACD's own code trips several /W3 conversion warnings; they are upstream's.
  target_compile_options(vhacd_native PRIVATE /W1 /utf-8)
elseif(APPLE)
  target_link_options(vhacd_native PRIVATE -Wl,-dead_strip "-Wl,-exported_symbol,_vhacd_net_*")
else()
  # Self-contained: no dependency on the consumer's libstdc++ version. The
  # version script keeps the std:: template instantiations V-HACD pulls in
  # (which libstdc++ marks default-visibility) from being exported, where
  # they could interpose with the host process's libstdc++.
  target_link_options(vhacd_native PRIVATE
    -static-libstdc++ -static-libgcc
    -Wl,--exclude-libs,ALL -Wl,--no-undefined -Wl,--as-needed
    "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports.map")
  set_property(TARGET vhacd_native APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/exports.map")
endif()

install(TARGETS vhacd_native
  RUNTIME DESTINATION .
  LIBRARY DESTINATION .)
