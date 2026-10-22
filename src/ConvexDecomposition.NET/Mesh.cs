using System;
using System.Numerics;

namespace ConvexDecomposition;

/// <summary>
/// An indexed triangle mesh: <see cref="Vertices"/> plus a flat index buffer
/// with three entries per triangle.
/// </summary>
public sealed partial class Mesh
{
    /// <summary>Creates a mesh over the given arrays (not copied).</summary>
    /// <exception cref="ArgumentException"><paramref name="indices"/> length is not a multiple of 3.</exception>
    public Mesh(Vector3[] vertices, int[] indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Index count must be a multiple of 3.", nameof(indices));
        Vertices = vertices;
        Indices = indices;
    }

    /// <summary>Vertex positions.</summary>
    public Vector3[] Vertices { get; }

    /// <summary>Triangle vertex indices, three per triangle.</summary>
    public int[] Indices { get; }

    /// <summary>Number of triangles.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// Enclosed volume via the divergence theorem. Meaningful for closed,
    /// consistently oriented meshes such as convex hulls.
    /// </summary>
    public float ComputeVolume()
    {
        if (Vertices.Length == 0)
            return 0;
        // In double, relative to a vertex: world-space meshes far from the
        // origin would otherwise lose everything to cancellation.
        Vector3 o = Vertices[0];
        double sum = 0;
        for (int i = 0; i < Indices.Length; i += 3)
            sum += Triple(Vertices[Indices[i]] - o, Vertices[Indices[i + 1]] - o, Vertices[Indices[i + 2]] - o);
        return (float)Math.Abs(sum / 6.0);
    }

    // a · (b × c) in double.
    private static double Triple(Vector3 a, Vector3 b, Vector3 c)
    {
        double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z, cx = c.X, cy = c.Y, cz = c.Z;
        return ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx);
    }

    /// <summary>Axis-aligned bounds of the vertices.</summary>
    /// <exception cref="InvalidOperationException">The mesh has no vertices.</exception>
    public (Vector3 Min, Vector3 Max) ComputeBounds()
    {
        if (Vertices.Length == 0)
            throw new InvalidOperationException("The mesh has no vertices.");
        Vector3 lo = Vertices[0], hi = Vertices[0];
        foreach (Vector3 p in Vertices)
        {
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        return (lo, hi);
    }

    /// <inheritdoc cref="MassProperties.Compute"/>
    public MassProperties ComputeMassProperties(float density = 1f) => MassProperties.Compute(this, density);

    internal Mesh Clone() => new((Vector3[])Vertices.Clone(), (int[])Indices.Clone());
}
