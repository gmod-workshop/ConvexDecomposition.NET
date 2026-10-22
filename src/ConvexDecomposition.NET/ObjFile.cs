using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace ConvexDecomposition;

/// <summary>
/// Minimal Wavefront OBJ support: positions and faces, which is all
/// decomposition needs. Handy for debugging and for loading test meshes.
/// </summary>
public static class ObjFile
{
    /// <summary>Reads every object in an OBJ file into a single mesh, triangulating polygons as fans.</summary>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public static Mesh Read(string path)
    {
        using var reader = new StreamReader(path);
        return Read(reader);
    }

    /// <inheritdoc cref="Read(string)"/>
    public static Mesh Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var face = new List<int>();
        Span<float> xyz = stackalloc float[3];
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            ReadOnlySpan<char> s = line.AsSpan().Trim();
            if (s.StartsWith("v ") || s.StartsWith("v\t"))
            {
                s = s[1..];
                for (int k = 0; k < 3; k++)
                    if (!float.TryParse(NextToken(ref s), NumberStyles.Float, CultureInfo.InvariantCulture, out xyz[k]))
                        throw new InvalidDataException($"Line {lineNumber}: bad vertex.");
                vertices.Add(new Vector3(xyz[0], xyz[1], xyz[2]));
            }
            else if (s.StartsWith("f ") || s.StartsWith("f\t"))
            {
                face.Clear();
                s = s[1..];
                while (NextToken(ref s) is { Length: > 0 } token)
                {
                    int slash = token.IndexOf('/');
                    if (slash >= 0)
                        token = token[..slash];
                    if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) || i == 0)
                        throw new InvalidDataException($"Line {lineNumber}: bad face index.");
                    // 1-based; negative counts back from the latest vertex.
                    i = i > 0 ? i - 1 : vertices.Count + i;
                    if ((uint)i >= (uint)vertices.Count)
                        throw new InvalidDataException($"Line {lineNumber}: face index out of range.");
                    face.Add(i);
                }
                if (face.Count < 3)
                    throw new InvalidDataException($"Line {lineNumber}: a face needs at least 3 vertices.");
                for (int k = 1; k + 1 < face.Count; k++)
                {
                    indices.Add(face[0]);
                    indices.Add(face[k]);
                    indices.Add(face[k + 1]);
                }
            }
        }
        return new Mesh(vertices.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// Writes meshes as separate objects (<c>o hull_0</c>, <c>o hull_1</c>, ...)
    /// in one file, the layout CoACD and CuACD's tools produce.
    /// </summary>
    /// <param name="path">Output file.</param>
    /// <param name="meshes">Meshes to write, e.g. the hulls of a decomposition.</param>
    /// <param name="objectName">Object name prefix.</param>
    public static void Write(string path, IEnumerable<Mesh> meshes, string objectName = "hull")
    {
        using var writer = new StreamWriter(path);
        Write(writer, meshes, objectName);
    }

    /// <inheritdoc cref="Write(string, IEnumerable{Mesh}, string)"/>
    /// <param name="writer">Destination.</param>
    /// <param name="meshes">Meshes to write.</param>
    /// <param name="objectName">Object name prefix.</param>
    public static void Write(TextWriter writer, IEnumerable<Mesh> meshes, string objectName = "hull")
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(meshes);
        int baseIndex = 1, n = 0;
        foreach (Mesh mesh in meshes)
        {
            writer.Write("o ");
            writer.Write(objectName);
            writer.Write('_');
            writer.WriteLine(n++.ToString(CultureInfo.InvariantCulture));
            foreach (Vector3 v in mesh.Vertices)
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"v {v.X:R} {v.Y:R} {v.Z:R}"));
            for (int i = 0; i < mesh.Indices.Length; i += 3)
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"f {mesh.Indices[i] + baseIndex} {mesh.Indices[i + 1] + baseIndex} {mesh.Indices[i + 2] + baseIndex}"));
            baseIndex += mesh.Vertices.Length;
        }
    }

    private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> s)
    {
        s = s.TrimStart();
        int end = s.IndexOfAny(' ', '\t');
        if (end < 0)
            end = s.Length;
        ReadOnlySpan<char> token = s[..end];
        s = s[end..];
        return token;
    }
}
