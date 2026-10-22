using System.Numerics;
using ConvexDecomposition;
using static ConvexDecomposition.Tests.ConvexHullTests;

namespace ConvexDecomposition.Tests;

public sealed class MeshToolsTests
{
    [Fact]
    public void BoxMassPropertiesMatchClosedForm()
    {
        // 2 x 3 x 4 box, density 0.5, far from the origin.
        var min = new Vector3(100, -50, 7);
        Mesh box = TestMeshes.Box(min, min + new Vector3(2, 3, 4));

        MassProperties mp = box.ComputeMassProperties(density: 0.5f);

        Assert.Equal(24f, mp.Volume, 3);
        Assert.Equal(12f, mp.Mass, 3);
        AssertClose(min + new Vector3(1, 1.5f, 2), mp.CenterOfMass, 1e-3f);
        float m = mp.Mass;
        Assert.Equal(m * (9 + 16) / 12, mp.Inertia.Ixx, 2);
        Assert.Equal(m * (4 + 16) / 12, mp.Inertia.Iyy, 2);
        Assert.Equal(m * (4 + 9) / 12, mp.Inertia.Izz, 2);
        Assert.Equal(0f, mp.Inertia.Ixy, 2);
        Assert.Equal(0f, mp.Inertia.Ixz, 2);
        Assert.Equal(0f, mp.Inertia.Iyz, 2);
    }

    [Fact]
    public void CombinedHalvesEqualTheWhole()
    {
        Mesh whole = TestMeshes.Box(Vector3.Zero, new Vector3(2, 1, 1));
        Mesh left = TestMeshes.Box(Vector3.Zero, new Vector3(1, 1, 1));
        Mesh right = TestMeshes.Box(new Vector3(1, 0, 0), new Vector3(2, 1, 1));

        MassProperties expected = whole.ComputeMassProperties();
        MassProperties combined = MassProperties.Combine(new[] { left.ComputeMassProperties(), right.ComputeMassProperties() });

        Assert.Equal(expected.Mass, combined.Mass, 4);
        AssertClose(expected.CenterOfMass, combined.CenterOfMass, 1e-5f);
        Assert.Equal(expected.Inertia.Ixx, combined.Inertia.Ixx, 4);
        Assert.Equal(expected.Inertia.Iyy, combined.Inertia.Iyy, 4);
        Assert.Equal(expected.Inertia.Izz, combined.Inertia.Izz, 4);
    }

    [Fact]
    public void ProductsOfInertiaHaveConventionalSign()
    {
        // Two small cubes on the +x+y / −x−y diagonal: ∫xy > 0, so Ixy < 0.
        var parts = new[]
        {
            TestMeshes.Box(new Vector3(1, 1, -0.5f), new Vector3(2, 2, 0.5f)).ComputeMassProperties(),
            TestMeshes.Box(new Vector3(-2, -2, -0.5f), new Vector3(-1, -1, 0.5f)).ComputeMassProperties(),
        };

        Assert.True(MassProperties.Combine(parts).Inertia.Ixy < 0);
    }

    [Fact]
    public void AnalyzeReportsDefects()
    {
        Mesh box = TestMeshes.Box(Vector3.Zero, Vector3.One);
        MeshReport ok = box.Analyze();
        Assert.True(ok.IsClosed);
        Assert.False(ok.IsInsideOut);
        Assert.Equal(1, ok.Components);
        Assert.Equal(1.0, ok.SignedVolume, 6);

        MeshReport open = new Mesh(box.Vertices, box.Indices[3..]).Analyze();
        Assert.Equal(3, open.BoundaryEdges);
        Assert.False(open.IsWatertight);

        int[] flipped = (int[])box.Indices.Clone();
        (flipped[1], flipped[2]) = (flipped[2], flipped[1]);
        MeshReport inconsistent = new Mesh(box.Vertices, flipped).Analyze();
        Assert.True(inconsistent.IsWatertight);
        Assert.Equal(3, inconsistent.InconsistentEdges);
        Assert.False(inconsistent.IsClosed);

        MeshReport inverted = Inverted(box).Analyze();
        Assert.True(inverted.IsInsideOut);

        Mesh two = Concat(box, TestMeshes.Box(new Vector3(5, 0, 0), new Vector3(6, 1, 1)));
        Assert.Equal(2, two.Analyze().Components);

        var degenerate = new Mesh(box.Vertices, box.Indices.Concat(new[] { 0, 0, 1 }).ToArray()).Analyze();
        Assert.Equal(1, degenerate.DegenerateTriangles);
    }

    [Fact]
    public void CleanWeldsTriangleSoupAndFixesWinding()
    {
        // A box as unwelded triangle soup (like STL), wound inside out, with
        // tiny position noise, a degenerate triangle and a repeated triangle.
        Mesh box = Inverted(TestMeshes.Box(Vector3.Zero, Vector3.One));
        var rng = new Random(3);
        var soup = new List<Vector3>();
        foreach (int i in box.Indices)
            soup.Add(box.Vertices[i] + new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * 1e-5f);
        var indices = Enumerable.Range(0, soup.Count).ToList();
        indices.AddRange(new[] { 0, 1, 2, 3, 3, 4 });

        Mesh cleaned = new Mesh(soup.ToArray(), indices.ToArray()).Clean(weldTolerance: 1e-3f);

        Assert.Equal(8, cleaned.Vertices.Length);
        Assert.Equal(12, cleaned.TriangleCount);
        MeshReport report = cleaned.Analyze();
        Assert.True(report.IsClosed, report.ToString());
        Assert.False(report.IsInsideOut);
    }

    [Fact]
    public void ObjRoundTrips()
    {
        var hulls = new[] { TestMeshes.Box(Vector3.Zero, Vector3.One), TestMeshes.Box(new Vector3(2, 0, 0), new Vector3(3, 1.5f, 1)) };
        var text = new StringWriter();

        ObjFile.Write(text, hulls);
        Mesh read = ObjFile.Read(new StringReader(text.ToString()));

        Assert.Contains("o hull_1", text.ToString());
        Assert.Equal(16, read.Vertices.Length);
        Assert.Equal(24, read.TriangleCount);
        Assert.Equal(hulls.Sum(h => h.ComputeVolume()), read.ComputeVolume(), 4);
        Assert.Equal(hulls[1].Vertices[3], read.Vertices[11]);
    }

    [Fact]
    public void ObjReaderTriangulatesPolygonsAndHandlesRelativeIndices()
    {
        const string obj = """
            # quad pyramid
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vn 0 0 1
            v 0.5 0.5 1
            f 4/1/1 3/1/1 2/1/1 1/1/1
            f -5 -4 -1
            f 2 3 5
            f 3 4 5
            f 4 1 5
            """;

        Mesh m = ObjFile.Read(new StringReader(obj));

        Assert.Equal(5, m.Vertices.Length);
        Assert.Equal(6, m.TriangleCount);
        Assert.True(m.Analyze().IsClosed);
        Assert.Equal(1f / 3f, m.ComputeVolume(), 5);
    }

    internal static Mesh Inverted(Mesh m)
    {
        int[] idx = (int[])m.Indices.Clone();
        for (int i = 0; i < idx.Length; i += 3)
            (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        return new Mesh(m.Vertices, idx);
    }

    internal static Mesh Concat(params Mesh[] meshes)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        foreach (Mesh m in meshes)
        {
            int b = v.Count;
            v.AddRange(m.Vertices);
            t.AddRange(m.Indices.Select(i => i + b));
        }
        return new Mesh(v.ToArray(), t.ToArray());
    }
}
