using System.Diagnostics;
using System.Numerics;
using ConvexDecomposition;
using ConvexDecomposition.Tests;
using Xunit.Abstractions;

namespace CuACD.Tests;

public sealed class GpuFixture : IDisposable
{
    // Explicit pool: the default (80% of free VRAM) is needlessly greedy for tests.
    public CuAcdContext Context { get; } = new(device: 0, poolBytes: 2L << 30);

    public void Dispose() => Context.Dispose();
}

[CollectionDefinition("GPU", DisableParallelization = true)]
public sealed class GpuCollection : ICollectionFixture<GpuFixture>;

[Collection("GPU")]
public sealed class CuAcdContextTests(GpuFixture gpu, ITestOutputHelper output)
{
    private CuAcdContext Ctx => gpu.Context;

    [Fact]
    public void ReportsDevice()
    {
        output.WriteLine($"Device: {Ctx.DeviceName}");
        Assert.False(string.IsNullOrWhiteSpace(Ctx.DeviceName));
        Assert.True(CuAcdContext.IsAvailable(out string? reason), reason);
    }

    [Fact]
    public void WorksThroughSharedInterface()
    {
        IConvexDecomposer decomposer = Ctx;
        IReadOnlyList<Mesh> hulls = decomposer.Decompose(TestMeshes.LShape());
        Assert.InRange(hulls.Count, 2, 4);
        Assert.Equal(3f, hulls.Sum(h => h.ComputeVolume()), 1);
    }

    [Fact]
    public void ConvexHullOfCubeWithInteriorPoints()
    {
        var rng = new Random(1);
        var pts = new List<Vector3>();
        for (int i = 0; i < 8; i++)
            pts.Add(new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * 2 - Vector3.One);
        for (int i = 0; i < 500; i++)
            pts.Add(new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * 1.8f - new Vector3(0.9f));

        Mesh hull = Ctx.ComputeConvexHull(pts.ToArray());

        Assert.Equal(8f, hull.ComputeVolume(), 3);
        Assert.Equal(8, hull.Vertices.Length);
        Assert.Equal(12, hull.TriangleCount);
        AssertOutward(hull);
    }

    [Fact]
    public void CheckMeshDetectsHoles()
    {
        Mesh box = TestMeshes.Box(Vector3.Zero, Vector3.One);
        Assert.False(Ctx.CheckMesh(box.Vertices, box.Indices).NeedsRemesh);

        int[] open = box.Indices[..^6]; // drop one quad
        MeshAudit audit = Ctx.CheckMesh(box.Vertices, open);
        Assert.False(audit.IsWatertight);
        Assert.True(audit.Defects.HasFlag(MeshDefects.OpenEdge));
    }

    [Fact]
    public void ConvexInputYieldsOnePartInWorldSpace()
    {
        var min = new Vector3(10, 20, 30);
        var max = new Vector3(14, 22, 31);
        Mesh box = TestMeshes.Box(min, max);

        var parts = Ctx.Decompose(box);

        ConvexPart part = Assert.Single(parts);
        var (lo, hi) = TestMeshes.Bounds(part.Hull.Vertices);
        AssertClose(min, lo, 1e-3f);
        AssertClose(max, hi, 1e-3f);
        Assert.Equal(8f, part.Hull.ComputeVolume(), 2);
        Assert.Equal(8f, part.HullVolume, 2);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(250f)]
    public void NonConvexInputIsSplit(float scale)
    {
        Mesh l = TestMeshes.LShape(scale);

        var parts = Ctx.Decompose(l, new CuAcdOptions { Threshold = 0.02f });

        output.WriteLine($"L-shape x{scale}: {parts.Count} parts");
        Assert.InRange(parts.Count, 2, 4);
        float total = parts.Sum(p => p.Hull.ComputeVolume());
        float expected = 3 * scale * scale * scale;
        Assert.InRange(total, expected * 0.97f, expected * 1.1f);
        foreach (var p in parts)
            AssertOutward(p.Hull);
    }

