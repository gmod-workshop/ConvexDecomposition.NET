using System.Diagnostics;
using System.Numerics;
using CoACD.Interop;
using ConvexDecomposition;
using ConvexDecomposition.Tests;
using Xunit.Abstractions;

namespace CoACD.Tests;

public sealed class CoAcdDecomposerTests(ITestOutputHelper output)
{
    private readonly CoAcdDecomposer _coacd = new();

    [Fact]
    public void ConvexInputYieldsOneHullInWorldSpace()
    {
        var min = new Vector3(10, 20, 30);
        var max = new Vector3(14, 22, 31);

        var hulls = _coacd.Decompose(TestMeshes.Box(min, max));

        Mesh hull = Assert.Single(hulls);
        var (lo, hi) = TestMeshes.Bounds(hull.Vertices);
        AssertClose(min, lo, 1e-3f);
        AssertClose(max, hi, 1e-3f);
        Assert.Equal(8f, hull.ComputeVolume(), 2);
    }

    [Fact]
    public void NonConvexInputIsSplit()
    {
        var hulls = _coacd.Decompose(TestMeshes.LShape(10f));

        output.WriteLine($"L-shape: {hulls.Count} hulls");
        Assert.InRange(hulls.Count, 2, 4);
        Assert.InRange(hulls.Sum(h => h.ComputeVolume()), 3000f * 0.97f, 3000f * 1.1f);
    }

    [Fact]
    public void MaxHullVerticesIsEnforced()
    {
        Mesh sphere = TestMeshes.Sphere(1f, segments: 32, rings: 16);

        Mesh exact = Assert.Single(_coacd.Decompose(sphere));
        Mesh capped = Assert.Single(_coacd.Decompose(sphere, new CoAcdOptions { MaxHullVertices = 24 }));

        output.WriteLine($"sphere hull: {exact.Vertices.Length} verts exact, {capped.Vertices.Length} capped");
        Assert.True(exact.Vertices.Length > 100);
        Assert.InRange(capped.Vertices.Length, 4, 24);
    }

    [Fact]
    public void MaxConvexHullsIsEnforced()
    {
        var hulls = _coacd.Decompose(TestMeshes.UShape(), new CoAcdOptions { Threshold = 0.01f, MaxConvexHulls = 2 });
        Assert.InRange(hulls.Count, 1, 2);
    }

    [Fact]
    public void SameSeedIsDeterministic()
    {
        Mesh u = TestMeshes.UShape();
        var a = _coacd.Decompose(u, new CoAcdOptions { Seed = 7 });
        var b = _coacd.Decompose(u, new CoAcdOptions { Seed = 7 });
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Sum(h => h.ComputeVolume()), b.Sum(h => h.ComputeVolume()), 4);
    }

    [Fact]
    public async Task ConcurrentCallsAgree()
    {
        Mesh u = TestMeshes.UShape();
        int expected = _coacd.Decompose(u).Count;

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => _coacd.Decompose(u).Count)).ToArray();
        int[] counts = await Task.WhenAll(tasks);

        output.WriteLine($"sequential {expected}, concurrent {string.Join(", ", counts)}");
        Assert.All(counts, c => Assert.Equal(expected, c));
    }

    [Fact]
    public void OpenMeshIsPreprocessed()
    {
        // The remesh of an open surface is a thin closed shell, which is slow to
        // decompose finely; coarse settings keep this a quick smoke test.
        Mesh box = TestMeshes.Box(Vector3.Zero, new Vector3(2, 1, 1));
        var hulls = _coacd.Decompose(box.Vertices, box.Indices[..^6],
            new CoAcdOptions { Threshold = 0.2f, PreprocessResolution = 20, MctsIterations = 30 });
        Assert.NotEmpty(hulls);
        var (lo, hi) = TestMeshes.Bounds(hulls.SelectMany(h => h.Vertices));
        AssertClose(Vector3.Zero, lo, 0.1f);
        AssertClose(new Vector3(2, 1, 1), hi, 0.1f);
    }

    [Fact]
    public void WorksThroughSharedInterface()
    {
        IConvexDecomposer decomposer = new CoAcdDecomposer(new CoAcdOptions { Threshold = 0.05f });
        Assert.InRange(decomposer.Decompose(TestMeshes.LShape()).Count, 2, 4);
    }

    [Fact]
    public void RejectsInvalidInput()
    {
        Mesh box = TestMeshes.Box(Vector3.Zero, Vector3.One);
        Assert.Throws<ArgumentException>(() => _coacd.Decompose(box.Vertices, box.Indices[..^1]));
        int[] bad = (int[])box.Indices.Clone();
        bad[0] = -1;
        Assert.Throws<ArgumentException>(() => _coacd.Decompose(box.Vertices, bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => _coacd.Decompose(box, new CoAcdOptions { Threshold = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _coacd.Decompose(box, new CoAcdOptions { MaxHullVertices = 3 }));
    }

    /// <summary>
    /// Upstream's own C API lets C++ exceptions escape into the caller, which
    /// terminates a .NET process. Bypass managed validation and make CoACD throw.
    /// </summary>
    [Fact]
    public unsafe void NativeExceptionsBecomeErrors()
    {
        _ = _coacd.Decompose(TestMeshes.Box(Vector3.Zero, Vector3.One)); // load + init

        byte* err = stackalloc byte[512];
        fixed (byte* level = "bogus\0"u8)
            Assert.NotEqual(0, NativeMethods.coacd_net_set_log_level(level, err, 512));
        output.WriteLine(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)err));

        Mesh box = TestMeshes.Box(Vector3.Zero, Vector3.One);
        var p = new NativeMethods.Params
        {
            StructSize = sizeof(NativeMethods.Params), Threshold = 5, MaxConvexHull = -1, PrepResolution = 50,
            SampleResolution = 2000, MctsNodes = 20, MctsIteration = 150, MctsMaxDepth = 3, Merge = 1, MaxChVertex = 256,
        };
        int rc;
        nint result;
        fixed (Vector3* v = box.Vertices)
        fixed (int* t = box.Indices)
            rc = NativeMethods.coacd_net_run((float*)v, box.Vertices.Length, t, box.TriangleCount, in p, out result, err, 512);
        string message = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)err) ?? "";
        output.WriteLine(message);
        Assert.NotEqual(0, rc);
        Assert.Equal(0, result);
        Assert.Contains("threshold", message);
    }

    [Fact]
    public void DecomposesUpstreamTestMesh()
    {
        Mesh mesh = TestMeshes.LoadBinaryStl(Path.Combine(AppContext.BaseDirectory, "data", "49160.stl"));

        var sw = Stopwatch.StartNew();
        var hulls = _coacd.Decompose(mesh);
        sw.Stop();

        output.WriteLine($"49160.stl: {hulls.Count} hulls in {sw.Elapsed.TotalSeconds:F1} s");
        Assert.InRange(hulls.Count, 2, 400);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tol) =>
        Assert.True(Vector3.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");
}
