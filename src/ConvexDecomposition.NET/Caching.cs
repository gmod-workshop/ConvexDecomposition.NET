using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ConvexDecomposition;

/// <summary>Storage for <see cref="CachingDecomposer"/>. Implementations must be thread-safe.</summary>
public interface IDecompositionCache
{
    /// <summary>Looks up a stored decomposition.</summary>
    /// <param name="key">A key from <see cref="CachingDecomposer.ComputeKey"/>.</param>
    /// <param name="hulls">The hulls, which the caller may modify.</param>
    bool TryGet(string key, [NotNullWhen(true)] out IReadOnlyList<Mesh>? hulls);

    /// <summary>Stores a decomposition. Failing to store must not throw.</summary>
    void Set(string key, IReadOnlyList<Mesh> hulls);
}

/// <summary>An in-process cache. Unbounded; call <see cref="Clear"/> to release memory.</summary>
public sealed class MemoryDecompositionCache : IDecompositionCache
{
    private readonly ConcurrentDictionary<string, Mesh[]> _entries = new();

    /// <summary>Number of stored decompositions.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public bool TryGet(string key, [NotNullWhen(true)] out IReadOnlyList<Mesh>? hulls)
    {
        if (_entries.TryGetValue(key, out Mesh[]? stored))
        {
            hulls = Array.ConvertAll(stored, m => m.Clone());
            return true;
        }
        hulls = null;
        return false;
    }

    /// <inheritdoc/>
    public void Set(string key, IReadOnlyList<Mesh> hulls)
    {
        var copy = new Mesh[hulls.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = hulls[i].Clone();
        _entries[key] = copy;
    }

    /// <summary>Removes every entry.</summary>
    public void Clear() => _entries.Clear();
}

/// <summary>
/// A cache of one small binary file per decomposition under a directory,
/// shared between runs and processes. Unreadable entries count as misses, and
/// write failures are ignored. Nothing is ever evicted; delete the directory
/// to reset it.
/// </summary>
public sealed class DirectoryDecompositionCache : IDecompositionCache
{
    private const uint Magic = 0x43484443; // "CDHC"
    private const int FormatVersion = 1;

    /// <summary>Uses (and creates, if needed) <paramref name="directory"/>.</summary>
    public DirectoryDecompositionCache(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>The cache directory.</summary>
    public string Directory { get; }

    /// <inheritdoc/>
    public bool TryGet(string key, [NotNullWhen(true)] out IReadOnlyList<Mesh>? hulls)
    {
        hulls = null;
        string path = PathFor(key);
        if (!File.Exists(path))
            return false;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
                return false;
            int count = reader.ReadInt32();
            var result = new Mesh[count];
            for (int i = 0; i < count; i++)
            {
                var vertices = new Vector3[reader.ReadInt32()];
                var indices = new int[reader.ReadInt32()];
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(vertices.AsSpan()));
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(indices.AsSpan()));
                result[i] = new Mesh(vertices, indices);
            }
            hulls = result;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or OverflowException or OutOfMemoryException)
        {
            return false; // truncated or corrupt entry: treat as a miss
        }
    }

    /// <inheritdoc/>
    public void Set(string key, IReadOnlyList<Mesh> hulls)
    {
        string path = PathFor(key);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var writer = new BinaryWriter(File.Create(temp)))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(hulls.Count);
                foreach (Mesh m in hulls)
                {
                    writer.Write(m.Vertices.Length);
                    writer.Write(m.Indices.Length);
                    writer.Write(MemoryMarshal.AsBytes(m.Vertices.AsSpan()));
                    writer.Write(MemoryMarshal.AsBytes(m.Indices.AsSpan()));
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
        }
    }

    // Two-character fan-out keeps directories small.
    private string PathFor(string key)
    {
        if (key.Length < 3 || key.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid cache key.", nameof(key));
        return Path.Combine(Directory, key[..2], key + ".hulls");
    }
}

/// <summary>
/// Returns cached results for meshes seen before, keyed by the mesh data and
/// the wrapped decomposer's <see cref="IConvexDecomposer.Fingerprint"/>.
/// Create with <see cref="ConvexDecomposerExtensions.WithCache"/>.
/// </summary>
public sealed class CachingDecomposer : IConvexDecomposer, IDisposable
{
    /// <summary>Wraps <paramref name="inner"/>; disposing this disposes it.</summary>
    public CachingDecomposer(IConvexDecomposer inner, IDecompositionCache cache)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        Inner = inner;
        Cache = cache;
    }

    /// <summary>The wrapped decomposer.</summary>
    public IConvexDecomposer Inner { get; }

    /// <summary>The storage.</summary>
    public IDecompositionCache Cache { get; }

    /// <inheritdoc/>
    public string Fingerprint => Inner.Fingerprint;

    /// <inheritdoc/>
    public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        string key = ComputeKey(Inner.Fingerprint, vertices, indices);
        if (Cache.TryGet(key, out IReadOnlyList<Mesh>? hit))
            return hit;
        IReadOnlyList<Mesh> hulls = Inner.Decompose(vertices, indices);
        Cache.Set(key, hulls);
        return hulls;
    }

    /// <summary>
    /// SHA-256 over the fingerprint, vertex data and index data, as lowercase hex.
    /// </summary>
    public static string ComputeKey(string fingerprint, ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(fingerprint));
        Span<int> counts = stackalloc int[] { vertices.Length, indices.Length };
        hash.AppendData(MemoryMarshal.AsBytes(counts));
        hash.AppendData(MemoryMarshal.AsBytes(vertices));
        hash.AppendData(MemoryMarshal.AsBytes(indices));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <inheritdoc/>
    public void Dispose() => (Inner as IDisposable)?.Dispose();
}
