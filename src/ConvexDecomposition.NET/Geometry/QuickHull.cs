using System;
using System.Collections.Generic;

namespace ConvexDecomposition.Geometry;

/// <summary>A triangulated convex hull: compact vertices, outward-wound triangles.</summary>
internal sealed class HullResult
{
    public HullResult(Double3[] vertices, int[] indices)
    {
        Vertices = vertices;
        Indices = indices;
    }

    public Double3[] Vertices { get; }
    public int[] Indices { get; }

    public double ComputeVolume()
    {
        // Relative to a vertex, so far-from-origin hulls keep their precision.
        Double3 o = Vertices[0];
        double sum = 0;
        for (int i = 0; i < Indices.Length; i += 3)
            sum += Double3.Dot(Vertices[Indices[i]] - o,
                Double3.Cross(Vertices[Indices[i + 1]] - o, Vertices[Indices[i + 2]] - o));
        return sum / 6.0;
    }

    public Mesh ToMesh()
    {
        var v = new System.Numerics.Vector3[Vertices.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = Vertices[i].ToVector3();
        return new Mesh(v, (int[])Indices.Clone());
    }
}

/// <summary>
/// 3D quickhull (Barber et al. 1996) over triangles, in double precision.
/// </summary>
/// <remarks>
/// Points are added farthest-first, so stopping at a vertex budget yields the
/// greedy farthest-point inner approximation of the hull. Points within a
/// float-precision tolerance of the hull count as on it and are dropped, so
/// coplanar and duplicate input adds no vertices.
/// </remarks>
internal sealed class QuickHull
{
    private sealed class Face
    {
        public int A, B, C;
        public Double3 Normal;
        public double Offset;
        public List<int>? Outside;
        public int FarPoint = -1;
        public double FarDistance;
        public bool Alive = true;
        public int Mark;

        public double Distance(Double3 p) => Double3.Dot(Normal, p) - Offset;
    }

    // Single-precision epsilon: inputs are floats, so anything closer to a
    // plane than their rounding error is treated as lying on it.
    private const double Epsilon = 3 * 1.1920929e-7;

    private readonly Double3[] _p;
    private readonly double _tolerance;
    private readonly List<Face> _faces = new();
    private readonly Dictionary<long, Face> _edges = new();
    private readonly PriorityQueue<Face, double> _queue = new();
    private int _mark;
    private int _vertexCount;

    private QuickHull(Double3[] points, double minTolerance)
    {
        _p = points;
        double ax = 0, ay = 0, az = 0;
        foreach (Double3 p in points)
        {
            ax = Math.Max(ax, Math.Abs(p.X));
            ay = Math.Max(ay, Math.Abs(p.Y));
            az = Math.Max(az, Math.Abs(p.Z));
        }
        _tolerance = Math.Max(Epsilon * (ax + ay + az), minTolerance);
    }

    /// <summary>Computes the hull, or returns false if the points are (nearly) coplanar.</summary>
    /// <param name="points">Input points; not modified.</param>
    /// <param name="maxVertices">Vertex budget (at least 4).</param>
    /// <param name="hull">The hull, when this returns true.</param>
    /// <param name="minTolerance">
    /// Points closer than this to the hull are dropped (the default tolerance is
    /// float rounding error); larger values trade accuracy for fewer vertices.
    /// </param>
    public static bool TryCompute(ReadOnlySpan<Double3> points, int maxVertices, out HullResult hull, double minTolerance = 0)
    {
        hull = null!;
        if (points.Length < 4)
            return false;
        var qh = new QuickHull(points.ToArray(), minTolerance);
        if (!qh.BuildSimplex())
            return false;
        qh.Expand(Math.Max(4, maxVertices));
        hull = qh.Extract();
        return true;
    }

