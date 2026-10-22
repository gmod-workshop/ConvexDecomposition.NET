using System;
using System.Globalization;

namespace VHACD;

/// <summary>How V-HACD decides which voxels are inside the mesh.</summary>
public enum VhacdFillMode
{
    /// <summary>Flood-fill from outside (default). Works for closed meshes.</summary>
    FloodFill,
    /// <summary>
    /// Only the surface voxels: hulls wrap the surface, as for an open or
    /// shell-like mesh that should stay hollow.
    /// </summary>
    SurfaceOnly,
    /// <summary>
    /// Ray-cast to classify voxels. Use when a mesh with small holes leaks
    /// during flood-fill and comes out hollow.
    /// </summary>
    Raycast,
}

/// <summary>V-HACD settings. Defaults are upstream's.</summary>
public sealed class VhacdOptions
{
    /// <summary>Maximum number of hulls (1–100,000). Default 64.</summary>
    public int MaxConvexHulls { get; set; } = 64;

    /// <summary>
    /// Total voxel count of the grid the mesh is voxelized into
    /// (10,000–10,000,000). Higher captures finer detail, slower. Default 400,000.
    /// </summary>
    public int Resolution { get; set; } = 400_000;

    /// <summary>
    /// Stop splitting a part once its hull is within this percentage of its
    /// voxel volume (0.001–10). Lower → more, tighter hulls. Default 1.
    /// </summary>
    public double MinVolumeErrorPercent { get; set; } = 1.0;

    /// <summary>Maximum recursive splits (2–64). Default 10.</summary>
    public int MaxRecursionDepth { get; set; } = 10;

    /// <summary>Snap hull vertices back onto the source mesh surface. Default true.</summary>
    public bool ShrinkWrap { get; set; } = true;

    /// <summary>Voxel interior classification. Default <see cref="VhacdFillMode.FloodFill"/>.</summary>
    public VhacdFillMode FillMode { get; set; } = VhacdFillMode.FloodFill;

    /// <summary>Maximum vertices per hull (8–2048). Default 64.</summary>
    public int MaxVerticesPerHull { get; set; } = 64;

    /// <summary>Use V-HACD's internal thread pool (8 threads). Default true.</summary>
    public bool Multithreaded { get; set; } = true;

    /// <summary>Stop splitting parts smaller than this many voxels on every side (1–32). Default 2.</summary>
    public int MinEdgeLength { get; set; } = 2;

    /// <summary>Search for the best split plane instead of the midpoint (experimental, slower). Default false.</summary>
    public bool FindBestPlane { get; set; }

    // Multithreaded only affects speed, so it is left out.
    internal string ToFingerprint() => string.Create(CultureInfo.InvariantCulture,
        $"maxHulls={MaxConvexHulls};resolution={Resolution};error={MinVolumeErrorPercent:R};depth={MaxRecursionDepth};" +
        $"shrinkWrap={ShrinkWrap};fill={FillMode};maxVertices={MaxVerticesPerHull};minEdge={MinEdgeLength};bestPlane={FindBestPlane}");

    internal void Validate()
    {
        Check(MaxConvexHulls, 1, 100_000, nameof(MaxConvexHulls));
        Check(Resolution, 10_000, 10_000_000, nameof(Resolution));
        if (!(MinVolumeErrorPercent >= 0.001 && MinVolumeErrorPercent <= 10))
            throw new ArgumentOutOfRangeException(nameof(MinVolumeErrorPercent), MinVolumeErrorPercent, "Must be between 0.001 and 10.");
        Check(MaxRecursionDepth, 2, 64, nameof(MaxRecursionDepth));
        Check(MaxVerticesPerHull, 8, 2048, nameof(MaxVerticesPerHull));
        Check(MinEdgeLength, 1, 32, nameof(MinEdgeLength));
        if (!Enum.IsDefined(FillMode))
            throw new ArgumentOutOfRangeException(nameof(FillMode), FillMode, "Unknown fill mode.");

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(name, value, $"Must be between {min:N0} and {max:N0}.");
        }
    }
}
