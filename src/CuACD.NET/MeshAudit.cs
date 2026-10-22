using System;
using ConvexDecomposition;

namespace CuACD;

/// <summary>Topology defects reported by <see cref="CuAcdContext.CheckMesh"/> (mirrors upstream MAV_* flags).</summary>
[Flags]
public enum MeshDefects : uint
{
    /// <summary>No defects.</summary>
    None = 0,
    /// <summary>At least one zero-area or repeated-index triangle.</summary>
    DegenerateTriangle = 1u << 0,
    /// <summary>A triangle index is out of range.</summary>
    BadIndex = 1u << 1,
    /// <summary>An edge is used by only one triangle (the mesh has holes).</summary>
    OpenEdge = 1u << 2,
    /// <summary>An edge is used by more than two triangles.</summary>
    NonManifold = 1u << 3,
    /// <summary>Neighbouring triangles have inconsistent winding.</summary>
    Flipped = 1u << 4,
}

/// <summary>Result of a GPU topology audit.</summary>
public readonly record struct MeshAudit(MeshDefects Defects)
{
    /// <summary>Every edge is shared by exactly two triangles.</summary>
    public bool IsWatertight => (Defects & MeshDefects.OpenEdge) == 0;

    /// <summary>No edge has more than two incident triangles.</summary>
    public bool IsManifold => (Defects & MeshDefects.NonManifold) == 0;

    /// <summary>Winding is consistent and no triangle is degenerate.</summary>
    public bool IsOriented => (Defects & (MeshDefects.Flipped | MeshDefects.DegenerateTriangle)) == 0;

    /// <summary>Any defect at all; <see cref="PreprocessMode.Auto"/> remeshes in that case.</summary>
    public bool NeedsRemesh => Defects != MeshDefects.None;
}
