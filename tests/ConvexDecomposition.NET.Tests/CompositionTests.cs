using System.Numerics;
using ConvexDecomposition;
using static ConvexDecomposition.Tests.ConvexHullTests;

namespace ConvexDecomposition.Tests;

public sealed class CompositionTests
{
    // Four unit cubes along x: [0,1] [1,2] touching, then [5,6] [6,7] touching.
    private static readonly Mesh[] FourCubes =
    {
        TestMeshes.Box(new Vector3(0, 0, 0), new Vector3(1, 1, 1)),
        TestMeshes.Box(new Vector3(1, 0, 0), new Vector3(2, 1, 1)),
        TestMeshes.Box(new Vector3(5, 0, 0), new Vector3(6, 1, 1)),
        TestMeshes.Box(new Vector3(6, 0, 0), new Vector3(7, 1, 1)),
    };

    [Fact]
    public void MaxHullsMergesTheCheapestPairs()
    {
        var hulls = new HullPostProcessor { MaxHulls = 2 }.Process(FourCubes);

        Assert.Equal(2, hulls.Count);
        // Touching pairs merge losslessly into 2x1x1 boxes.
        Assert.All(hulls, h => Assert.Equal(2f, h.ComputeVolume(), 4));
        var spans = hulls.Select(h => h.ComputeBounds()).OrderBy(b => b.Min.X).ToArray();
        Assert.Equal(0f, spans[0].Min.X, 5);
        Assert.Equal(2f, spans[0].Max.X, 5);
        Assert.Equal(5f, spans[1].Min.X, 5);
    }

    [Fact]
    public void MaxHullsOfOneWrapsEverything()
    {
        Mesh hull = Assert.Single(new HullPostProcessor { MaxHulls = 1 }.Process(FourCubes));

        Assert.Equal(7f, hull.ComputeVolume(), 3);
        AssertValidHull(hull, FourCubes.SelectMany(c => c.Vertices));
    }

    [Fact]
    public void VertexLimitIsApplied()
    {
        Mesh sphere = ConvexHull.Compute(FibonacciSphere(500, 1f).ToArray());

        Mesh limited = Assert.Single(new HullPostProcessor { MaxVerticesPerHull = 32 }.Process(new[] { sphere }));

        Assert.InRange(limited.Vertices.Length, 4, 32);
        AssertValidHull(limited, limited.Vertices);
    }

    [Fact]
    public void TinyHullsAreRemovedButTheLargestStays()
    {
        Mesh big = TestMeshes.Box(Vector3.Zero, new Vector3(10, 10, 10));
        Mesh tiny = TestMeshes.Box(new Vector3(20, 0, 0), new Vector3(20.1f, 0.1f, 0.1f));

        var kept = new HullPostProcessor { MinHullVolumeFraction = 0.001f }.Process(new[] { tiny, big });

        Mesh only = Assert.Single(kept);
        Assert.Equal(1000f, only.ComputeVolume(), 2);
        Assert.Single(new HullPostProcessor { MinHullVolumeFraction = 0.9f }.Process(new[] { tiny }));
    }

    [Fact]
    public void MarginGrowsEveryHull()
    {
        var hulls = new HullPostProcessor { Margin = 0.5f }.Process(FourCubes);

        Assert.All(hulls, h => Assert.Equal(8f, h.ComputeVolume(), 3));
    }

