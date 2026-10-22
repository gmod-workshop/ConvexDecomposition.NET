# CuACD.NET

.NET bindings for [CuACD](https://github.com/eliphatfs/cuacd), a fully
GPU-resident **approximate convex decomposition** of 3D meshes (SIGGRAPH Asia
2026). It is the same lookahead-search formulation as
[CoACD](https://github.com/SarahWeiii/CoACD), run entirely on an NVIDIA GPU,
which makes it one to two orders of magnitude faster at matched quality.

| `49160.stl` (552 tris, τ = 0.05), RTX 4080 | Hulls | Time |
|---|---:|---:|
| CoACD.NET / CoACD 1.0.14 (CPU) | 148–150 | 25.5 s |
| **CuACD.NET** | **144** | **0.28 s** |

Part of a family of packages: [`CoACD.NET`](https://www.nuget.org/packages/CoACD.NET)
and [`VHACD.NET`](https://www.nuget.org/packages/VHACD.NET) run on the CPU, and
all of them implement `IConvexDecomposer` from
[`ConvexDecomposition.NET`](https://www.nuget.org/packages/ConvexDecomposition.NET),
which also provides GPU-to-CPU fallback, caching and hull post-processing.

## Requirements

- **NVIDIA GPU**, Volta (sm_70) or newer, and an NVIDIA driver with CUDA 12
  support. No CUDA toolkit is needed: the kernels ship precompiled (sm_70 to
  sm_120, plus PTX for newer GPUs).
- **win-x64** or **linux-x64** (glibc 2.17+), **.NET 8** or later.

Check at runtime with `CuAcdContext.IsAvailable(out string? reason)`. It is
cheap and creates no context. Or let `FallbackDecomposer` handle it (see
[`ConvexDecomposition.NET`](https://www.nuget.org/packages/ConvexDecomposition.NET)).

## Usage

```csharp
using ConvexDecomposition;
using CuACD;

// Creating a context takes a fraction of a second (CUDA init + kernel load),
// so make one and reuse it. It is thread-safe; calls are serialized.
using var cuacd = new CuAcdContext();

IReadOnlyList<ConvexPart> parts = cuacd.Decompose(vertices, indices,
    new CuAcdOptions { Threshold = 0.05f });

foreach (ConvexPart part in parts)
{
    Mesh hull = part.Hull;   // same coordinate space as the input
    // hull.Vertices (Vector3[]), hull.Indices (int[], 3 per triangle)
}
```

Input is normalized internally (largest extent → 2, as CoACD does), and results
are mapped back to the input's coordinate space. Each `ConvexPart` also carries
`Piece`, the fragment of the original mesh it wraps, plus `HullVolume` and
`PieceVolume`. Through `IConvexDecomposer` you get just the hulls, using
`DefaultOptions`.

Other `CuAcdContext` members:

| Member | Purpose |
|---|---|
| `CheckMesh(v, i)` | GPU topology audit → `MeshAudit` (watertight / manifold / oriented) |
| `Preprocess(v, i, resolution = 64)` | Remesh to a watertight manifold (PaMO stage 1) |
| `ComputeConvexHull(points)` | Exact GPU convex hull of a point cloud |
| `DefaultOptions` | Options used when a call passes none |
| `DeviceName`, `PoolUsage` | Diagnostics |

### Options vs. CoACD

| CoACD | `CuAcdOptions` |
|---|---|
| `threshold` | `Threshold`: same meaning and scale |
| `preprocess_mode` | `Preprocess` (`Auto`/`On`/`Off`) |
| `preprocess_resolution` (default 50) | `PreprocessResolution` (default 64; **128 ≈ CoACD's 50**) |
| `merge` | `MergeHulls` |
| `mcts_iterations`, `mcts_max_depth`, `mcts_nodes` | `MaxIterations`, `Depth`, `Width` / `Width2` (different search) |
| `max_convex_hull` | none here: use `HullPostProcessor.MaxHulls` |
| `max_ch_vertex` + `decimate` | none here: use `HullPostProcessor.MaxVerticesPerHull` |
| `seed` | none: results vary slightly from run to run (part count ±1–2) |

Defaults match the upstream `cuacd` CLI (width 30/5, depth 2, 16 concave
edges, component splitting and hull merging on). That is the configuration
the paper benchmarks.

### Things to know

- **VRAM.** By default a context reserves 80% of free VRAM for its device heap
  (upstream behaviour). If you share the GPU with a renderer, pass a budget:
  `new CuAcdContext(poolBytes: 1L << 30)`. Typical meshes use a few hundred MiB
  (see `PoolUsage`).
- **Open meshes.** With `Preprocess = Auto`, meshes that aren't watertight get
  remeshed. An open surface becomes a thin closed shell, which can need many
  hulls. Close holes upstream when you can.
- **Errors.** Failures throw `CuAcdException`. After a CUDA fault the context is
  unusable: dispose it and create a new one.

## License

LGPL-2.1-or-later, the same as CuACD. Referencing the package from your own
(including closed-source) application is fine, since it is dynamically linked.
Modified versions of this library must remain LGPL.

> Shi, R., Wei, X., Xiang, F., Xu, Z., & Su, H. (2026). CuACD: A fully
> GPU-resident approximate convex decomposition. In *SIGGRAPH Asia 2026
> Conference Papers*. ACM. https://doi.org/10.1145/3829340.3842217
