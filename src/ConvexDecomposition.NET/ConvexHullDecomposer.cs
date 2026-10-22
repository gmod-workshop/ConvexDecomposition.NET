using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ConvexDecomposition.Geometry;

namespace ConvexDecomposition;

/// <summary>
/// The trivial decomposition: one convex hull around the whole mesh. Right
/// for small or nearly convex props, and instant.
/// </summary>
public sealed class ConvexHullDecomposer : IConvexDecomposer
{
    /// <summary>Creates the decomposer.</summary>
    /// <param name="maxVertices">Vertex budget for the hull (at least 4); see <see cref="ConvexHull.Compute"/>.</param>
    public ConvexHullDecomposer(int maxVertices = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxVertices, 4);
        MaxVertices = maxVertices;
    }

    /// <summary>Vertex budget for the hull.</summary>
    public int MaxVertices { get; }

    /// <inheritdoc/>
    public string Fingerprint => string.Create(CultureInfo.InvariantCulture, $"convex-hull/1;maxVertices={MaxVertices}");

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The mesh is malformed or flat.</exception>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        MeshValidation.ValidateMesh(vertices, indices);
        // Hull only the referenced vertices.
        var used = new bool[vertices.Length];
        var points = new List<Double3>();
        foreach (int i in indices)
            if (!used[i])
            {
                used[i] = true;
                points.Add(new Double3(vertices[i]));
            }
        if (!QuickHull.TryCompute(points.ToArray(), MaxVertices, out HullResult hull))
            throw new ArgumentException("The mesh is flat; its hull has no volume.", nameof(vertices));
        return new[] { hull.ToMesh() };
    }
}
