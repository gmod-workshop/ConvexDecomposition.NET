using System.Numerics;
using ConvexDecomposition;

namespace ConvexDecomposition.Tests;

public sealed class ConvexHullTests
{
    [Fact]
    public void CubeWithInteriorAndSurfacePointsHasEightVertices()
    {
        // A 5x5x5 lattice: corners, edge, face and interior points, all exact.
        var points = new List<Vector3>();
        for (int x = 0; x <= 4; x++)
            for (int y = 0; y <= 4; y++)
                for (int z = 0; z <= 4; z++)
                    points.Add(new Vector3(x, y, z) * 0.25f);

        Mesh hull = ConvexHull.Compute(points.ToArray());

        Assert.Equal(8, hull.Vertices.Length);
        Assert.Equal(12, hull.TriangleCount);
        Assert.Equal(1f, hull.ComputeVolume(), 5);
        AssertValidHull(hull, points);
    }

    [Theory]
    [InlineData(0, 2000)]
    [InlineData(1, 5000)]
    [InlineData(2, 300)]
    public void RandomCloudsGiveClosedConvexHulls(int seed, int count)
    {
        var rng = new Random(seed);
        var points = Enumerable.Range(0, count)
            .Select(_ => new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * 2 - Vector3.One)
            .ToList();

        AssertValidHull(ConvexHull.Compute(points.ToArray()), points);
    }

    [Fact]
    public void PointsOnASphereAreAllKept()
    {
        // Every point is extreme and neighbours are nearly coplanar.
        var points = FibonacciSphere(3000, 5f);

        Mesh hull = ConvexHull.Compute(points.ToArray());

        AssertValidHull(hull, points);
        Assert.InRange(hull.Vertices.Length, 2990, 3000);
        Assert.Equal(4f / 3f * MathF.PI * 125f, hull.ComputeVolume(), 125f * 0.01f);
    }

    [Fact]
    public void DuplicatesAndFarOffsetsAreHandled()
    {
        var offset = new Vector3(10000, -5000, 20000);
        var points = FibonacciSphere(500, 1f).SelectMany(p => new[] { p + offset, p + offset, p + offset }).ToList();

        Mesh hull = ConvexHull.Compute(points.ToArray());

        AssertValidHull(hull, points);
        Assert.Equal(4f / 3f * MathF.PI, hull.ComputeVolume(), 0.1f);
    }

    [Fact]
    public void VertexBudgetKeepsHullInside()
    {
        var points = FibonacciSphere(1000, 1f);

        Mesh full = ConvexHull.Compute(points.ToArray());
        Mesh reduced = ConvexHull.Compute(points.ToArray(), maxVertices: 24);

        Assert.InRange(reduced.Vertices.Length, 4, 24);
        AssertValidHull(reduced, reduced.Vertices);
        Assert.All(reduced.Vertices, v => Assert.Contains(v, points));
        Assert.InRange(reduced.ComputeVolume(), full.ComputeVolume() * 0.7f, full.ComputeVolume());
    }

    [Fact]
    public void CoplanarPointsAreRejected()
    {
        var flat = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(0.5f, 0.5f, 0) };

