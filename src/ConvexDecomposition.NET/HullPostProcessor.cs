using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using ConvexDecomposition.Geometry;

namespace ConvexDecomposition;

/// <summary>
/// Backend-independent clean-up of a decomposition's hulls: drop tiny hulls,
/// merge down to a hull budget, offset by a margin, and cap vertices per hull.
/// Steps run in that order; unset properties are skipped.
/// </summary>
/// <remarks>Stateless apart from its settings; <see cref="Process"/> is thread-safe.</remarks>
public sealed class HullPostProcessor
{
    /// <summary>
    /// Merge hulls until at most this many remain, each time joining the pair
    /// whose combined hull adds the least volume. <c>null</c> (default) = no limit.
    /// </summary>
    public int? MaxHulls { get; set; }

    /// <summary>
    /// Maximum vertices per hull (at least 4). Larger hulls are replaced by the
    /// hull of their farthest-first vertex subset, which lies slightly inside
    /// the original. <c>null</c> (default) = no limit.
    /// </summary>
    public int? MaxVerticesPerHull { get; set; }

    /// <summary>
    /// Remove hulls smaller than this fraction of the total hull volume
    /// (0–1), e.g. 0.001 for 0.1%. The largest hull is always kept. Removed
    /// hulls leave a gap in the collision shape, so keep this small.
    /// <c>null</c> (default) = keep all.
    /// </summary>
    public float? MinHullVolumeFraction { get; set; }

    /// <summary>
    /// Move every hull face outwards (positive) or inwards (negative) by this
    /// distance, in mesh units; see <see cref="ConvexHull.Offset"/>. Useful for
    /// engines that add a collision margin around convex shapes. Default 0.
    /// </summary>
    public float Margin { get; set; }

    /// <summary>Settings as a stable string, for <see cref="IConvexDecomposer.Fingerprint"/>.</summary>
    public string Fingerprint => string.Create(CultureInfo.InvariantCulture,
        $"post/1;maxHulls={MaxHulls};maxVertices={MaxVerticesPerHull};minVolume={MinHullVolumeFraction:R};margin={Margin:R}");

    /// <summary>Applies the configured steps.</summary>
    /// <param name="hulls">Convex hulls, e.g. a decomposition's output.</param>
    /// <returns>New hull meshes; <paramref name="hulls"/> itself when nothing is configured.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public IReadOnlyList<Mesh> Process(IReadOnlyList<Mesh> hulls)
    {
        ArgumentNullException.ThrowIfNull(hulls);
        Validate();
        if (hulls.Count == 0 ||
            (MaxHulls is null && MaxVerticesPerHull is null && MinHullVolumeFraction is null && Margin == 0))
            return hulls;

        var parts = new List<Part>(hulls.Count);
        foreach (Mesh h in hulls)
            parts.Add(Part.From(h));

        if (MinHullVolumeFraction is float fraction)
            RemoveSmall(parts, fraction);
        if (MaxHulls is int maxHulls && parts.Count > maxHulls)
            parts = Merge(parts, maxHulls);

        // Flat parts pass through untouched (two merged flat parts are dropped).
        var result = new Mesh?[parts.Count];
        Parallel.For(0, parts.Count, i =>
        {
            Part p = parts[i];
            if (p.Hull is null)
            {
                result[i] = p.Source;
                return;
            }
            HullResult hull = p.Hull;
            if (Margin != 0)
                hull = HullOperations.Offset(hull, Margin);
            if (MaxVerticesPerHull is int maxVertices && hull.Vertices.Length > maxVertices &&
                QuickHull.TryCompute(hull.Vertices, maxVertices, out HullResult reduced))
                hull = reduced;
            result[i] = hull.ToMesh();
        });
        return Array.FindAll(result, m => m is not null)!;
    }

