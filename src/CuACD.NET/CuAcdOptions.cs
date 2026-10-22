using System;
using System.Globalization;
using ConvexDecomposition;

namespace CuACD;

/// <summary>
/// CuACD settings. Defaults match the upstream <c>cuacd</c> CLI, which is the
/// configuration the CuACD paper benchmarks.
/// </summary>
public sealed class CuAcdOptions
{
    /// <summary>
    /// Concavity tolerance per part, in units of the normalized mesh (largest
    /// extent = 2) — the same convention as CoACD's <c>threshold</c>. Lower →
    /// more, tighter parts. Default 0.05.
    /// </summary>
    public float Threshold { get; set; } = 0.05f;

    /// <summary>Maximum outer iterations. Default 100.</summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>Candidate cuts per full expansion level. Default 30.</summary>
    public int Width { get; set; } = 30;

    /// <summary>Candidate cuts at deeper expansion levels. Default 5.</summary>
    public int Width2 { get; set; } = 5;

    /// <summary>Full lookahead expansion levels. Default 2.</summary>
    public int Depth { get; set; } = 2;

    /// <summary>Cheap best-axis-midpoint levels after <see cref="Depth"/>. Default 0.</summary>
    public int QuickDepth { get; set; }

    /// <summary>Parts cut in parallel per iteration. Default 16.</summary>
    public int MaxParallelCuts { get; set; } = 16;

    /// <summary>Split the input into connected components first. Default true.</summary>
    public bool DecomposeComponents { get; set; } = true;

    /// <summary>Also split disconnected fragments after every cut. Default true.</summary>
    public bool DecomposeComponentsPerIteration { get; set; } = true;

    /// <summary>Concave edges sampled per cutting part to propose cut planes. Default 16.</summary>
    public int ConcaveEdgeSamples { get; set; } = 16;

    /// <summary>Offset for planes derived from concave edges. Default 0.005.</summary>
    public float ConcaveEdgeEpsilon { get; set; } = 0.005f;

    /// <summary>Dihedral angle (radians) below which an edge counts as concave. Default 3.49 (≈200°).</summary>
    public float ConcaveAngleThreshold { get; set; } = 3.49f;

    /// <summary>Number of leading iterations that sample concave edges. Default 10.</summary>
    public int ConcaveEdgeIterations { get; set; } = 10;

    /// <summary>Greedy merge pass that fuses neighbouring hulls when concavity allows. Default true.</summary>
    public bool MergeHulls { get; set; } = true;

    /// <summary>
    /// Remesh before decomposing. Default <see cref="PreprocessMode.Auto"/>, which
    /// remeshes when <see cref="CuAcdContext.CheckMesh"/> finds any defect.
    /// </summary>
    public PreprocessMode Preprocess { get; set; } = PreprocessMode.Auto;

    /// <summary>Remesh voxel grid resolution (power of two ≥ 8). Default 64.</summary>
    public int PreprocessResolution { get; set; } = 64;

    /// <summary>
    /// Scale/center the mesh so its largest extent is 2 before decomposing, and
    /// map results back afterwards. CuACD requires normalized input; only turn
    /// this off if your mesh already is. Default true.
    /// </summary>
    public bool Normalize { get; set; } = true;

    /// <summary>Print upstream progress diagnostics to stderr. Default false.</summary>
    public bool Verbose { get; set; }

    // Verbose only affects logging, so it is left out.
    internal string ToFingerprint() => string.Create(CultureInfo.InvariantCulture,
        $"threshold={Threshold:R};iters={MaxIterations};width={Width},{Width2};depth={Depth},{QuickDepth};" +
        $"cuts={MaxParallelCuts};components={DecomposeComponents},{DecomposeComponentsPerIteration};" +
        $"concave={ConcaveEdgeSamples},{ConcaveEdgeEpsilon:R},{ConcaveAngleThreshold:R},{ConcaveEdgeIterations};" +
        $"merge={MergeHulls};preprocess={Preprocess},{PreprocessResolution};normalize={Normalize}");

    internal void Validate()
    {
        if (!(Threshold > 0) || !float.IsFinite(Threshold))
            throw new ArgumentOutOfRangeException(nameof(Threshold), Threshold, "Must be a positive number.");
        if (MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), MaxIterations, "Must be at least 1.");
        if (Width < 1 || Width2 < 1)
            throw new ArgumentOutOfRangeException(nameof(Width), "Width and Width2 must be at least 1.");
        if (Depth < 0 || QuickDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(Depth), "Depth and QuickDepth must not be negative.");
        if (MaxParallelCuts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxParallelCuts), MaxParallelCuts, "Must be at least 1.");
        if (ConcaveEdgeSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(ConcaveEdgeSamples), ConcaveEdgeSamples, "Must not be negative.");
        if (Width + 4 * ConcaveEdgeSamples >= 512)
            throw new ArgumentException(
                $"Width + 4 * ConcaveEdgeSamples must be < 512 (got {Width + 4 * ConcaveEdgeSamples}).");
        if (Preprocess != PreprocessMode.Off && !IsValidResolution(PreprocessResolution))
            throw new ArgumentOutOfRangeException(nameof(PreprocessResolution), PreprocessResolution,
                "Must be a power of two >= 8.");
    }

    internal static bool IsValidResolution(int r) => r >= 8 && (r & (r - 1)) == 0;
}
