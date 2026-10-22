using System;
using System.Collections.Generic;
using System.Numerics;

namespace ConvexDecomposition;

/// <summary>
/// An approximate convex decomposition backend (e.g. CuACD on the GPU, CoACD
/// or V-HACD on the CPU), configured when it is created.
/// </summary>
/// <remarks>Implementations are thread-safe.</remarks>
public interface IConvexDecomposer
{
    /// <summary>
    /// Identifies the algorithm, its version and its current settings: two
    /// decomposers with equal fingerprints produce interchangeable results.
    /// Used as part of the key by <see cref="CachingDecomposer"/>.
    /// </summary>
    string Fingerprint { get; }

    /// <summary>Decomposes a mesh into convex hulls.</summary>
    /// <param name="vertices">Vertex positions, in any units.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <returns>Convex hull meshes in the input's coordinate space.</returns>
    /// <exception cref="ArgumentException">The mesh is malformed.</exception>
    IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices);
}

/// <summary>Convenience overloads and composition helpers for <see cref="IConvexDecomposer"/>.</summary>
public static class ConvexDecomposerExtensions
{
    /// <inheritdoc cref="IConvexDecomposer.Decompose"/>
    /// <param name="decomposer">The backend.</param>
    /// <param name="mesh">The mesh to decompose.</param>
    public static IReadOnlyList<Mesh> Decompose(this IConvexDecomposer decomposer, Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(decomposer);
        ArgumentNullException.ThrowIfNull(mesh);
        return decomposer.Decompose(mesh.Vertices, mesh.Indices);
    }

    /// <summary>
    /// Wraps <paramref name="decomposer"/> so every result passes through
    /// <paramref name="processor"/>, giving the same hull count and vertex
    /// limits whichever backend runs. Disposing the wrapper disposes
    /// <paramref name="decomposer"/>.
    /// </summary>
    public static PostProcessingDecomposer WithPostProcessing(this IConvexDecomposer decomposer, HullPostProcessor processor) =>
        new(decomposer, processor);

    /// <summary>
    /// Wraps <paramref name="decomposer"/> so results are looked up in, and
    /// stored to, <paramref name="cache"/>. Disposing the wrapper disposes
    /// <paramref name="decomposer"/>.
    /// </summary>
    /// <remarks>
    /// Apply post-processing outside the cache
    /// (<c>backend.WithCache(cache).WithPostProcessing(p)</c>) so that changing
    /// the limits reuses the cached decompositions.
    /// </remarks>
    public static CachingDecomposer WithCache(this IConvexDecomposer decomposer, IDecompositionCache cache) =>
        new(decomposer, cache);
}