    private void Validate()
    {
        if (MaxHulls is < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxHulls), MaxHulls, "Must be at least 1, or null for no limit.");
        if (MaxVerticesPerHull is < 4)
            throw new ArgumentOutOfRangeException(nameof(MaxVerticesPerHull), MaxVerticesPerHull, "Must be at least 4, or null for no limit.");
        if (MinHullVolumeFraction is float f && !(f >= 0 && f < 1))
            throw new ArgumentOutOfRangeException(nameof(MinHullVolumeFraction), f, "Must be in [0, 1), or null to keep all hulls.");
        if (!float.IsFinite(Margin))
            throw new ArgumentOutOfRangeException(nameof(Margin), Margin, "Must be finite.");
    }

    private static void RemoveSmall(List<Part> parts, float fraction)
    {
        double total = 0;
        int largest = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            total += parts[i].Volume;
            if (parts[i].Volume > parts[largest].Volume)
                largest = i;
        }
        double min = fraction * total;
        Part keep = parts[largest];
        parts.RemoveAll(p => p != keep && p.Volume < min);
    }

    // Greedy agglomerative merge: always join the pair whose hull grows the
    // least, i.e. vol(hull(A ∪ B)) − vol(A) − vol(B).
    private static List<Part> Merge(List<Part> parts, int target)
    {
        int n = parts.Count;
        Part[] items = parts.ToArray();
        var cost = new double[n, n];
        Parallel.For(0, n, i =>
        {
            for (int j = i + 1; j < n; j++)
                cost[i, j] = MergeCost(items[i], items[j]);
        });

        var alive = new bool[n];
        Array.Fill(alive, true);
        for (int count = n; count > target; count--)
        {
            int bi = -1, bj = -1;
            double best = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (!alive[i])
                    continue;
                for (int j = i + 1; j < n; j++)
                    if (alive[j] && cost[i, j] < best)
                    {
                        best = cost[i, j];
                        bi = i;
                        bj = j;
                    }
            }

            items[bi] = Part.Merge(items[bi], items[bj]);
            alive[bj] = false;
            items[bj] = null!;
            Part merged = items[bi];
            Parallel.For(0, n, k =>
            {
                if (k == bi || !alive[k])
                    return;
                double c = MergeCost(merged, items[k]);
                if (k < bi)
                    cost[k, bi] = c;
                else
                    cost[bi, k] = c;
            });
        }

        var result = new List<Part>(target);
        for (int i = 0; i < n; i++)
            if (alive[i])
                result.Add(items[i]);
        return result;
    }

    private static double MergeCost(Part a, Part b)
    {
        Part m = Part.Merge(a, b);
        return m.Volume - a.Volume - b.Volume;
    }

    private sealed class Part
    {
        private Part(Double3[] points, HullResult? hull, Mesh? source)
        {
            Points = points;
            Hull = hull;
            Source = source;
            Volume = hull?.ComputeVolume() ?? 0;
        }

        /// <summary>Hull vertices, or the raw points of a flat part.</summary>
        public Double3[] Points { get; }

        /// <summary>The hull, or null for a flat (zero-volume) part.</summary>
        public HullResult? Hull { get; }

        /// <summary>The input mesh, kept to pass flat parts through unchanged.</summary>
        public Mesh? Source { get; }

        public double Volume { get; }

        public static Part From(Mesh mesh)
        {
            var points = new Double3[mesh.Vertices.Length];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Double3(mesh.Vertices[i]);
            return QuickHull.TryCompute(points, int.MaxValue, out HullResult hull)
                ? new Part(hull.Vertices, hull, null)
                : new Part(points, null, mesh);
        }

        public static Part Merge(Part a, Part b)
        {
            var points = new Double3[a.Points.Length + b.Points.Length];
            a.Points.CopyTo(points, 0);
            b.Points.CopyTo(points, a.Points.Length);
            return QuickHull.TryCompute(points, int.MaxValue, out HullResult hull)
                ? new Part(hull.Vertices, hull, null)
                : new Part(points, null, null);
        }
    }
}

/// <summary>
/// Runs a decomposer, then a <see cref="HullPostProcessor"/> on its output.
/// Create with <see cref="ConvexDecomposerExtensions.WithPostProcessing"/>.
/// </summary>
public sealed class PostProcessingDecomposer : IConvexDecomposer, IDisposable
{
    /// <summary>Wraps <paramref name="inner"/>; disposing this disposes it.</summary>
    public PostProcessingDecomposer(IConvexDecomposer inner, HullPostProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(processor);
        Inner = inner;
        Processor = processor;
    }

    /// <summary>The wrapped decomposer.</summary>
    public IConvexDecomposer Inner { get; }

    /// <summary>The post-processing settings.</summary>
    public HullPostProcessor Processor { get; }

    /// <inheritdoc/>
    public string Fingerprint => Inner.Fingerprint + "|" + Processor.Fingerprint;

    /// <inheritdoc/>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices) =>
        Processor.Process(Inner.Decompose(vertices, indices));

    /// <inheritdoc/>
    public void Dispose() => (Inner as IDisposable)?.Dispose();
}
