using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CoACD.Interop;
using ConvexDecomposition;

namespace CoACD;

/// <summary>Verbosity of CoACD's own console logging.</summary>
public enum CoAcdLogLevel
{
    /// <summary>No output (default).</summary>
    Off,
    /// <summary>Critical errors only.</summary>
    Critical,
    /// <summary>Errors.</summary>
    Error,
    /// <summary>Warnings and errors.</summary>
    Warning,
    /// <summary>Progress and parameters (upstream's default).</summary>
    Info,
    /// <summary>Everything.</summary>
    Debug,
}

/// <summary>
/// CPU approximate convex decomposition with CoACD (Wei et al., SIGGRAPH 2022).
/// </summary>
/// <remarks>
/// Stateless and thread-safe; each call uses all cores internally. CoACD
/// normalizes the mesh itself, so input can be in any units, and hulls come
/// back in the input's coordinate space.
/// </remarks>
public sealed unsafe class CoAcdDecomposer : IConvexDecomposer
{
    private static readonly object InitGate = new();
    private static bool _initialized;
    private static CoAcdLogLevel _logLevel = CoAcdLogLevel.Off;

    /// <summary>Creates a decomposer that uses <paramref name="options"/> by default.</summary>
    public CoAcdDecomposer(CoAcdOptions? options = null)
    {
        Options = options ?? new CoAcdOptions();
    }

    /// <summary>Settings used by calls that don't pass their own.</summary>
    public CoAcdOptions Options { get; }

    /// <summary>CoACD and wrapper version plus the current <see cref="Options"/>.</summary>
    public string Fingerprint => $"coacd/{typeof(CoAcdDecomposer).Assembly.GetName().Version};" + Options.ToFingerprint();

    /// <summary>
    /// Process-wide verbosity of CoACD's console output. Default
    /// <see cref="CoAcdLogLevel.Off"/>; errors are reported as exceptions regardless.
    /// </summary>
    public static CoAcdLogLevel LogLevel
    {
        get { lock (InitGate) return _logLevel; }
        set
        {
            lock (InitGate)
            {
                _logLevel = value;
                if (_initialized)
                    ApplyLogLevel(value);
            }
        }
    }

    /// <summary>Decomposes a mesh into convex hulls using <see cref="Options"/>.</summary>
    /// <param name="vertices">Vertex positions, in any units.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <returns>Convex hulls in the input's coordinate space.</returns>
    /// <exception cref="ArgumentException">The mesh is malformed.</exception>
    /// <exception cref="CoAcdException">CoACD reported an error.</exception>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices) =>
        Decompose(vertices, indices, Options);

    /// <summary>Decomposes a mesh into convex hulls.</summary>
    /// <param name="vertices">Vertex positions, in any units.</param>
    /// <param name="indices">Triangle indices, three per triangle.</param>
    /// <param name="options">Settings for this call.</param>
    /// <returns>Convex hulls in the input's coordinate space.</returns>
    /// <exception cref="ArgumentException">The mesh or options are invalid.</exception>
    /// <exception cref="CoAcdException">CoACD reported an error.</exception>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, CoAcdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        MeshValidation.ValidateMesh(vertices, indices);
        EnsureInitialized();

        var p = new NativeMethods.Params
        {
            StructSize = sizeof(NativeMethods.Params),
            Threshold = options.Threshold,
            MaxConvexHull = options.MaxConvexHulls ?? -1,
            PreprocessMode = options.Preprocess switch
            {
                PreprocessMode.On => 1,
                PreprocessMode.Off => 2,
                _ => 0,
            },
            PrepResolution = options.PreprocessResolution,
            SampleResolution = options.SampleResolution,
            MctsNodes = options.MctsNodes,
            MctsIteration = options.MctsIterations,
            MctsMaxDepth = options.MctsMaxDepth,
            Pca = options.Pca ? 1 : 0,
            Merge = options.Merge ? 1 : 0,
            Decimate = options.MaxHullVertices.HasValue ? 1 : 0,
            MaxChVertex = options.MaxHullVertices ?? 256,
            Extrude = options.Extrude ? 1 : 0,
            ExtrudeMargin = options.ExtrudeMargin,
            ApxMode = options.Approximation == HullApproximation.Box ? 1 : 0,
            Seed = options.Seed,
            RealMetric = options.RealMetric ? 1 : 0,
        };