    /// <inheritdoc cref="TryCompute(ReadOnlySpan{Double3}, int, out HullResult, double)"/>
    public static bool TryCompute(ReadOnlySpan<System.Numerics.Vector3> points, int maxVertices, out HullResult hull)
    {
        var d = new Double3[points.Length];
        for (int i = 0; i < d.Length; i++)
            d[i] = new Double3(points[i]);
        return TryCompute(d, maxVertices, out hull);
    }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    private bool BuildSimplex()
    {
        // Extreme points along each axis; the farthest pair seeds the simplex.
        Span<int> ext = stackalloc int[6];
        ext.Clear();
        for (int i = 1; i < _p.Length; i++)
        {
            Double3 p = _p[i];
            if (p.X < _p[ext[0]].X) ext[0] = i;
            if (p.X > _p[ext[1]].X) ext[1] = i;
            if (p.Y < _p[ext[2]].Y) ext[2] = i;
            if (p.Y > _p[ext[3]].Y) ext[3] = i;
            if (p.Z < _p[ext[4]].Z) ext[4] = i;
            if (p.Z > _p[ext[5]].Z) ext[5] = i;
        }

        int i0 = 0, i1 = 0;
        double best = -1;
        for (int a = 0; a < 6; a++)
            for (int b = a + 1; b < 6; b++)
            {
                double d = (_p[ext[a]] - _p[ext[b]]).LengthSquared;
                if (d > best)
                {
                    best = d;
                    i0 = ext[a];
                    i1 = ext[b];
                }
            }
        if (Math.Sqrt(best) <= _tolerance)
            return false;

        Double3 dir = _p[i1] - _p[i0];
        double dirLen = dir.Length;
        int i2 = -1;
        best = _tolerance;
        for (int i = 0; i < _p.Length; i++)
        {
            double d = Double3.Cross(_p[i] - _p[i0], dir).Length / dirLen;
            if (d > best)
            {
                best = d;
                i2 = i;
            }
        }
        if (i2 < 0)
            return false;

        Double3 n = Double3.Cross(_p[i1] - _p[i0], _p[i2] - _p[i0]);
        n /= n.Length;
        int i3 = -1;
        best = _tolerance;
        for (int i = 0; i < _p.Length; i++)
        {
            double d = Math.Abs(Double3.Dot(n, _p[i] - _p[i0]));
            if (d > best)
            {
                best = d;
                i3 = i;
            }
        }
        if (i3 < 0)
            return false;

        Double3 centroid = (_p[i0] + _p[i1] + _p[i2] + _p[i3]) * 0.25;
        var initial = new[]
        {
            Oriented(i0, i1, i2, centroid), Oriented(i0, i1, i3, centroid),
            Oriented(i0, i2, i3, centroid), Oriented(i1, i2, i3, centroid),
        };
        _vertexCount = 4;

        for (int i = 0; i < _p.Length; i++)
        {
            if (i == i0 || i == i1 || i == i2 || i == i3)
                continue;
            Assign(i, initial);
        }
        foreach (Face f in initial)
            Enqueue(f);
        return true;
    }

    private Face Oriented(int a, int b, int c, Double3 inside)
    {
        Double3 n = Double3.Cross(_p[b] - _p[a], _p[c] - _p[a]);
        return Double3.Dot(n, inside - _p[a]) > 0 ? AddFace(a, c, b) : AddFace(a, b, c);
    }

    private Face AddFace(int a, int b, int c)
    {
        Double3 n = Double3.Cross(_p[b] - _p[a], _p[c] - _p[a]);
        double len = n.Length;
        if (len > 0)
            n /= len;
        var f = new Face
        {
            A = a,
            B = b,
            C = c,
            Normal = n,
            Offset = Double3.Dot(n, (_p[a] + _p[b] + _p[c]) * (1.0 / 3.0)),
        };
        _faces.Add(f);
        _edges[Key(a, b)] = f;
        _edges[Key(b, c)] = f;
        _edges[Key(c, a)] = f;
        return f;
    }

