using System;

namespace CoACD;

/// <summary>CoACD reported an error (it is caught natively and never crashes the process).</summary>
public sealed class CoAcdException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CoAcdException(string message, Exception? inner = null) : base(message, inner) { }
}
