using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ConvexDecomposition;
using CuACD.Interop;

namespace CuACD;

/// <summary>
/// A CuACD GPU context: a CUDA context, the loaded kernels and a device heap pool.
/// </summary>
/// <remarks>
/// <para>
/// Creating a context costs a fraction of a second to a second (CUDA context
/// creation plus kernel loading), so create one and reuse it for many meshes.
/// Use <see cref="IsAvailable(out string?)"/> to decide whether to fall back to a CPU
/// backend first.
/// </para>
/// <para>
/// Instances are thread-safe: calls are serialized, and may come from any
/// thread. For throughput across several GPUs, use one context per device.
/// </para>
/// </remarks>
public sealed unsafe class CuAcdContext : IConvexDecomposer, IDisposable
{
    private readonly object _gate = new();
    private readonly ContextHandle _handle;

    /// <summary>Creates a context.</summary>
    /// <param name="device">CUDA device ordinal; -1 reuses the calling thread's current CUDA context, else device 0.</param>
    /// <param name="poolBytes">
    /// Device memory reserved for CuACD's heap. 0 (default) reserves 80% of the
    /// free VRAM at creation time, as upstream does; pass an explicit size when
    /// sharing the GPU with a renderer or other CUDA work.
    /// </param>
    /// <exception cref="CuAcdException">No usable NVIDIA driver/GPU, or CUDA initialization failed.</exception>
    public CuAcdContext(int device = -1, long poolBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(poolBytes);
        KernelImage.EnsureInstalled();

        const int errLen = 1024;
        byte* err = stackalloc byte[errLen];
        err[0] = 0;
        int rc = NativeMethods.cuacd_create(device, (ulong)poolBytes, out nint ctx, err, errLen);
        if (rc != 0 || ctx == 0)
        {
            string msg = Marshal.PtrToStringUTF8((nint)err) is { Length: > 0 } s ? s : $"error code {rc}";
            throw new CuAcdException($"Failed to create CuACD context: {msg}", rc);
        }
        _handle = new ContextHandle(ctx);

        const int nameLen = 256;
        byte* name = stackalloc byte[nameLen];
        DeviceName = NativeMethods.cuacd_device_name(ctx, name, nameLen) == 0
            ? Marshal.PtrToStringUTF8((nint)name) ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Whether CuACD can run here: a supported platform, an NVIDIA driver, and a
    /// GPU of compute capability 7.0 (Volta) or newer. Cheap: creates no context.
    /// </summary>
    /// <param name="reason">Why not, when it returns false.</param>
    public static bool IsAvailable(out string? reason)
    {
        const int errLen = 512;
        byte* err = stackalloc byte[errLen];
        err[0] = 0;
        int count;
        try
        {
            if (NativeMethods.cuacd_query_devices(out count, err, errLen) != 0)
            {
                reason = Marshal.PtrToStringUTF8((nint)err) ?? "CUDA driver initialization failed.";
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            // cuacd_native links the driver (nvcuda.dll / libcuda.so.1), so this
            // covers both an unsupported platform and a missing NVIDIA driver.
            reason = $"No NVIDIA driver found, or {RuntimeInformation.RuntimeIdentifier} is not supported " +
                     "(CuACD supports win-x64 and linux-x64).";
            return false;
        }
        if (count == 0)
        {
            reason = "No CUDA GPU with compute capability 7.0 (Volta) or newer was found.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <inheritdoc cref="IsAvailable(out string?)"/>
    public static bool IsAvailable() => IsAvailable(out _);

    /// <summary>Name of the GPU this context runs on.</summary>
    public string DeviceName { get; }

    /// <summary>
    /// Settings used by calls that don't pass their own, including calls made
    /// through <see cref="IConvexDecomposer"/>.
    /// </summary>
    public CuAcdOptions DefaultOptions { get; set; } = new();

    /// <summary>CuACD and wrapper version plus the current <see cref="DefaultOptions"/>.</summary>
    public string Fingerprint => $"cuacd/{typeof(CuAcdContext).Assembly.GetName().Version};" + DefaultOptions.ToFingerprint();

    /// <summary>High-water mark of bytes used from the device pool.</summary>
    public long PoolUsage
    {
        get
        {
            lock (_gate)
                return (long)NativeMethods.cuacd_pool_usage(Handle);
        }
    }

    /// <summary>
    /// Decomposes a mesh into approximately convex parts.
    /// </summary>
    /// <param name="vertices">Vertex positions, in any units.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <param name="options">Settings; <c>null</c> uses <see cref="DefaultOptions"/>.</param>
    /// <returns>The parts, in the input's coordinate space.</returns>
    /// <remarks>
    /// Not bit-reproducible: parallel search introduces small run-to-run
    /// variation, and the part count may shift by one or two.
    /// </remarks>
    /// <exception cref="ArgumentException">The mesh is malformed.</exception>
    /// <exception cref="CuAcdException">The decomposition failed on the GPU.</exception>
    public IReadOnlyList<ConvexPart> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices,
        CuAcdOptions? options = null)
    {
        options ??= DefaultOptions;
        options.Validate();
        MeshValidation.ValidateMesh(vertices, indices);

        lock (_gate)
        {
            ReadOnlySpan<Vector3> v = vertices;
            ReadOnlySpan<int> t = indices;
            Mesh? remeshed = null;
            if (options.Preprocess == PreprocessMode.On ||
                (options.Preprocess == PreprocessMode.Auto && CheckMeshCore(v, t).NeedsRemesh))
            {
                remeshed = PreprocessCore(v, t, options.PreprocessResolution);
                v = remeshed.Vertices;
                t = remeshed.Indices;
            }

            Vector3 center = Vector3.Zero;
            float scale = 1f;
            Vector3[]? normalized = null;
            if (options.Normalize)
            {
                ComputeNormalization(v, out center, out scale);
                normalized = remeshed?.Vertices ?? new Vector3[v.Length];
                float inv = 1f / scale;
                for (int i = 0; i < v.Length; i++)
                    normalized[i] = (v[i] - center) * inv;
                v = normalized;
            }

            var p = new NativeMethods.DecomposeParams
            {
                StructSize = sizeof(NativeMethods.DecomposeParams),
                MaxIters = options.MaxIterations,
                Width = options.Width,
                Width2 = options.Width2,
                Threshold = options.Threshold,
                Depth = options.Depth,
                QuickDepth = options.QuickDepth,
                MaxNCutting = options.MaxParallelCuts,
                Verbose = options.Verbose ? 1 : 0,
                DecomposeComponents = options.DecomposeComponents ? 1 : 0,
                DecomposeComponentsPerIter = options.DecomposeComponentsPerIteration ? 1 : 0,
                NConcaveEdges = options.ConcaveEdgeSamples,
                ConcaveEps = options.ConcaveEdgeEpsilon,
                ConcaveThreshold = options.ConcaveAngleThreshold,
                ConcaveIters = options.ConcaveEdgeIterations,
                MergeHulls = options.MergeHulls ? 1 : 0,
            };

            nint result;
            int rc;
            fixed (Vector3* pv = v)
            fixed (int* pt = t)
                rc = NativeMethods.cuacd_decompose(Handle, (float*)pv, v.Length, pt, t.Length / 3, in p, out result);
            if (rc != 0)
                throw LastError(rc, "Decomposition");

            try
            {
                int count = NativeMethods.cuacd_result_count(result);
                var parts = new ConvexPart[count];
                float volumeScale = scale * scale * scale;
                for (int i = 0; i < count; i++)
                {
                    if (NativeMethods.cuacd_result_part(result, i, out NativeMethods.PartView pv) != 0)
                        throw new CuAcdException($"Failed to read part {i} of {count}.", -1);
                    var hull = new Mesh(ToVectors(pv.HullVerts, pv.HullNv, scale, center), ToIndices(pv.HullTris, pv.HullNt));
                    var piece = new Mesh(ToVectors(pv.Verts, pv.Nv, scale, center), ToIndices(pv.Tris, pv.Nt));
                    parts[i] = new ConvexPart(hull, piece, pv.HullVol * volumeScale, pv.MeshVol * volumeScale);
                }
                return parts;
            }
            finally
            {
                NativeMethods.cuacd_result_free(result);
            }
        }
    }

    /// <inheritdoc cref="Decompose(ReadOnlySpan{Vector3}, ReadOnlySpan{int}, CuAcdOptions?)"/>
    /// <param name="mesh">The mesh to decompose.</param>
    /// <param name="options">Settings; <c>null</c> uses <see cref="DefaultOptions"/>.</param>
    public IReadOnlyList<ConvexPart> Decompose(Mesh mesh, CuAcdOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return Decompose(mesh.Vertices, mesh.Indices, options);
    }

    /// <summary>Decomposes with <see cref="DefaultOptions"/>, returning only the hulls.</summary>
    IReadOnlyList<Mesh> IConvexDecomposer.Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        IReadOnlyList<ConvexPart> parts = Decompose(vertices, indices);
        var hulls = new Mesh[parts.Count];
        for (int i = 0; i < hulls.Length; i++)
            hulls[i] = parts[i].Hull;
        return hulls;
    }

    /// <summary>Audits mesh topology on the GPU (watertight / manifold / oriented).</summary>
    public MeshAudit CheckMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        MeshValidation.ValidateMesh(vertices, indices, checkIndexRange: false);
        lock (_gate)
            return CheckMeshCore(vertices, indices);
    }

    /// <summary>
    /// Remeshes into a watertight, manifold, consistently oriented mesh
    /// (PaMO stage-1: signed distance field + dual marching cubes). The output
    /// is slightly dilated, which thickens very thin walls.
    /// </summary>
    /// <param name="vertices">Vertex positions.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <param name="resolution">Voxel grid resolution, a power of two ≥ 8. 64 is the practical default; 128 ≈ CoACD's preprocess resolution 50.</param>
    public Mesh Preprocess(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, int resolution = 64)
    {
        if (!CuAcdOptions.IsValidResolution(resolution))
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Must be a power of two >= 8.");
        MeshValidation.ValidateMesh(vertices, indices);
        lock (_gate)
            return PreprocessCore(vertices, indices, resolution);
    }

    /// <summary>Computes the exact convex hull of a point cloud on the GPU.</summary>
    /// <param name="points">At least 4 non-coplanar points.</param>
    /// <returns>An outward-oriented hull mesh.</returns>
    public Mesh ComputeConvexHull(ReadOnlySpan<Vector3> points)
    {
        MeshValidation.ValidateVertices(points, nameof(points));
        lock (_gate)
        {
            float* hv;
            int* ht;
            int hnv, hnt, rc;
            fixed (Vector3* pp = points)
                rc = NativeMethods.cuacd_convex_hull(Handle, (float*)pp, points.Length,
                    out hv, out hnv, out ht, out hnt, out _);
            try
            {
                if (rc != 0)
                    throw LastError(rc, "Convex hull");
                return new Mesh(ToVectors(hv, hnv, 1f, Vector3.Zero), ToIndices(ht, hnt));
            }
            finally
            {
                NativeMethods.cuacd_free(hv);
                NativeMethods.cuacd_free(ht);
            }
        }
    }

    /// <summary>Destroys the context and releases its device memory.</summary>
    public void Dispose()
    {
        lock (_gate)
            _handle.Dispose();
    }

    // ------------------------------------------------------------------

    private nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.DangerousGetHandle();
        }
    }

    private MeshAudit CheckMeshCore(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        int rc;
        uint flags;
        fixed (Vector3* pv = vertices)
        fixed (int* pt = indices)
            rc = NativeMethods.cuacd_check_mesh(Handle, (float*)pv, vertices.Length, pt, indices.Length / 3, out flags);
        if (rc != 0)
            throw LastError(rc, "Mesh audit");
        return new MeshAudit((MeshDefects)flags);
    }

    private Mesh PreprocessCore(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, int resolution)
    {
        float* ov;
        int* ot;
        int onv, ont, rc;
        fixed (Vector3* pv = vertices)
        fixed (int* pt = indices)
            rc = NativeMethods.cuacd_preprocess(Handle, (float*)pv, vertices.Length, pt, indices.Length / 3,
                resolution, out ov, out onv, out ot, out ont);
        try
        {
            if (rc != 0)
                throw LastError(rc, "Preprocess");
            if (onv < 4 || ont < 1)
                throw new CuAcdException("Preprocess produced an empty mesh.", -1);
            return new Mesh(ToVectors(ov, onv, 1f, Vector3.Zero), ToIndices(ot, ont));
        }
        finally
        {
            NativeMethods.cuacd_free(ov);
            NativeMethods.cuacd_free(ot);
        }
    }

    private CuAcdException LastError(int rc, string what)
    {
        string? msg = Marshal.PtrToStringUTF8((nint)NativeMethods.cuacd_last_error(Handle));
        return new CuAcdException(msg ?? $"{what} failed with code {rc}.", rc);
    }

    private static void ComputeNormalization(ReadOnlySpan<Vector3> v, out Vector3 center, out float scale)
    {
        Vector3 lo = v[0], hi = v[0];
        for (int i = 1; i < v.Length; i++)
        {
            lo = Vector3.Min(lo, v[i]);
            hi = Vector3.Max(hi, v[i]);
        }
        center = (lo + hi) * 0.5f;
        Vector3 size = hi - lo;
        float extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        scale = extent > 0 ? extent * 0.5f : 1f;
    }

    private static Vector3[] ToVectors(float* p, int n, float scale, Vector3 center)
    {
        var result = new ReadOnlySpan<Vector3>(p, n).ToArray();
        if (scale != 1f || center != Vector3.Zero)
            for (int i = 0; i < result.Length; i++)
                result[i] = result[i] * scale + center;
        return result;
    }

    private static int[] ToIndices(int* p, int nt) => new ReadOnlySpan<int>(p, nt * 3).ToArray();

    private sealed class ContextHandle : SafeHandle
    {
        public ContextHandle(nint handle) : base(0, ownsHandle: true) => SetHandle(handle);

        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            NativeMethods.cuacd_destroy(handle);
            return true;
        }
    }
}