    private void Assign(int point, IReadOnlyList<Face> candidates)
    {
        Face? target = null;
        double best = _tolerance;
        foreach (Face f in candidates)
        {
            double d = f.Distance(_p[point]);
            if (d > best)
            {
                best = d;
                target = f;
            }
        }
        if (target is null)
            return; // inside (or on) the hull
        (target.Outside ??= new List<int>()).Add(point);
        if (best > target.FarDistance)
        {
            target.FarDistance = best;
            target.FarPoint = point;
        }
    }

    private void Enqueue(Face f)
    {
        if (f.Outside is { Count: > 0 })
            _queue.Enqueue(f, -f.FarDistance);
    }

    private void Expand(int maxVertices)
    {
        while (_vertexCount < maxVertices && _queue.TryDequeue(out Face? f, out _))
            if (f.Alive)
                AddPoint(f);
    }

    private void AddPoint(Face start)
    {
        int eye = start.FarPoint;
        Double3 e = _p[eye];

        // Faces that see the eye point, grown from the face it was assigned to.
        int mark = ++_mark;
        var visible = new List<Face> { start };
        start.Mark = mark;
        for (int k = 0; k < visible.Count; k++)
        {
            Face v = visible[k];
            Visit(v.B, v.A);
            Visit(v.C, v.B);
            Visit(v.A, v.C);
        }

        // Visibility uses the exact sign, not the tolerance: for two triangles
        // sharing an edge, "eye above the neighbour" and "neighbour's far
        // vertex above the new face" are the same orientation test, so every
        // new horizon edge is convex. (Treating a face the eye sees by less
        // than the tolerance as hidden folds the hull inwards next to slivers.)
        void Visit(int a, int b)
        {
            Face n = _edges[Key(a, b)];
            if (n.Mark != mark && n.Distance(e) > 0)
            {
                n.Mark = mark;
                visible.Add(n);
            }
        }

        // Horizon: edges of visible faces whose twin face is not visible.
        var horizon = new List<(int A, int B)>();
        var horizonVerts = new HashSet<int>();
        var visibleVerts = new HashSet<int>();
        var orphans = new List<int>();
        foreach (Face v in visible)
        {
            CheckEdge(v.A, v.B);
            CheckEdge(v.B, v.C);
            CheckEdge(v.C, v.A);
            visibleVerts.Add(v.A);
            visibleVerts.Add(v.B);
            visibleVerts.Add(v.C);
            if (v.Outside is not null)
                foreach (int p in v.Outside)
                    if (p != eye)
                        orphans.Add(p);
        }

        void CheckEdge(int a, int b)
        {
            if (_edges[Key(b, a)].Mark != mark)
            {
                horizon.Add((a, b));
                horizonVerts.Add(a);
                horizonVerts.Add(b);
            }
        }

        foreach (Face v in visible)
        {
            v.Alive = false;
            v.Outside = null;
            _edges.Remove(Key(v.A, v.B));
            _edges.Remove(Key(v.B, v.C));
            _edges.Remove(Key(v.C, v.A));
        }

        var created = new List<Face>(horizon.Count);
        foreach (var (a, b) in horizon)
            created.Add(AddFace(a, b, eye));
        foreach (int p in orphans)
            Assign(p, created);
        foreach (Face f in created)
            Enqueue(f);

        visibleVerts.ExceptWith(horizonVerts);
        _vertexCount += 1 - visibleVerts.Count;
    }

    private HullResult Extract()
    {
        var remap = new Dictionary<int, int>();
        var vertices = new List<Double3>();
        var indices = new List<int>();
        foreach (Face f in _faces)
        {
            if (!f.Alive)
                continue;
            indices.Add(Map(f.A));
            indices.Add(Map(f.B));
            indices.Add(Map(f.C));
        }
        return new HullResult(vertices.ToArray(), indices.ToArray());

        int Map(int i)
        {
            if (!remap.TryGetValue(i, out int j))
            {
                j = vertices.Count;
                vertices.Add(_p[i]);
                remap[i] = j;
            }
            return j;
        }
    }
}
