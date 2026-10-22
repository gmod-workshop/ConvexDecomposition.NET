# Build the win-x64 native shims into artifacts/native/win-x64/.
# Needs Visual Studio 2022 (or Build Tools) with the C++ workload, and CMake >= 3.24.
#   ./scripts/build-native.ps1               # all of them
#   ./scripts/build-native.ps1 -Only vhacd   # just one of: cuacd, coacd, vhacd
param([ValidateSet('cuacd', 'coacd', 'vhacd')][string]$Only)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts/native/win-x64'
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($lib in 'cuacd', 'coacd', 'vhacd') {
    if ($Only -and $Only -ne $lib) { continue }
    $build = Join-Path $root "build/$lib-win-x64"
    cmake -S (Join-Path $root "native/$lib") -B $build -G 'Visual Studio 17 2022' -A x64
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    cmake --build $build --config Release --target "${lib}_native" --parallel
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Copy-Item (Join-Path $build "Release/${lib}_native.dll") $out -Force
    Write-Host "-> artifacts/native/win-x64/${lib}_native.dll"
}
