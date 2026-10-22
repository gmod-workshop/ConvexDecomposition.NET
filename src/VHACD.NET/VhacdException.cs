using System;

namespace VHACD;

/// <summary>V-HACD reported an error (it is caught natively and never crashes the process).</summary>
public sealed class VhacdException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VhacdException(string message, Exception? inner = null) : base(message, inner) { }
}
