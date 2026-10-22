# VHACD.NET

.NET bindings for [V-HACD 4](https://github.com/kmammou/v-hacd) (Volumetric
Hierarchical Approximate Convex Decomposition, Khaled Mamou). It voxelizes the
mesh and recursively splits the voxels, which makes it much faster than CoACD,
at the cost of looser hulls. It runs on the CPU, on any machine.

```
dotnet add package VHACD.NET
```

Platforms: **win-x64**, **linux-x64**, **linux-arm64**, **osx-arm64**, on
**.NET 8** or later, with no runtime dependencies (no VC++ redistributable).

| `49160.stl` (552 tris) | Hulls | Time |
|---|---:|---:|
| CoACD.NET (τ = 0.05) | 148 | 25.5 s |
| **VHACD.NET** (defaults) | 64 | 3.9 s |
| CuACD.NET (τ = 0.05, RTX 4080) | 144 | 0.28 s |

## Usage

```csharp
using ConvexDecomposition;
using VHACD;

var vhacd = new VhacdDecomposer(new VhacdOptions
{
    MaxConvexHulls = 32,
    MaxVerticesPerHull = 32,
});

IReadOnlyList<Mesh> hulls = vhacd.Decompose(vertices, indices);   // Vector3[] + int[]
```

`VhacdDecomposer` is stateless and thread-safe. Each call runs on V-HACD's own
8-thread pool, and concurrent calls work too. Hulls come back in the input's
coordinate space.

## Options

`VhacdOptions` mirrors `VHACD::IVHACD::Parameters`, with upstream defaults:

| `VhacdOptions` | V-HACD | Default |
|---|---|---|
| `MaxConvexHulls` | `m_maxConvexHulls` | 64 |
| `Resolution` | `m_resolution` (total voxels) | 400,000 |
| `MinVolumeErrorPercent` | `m_minimumVolumePercentErrorAllowed` | 1 |
| `MaxRecursionDepth` | `m_maxRecursionDepth` | 10 |
| `ShrinkWrap` | `m_shrinkWrap` | true |
| `FillMode` | `m_fillMode` | `FloodFill` |
| `MaxVerticesPerHull` | `m_maxNumVerticesPerCH` | 64 |
| `Multithreaded` | `m_asyncACD` | true |
| `MinEdgeLength` | `m_minEdgeLength` | 2 |
| `FindBestPlane` | `m_findBestPlane` | false |

### Things to know

- **V-HACD over-splits by default.** The voxelized volume rarely gets within
  1% of a part's hull, so V-HACD keeps splitting until it reaches
  `MaxConvexHulls`. That holds even for a sphere or a seven-cube U shape
  (34 hulls), and upstream's own `TestVHACD` gives the same counts. Lower
  `MaxConvexHulls`, raise `MinVolumeErrorPercent`, or merge afterwards with
  `HullPostProcessor` from `ConvexDecomposition.NET`.
- **Hollow results** on meshes with small holes: try `FillMode = Raycast`.
  For shells that should stay hollow, use `SurfaceOnly`.
- Upstream has archived V-HACD (no longer maintained) and points to CoACD.
  It remains widely used and is the fast option here; for the best hulls,
  use CoACD (CPU) or CuACD (GPU) behind the same `IConvexDecomposer` interface.

## License

BSD-3-Clause, the same as V-HACD. This build is upstream commit `f900e42`,
which includes a shrink-wrap fix (#154) made after the v4.1.0 tag.
