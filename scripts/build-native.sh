#!/usr/bin/env bash
# Build the native shims for this machine (linux-x64, linux-arm64 or osx-arm64)
# into artifacts/native/<rid>/.
#   scripts/build-native.sh                 # all libraries this platform supports
#   scripts/build-native.sh vhacd           # just some of: cuacd, coacd, vhacd
# Extra CMake args via CMAKE_ARGS, e.g. CMAKE_ARGS=-DCUDA_DRIVER_LIBRARY=/path/libcuda.so
# CuACD (linux-x64 only) needs libcuda: the driver, or a CUDA toolkit's stub.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)          rid=linux-x64;   ext=so;    libs="cuacd coacd vhacd" ;;
  Linux-aarch64)         rid=linux-arm64; ext=so;    libs="coacd vhacd" ;;
  Darwin-arm64)          rid=osx-arm64;   ext=dylib; libs="coacd vhacd" ;;
  *) echo "unsupported platform: $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac
[ $# -gt 0 ] && libs="$*"

jobs="$( (command -v nproc >/dev/null && nproc) || sysctl -n hw.ncpu)"
mkdir -p "$root/artifacts/native/$rid"
for lib in $libs; do
  build="$root/build/$lib-$rid"
  # shellcheck disable=SC2086
  cmake -S "$root/native/$lib" -B "$build" -DCMAKE_BUILD_TYPE=Release ${CMAKE_ARGS:-}
  cmake --build "$build" --target "${lib}_native" --parallel "$jobs"
  cp "$build/lib${lib}_native.$ext" "$root/artifacts/native/$rid/"
  echo "-> artifacts/native/$rid/lib${lib}_native.$ext"
done
