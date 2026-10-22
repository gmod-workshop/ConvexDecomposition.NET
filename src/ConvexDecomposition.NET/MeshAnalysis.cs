using System;
using System.Collections.Generic;
using System.Numerics;

namespace ConvexDecomposition;

/// <summary>Topology and validity report for a triangle mesh; see <see cref="Mesh.Analyze"/>.</summary>
public sealed class MeshReport
{
    internal MeshReport() { }

    /// <summary>Number of vertices.</summary>
    public int VertexCount { get; internal init; }

    /// <summary>Number of triangles.</summary>
    public int TriangleCount { get; internal init; }

    /// <summary>Vertices no triangle references.</summary>
    public int UnreferencedVertices { get; internal init; }

    /// <summary>Triangles with a repeated vertex or zero area.</summary>
    public int DegenerateTriangles { get; internal init; }

    /// <summary>Triangles that repeat another triangle's three vertices.</summary>
    public int DuplicateTriangles { get; internal init; }

    /// <summary>Edges used by only one triangle: the rims of holes.</summary>
    public int BoundaryEdges { get; internal init; }

    /// <summary>Edges shared by more than two triangles.</summary>
    public int NonManifoldEdges { get; internal init; }

    /// <summary>
    /// Edges whose two triangles traverse them in the same direction, i.e.
    /// neighbours with opposite winding.
    /// </summary>
    public int InconsistentEdges { get; internal init; }

    /// <summary>Number of edge-connected pieces.</summary>
    public int Components { get; internal init; }

    /// <summary>
    /// Signed enclosed volume; negative when the mesh is closed but wound
    /// inside out. Only meaningful when <see cref="IsClosed"/>.
    /// </summary>
    public double SignedVolume { get; internal init; }

    /// <summary>No holes and no edges shared by more than two triangles.</summary>
    public bool IsWatertight => BoundaryEdges == 0 && NonManifoldEdges == 0;

    /// <summary>Watertight, consistently wound, and free of degenerate triangles: a proper solid.</summary>
    public bool IsClosed => IsWatertight && InconsistentEdges == 0 && DegenerateTriangles == 0;

    /// <summary>A closed mesh whose triangles all face inwards.</summary>
    public bool IsInsideOut => IsClosed && SignedVolume < 0;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{VertexCount} vertices, {TriangleCount} triangles, {Components} component(s); " +
        $"boundary {BoundaryEdges}, non-manifold {NonManifoldEdges}, inconsistent {InconsistentEdges}, " +
        $"degenerate {DegenerateTriangles}, duplicate {DuplicateTriangles}, unreferenced {UnreferencedVertices}";
}

public sealed partial class Mesh
{
    /// <summary>
    /// Checks topology: holes, non-manifold and inconsistently wound edges,
    /// degenerate and duplicate triangles, and connected components. Both
    /// CoACD and CuACD expect closed meshes; anything else gets remeshed by
    /// their preprocessing (or decomposes poorly with it off).
    /// </summary>
    /// <exception cref="ArgumentException">An index is out of range.</exception>
    public MeshReport Analyze()
    {
        MeshValidation.ValidateIndices(Vertices.Length, Indices, nameof(Indices));
        int nt = TriangleCount;

        var referenced = new bool[Vertices.Length];
        var edges = new Dictionary<long, (int Forward, int Backward)>();
        var triangles = new HashSet<(int, int, int)>();
        var parent = new int[nt];
        for (int i = 0; i < nt; i++)
            parent[i] = i;
        var edgeOwner = new Dictionary<long, int>();
        var skipped = new bool[nt];
        int degenerate = 0, duplicate = 0;
        double volume = 0;
        Vector3 origin = Vertices.Length > 0 ? Vertices[0] : default;

        for (int t = 0; t < nt; t++)
        {
            int a = Indices[3 * t], b = Indices[3 * t + 1], c = Indices[3 * t + 2];
            referenced[a] = referenced[b] = referenced[c] = true;
            if (a == b || b == c || a == c ||
                Vector3.Cross(Vertices[b] - Vertices[a], Vertices[c] - Vertices[a]).LengthSquared() == 0)
            {
                degenerate++;
                skipped[t] = true;
                continue;
            }
            if (!triangles.Add(Sorted(a, b, c)))
                duplicate++;
            volume += Triple(Vertices[a] - origin, Vertices[b] - origin, Vertices[c] - origin);
            AddEdge(a, b, t);
            AddEdge(b, c, t);
            AddEdge(c, a, t);
        }

        int boundary = 0, nonManifold = 0, inconsistent = 0;
        foreach (var (forward, backward) in edges.Values)
        {
            int uses = forward + backward;
            if (uses == 1)
                boundary++;
            else if (uses > 2)
                nonManifold++;
            else if (forward != 1)
                inconsistent++;
        }

        int components = 0;
        for (int t = 0; t < nt; t++)
            if (!skipped[t] && Find(t) == t)
                components++;

        int unreferenced = 0;
        foreach (bool r in referenced)
            if (!r)
                unreferenced++;

        return new MeshReport
        {
            VertexCount = Vertices.Length,
            TriangleCount = nt,
            UnreferencedVertices = unreferenced,
            DegenerateTriangles = degenerate,
            DuplicateTriangles = duplicate,
            BoundaryEdges = boundary,
            NonManifoldEdges = nonManifold,
            InconsistentEdges = inconsistent,
            Components = components,
            SignedVolume = volume / 6,
        };

        void AddEdge(int from, int to, int tri)
        {
            long key = from < to ? ((long)from << 32) | (uint)to : ((long)to << 32) | (uint)from;
            edges.TryGetValue(key, out var e);
            edges[key] = from < to ? (e.Forward + 1, e.Backward) : (e.Forward, e.Backward + 1);
            if (edgeOwner.TryGetValue(key, out int other))
                parent[Find(tri)] = Find(other);
            else
                edgeOwner[key] = tri;
        }

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
    }

