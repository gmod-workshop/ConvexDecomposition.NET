using ConvexDecomposition;

namespace CuACD;

/// <summary>One part of a convex decomposition, in the input mesh's coordinate space.</summary>
public sealed class ConvexPart
{
    internal ConvexPart(Mesh hull, Mesh piece, float hullVolume, float pieceVolume)
    {
        Hull = hull;
        Piece = piece;
        HullVolume = hullVolume;
        PieceVolume = pieceVolume;
    }

    /// <summary>The convex hull of this part — what a physics engine consumes.</summary>
    public Mesh Hull { get; }

    /// <summary>The fragment of the original mesh this hull wraps.</summary>
    public Mesh Piece { get; }

    /// <summary>Volume of <see cref="Hull"/>.</summary>
    public float HullVolume { get; }

    /// <summary>Volume of <see cref="Piece"/>.</summary>
    public float PieceVolume { get; }
}
