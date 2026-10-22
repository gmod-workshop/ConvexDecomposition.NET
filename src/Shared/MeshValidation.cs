using System;
using System.Numerics;

namespace ConvexDecomposition;

// Compiled into each backend assembly (linked source, internal).
internal static class MeshValidation
{
    public static void ValidateVertices(ReadOnlySpan<Vector3> vertices, string paramName)
    {
        if (vertices.Length < 4)
            throw new ArgumentException("At least 4 vertices are required.", paramName);
        foreach (Vector3 p in vertices)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                throw new ArgumentException("Vertices must be finite (found NaN or infinity).", paramName);
    }

    public static void ValidateMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, bool checkIndexRange = true)
    {
        ValidateVertices(vertices, nameof(vertices));
        if (indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("Index count must be a positive multiple of 3.", nameof(indices));
        if (checkIndexRange)
            ValidateIndices(vertices.Length, indices, nameof(indices));
    }

    public static void ValidateIndices(int vertexCount, ReadOnlySpan<int> indices, string paramName)
    {
        uint n = (uint)vertexCount;
        foreach (int i in indices)
            if ((uint)i >= n)
                throw new ArgumentException($"Index {i} is out of range for {n} vertices.", paramName);
    }
}