        Assert.Throws<ArgumentException>(() => ConvexHull.Compute(flat));
        Assert.Throws<ArgumentException>(() => ConvexHull.Compute(flat.Take(3).ToArray()));
    }

    [Theory]
    [InlineData(0.1f, 1.2f)]
    [InlineData(-0.1f, 0.8f)]
    public void OffsetMovesBoxFaces(float distance, float size)
    {
        Mesh box = TestMeshes.Box(new Vector3(2, 3, 4), new Vector3(3, 4, 5));

        Mesh offset = ConvexHull.Offset(box, distance);

        var (lo, hi) = offset.ComputeBounds();
        AssertClose(new Vector3(2, 3, 4) - new Vector3(distance), lo, 1e-4f);
        AssertClose(new Vector3(3, 4, 5) + new Vector3(distance), hi, 1e-4f);
        Assert.Equal(size * size * size, offset.ComputeVolume(), 3);
        Assert.Equal(8, offset.Vertices.Length);
    }

    [Fact]
    public void OffsetGrowsSphereByAboutAreaTimesDistance()
    {
        Mesh hull = ConvexHull.Compute(FibonacciSphere(400, 1f).ToArray());

        Mesh grown = ConvexHull.Offset(hull, 0.05f);

        AssertValidHull(grown, grown.Vertices);
        float expected = 4f / 3f * MathF.PI * MathF.Pow(1.05f, 3);
        Assert.InRange(grown.ComputeVolume(), expected * 0.98f, expected * 1.03f);
    }

    [Fact]
    public void ShrinkingAThinPlateDoesNotCollapseIt()
    {
        Mesh plate = TestMeshes.Box(Vector3.Zero, new Vector3(10, 10, 0.2f));

        Mesh shrunk = ConvexHull.Offset(plate, -1f);

        var (lo, hi) = shrunk.ComputeBounds();
        Assert.True(hi.Z - lo.Z > 0.05f, $"thickness {hi.Z - lo.Z}");
        Assert.True(shrunk.ComputeVolume() > 0);
    }

    // ------------------------------------------------------------------

    internal static List<Vector3> FibonacciSphere(int n, float radius)
    {
        var points = new List<Vector3>(n);
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float y = 1 - 2 * (i + 0.5f) / n;
            float r = MathF.Sqrt(1 - y * y);
            points.Add(radius * new Vector3(MathF.Cos(golden * i) * r, y, MathF.Sin(golden * i) * r));
        }
        return points;
    }

    /// <summary>Closed, outward-wound, and every point on or behind every face.</summary>
    internal static void AssertValidHull(Mesh hull, IEnumerable<Vector3> points)
    {
        MeshReport report = hull.Analyze();
        Assert.True(report.IsClosed, report.ToString());
        Assert.True(report.SignedVolume > 0, "hull is inside out");
        Assert.Equal(1, report.Components);

        var (lo, hi) = hull.ComputeBounds();
        float scale = MathF.Max(Vector3.Distance(lo, hi), 1f);
        float maxAbs = MathF.Max(MathF.Max(MathF.Abs(lo.X), MathF.Abs(hi.X)), MathF.Max(MathF.Max(MathF.Abs(lo.Y), MathF.Abs(hi.Y)), MathF.Max(MathF.Abs(lo.Z), MathF.Abs(hi.Z))));
        float tol = 1e-5f * MathF.Max(scale, maxAbs);
        // Every edge convex: each triangle's far vertex is behind its neighbour.
        var faceOf = new Dictionary<(int, int), int>();
        for (int i = 0; i < hull.Indices.Length; i += 3)
            for (int k = 0; k < 3; k++)
                faceOf[(hull.Indices[i + k], hull.Indices[i + (k + 1) % 3])] = i;
        foreach (var ((a, b), f) in faceOf)
        {
            int g = faceOf[(b, a)];
            int far = hull.Indices.Skip(g).Take(3).Single(v => v != a && v != b);
            Vector3 p0 = hull.Vertices[hull.Indices[f]];
            Vector3 n = Vector3.Normalize(Vector3.Cross(hull.Vertices[hull.Indices[f + 1]] - p0, hull.Vertices[hull.Indices[f + 2]] - p0));
            float h = Vector3.Dot(n, hull.Vertices[far] - p0);
            Assert.True(h <= tol, $"concave edge {a}-{b}: {h}");
        }

        var pts = points.ToArray();
        for (int i = 0; i < hull.Indices.Length; i += 3)
        {
            Vector3 a = hull.Vertices[hull.Indices[i]];
            Vector3 n = Vector3.Cross(hull.Vertices[hull.Indices[i + 1]] - a, hull.Vertices[hull.Indices[i + 2]] - a);
            float len = n.Length();
            Assert.True(len > 0, "zero-area hull face");
            n /= len;
            foreach (Vector3 p in pts)
            {
                float d = Vector3.Dot(n, p - a);
                Assert.True(d <= tol, $"point {p} is {d} outside face {i / 3}");
            }
        }
    }

    internal static void AssertClose(Vector3 expected, Vector3 actual, float tol) =>
        Assert.True(Vector3.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");
}
