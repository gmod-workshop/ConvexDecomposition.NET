using System;

namespace CuACD;

/// <summary>
/// A CuACD native call failed. After a CUDA fault (e.g. an illegal address)
/// the context is unusable: dispose it and create a new one.
/// </summary>
public sealed class CuAcdException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CuAcdException(string message, int errorCode, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The native return code (a CUresult, an upstream KERR_* bitmask, or -1).</summary>
    public int ErrorCode { get; }
}
