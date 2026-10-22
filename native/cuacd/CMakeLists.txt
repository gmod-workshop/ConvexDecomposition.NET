cmake_minimum_required(VERSION 3.21)

# Static CRT on Windows so consumers need no VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
cmake_policy(SET CMP0091 NEW)

project(cuacd_native LANGUAGES C)

set(CUACD_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external/cuacd" CACHE PATH
    "Path to the upstream cuacd checkout (git submodule)")

if(NOT EXISTS "${CUACD_SOURCE_DIR}/csrc/heap.c")
  message(FATAL_ERROR "cuacd sources not found at ${CUACD_SOURCE_DIR}; "
                      "run `git submodule update --init`.")
endif()

# ---------------------------------------------------------------------------
# CUDA driver API headers / import library
#
# Only cuda.h and the driver import library are needed (the kernels ship as a
# prebuilt fatbin). By default they come from NVIDIA's pip wheel
# nvidia-cuda-runtime-cu12 — the same build dependency upstream uses — so no
# CUDA toolkit install is required. Override with CUDA_INCLUDE_DIR /
# CUDA_DRIVER_LIBRARY to use a local toolkit instead.
# ---------------------------------------------------------------------------

set(_cudart_ver "12.9.79")
if(WIN32)
  set(_cudart_url "https://files.pythonhosted.org/packages/59/df/e7c3a360be4f7b93cee39271b792669baeb3846c58a4df6dfcf187a7ffab/nvidia_cuda_runtime_cu12-12.9.79-py3-none-win_amd64.whl")
  set(_cudart_sha "8e018af8fa02363876860388bd10ccb89eb9ab8fb0aa749aaf58430a9f7c4891")
else()
  set(_cudart_url "https://files.pythonhosted.org/packages/bc/46/a92db19b8309581092a3add7e6fceb4c301a3fd233969856a8cbf042cd3c/nvidia_cuda_runtime_cu12-12.9.79-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl")
  set(_cudart_sha "25bba2dfb01d48a9b59ca474a1ac43c6ebf7011f1b0b8cc44f54eb6ac48a96c3")
endif()

if(NOT CUDA_INCLUDE_DIR OR (WIN32 AND NOT CUDA_DRIVER_LIBRARY))
  set(_deps "${CMAKE_BINARY_DIR}/_deps/cudart-${_cudart_ver}")
  set(_whl "${_deps}/cudart.whl")
  if(NOT EXISTS "${_deps}/nvidia/cuda_runtime/include/cuda.h")
    message(STATUS "Downloading CUDA driver headers (nvidia-cuda-runtime-cu12 ${_cudart_ver})")
    file(DOWNLOAD "${_cudart_url}" "${_whl}"
         EXPECTED_HASH SHA256=${_cudart_sha} TLS_VERIFY ON SHOW_PROGRESS)
    file(ARCHIVE_EXTRACT INPUT "${_whl}" DESTINATION "${_deps}"
         PATTERNS "nvidia/cuda_runtime/include/*" "nvidia/cuda_runtime/lib/*")
    file(REMOVE "${_whl}")
  endif()
  if(NOT CUDA_INCLUDE_DIR)
    set(CUDA_INCLUDE_DIR "${_deps}/nvidia/cuda_runtime/include")
  endif()
  if(WIN32 AND NOT CUDA_DRIVER_LIBRARY)
    set(CUDA_DRIVER_LIBRARY "${_deps}/nvidia/cuda_runtime/lib/x64/cuda.lib")
  endif()
endif()

if(NOT CUDA_DRIVER_LIBRARY)
  # The pip wheel has no libcuda stub on Linux: use a toolkit's stub or the
  # driver itself (both carry SONAME libcuda.so.1).
  find_library(CUDA_DRIVER_LIBRARY NAMES cuda
    HINTS ENV CUDA_HOME ENV CUDA_PATH
    PATH_SUFFIXES lib64/stubs lib/stubs targets/x86_64-linux/lib/stubs lib64 lib
    PATHS /usr/local/cuda /usr/lib/wsl/lib /usr/lib/x86_64-linux-gnu)
endif()
if(NOT CUDA_DRIVER_LIBRARY)
  message(FATAL_ERROR "libcuda not found: install the CUDA driver dev package "
                      "(e.g. cuda-driver-dev-12-x) or pass -DCUDA_DRIVER_LIBRARY=...")
endif()
message(STATUS "CUDA include dir: ${CUDA_INCLUDE_DIR}")
message(STATUS "CUDA driver lib:  ${CUDA_DRIVER_LIBRARY}")

# ---------------------------------------------------------------------------
# cuacd_native shared library
# ---------------------------------------------------------------------------

add_library(cuacd_native SHARED
  cuacd_capi.c
  "${CUACD_SOURCE_DIR}/csrc/heap.c"
  "${CUACD_SOURCE_DIR}/csrc/lookahead.c"
  "${CUACD_SOURCE_DIR}/csrc/test.c"
  "${CUACD_SOURCE_DIR}/csrc/postprocess.c"
  "${CUACD_SOURCE_DIR}/csrc/preprocess.c"
)

set_target_properties(cuacd_native PROPERTIES
  C_STANDARD 11
  C_STANDARD_REQUIRED ON
  C_VISIBILITY_PRESET hidden
  POSITION_INDEPENDENT_CODE ON)

target_include_directories(cuacd_native PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}"
  "${CUACD_SOURCE_DIR}/csrc"
  "${CUDA_INCLUDE_DIR}")

target_link_libraries(cuacd_native PRIVATE "${CUDA_DRIVER_LIBRARY}")

if(MSVC)
  target_compile_definitions(cuacd_native PRIVATE _CRT_SECURE_NO_WARNINGS)
  target_compile_options(cuacd_native PRIVATE /O2 /W3 /utf-8)
else()
  # Same as upstream setup.py: exposes clock_gettime for the timing macros.
  target_compile_definitions(cuacd_native PRIVATE _POSIX_C_SOURCE=199309L)
  target_compile_options(cuacd_native PRIVATE -O2)
  target_link_libraries(cuacd_native PRIVATE m)
  target_link_options(cuacd_native PRIVATE -Wl,--no-undefined -Wl,--as-needed)
endif()

install(TARGETS cuacd_native
  RUNTIME DESTINATION .
  LIBRARY DESTINATION .)
