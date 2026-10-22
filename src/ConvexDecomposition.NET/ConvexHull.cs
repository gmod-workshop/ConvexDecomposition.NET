using System;
using System.Numerics;
using ConvexDecomposition.Geometry;

namespace ConvexDecomposition;

/// <summary>Convex hull construction and adjustment, in managed code.</summary>
public static class ConvexHull
{
    /// <summary>Computes the convex hull of a point cloud.</summary>
    /// <param name="points">At least 4 points, not all coplanar.</param>
    /// <param name="maxVertices">
    /// Vertex budget (at least 4). When the exact hull has more vertices, the
    /// result is the hull of the farthest-first subset: it lies inside the
    /// exact hull and keeps its extreme points.
    /// </param>
    /// <returns>A closed hull mesh with outward-facing (counter-clockwise) triangles.</returns>
    /// <exception cref="ArgumentException">Fewer than 4 points, non-finite values, or all points coplanar.</exception>
    public static Mesh Compute(ReadOnlySpan<Vector3> points, int maxVertices = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxVertices, 4);
        MeshValidation.ValidateVertices(points, nameof(points));
        if (!QuickHull.TryCompute(points, maxVertices, out HullResult hull))
            throw new ArgumentException("The points are coplanar or collinear; their hull has no volume.", nameof(points));
        return hull.ToMesh();
    }

    /// <summary>
    /// Moves every face of a convex hull outwards (positive
    /// <paramref name="distance"/>) or inwards (negative), keeping it convex.
    /// </summary>
    /// <remarks>
    /// The result is accurate to 1% of <paramref name="distance"/>. Corners
    /// stay sharp rather than rounding off, so growing a hull with
    /// very acute corners can move them noticeably more than
    /// <paramref name="distance"/>. A hull too thin to shrink by the full
    /// amount is shrunk by half its thickness at most, so it never collapses.
    /// </remarks>
    /// <param name="hull">A convex mesh; only its vertices are used.</param>
    /// <param name="distance">Offset along each face normal, in mesh units.</param>
    /// <exception cref="ArgumentException">The hull has no volume, or the distance is not finite.</exception>
    public static Mesh Offset(Mesh hull, float distance)
    {
        ArgumentNullException.ThrowIfNull(hull);
        if (!float.IsFinite(distance))
            throw new ArgumentException("Distance must be finite.", nameof(distance));
        MeshValidation.ValidateVertices(hull.Vertices, nameof(hull));
        if (!QuickHull.TryCompute(hull.Vertices, int.MaxValue, out HullResult h))
            throw new ArgumentException("The hull has no volume.", nameof(hull));
        return HullOperations.Offset(h, distance).ToMesh();
    }
}

internal static class HullOperations
{
    /// <summary>
    /// Offsets every face plane by <paramref name="distance"/>, by way of the
    /// polar dual: a hull containing point c is the intersection of planes
    /// n·(x − c) = s; their duals n / s span a hull whose faces map back to
    /// the vertices of the offset hull.
    /// </summary>
    public static HullResult Offset(HullResult hull, double distance)
    {
        if (distance == 0)
            return hull;

        Double3 c = default;
        foreach (Double3 v in hull.Vertices)
            c += v;
        c /= hull.Vertices.Length;

        int faces = hull.Indices.Length / 3;
        var normals = new Double3[faces];
        var support = new double[faces];
        double minSupport = double.MaxValue;
        for (int f = 0; f < faces; f++)
        {
            Double3 a = hull.Vertices[hull.Indices[3 * f]];
            Double3 n = Double3.Cross(hull.Vertices[hull.Indices[3 * f + 1]] - a, hull.Vertices[hull.Indices[3 * f + 2]] - a);
            double len = n.Length;
            normals[f] = len > 0 ? n / len : default;
            support[f] = Double3.Dot(normals[f], a - c);
            if (len > 0)
                minSupport = Math.Min(minSupport, support[f]);
        }
        if (!(minSupport > 0))
            return hull;
        distance = Math.Max(distance, -0.5 * minSupport);

        var dual = new Double3[faces];
        int count = 0;
        for (int f = 0; f < faces; f++)
            if (normals[f].LengthSquared > 0)
                dual[count++] = normals[f] / (support[f] + distance);
        if (!QuickHull.TryCompute(dual.AsSpan(0, count), int.MaxValue, out HullResult dh))
            return hull;

        var primal = new Double3[dh.Indices.Length / 3];
        for (int f = 0; f < primal.Length; f++)
        {
            Double3 qa = dh.Vertices[dh.Indices[3 * f]];
            Double3 n = Double3.Cross(dh.Vertices[dh.Indices[3 * f + 1]] - qa, dh.Vertices[dh.Indices[3 * f + 2]] - qa);
            double b = Double3.Dot(n, qa);
            primal[f] = c + n / b;
        }
        // Where more than three faces met, the offset planes no longer meet in
        // one point, leaving clusters of near-duplicate vertices; treating
        // points within 1% of the distance as on the hull merges them, at a
        // volume cost below 0.02% (and gives back the input's vertex count).
        return QuickHull.TryCompute(primal, int.MaxValue, out HullResult result, 0.01 * Math.Abs(distance)) ? result : hull;
    }
}
