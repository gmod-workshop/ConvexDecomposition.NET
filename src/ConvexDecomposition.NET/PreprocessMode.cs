namespace ConvexDecomposition;

/// <summary>Whether to remesh the input into a watertight manifold before decomposing.</summary>
public enum PreprocessMode
{
    /// <summary>Remesh only when the input is not a watertight manifold.</summary>
    Auto,
    /// <summary>Always remesh.</summary>
    On,
    /// <summary>Never remesh.</summary>
    Off,
}
