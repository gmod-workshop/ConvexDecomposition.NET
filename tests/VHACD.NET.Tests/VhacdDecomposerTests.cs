using System.Diagnostics;
using System.Numerics;
using ConvexDecomposition;
using ConvexDecomposition.Tests;
using Xunit.Abstractions;

namespace VHACD.Tests;

public sealed class VhacdDecomposerTests(ITestOutputHelper output)
{
    private readonly VhacdDecomposer _vhacd = new();

    [Fact]
    public void ConvexInputYieldsOneHullInWorldSpace()
    {
        var min = new Vector3(10, 20, 30);
        var max = new Vector3(14, 22, 31);

        var hulls = _vhacd.Decompose(TestMeshes.Box(min, max));

        Mesh hull = Assert.Single(hulls);
        var (lo, hi) = TestMeshes.Bounds(hull.Vertices);
        AssertClose(min, lo, 0.05f);
        AssertClose(max, hi, 0.05f);
        Assert.Equal(8f, hull.ComputeVolume(), 0.2f);
        Assert.True(hull.Analyze().IsClosed);
    }

    [Fact]
    public void NonConvexInputIsSplit()
    {
        var hulls = _vhacd.Decompose(TestMeshes.LShape(10f));

        output.WriteLine($"L-shape: {hulls.Count} hulls, volume {hulls.Sum(h => h.ComputeVolume()):F0}");
        Assert.InRange(hulls.Count, 2, 4);
        Assert.InRange(hulls.Sum(h => h.ComputeVolume()), 3000f * 0.95f, 3000f * 1.1f);
    }

    [Fact]
    public void LimitsAreEnforced()
    {
        // V-HACD over-splits curved convex shapes: the voxelized volume never
        // gets within 1% of the hull's, so a sphere splits up to the hull limit.
        Mesh sphere = TestMeshes.Sphere(1f, segments: 64, rings: 32);
        var capped = _vhacd.Decompose(sphere, new VhacdOptions { MaxVerticesPerHull = 16, MaxConvexHulls = 8 });
        output.WriteLine($"sphere: {capped.Count} hulls, max {capped.Max(h => h.Vertices.Length)} vertices");
        Assert.InRange(capped.Count, 1, 8);
        Assert.All(capped, h => Assert.InRange(h.Vertices.Length, 4, 16));

        var u = _vhacd.Decompose(TestMeshes.UShape(), new VhacdOptions { MaxConvexHulls = 2, MinVolumeErrorPercent = 0.01 });
        Assert.InRange(u.Count, 1, 2);
    }

    [Fact]
    public async Task ConcurrentCallsAgree()
    {
        Mesh u = TestMeshes.UShape();
        int expected = _vhacd.Decompose(u).Count;

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => _vhacd.Decompose(u).Count)).ToArray();
        int[] counts = await Task.WhenAll(tasks);

        output.WriteLine($"sequential {expected}, concurrent {string.Join(", ", counts)}");
        Assert.All(counts, c => Assert.Equal(expected, c));
    }

    [Fact]
    public void WorksThroughSharedInterfaceWithPostProcessing()
    {
        IConvexDecomposer decomposer = new VhacdDecomposer()
            .WithPostProcessing(new HullPostProcessor { MaxHulls = 1, MaxVerticesPerHull = 8 });

        Mesh hull = Assert.Single(decomposer.Decompose(TestMeshes.UShape()));

        Assert.InRange(hull.Vertices.Length, 4, 8);
        Assert.StartsWith("vhacd/", decomposer.Fingerprint);
    }

    [Fact]
    public void RejectsInvalidInput()
    {
        Mesh box = TestMeshes.Box(Vector3.Zero, Vector3.One);
        Assert.Throws<ArgumentException>(() => _vhacd.Decompose(box.Vertices, box.Indices[..^1]));
        int[] bad = (int[])box.Indices.Clone();
        bad[0] = 99;
        Assert.Throws<ArgumentException>(() => _vhacd.Decompose(box.Vertices, bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => _vhacd.Decompose(box, new VhacdOptions { Resolution = 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _vhacd.Decompose(box, new VhacdOptions { MaxVerticesPerHull = 4 }));
    }

    [Fact]
    public void DecomposesUpstreamTestMesh()
    {
        Mesh mesh = TestMeshes.LoadBinaryStl(Path.Combine(AppContext.BaseDirectory, "data", "49160.stl"));

        var sw = Stopwatch.StartNew();
        var hulls = _vhacd.Decompose(mesh);
        sw.Stop();

        output.WriteLine($"49160.stl: {hulls.Count} hulls in {sw.Elapsed.TotalSeconds:F2} s, " +
                         $"{hulls.Max(h => h.Vertices.Length)} max vertices");
        Assert.InRange(hulls.Count, 2, 64);
        Assert.All(hulls, h => Assert.True(h.Vertices.Length <= 64));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tol) =>
        Assert.True(Vector3.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");
}