    /// <summary>
    /// Returns a cleaned copy: vertices closer than
    /// <paramref name="weldTolerance"/> are merged; degenerate triangles,
    /// repeated triangles (same vertices and winding) and unreferenced vertices
    /// are removed; and a closed mesh that is wound inside out is flipped.
    /// </summary>
    /// <param name="weldTolerance">Merge distance; 0 merges only identical positions.</param>
    /// <exception cref="ArgumentException">An index is out of range, or the tolerance is negative.</exception>
    public Mesh Clean(float weldTolerance = 0f)
    {
        if (!(weldTolerance >= 0) || !float.IsFinite(weldTolerance))
            throw new ArgumentOutOfRangeException(nameof(weldTolerance), weldTolerance, "Must be a non-negative number.");
        MeshValidation.ValidateIndices(Vertices.Length, Indices, nameof(Indices));

        int[] weld = Weld(weldTolerance, out int welded);

        // Drop degenerate and duplicate triangles, then unreferenced vertices.
        var seen = new HashSet<(int, int, int)>();
        var kept = new List<int>(Indices.Length);
        for (int i = 0; i < Indices.Length; i += 3)
        {
            int a = weld[Indices[i]], b = weld[Indices[i + 1]], c = weld[Indices[i + 2]];
            if (a == b || b == c || a == c)
                continue;
            if (Vector3.Cross(Vertices[b] - Vertices[a], Vertices[c] - Vertices[a]).LengthSquared() == 0)
                continue;
            if (!seen.Add(Rotated(a, b, c)))
                continue;
            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        var remap = new int[Vertices.Length];
        Array.Fill(remap, -1);
        var vertices = new List<Vector3>(welded);
        int[] indices = kept.ToArray();
        for (int i = 0; i < indices.Length; i++)
        {
            int v = indices[i];
            if (remap[v] < 0)
            {
                remap[v] = vertices.Count;
                vertices.Add(Vertices[v]);
            }
            indices[i] = remap[v];
        }

        var result = new Mesh(vertices.ToArray(), indices);
        if (indices.Length > 0 && result.Analyze().IsInsideOut)
            for (int i = 0; i < indices.Length; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        return result;
    }

    // Maps each vertex to the index of its weld representative (the first
    // vertex within tolerance), using a hash grid with cell = tolerance.
    private int[] Weld(float tolerance, out int count)
    {
        var map = new int[Vertices.Length];
        count = 0;
        if (tolerance == 0)
        {
            var exact = new Dictionary<Vector3, int>();
            for (int i = 0; i < Vertices.Length; i++)
            {
                if (!exact.TryGetValue(Vertices[i], out int rep))
                {
                    exact[Vertices[i]] = rep = i;
                    count++;
                }
                map[i] = rep;
            }
            return map;
        }

        float inv = 1f / tolerance;
        float tol2 = tolerance * tolerance;
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < Vertices.Length; i++)
        {
            Vector3 p = Vertices[i];
            long cx = (long)MathF.Floor(p.X * inv), cy = (long)MathF.Floor(p.Y * inv), cz = (long)MathF.Floor(p.Z * inv);
            int rep = -1;
            for (long dx = -1; dx <= 1 && rep < 0; dx++)
                for (long dy = -1; dy <= 1 && rep < 0; dy++)
                    for (long dz = -1; dz <= 1 && rep < 0; dz++)
                        if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var cell))
                            foreach (int j in cell)
                                if (Vector3.DistanceSquared(Vertices[j], p) <= tol2)
                                {
                                    rep = j;
                                    break;
                                }
            if (rep < 0)
            {
                rep = i;
                count++;
                if (!grid.TryGetValue((cx, cy, cz), out var cell))
                    grid[(cx, cy, cz)] = cell = new List<int>();
                cell.Add(i);
            }
            map[i] = rep;
        }
        return map;
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    // Same triangle with the same winding, starting from its smallest index.
    private static (int, int, int) Rotated(int a, int b, int c) =>
        a <= b && a <= c ? (a, b, c) : b <= c ? (b, c, a) : (c, a, b);
}
