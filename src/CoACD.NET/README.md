# CoACD.NET

.NET bindings for [CoACD](https://github.com/SarahWeiii/CoACD), *Approximate
Convex Decomposition for 3D Meshes with Collision-Aware Concavity and Tree
Search* (Wei et al., SIGGRAPH 2022). It runs on the CPU, on any machine.

```
dotnet add package CoACD.NET
```

Platforms: **win-x64**, **linux-x64**, **linux-arm64**, **osx-arm64**, on
**.NET 8** or later. There are no runtime dependencies: no Python, no VC++
redistributable, no OpenMP runtime.

## Usage

```csharp
using ConvexDecomposition;
using CoACD;

var coacd = new CoAcdDecomposer(new CoAcdOptions
{
    Threshold = 0.05f,
    MaxHullVertices = 64,   // e.g. a physics engine's per-hull vertex cap
});

IReadOnlyList<Mesh> hulls = coacd.Decompose(vertices, indices);   // Vector3[] + int[]
foreach (Mesh hull in hulls)
{
    // hull.Vertices / hull.Indices, in the input's coordinate space
}
```

`CoAcdDecomposer` is stateless and thread-safe. Each call already uses all
cores, and concurrent calls work too.

## What this wrapper fixes compared with calling `lib_coacd` directly

CoACD is built from source and linked into a small native shim, instead of
binding upstream's `extern "C"` API:

- **No process crashes.** Upstream's `CoACD_run` lets C++ exceptions escape
  (for example *"CoACD threshold > 1"* or internal MCTS errors). Through
  P/Invoke that terminates the process. Here they become a `CoAcdException`.
- **`MaxHullVertices` actually applies.** Upstream ignores `max_ch_vertex`
  unless `decimate` is also true. Setting `MaxHullVertices` turns decimation on.
- **No leak per call.** Upstream's `CoACD_freeMeshArray` leaks its array.
- **Quiet by default.** CoACD prints progress to stdout at `info` level; this
  wrapper defaults to `CoAcdDecomposer.LogLevel = CoAcdLogLevel.Off`.
- **Plain `Vector3[]` / `int[]`** in and out: no `double[]` staging and no
  manual marshalling.
- Native libraries ship in the package under `runtimes/<rid>/native`, so they
  also resolve in plugin `AssemblyLoadContext`s that use
  `AssemblyDependencyResolver`.

## Options

`CoAcdOptions` mirrors `coacd.run_coacd`, with upstream defaults:

| `CoAcdOptions` | CoACD | Default |
|---|---|---|
| `Threshold` | `threshold` | 0.05 |
| `MaxConvexHulls` | `max_convex_hull` | `null` (no limit) |
| `MaxHullVertices` | `max_ch_vertex` + `decimate` | `null` (exact hulls) |
| `Preprocess` | `preprocess_mode` | `Auto` |
| `PreprocessResolution` | `preprocess_resolution` | 50 |
| `SampleResolution` | `resolution` | 2000 |
| `MctsNodes` / `MctsIterations` / `MctsMaxDepth` | `mcts_*` | 20 / 150 / 3 |
| `Pca` | `pca` | false |
| `Merge` | `merge` | true |
| `Extrude` / `ExtrudeMargin` | `extrude` / `extrude_margin` | false / 0.01 |
| `Approximation` | `apx_mode` | `ConvexHull` |
| `Seed` | `seed` | 0 (results are deterministic per seed) |
| `RealMetric` | `real_metric` | false |

Need speed? On an NVIDIA GPU, [`CuACD.NET`](https://www.nuget.org/packages/CuACD.NET)
does the same job about 90× faster; on any CPU,
[`VHACD.NET`](https://www.nuget.org/packages/VHACD.NET) is several times faster
with looser hulls. All implement `IConvexDecomposer` from
[`ConvexDecomposition.NET`](https://www.nuget.org/packages/ConvexDecomposition.NET),
which can chain them with automatic fallback and cache the results.

Changing `MaxConvexHulls` or `MaxHullVertices` reruns the whole decomposition.
To try different budgets on one result, decompose once and apply
`HullPostProcessor` from `ConvexDecomposition.NET` instead: on upstream's
`49160.stl` it reaches 32, 16 and 8 hulls within 4% of the volume CoACD's own
limit gives, in under a second instead of over a minute per budget.

## License

MIT, the same as CoACD. If it is useful, please cite:

> Wei, X., Liu, M., Ling, Z., & Su, H. (2022). Approximate convex decomposition
> for 3D meshes with collision-aware concavity and tree search. *ACM
> Transactions on Graphics, 41*(4), 1–18.