    [Fact]
    public void DecomposesUpstreamTestMesh()
    {
        Mesh mesh = TestMeshes.LoadBinaryStl(Path.Combine(AppContext.BaseDirectory, "data", "49160.stl"));
        var (inLo, inHi) = TestMeshes.Bounds(mesh.Vertices);
        float pad = (inHi - inLo).Length() * 0.02f;

        var sw = Stopwatch.StartNew();
        var parts = Ctx.Decompose(mesh);
        sw.Stop();

        output.WriteLine($"49160.stl: {mesh.Vertices.Length} verts, {mesh.TriangleCount} tris -> " +
                         $"{parts.Count} parts in {sw.ElapsedMilliseconds} ms; pool used {Ctx.PoolUsage >> 20} MiB");
        Assert.NotEmpty(parts);
        foreach (var p in parts)
        {
            Assert.True(p.Hull.TriangleCount >= 4);
            var (lo, hi) = TestMeshes.Bounds(p.Hull.Vertices);
            Assert.True(Vector3.Min(lo, inLo - new Vector3(pad)) == inLo - new Vector3(pad), $"hull below input bounds: {lo}");
            Assert.True(Vector3.Max(hi, inHi + new Vector3(pad)) == inHi + new Vector3(pad), $"hull above input bounds: {hi}");
        }

        // Real output through the shared post-processor, e.g. for an engine's hull budget.
        var hulls = parts.Select(p => p.Hull).ToArray();
        sw.Restart();
        var limited = new HullPostProcessor { MaxHulls = 16, MaxVerticesPerHull = 32 }.Process(hulls);
        sw.Stop();
        float before = hulls.Sum(h => h.ComputeVolume()), after = limited.Sum(h => h.ComputeVolume());
        output.WriteLine($"post-processed {hulls.Length} -> {limited.Count} hulls in {sw.ElapsedMilliseconds} ms; " +
                         $"volume {before:F3} -> {after:F3}");
        Assert.InRange(limited.Count, 1, 16);
        Assert.All(limited, h => Assert.InRange(h.Vertices.Length, 4, 32));
    }

    [Fact]
    public void PreprocessClosesHoles()
    {
        Mesh box = TestMeshes.Box(Vector3.Zero, new Vector3(2, 1, 1));
        int[] open = box.Indices[..^6];

        Mesh fixedMesh = Ctx.Preprocess(box.Vertices, open);

        Assert.False(Ctx.CheckMesh(fixedMesh.Vertices, fixedMesh.Indices).NeedsRemesh);
        var (lo, hi) = TestMeshes.Bounds(fixedMesh.Vertices);
        AssertClose(new Vector3(1, 0.5f, 0.5f), (lo + hi) / 2, 0.1f);
    }

    [Fact]
    public void AutoPreprocessHandlesOpenMesh()
    {
        // Remeshing an open surface yields a thickened hollow shell (upstream
        // behaviour), so expect many parts that together cover the input.
        Mesh u = TestMeshes.UShape();
        int[] open = u.Indices[..^6];

        var parts = Ctx.Decompose(u.Vertices, open);

        Assert.True(parts.Count >= 2);
        var (lo, hi) = TestMeshes.Bounds(parts.SelectMany(p => p.Hull.Vertices));
        AssertClose(Vector3.Zero, lo, 0.15f);
        AssertClose(new Vector3(3, 3, 1), hi, 0.15f);
    }

    [Fact]
    public async Task UsableFromManyThreads()
    {
        Mesh u = TestMeshes.UShape();
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => Ctx.Decompose(u).Count))
            .ToArray();
        int[] counts = await Task.WhenAll(tasks);
        output.WriteLine($"U-shape part counts: {string.Join(", ", counts)}");
        Assert.All(counts, c => Assert.InRange(c, 3, 6));
    }

    [Fact]
    public void RejectsMalformedInput()
    {
        Mesh box = TestMeshes.Box(Vector3.Zero, Vector3.One);
        Assert.Throws<ArgumentException>(() => Ctx.Decompose(box.Vertices, box.Indices[..^1]));
        int[] bad = (int[])box.Indices.Clone();
        bad[5] = 99;
        Assert.Throws<ArgumentException>(() => Ctx.Decompose(box.Vertices, bad));
        Vector3[] nan = (Vector3[])box.Vertices.Clone();
        nan[0].X = float.NaN;
        Assert.Throws<ArgumentException>(() => Ctx.Decompose(nan, box.Indices));
        Assert.Throws<ArgumentException>(() =>
            Ctx.Decompose(box, new CuAcdOptions { Width = 100, ConcaveEdgeSamples = 120 }));
    }

    [Fact]
    public void DegenerateHullReportsError()
    {
        var flat = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new Vector3(1, 1, 0) };
        var ex = Assert.Throws<CuAcdException>(() => Ctx.ComputeConvexHull(flat));
        output.WriteLine(ex.Message);
        Assert.Contains("hull", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertOutward(Mesh m)
    {
        Vector3 c = m.Vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / m.Vertices.Length;
        for (int i = 0; i < m.Indices.Length; i += 3)
        {
            Vector3 a = m.Vertices[m.Indices[i]], b = m.Vertices[m.Indices[i + 1]], d = m.Vertices[m.Indices[i + 2]];
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, d - a), a - c) >= -1e-4f, "inward-facing hull triangle");
        }
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tol) =>
        Assert.True(Vector3.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");
}
