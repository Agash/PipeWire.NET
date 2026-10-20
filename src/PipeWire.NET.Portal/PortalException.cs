namespace PipeWire.NET.Portal;

/// <summary>How a portal request ended, as its <c>Response</c> signal reports it.</summary>
public enum PortalResponse : uint
{
    /// <summary>The request succeeded.</summary>
    Success = 0,

    /// <summary>The user cancelled the interaction, for example by closing the dialog.</summary>
    Cancelled = 1,

    /// <summary>The interaction ended some other way, for example because the portal refused it.</summary>
    Failed = 2,
}

/// <summary>A portal request that did not succeed.</summary>
public sealed class PortalException : Exception
{
    /// <summary>Creates an exception for a request that ended without success.</summary>
    /// <param name="response">How the request ended.</param>
    /// <param name="message">What was asked.</param>
    public PortalException(PortalResponse response, string message)
        : base(message) => Response = response;

    /// <summary>Creates an exception.</summary>
    public PortalException()
        : this(PortalResponse.Failed, "The portal request failed.") { }

    /// <summary>Creates an exception.</summary>
    /// <param name="message">The message.</param>
    public PortalException(string message)
        : this(PortalResponse.Failed, message) { }

    /// <summary>Creates an exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PortalException(string message, Exception innerException)
        : base(message, innerException) => Response = PortalResponse.Failed;

    /// <summary>How the request ended.</summary>
    public PortalResponse Response { get; }
}
