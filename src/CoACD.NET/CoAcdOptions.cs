using System;
using System.Globalization;
using ConvexDecomposition;

namespace CoACD;

/// <summary>How each part is approximated.</summary>
public enum HullApproximation
{
    /// <summary>Convex hull (default).</summary>
    ConvexHull,
    /// <summary>Oriented bounding box.</summary>
    Box,
}

/// <summary>
/// CoACD settings. Names follow <c>coacd.run_coacd</c>; defaults are upstream's.
/// </summary>
public sealed class CoAcdOptions
{
    /// <summary>
    /// Concavity tolerance per part, in units of the normalized mesh (0–1).
    /// Lower → more, tighter parts. Default 0.05.
    /// </summary>
    public float Threshold { get; set; } = 0.05f;

    /// <summary>
    /// Upper bound on the number of hulls; hulls are merged until it is met.
    /// <c>null</c> (default) = no limit.
    /// </summary>
    public int? MaxConvexHulls { get; set; }

    /// <summary>
    /// Maximum vertices per hull. Setting this enables CoACD's hull decimation
    /// (upstream ignores <c>max_ch_vertex</c> unless <c>decimate</c> is on).
    /// <c>null</c> (default) = exact hulls, no limit.
    /// </summary>
    public int? MaxHullVertices { get; set; }

    /// <summary>Manifold remesh before decomposing. Default <see cref="PreprocessMode.Auto"/>.</summary>
    public PreprocessMode Preprocess { get; set; } = PreprocessMode.Auto;

    /// <summary>Remesh voxel resolution (5–1000). Default 50.</summary>
    public int PreprocessResolution { get; set; } = 50;

    /// <summary>Surface samples used to measure concavity. Default 2000.</summary>
    public int SampleResolution { get; set; } = 2000;

    /// <summary>Cutting candidates per MCTS search node. Default 20.</summary>
    public int MctsNodes { get; set; } = 20;

    /// <summary>MCTS iterations per cut. Default 150.</summary>
    public int MctsIterations { get; set; } = 150;

    /// <summary>MCTS lookahead depth. Default 3.</summary>
    public int MctsMaxDepth { get; set; } = 3;

    /// <summary>Align the mesh to its principal axes before cutting. Default false.</summary>
    public bool Pca { get; set; }

    /// <summary>Merge neighbouring hulls when concavity allows. Default true.</summary>
    public bool Merge { get; set; } = true;

    /// <summary>Extrude hulls along shared faces to close small gaps. Default false.</summary>
    public bool Extrude { get; set; }

    /// <summary>Extrusion distance when <see cref="Extrude"/> is on. Default 0.01.</summary>
    public float ExtrudeMargin { get; set; } = 0.01f;

    /// <summary>Part approximation shape. Default <see cref="HullApproximation.ConvexHull"/>.</summary>
    public HullApproximation Approximation { get; set; } = HullApproximation.ConvexHull;

    /// <summary>Random seed; same input + seed gives the same result. Default 0.</summary>
    public uint Seed { get; set; }

    /// <summary>
    /// Interpret <see cref="Threshold"/> in the mesh's own units instead of the
    /// normalized 0–1 scale (upstream <c>real_metric</c>). Default false.
    /// </summary>
    public bool RealMetric { get; set; }

    internal string ToFingerprint() => string.Create(CultureInfo.InvariantCulture,
        $"threshold={Threshold:R};maxHulls={MaxConvexHulls};maxVertices={MaxHullVertices};preprocess={Preprocess};" +
        $"prepRes={PreprocessResolution};sampleRes={SampleResolution};mcts={MctsNodes},{MctsIterations},{MctsMaxDepth};" +
        $"pca={Pca};merge={Merge};extrude={Extrude},{ExtrudeMargin:R};apx={Approximation};seed={Seed};realMetric={RealMetric}");

    internal void Validate()
    {
        if (!(Threshold > 0) || !float.IsFinite(Threshold) || (!RealMetric && Threshold > 1))
            throw new ArgumentOutOfRangeException(nameof(Threshold), Threshold,
                RealMetric ? "Must be a positive number." : "Must be in (0, 1]; set RealMetric for absolute units.");
        if (MaxConvexHulls is < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConvexHulls), MaxConvexHulls, "Must be at least 1, or null for no limit.");
        if (MaxHullVertices is < 4)
            throw new ArgumentOutOfRangeException(nameof(MaxHullVertices), MaxHullVertices, "Must be at least 4, or null for no limit.");
        if (PreprocessResolution is < 5 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(PreprocessResolution), PreprocessResolution, "Must be between 5 and 1000.");
        if (SampleResolution < 1 || MctsNodes < 1 || MctsIterations < 1 || MctsMaxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(SampleResolution),
                "SampleResolution and the MCTS settings must be at least 1.");
        if (!(ExtrudeMargin >= 0) || !float.IsFinite(ExtrudeMargin))
            throw new ArgumentOutOfRangeException(nameof(ExtrudeMargin), ExtrudeMargin, "Must not be negative.");
    }
}
