namespace Blocker.Core.Security;

/// <summary>A sealed blob failed structural or HMAC verification (tampered, truncated, wrong key or foreign data).</summary>
public sealed class TamperDetectedException : Exception
{
    /// <summary>Creates the exception with a diagnostic message.</summary>
    public TamperDetectedException(string message) : base(message) { }

    /// <summary>Creates the exception with a diagnostic message and cause.</summary>
    public TamperDetectedException(string message, Exception innerException) : base(message, innerException) { }
}