    [Fact]
    public void UnconfiguredProcessorIsANoOp()
    {
        Assert.Same(FourCubes, new HullPostProcessor().Process(FourCubes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HullPostProcessor { MaxVerticesPerHull = 3 }.Process(FourCubes));
    }

    [Fact]
    public void ConvexHullDecomposerWrapsTheMesh()
    {
        var decomposer = new ConvexHullDecomposer();

        Mesh hull = Assert.Single(decomposer.Decompose(TestMeshes.LShape()));

        Assert.Equal(3.5f, hull.ComputeVolume(), 4); // 2x2x1 minus the cut corner
        Assert.Contains("convex-hull", decomposer.Fingerprint);
    }

    [Fact]
    public void FallbackSkipsBackendsThatCannotBeCreated()
    {
        var events = new List<FallbackEvent>();
        var cpu = new FakeDecomposer("cpu");
        using var chain = new FallbackDecomposer(
            () => throw new DllNotFoundException("no GPU"),
            () => cpu)
        {
            FallbackOccurred = events.Add,
        };

        chain.Decompose(TestMeshes.LShape());
        chain.Decompose(TestMeshes.LShape());

        Assert.Same(cpu, chain.Active);
        Assert.Equal("cpu", chain.Fingerprint);
        Assert.Equal(2, cpu.Calls);
        FallbackEvent e = Assert.Single(events); // the failed factory is not retried
        Assert.True(e.DuringCreation);
        Assert.Equal(0, e.BackendIndex);
        Assert.IsType<DllNotFoundException>(e.Exception);
    }

    [Fact]
    public void FallbackRetriesAFailedCallOnTheNextBackend()
    {
        var gpu = new FakeDecomposer("gpu") { FailNext = 1 };
        var cpu = new FakeDecomposer("cpu");
        var events = new List<FallbackEvent>();
        using var chain = new FallbackDecomposer(() => gpu, () => cpu) { FallbackOccurred = events.Add };

        chain.Decompose(TestMeshes.LShape()); // gpu throws, cpu answers
        chain.Decompose(TestMeshes.LShape()); // gpu answers again

        Assert.Equal((2, 1), (gpu.Calls, cpu.Calls));
        Assert.False(Assert.Single(events).DuringCreation);
        Assert.Equal("gpu", chain.Fingerprint);
    }

    [Fact]
    public void FallbackDoesNotRetryBadInputAndReportsTotalFailure()
    {
        var gpu = new FakeDecomposer("gpu");
        var cpu = new FakeDecomposer("cpu");
        using (var chain = new FallbackDecomposer(() => gpu, () => cpu))
        {
            Assert.Throws<ArgumentException>(() => chain.Decompose(new[] { Vector3.Zero }, new[] { 0, 0, 0 }));
            Assert.Equal(0, cpu.Calls);
        }
        Assert.True(gpu.Disposed);
        Assert.False(cpu.Disposed); // never created

        using var broken = new FallbackDecomposer(() => new FakeDecomposer("a") { FailNext = 9 }, () => throw new InvalidOperationException("b"));
        var all = Assert.Throws<AggregateException>(() => broken.Decompose(TestMeshes.LShape()));
        Assert.Equal(2, all.InnerExceptions.Count);
    }

    [Fact]
    public void MemoryCacheReturnsCopiesAndKeysOnFingerprint()
    {
        var inner = new FakeDecomposer("v1");
        var cache = new MemoryDecompositionCache();
        var cached = inner.WithCache(cache);
        Mesh mesh = TestMeshes.LShape();

        var first = cached.Decompose(mesh);
        first[0].Vertices[0] = new Vector3(999); // must not poison the cache
        var second = cached.Decompose(mesh);
        inner.Name = "v2";
        cached.Decompose(mesh);
        cached.Decompose(TestMeshes.UShape());

        Assert.Equal(3, inner.Calls);
        Assert.Equal(3, cache.Count);
        Assert.NotEqual(new Vector3(999), second[0].Vertices[0]);
    }

    [Fact]
    public void DirectoryCachePersistsAndIgnoresCorruption()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cdtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Mesh mesh = TestMeshes.UShape();
            var inner = new FakeDecomposer("x");
            var expected = inner.Decompose(mesh);

            new CachingDecomposer(inner, new DirectoryDecompositionCache(dir)).Decompose(mesh);
            // A fresh cache instance (e.g. the next run) sees the stored entry.
            var fresh = new CachingDecomposer(inner, new DirectoryDecompositionCache(dir));
            var hit = fresh.Decompose(mesh);
            Assert.Equal(2, inner.Calls);
            Assert.Equal(expected.Count, hit.Count);
            Assert.Equal(expected[0].Vertices, hit[0].Vertices);
            Assert.Equal(expected[0].Indices, hit[0].Indices);

            string file = Directory.GetFiles(dir, "*.hulls", SearchOption.AllDirectories).Single();
            File.WriteAllBytes(file, File.ReadAllBytes(file)[..20]);
            fresh.Decompose(mesh);
            Assert.Equal(3, inner.Calls); // truncated entry is a miss, then rewritten
            fresh.Decompose(mesh);
            Assert.Equal(3, inner.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CacheKeyCoversFingerprintAndData()
    {
        Mesh a = TestMeshes.LShape();
        string k = CachingDecomposer.ComputeKey("f", a.Vertices, a.Indices);

        Assert.Equal(64, k.Length);
        Assert.Equal(k, CachingDecomposer.ComputeKey("f", a.Vertices, a.Indices));
        Assert.NotEqual(k, CachingDecomposer.ComputeKey("g", a.Vertices, a.Indices));
        Assert.NotEqual(k, CachingDecomposer.ComputeKey("f", a.Vertices, Enumerable.Reverse(a.Indices).ToArray()));
    }

    [Fact]
    public void PipelineComposes()
    {
        var inner = new FakeDecomposer("split") { Result = FourCubes };
        var cache = new MemoryDecompositionCache();
        var processor = new HullPostProcessor { MaxHulls = 2 };
        using var pipeline = new FallbackDecomposer(() => inner).WithCache(cache).WithPostProcessing(processor);

        Assert.Equal(2, pipeline.Decompose(TestMeshes.LShape()).Count);
        processor.MaxHulls = 3;
        Assert.Equal(3, pipeline.Decompose(TestMeshes.LShape()).Count);
        Assert.Equal(1, inner.Calls); // changing limits reused the cached decomposition
        Assert.Contains("maxHulls=3", pipeline.Fingerprint);
    }

    private sealed class FakeDecomposer(string name) : IConvexDecomposer, IDisposable
    {
        public string Name { get; set; } = name;
        public int Calls { get; private set; }
        public int FailNext { get; set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<Mesh>? Result { get; init; }

        public string Fingerprint => Name;

        public IReadOnlyList<Mesh> Decompose(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
        {
            if (vertices.Length < 4)
                throw new ArgumentException("too few vertices");
            Calls++;
            if (FailNext > 0)
            {
                FailNext--;
                throw new InvalidOperationException($"{Name} failed");
            }
            return Result ?? new[] { ConvexHull.Compute(vertices) };
        }

        public void Dispose() => Disposed = true;
    }
}
