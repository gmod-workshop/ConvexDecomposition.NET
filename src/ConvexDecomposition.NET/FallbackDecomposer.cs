using System;
using System.Collections.Generic;
using System.Numerics;

namespace ConvexDecomposition;

/// <summary>Why <see cref="FallbackDecomposer"/> moved past a backend.</summary>
/// <param name="BackendIndex">Position of the failing backend in the chain.</param>
/// <param name="Exception">What it threw.</param>
/// <param name="DuringCreation">
/// True if creating the backend failed, so it is skipped from now on; false if
/// one decomposition failed, so only that call moves on to the next backend.
/// </param>
public readonly record struct FallbackEvent(int BackendIndex, Exception Exception, bool DuringCreation);

/// <summary>
/// Tries backends in order, e.g. CuACD (GPU), then CoACD (CPU).
/// </summary>
/// <remarks>
/// <para>
/// Backends are created lazily from factories, on first use, so an
/// unavailable backend (no NVIDIA GPU, unsupported platform) costs one failed
/// construction and is then skipped for good. If a created backend throws
/// while decomposing, that call is retried on the next backend, but the
/// failing one stays first in line for later calls, since most failures are
/// specific to one mesh (e.g. running out of GPU memory on a huge input).
/// </para>
/// <para>
/// <see cref="ArgumentException"/> (malformed input) and
/// <see cref="OperationCanceledException"/> are not retried: every backend
/// would reject them the same way.
/// </para>
/// <para>Thread-safe; disposing it disposes the backends it created.</para>
/// </remarks>
public sealed class FallbackDecomposer : IConvexDecomposer, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IConvexDecomposer>[] _factories;
    private readonly IConvexDecomposer?[] _backends;
    private readonly Exception?[] _creationErrors;
    private bool _disposed;

    /// <summary>Creates the chain.</summary>
    /// <param name="backends">Factories, in order of preference.</param>
    /// <example>
    /// <code>
    /// using var decomposer = new FallbackDecomposer(
    ///     () => new CuAcdContext(poolBytes: 1L &lt;&lt; 30),
    ///     () => new CoAcdDecomposer());
    /// </code>
    /// </example>
    public FallbackDecomposer(params Func<IConvexDecomposer>[] backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        if (backends.Length == 0)
            throw new ArgumentException("At least one backend is required.", nameof(backends));
        foreach (var f in backends)
            ArgumentNullException.ThrowIfNull(f, nameof(backends));
        _factories = (Func<IConvexDecomposer>[])backends.Clone();
        _backends = new IConvexDecomposer?[backends.Length];
        _creationErrors = new Exception?[backends.Length];
    }

    /// <summary>
    /// Called (on the decomposing thread) whenever a backend is skipped; use it
    /// to log why the GPU path was not taken.
    /// </summary>
    public Action<FallbackEvent>? FallbackOccurred { get; set; }

    /// <summary>
    /// The first backend that could be created, creating backends as needed.
    /// </summary>
    /// <exception cref="AggregateException">No backend could be created.</exception>
    public IConvexDecomposer Active => GetBackend(0, out _);

    /// <summary>
    /// The <see cref="Active"/> backend's fingerprint: results are cached as
    /// that backend's, including the rare result a later backend produced
    /// after it failed on one mesh.
    /// </summary>
    public string Fingerprint => Active.Fingerprint;

    /// <inheritdoc/>
    /// <exception cref="AggregateException">Every backend failed; holds each one's exception.</exception>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        List<Exception>? errors = null;
        for (int i = 0; i < _factories.Length; i++)
        {
            IConvexDecomposer? backend = TryGetBackend(i);
            if (backend is null)
            {
                (errors ??= new()).Add(_creationErrors[i]!);
                continue;
            }
            try
            {
                return backend.Decompose(vertices, indices);
            }
            catch (Exception e) when (e is not ArgumentException and not OperationCanceledException)
            {
                (errors ??= new()).Add(e);
                FallbackOccurred?.Invoke(new FallbackEvent(i, e, DuringCreation: false));
            }
        }
        throw new AggregateException("Every convex decomposition backend failed.", errors!);
    }

    /// <summary>Disposes every backend this instance created.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (IConvexDecomposer? b in _backends)
                (b as IDisposable)?.Dispose();
        }
    }

    private IConvexDecomposer GetBackend(int start, out int index)
    {
        for (index = start; index < _factories.Length; index++)
            if (TryGetBackend(index) is { } b)
                return b;
        var errors = new List<Exception>();
        foreach (Exception? e in _creationErrors)
            if (e is not null)
                errors.Add(e);
        throw new AggregateException("No convex decomposition backend could be created.", errors);
    }

    private IConvexDecomposer? TryGetBackend(int i)
    {
        FallbackEvent? failed = null;
        IConvexDecomposer? backend;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            backend = _backends[i];
            if (backend is null && _creationErrors[i] is null)
            {
                try
                {
                    backend = _backends[i] = _factories[i]() ??
                        throw new InvalidOperationException($"Backend factory {i} returned null.");
                }
                catch (Exception e)
                {
                    _creationErrors[i] = e;
                    failed = new FallbackEvent(i, e, DuringCreation: true);
                }
            }
        }
        if (failed is { } ev)
            FallbackOccurred?.Invoke(ev);
        return backend;
    }
}