        const int errLen = 1024;
        byte* err = stackalloc byte[errLen];
        err[0] = 0;
        nint result;
        int rc;
        fixed (Vector3* pv = vertices)
        fixed (int* pt = indices)
            rc = NativeMethods.coacd_net_run((float*)pv, vertices.Length, pt, indices.Length / 3, in p,
                out result, err, errLen);
        if (rc != 0)
            throw new CoAcdException(ReadError(err) ?? $"CoACD failed with code {rc}.");

        try
        {
            int count = NativeMethods.coacd_net_result_count(result);
            var hulls = new Mesh[count];
            for (int i = 0; i < count; i++)
            {
                if (NativeMethods.coacd_net_result_hull(result, i, out float* hv, out int hnv, out int* ht, out int hnt) != 0)
                    throw new CoAcdException($"Failed to read hull {i} of {count}.");
                hulls[i] = new Mesh(
                    new ReadOnlySpan<Vector3>(hv, hnv).ToArray(),
                    new ReadOnlySpan<int>(ht, hnt * 3).ToArray());
            }
            return hulls;
        }
        finally
        {
            NativeMethods.coacd_net_result_free(result);
        }
    }

    /// <inheritdoc cref="Decompose(ReadOnlySpan{Vector3}, ReadOnlySpan{int}, CoAcdOptions)"/>
    /// <param name="mesh">The mesh to decompose.</param>
    /// <param name="options">Settings for this call; <c>null</c> uses <see cref="Options"/>.</param>
    public IReadOnlyList<Mesh> Decompose(Mesh mesh, CoAcdOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return Decompose(mesh.Vertices, mesh.Indices, options ?? Options);
    }

    // ------------------------------------------------------------------

    private static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized)
                return;
            int version;
            try
            {
                version = NativeMethods.coacd_net_capi_version();
            }
            catch (DllNotFoundException e)
            {
                throw new CoAcdException(
                    $"The CoACD native library could not be loaded for {RuntimeInformation.RuntimeIdentifier}. " +
                    "Supported platforms are win-x64, linux-x64, linux-arm64 and osx-arm64.", e);
            }
            if (version != NativeMethods.ExpectedCapiVersion)
                throw new CoAcdException(
                    $"coacd_native C API version {version} does not match the managed bindings " +
                    $"({NativeMethods.ExpectedCapiVersion}).");
            ApplyLogLevel(_logLevel);
            _initialized = true;
        }
    }

    private static void ApplyLogLevel(CoAcdLogLevel level)
    {
        ReadOnlySpan<byte> name = level switch
        {
            CoAcdLogLevel.Critical => "critical\0"u8,
            CoAcdLogLevel.Error => "error\0"u8,
            CoAcdLogLevel.Warning => "warn\0"u8,
            CoAcdLogLevel.Info => "info\0"u8,
            CoAcdLogLevel.Debug => "debug\0"u8,
            _ => "off\0"u8,
        };
        const int errLen = 256;
        byte* err = stackalloc byte[errLen];
        err[0] = 0;
        fixed (byte* pn = name)
            if (NativeMethods.coacd_net_set_log_level(pn, err, errLen) != 0)
                throw new CoAcdException(ReadError(err) ?? "Failed to set CoACD log level.");
    }

    private static string? ReadError(byte* err) =>
        Marshal.PtrToStringUTF8((nint)err) is { Length: > 0 } s ? s : null;
}
