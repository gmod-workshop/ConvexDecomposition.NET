using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ConvexDecomposition;
using VHACD.Interop;

namespace VHACD;

/// <summary>
/// CPU approximate convex decomposition with V-HACD 4 (Mamou et al.):
/// voxelizes the mesh and splits it recursively. Much faster than CoACD,
/// with looser hulls.
/// </summary>
/// <remarks>
/// Stateless and thread-safe; each call uses V-HACD's own thread pool when
/// <see cref="VhacdOptions.Multithreaded"/> is on. V-HACD normalizes the mesh
/// itself, so input can be in any units, and hulls come back in the input's
/// coordinate space.
/// </remarks>
public sealed unsafe class VhacdDecomposer : IConvexDecomposer
{
    private static readonly object InitGate = new();
    private static bool _initialized;

    /// <summary>Creates a decomposer that uses <paramref name="options"/> by default.</summary>
    public VhacdDecomposer(VhacdOptions? options = null)
    {
        Options = options ?? new VhacdOptions();
    }

    /// <summary>Settings used by calls that don't pass their own.</summary>
    public VhacdOptions Options { get; }

    /// <summary>V-HACD and wrapper version plus the current <see cref="Options"/>.</summary>
    public string Fingerprint => $"vhacd/{typeof(VhacdDecomposer).Assembly.GetName().Version};" + Options.ToFingerprint();

    /// <summary>Decomposes a mesh into convex hulls using <see cref="Options"/>.</summary>
    /// <param name="vertices">Vertex positions, in any units.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <returns>Convex hulls in the input's coordinate space.</returns>
    /// <exception cref="ArgumentException">The mesh is malformed.</exception>
    /// <exception cref="VhacdException">V-HACD reported an error.</exception>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices) =>
        Decompose(vertices, indices, Options);

    /// <summary>Decomposes a mesh into convex hulls.</summary>
    /// <param name="vertices">Vertex positions, in any units.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <param name="options">Settings for this call.</param>
    /// <returns>Convex hulls in the input's coordinate space.</returns>
    /// <exception cref="ArgumentException">The mesh or options are invalid.</exception>
    /// <exception cref="VhacdException">V-HACD reported an error.</exception>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, VhacdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        MeshValidation.ValidateMesh(vertices, indices);
        EnsureInitialized();

        var p = new NativeMethods.Params
        {
            StructSize = sizeof(NativeMethods.Params),
            MaxConvexHulls = (uint)options.MaxConvexHulls,
            Resolution = (uint)options.Resolution,
            MinVolumePercentError = options.MinVolumeErrorPercent,
            MaxRecursionDepth = (uint)options.MaxRecursionDepth,
            ShrinkWrap = options.ShrinkWrap ? 1 : 0,
            FillMode = options.FillMode switch
            {
                VhacdFillMode.SurfaceOnly => 1,
                VhacdFillMode.Raycast => 2,
                _ => 0,
            },
            MaxVerticesPerHull = (uint)options.MaxVerticesPerHull,
            Multithreaded = options.Multithreaded ? 1 : 0,
            MinEdgeLength = (uint)options.MinEdgeLength,
            FindBestPlane = options.FindBestPlane ? 1 : 0,
        };

        const int errLen = 1024;
        byte* err = stackalloc byte[errLen];
        err[0] = 0;
        nint result;
        int rc;
        fixed (Vector3* pv = vertices)
        fixed (int* pt = indices)
            rc = NativeMethods.vhacd_net_run((float*)pv, vertices.Length, pt, indices.Length / 3, in p,
                out result, err, errLen);
        if (rc != 0)
            throw new VhacdException(Marshal.PtrToStringUTF8((nint)err) is { Length: > 0 } s ? s : $"V-HACD failed with code {rc}.");

        try
        {
            int count = NativeMethods.vhacd_net_result_count(result);
            var hulls = new List<Mesh>(count);
            for (int i = 0; i < count; i++)
            {
                if (NativeMethods.vhacd_net_result_hull(result, i, out float* hv, out int hnv, out int* ht, out int hnt) != 0)
                    throw new VhacdException($"Failed to read hull {i} of {count}.");
                if (hnv < 4 || hnt < 4)
                    continue; // a hull V-HACD could not retrieve
                hulls.Add(new Mesh(
                    new ReadOnlySpan<Vector3>(hv, hnv).ToArray(),
                    new ReadOnlySpan<int>(ht, hnt * 3).ToArray()));
            }
            return hulls;
        }
        finally
        {
            NativeMethods.vhacd_net_result_free(result);
        }
    }

    /// <inheritdoc cref="Decompose(ReadOnlySpan{Vector3}, ReadOnlySpan{int}, VhacdOptions)"/>
    /// <param name="mesh">The mesh to decompose.</param>
    /// <param name="options">Settings for this call; <c>null</c> uses <see cref="Options"/>.</param>
    public IReadOnlyList<Mesh> Decompose(Mesh mesh, VhacdOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return Decompose(mesh.Vertices, mesh.Indices, options ?? Options);
    }

    private static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized)
                return;
            int version;
            try
            {
                version = NativeMethods.vhacd_net_capi_version();
            }
            catch (DllNotFoundException e)
            {
                throw new VhacdException(
                    $"The V-HACD native library could not be loaded for {RuntimeInformation.RuntimeIdentifier}. " +
                    "Supported platforms are win-x64, linux-x64, linux-arm64 and osx-arm64.", e);
            }
            if (version != NativeMethods.ExpectedCapiVersion)
                throw new VhacdException(
                    $"vhacd_native C API version {version} does not match the managed bindings " +
                    $"({NativeMethods.ExpectedCapiVersion}).");
            _initialized = true;
        }
    }
}
