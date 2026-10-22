# Convex decomposition for .NET

.NET packages for approximate convex decomposition (ACD) of 3D meshes: turning
a mesh into a set of convex hulls for physics engines, collision detection and
motion planning.

| Package | What | Platforms | License |
|---|---|---|---|
| [`ConvexDecomposition.NET`](src/ConvexDecomposition.NET/README.md) | Core: `Mesh`, `IConvexDecomposer`, fallback chains, caching, hull post-processing, convex hulls, mass properties, mesh checks, OBJ I/O | any | MIT |
| [`CuACD.NET`](src/CuACD.NET/README.md) | [CuACD](https://github.com/eliphatfs/cuacd), NVIDIA GPU (sm_70+) | win-x64, linux-x64 | LGPL-2.1+ |
| [`CoACD.NET`](src/CoACD.NET/README.md) | [CoACD](https://github.com/SarahWeiii/CoACD), CPU, best hulls | win-x64, linux-x64, linux-arm64, osx-arm64 | MIT |
| [`VHACD.NET`](src/VHACD.NET/README.md) | [V-HACD 4](https://github.com/kmammou/v-hacd), CPU, fast | win-x64, linux-x64, linux-arm64, osx-arm64 | BSD-3-Clause |

Upstream's `49160.stl` (552 triangles), CPU backends on all cores, CuACD on an RTX 4080:

| Backend | Hulls | Time |
|---|---:|---:|
| CuACD.NET (τ = 0.05) | 144 | 0.28 s |
| VHACD.NET (defaults) | 64 | 3.9 s |
| CoACD.NET (τ = 0.05) | 148 | 25.5 s |

## GPU first, CPU fallback, cached, with engine limits

```csharp
using ConvexDecomposition;

using var decomposer = new FallbackDecomposer(
        () => new CuACD.CuAcdContext(poolBytes: 1L << 30),   // throws if there's no usable NVIDIA GPU
        () => new CoACD.CoAcdDecomposer())
    .WithCache(new DirectoryDecompositionCache("hull-cache"))
    .WithPostProcessing(new HullPostProcessor { MaxHulls = 20, MaxVerticesPerHull = 64 });

IReadOnlyList<Mesh> hulls = decomposer.Decompose(vertices, indices);   // Vector3[], int[]
```

See [`ConvexDecomposition.NET`](src/ConvexDecomposition.NET/README.md) for what each
piece does.

## Repository layout

```
src/ConvexDecomposition.NET/  core package (managed only)
src/CuACD.NET/                GPU backend
src/CoACD.NET/                CPU backend
src/VHACD.NET/                CPU backend
src/Shared/                   source compiled into several assemblies
native/cuacd/                 C ABI shim over CuACD's host C code
native/coacd/                 exception-safe C ABI shim over CoACD (C++)
native/vhacd/                 exception-safe C ABI shim over V-HACD (header-only C++)
external/                     upstream sources, pinned git submodules
eng/Native.targets            packs runtimes/<rid>/native assets
scripts/build-native.*        build the shims into artifacts/native/<rid>/
tests/                        xUnit tests (CuACD tests need an NVIDIA GPU)
```

## Building

```
git clone --recursive https://github.com/gmod-workshop/ConvexDecomposition.NET.git
./scripts/build-native.ps1          # Windows: VS 2022 C++ build tools + CMake 3.24+
bash scripts/build-native.sh        # Linux/macOS: gcc/clang + CMake; CuACD also needs libcuda
dotnet test
dotnet pack -c Release -o out
```

- The **CuACD** shim compiles upstream's host C code. `cuda.h` / `cuda.lib` come
  from NVIDIA's `nvidia-cuda-runtime-cu12` pip wheel, so no CUDA toolkit is
  needed. The GPU kernels (`kernels.fatbin`, the same on every OS) are taken
  from the official `cuacd` wheel on PyPI (hash-verified) and embedded in the
  managed assembly.
- The **CoACD** shim statically links CoACD and its preprocessing dependencies
  (OpenVDB, TBB, Boost, spdlog, all fetched by CoACD's CMake). The first build
  takes a few minutes. OpenMP is off in favour of CoACD's `std::thread`
  backend, which has the same speed and needs no `vcomp140.dll` / `libgomp`.
- The **V-HACD** shim compiles the single header. It builds in seconds.
- Every native library uses the static C/C++ runtime, and on Linux exports only
  its C API (CI checks this).

## Releasing

Versions live in each csproj (`VersionPrefix`). The backends track their
upstream version; bump the 4th component for wrapper-only fixes. Push a `v*`
tag and `.github/workflows/build.yml` builds the natives on all four platforms,
runs the core, CoACD and V-HACD tests on each, packs, and pushes to nuget.org
through nuget.org [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
(policy: this repo, workflow `build.yml`, owned by nuget.org user `dhkatz`). Packages whose version already exists are skipped.

Upgrading CuACD: the fatbin and the host C code must come from the same
upstream version, because kernels are launched with untyped argument lists.
Bump `external/cuacd` and the wheel URL/hashes in `CuACD.NET.csproj` together.
