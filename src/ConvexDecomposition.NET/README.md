# ConvexDecomposition.NET

The core of the .NET convex decomposition packages. It has the shared types
the backends implement, plus backend-independent tools, all in managed code:

| Backend package | Runs on | Speed (upstream's `49160.stl`) |
|---|---|---|
| [`CuACD.NET`](https://www.nuget.org/packages/CuACD.NET) | NVIDIA GPU | 0.28 s |
| [`VHACD.NET`](https://www.nuget.org/packages/VHACD.NET) | CPU | ~4 s, looser hulls |
| [`CoACD.NET`](https://www.nuget.org/packages/CoACD.NET) | CPU | ~25 s |
| `ConvexHullDecomposer` (this package) | CPU | instant: one hull |

## GPU first, CPU fallback, cached, with engine limits

```csharp
using ConvexDecomposition;

using var decomposer = new FallbackDecomposer(
        () => new CuACD.CuAcdContext(poolBytes: 1L << 30),   // throws if there's no usable NVIDIA GPU
        () => new CoACD.CoAcdDecomposer())
    {
        FallbackOccurred = e => log.Warn($"backend {e.BackendIndex} skipped: {e.Exception.Message}"),
    }
    .WithCache(new DirectoryDecompositionCache("hull-cache"))
    .WithPostProcessing(new HullPostProcessor { MaxHulls = 20, MaxVerticesPerHull = 64 });

IReadOnlyList<Mesh> hulls = decomposer.Decompose(vertices, indices);   // Vector3[], int[]
```

- **`FallbackDecomposer`** creates backends lazily, in order. A backend whose
  creation fails is skipped from then on. If a call fails (say the GPU runs out
  of memory on one huge mesh), only that call moves on to the next backend.
  Bad input (`ArgumentException`) is not retried.
- **`CachingDecomposer`** (`.WithCache`) keys results on the mesh data plus
  the backend's `Fingerprint`: algorithm, version and every setting that
  affects the output. Changing an option is never served a stale result.
  `DirectoryDecompositionCache` persists across runs; `MemoryDecompositionCache`
  lives in-process. Both are safe for concurrent use.
- **`HullPostProcessor`** (`.WithPostProcessing`) enforces the same limits
  whichever backend ran. CuACD has no hull-count or vertex limits of its own,
  and V-HACD tends to over-split. Put it *outside* the cache, as above, so that
  changing the limits reuses the cached decomposition.

Disposing the outer wrapper disposes everything inside it.

## Hull post-processing

Steps run in this order; each is off unless set:

| Property | Effect |
|---|---|
| `MinHullVolumeFraction` | Drop hulls below this fraction of the total volume (the largest always stays) |
| `MaxHulls` | Merge greedily, each time joining the pair whose combined hull adds the least volume |
| `Margin` | Move every face outwards (+) or inwards (−), e.g. for engines with a collision margin |
| `MaxVerticesPerHull` | Replace larger hulls with the hull of their farthest-first vertex subset (stays inside the original) |

On CoACD's 148-hull decomposition of `49160.stl`, `MaxHulls` reaches 32, 16
and 8 hulls within 4% of the volume CoACD's own `max_convex_hull` gives, in
0.15–1 s, where rerunning CoACD with the limit takes 60–84 s.

## Geometry tools

| API | Purpose |
|---|---|
| `ConvexHull.Compute(points, maxVertices)` | Quickhull in double precision; robust to coplanar and duplicate points |
| `ConvexHull.Offset(hull, distance)` | Grow or shrink a convex hull, keeping it convex |
| `mesh.ComputeMassProperties(density)` | Volume, mass, center of mass, inertia tensor |
| `MassProperties.Combine(parts)` | Whole-body properties of a set of hulls |
| `mesh.Analyze()` | Holes, non-manifold and inconsistently wound edges, degenerate and duplicate triangles, components, inside-out check |
| `mesh.Clean(weldTolerance)` | Weld vertices, drop degenerate, duplicate and unreferenced elements, fix inside-out winding |
| `mesh.ComputeVolume()`, `mesh.ComputeBounds()` | Basics, accurate far from the origin |
| `ObjFile.Read` / `ObjFile.Write` | Load meshes; dump hulls as one OBJ object each, for viewing |

Both CoACD and CuACD expect closed meshes and remesh anything else, which can
cost many extra hulls. `Analyze()` tells you which meshes to fix first, and
`Clean()` fixes the common case of unwelded triangle soup.

## Your own backend

Implement `IConvexDecomposer`: `Decompose(vertices, indices)` returns hulls in
the input's coordinate space, and `Fingerprint` identifies the algorithm and
settings for caching. Implementations should be thread-safe.

MIT licensed.
