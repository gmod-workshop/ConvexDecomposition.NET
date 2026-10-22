using System.Numerics;
using ConvexDecomposition;

namespace ConvexDecomposition.Tests;

internal static class TestMeshes
{
    /// <summary>Axis-aligned box with outward winding.</summary>
    public static Mesh Box(Vector3 min, Vector3 max) =>
        Voxels(new[] { (0, 0, 0) }, max - min, min);

    /// <summary>L-shaped prism: three unit voxels, clearly non-convex.</summary>
    public static Mesh LShape(float voxel = 1f) =>
        Voxels(new[] { (0, 0, 0), (1, 0, 0), (0, 1, 0) }, new Vector3(voxel), Vector3.Zero);

    /// <summary>U-shaped prism: two posts joined by a base.</summary>
    public static Mesh UShape() =>
        Voxels(new[] { (0, 0, 0), (1, 0, 0), (2, 0, 0), (0, 1, 0), (0, 2, 0), (2, 1, 0), (2, 2, 0) },
            Vector3.One, Vector3.Zero);

    /// <summary>Closed UV sphere (convex, with many vertices).</summary>
    public static Mesh Sphere(float radius, int segments = 32, int rings = 16)
    {
        var verts = new List<Vector3> { new(0, radius, 0) };
        for (int r = 1; r < rings; r++)
        {
            float phi = MathF.PI * r / rings;
            for (int s = 0; s < segments; s++)
            {
                float theta = 2 * MathF.PI * s / segments;
                verts.Add(radius * new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta)));
            }
        }
        verts.Add(new Vector3(0, -radius, 0));
        int bottom = verts.Count - 1;
        int Ring(int r, int s) => 1 + (r - 1) * segments + (s % segments);

        var idx = new List<int>();
        for (int s = 0; s < segments; s++)
            idx.AddRange(new[] { 0, Ring(1, s + 1), Ring(1, s) });
        for (int r = 1; r < rings - 1; r++)
            for (int s = 0; s < segments; s++)
                idx.AddRange(new[] { Ring(r, s), Ring(r, s + 1), Ring(r + 1, s + 1), Ring(r, s), Ring(r + 1, s + 1), Ring(r + 1, s) });
        for (int s = 0; s < segments; s++)
            idx.AddRange(new[] { bottom, Ring(rings - 1, s), Ring(rings - 1, s + 1) });
        return new Mesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// Watertight, manifold surface of a set of voxels (face-connected only):
    /// emits the faces not shared with a neighbour, with vertices welded.
    /// </summary>
    public static Mesh Voxels(IReadOnlyCollection<(int X, int Y, int Z)> cells, Vector3 size, Vector3 origin)
    {
        var filled = new HashSet<(int, int, int)>(cells);
        var verts = new List<Vector3>();
        var lookup = new Dictionary<(int, int, int), int>();
        var indices = new List<int>();

        int Vertex(int x, int y, int z)
        {
            if (!lookup.TryGetValue((x, y, z), out int i))
            {
                i = verts.Count;
                verts.Add(origin + new Vector3(x, y, z) * size);
                lookup[(x, y, z)] = i;
            }
            return i;
        }

        // Each face: neighbour offset + 4 corners (counter-clockwise seen from outside).
        var faces = new (int dx, int dy, int dz, int[][] c)[]
        {
            (1, 0, 0, new[] { new[] { 1, 0, 0 }, new[] { 1, 1, 0 }, new[] { 1, 1, 1 }, new[] { 1, 0, 1 } }),
            (-1, 0, 0, new[] { new[] { 0, 0, 0 }, new[] { 0, 0, 1 }, new[] { 0, 1, 1 }, new[] { 0, 1, 0 } }),
            (0, 1, 0, new[] { new[] { 0, 1, 0 }, new[] { 0, 1, 1 }, new[] { 1, 1, 1 }, new[] { 1, 1, 0 } }),
            (0, -1, 0, new[] { new[] { 0, 0, 0 }, new[] { 1, 0, 0 }, new[] { 1, 0, 1 }, new[] { 0, 0, 1 } }),
            (0, 0, 1, new[] { new[] { 0, 0, 1 }, new[] { 1, 0, 1 }, new[] { 1, 1, 1 }, new[] { 0, 1, 1 } }),
            (0, 0, -1, new[] { new[] { 0, 0, 0 }, new[] { 0, 1, 0 }, new[] { 1, 1, 0 }, new[] { 1, 0, 0 } }),
        };

        foreach (var (x, y, z) in cells)
        {
            foreach (var (dx, dy, dz, c) in faces)
            {
                if (filled.Contains((x + dx, y + dy, z + dz)))
                    continue;
                int[] q = c.Select(k => Vertex(x + k[0], y + k[1], z + k[2])).ToArray();
                indices.AddRange(new[] { q[0], q[1], q[2], q[0], q[2], q[3] });
            }
        }

        return new Mesh(verts.ToArray(), indices.ToArray());
    }

    /// <summary>Reads a binary STL, welding identical vertices.</summary>
    public static Mesh LoadBinaryStl(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.ReadBytes(80);
        uint count = reader.ReadUInt32();
        var verts = new List<Vector3>();
        var lookup = new Dictionary<Vector3, int>();
        var indices = new int[count * 3];
        for (int t = 0; t < count; t++)
        {
            reader.ReadBytes(12); // normal
            for (int k = 0; k < 3; k++)
            {
                var v = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!lookup.TryGetValue(v, out int i))
                {
                    i = verts.Count;
                    verts.Add(v);
                    lookup[v] = i;
                }
                indices[t * 3 + k] = i;
            }
            reader.ReadUInt16(); // attribute
        }
        return new Mesh(verts.ToArray(), indices);
    }

    public static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in points)
        {
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        return (lo, hi);
    }
}
