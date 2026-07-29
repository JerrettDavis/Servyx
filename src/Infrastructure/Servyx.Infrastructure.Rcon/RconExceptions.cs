namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// Base type for every failure this assembly raises, so a caller that only wants to know "the control
/// channel did not work" can catch one type without also swallowing unrelated I/O failures.
/// </summary>
/// <remarks>
/// <strong>No exception in this hierarchy ever carries the RCON password.</strong> Messages are built from
/// the endpoint, the command id, and the server's own reply — never from the credential, and never from the
/// bytes of a packet that carried one. <see cref="SourceRconConnection"/> encodes the password straight
/// into a send buffer it zeroes immediately afterwards, so there is no intermediate value for a message to
/// accidentally interpolate.
/// </remarks>
public class RconException : Exception
{
    /// <summary>Creates an <see cref="RconException"/> with a default message.</summary>
    public RconException()
        : base("The RCON control channel failed.")
    {
    }

    /// <summary>Creates an <see cref="RconException"/> with the given message.</summary>
    public RconException(string message) : base(message) { }

    /// <summary>Creates an <see cref="RconException"/> with the given message and inner exception.</summary>
    public RconException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when the server rejected the credential — signalled on the wire by a
/// <c>SERVERDATA_AUTH_RESPONSE</c> whose request id is <c>-1</c>.
/// </summary>
/// <remarks>
/// This is the single most important distinction in the Source RCON protocol and the classic
/// implementation bug: an authentication failure arrives as a well-formed packet with an <em>empty body</em>
/// and an id of <c>-1</c>. A client that only looks at the body sees a successful empty response, reports
/// the command as having worked, and — for a quiesce step — hands a "flushed" verdict back to a backup that
/// never flushed anything. Servyx raises this instead, and never returns a
/// <see cref="Domain.Rcon.RconResponse"/> for an unauthenticated exchange.
/// </remarks>
public sealed class RconAuthenticationFailedException : RconException
{
    /// <summary>Creates an <see cref="RconAuthenticationFailedException"/> with a default message.</summary>
    public RconAuthenticationFailedException()
        : base("The RCON server rejected the supplied credential.")
    {
    }

    /// <summary>Creates an <see cref="RconAuthenticationFailedException"/> with the given message.</summary>
    public RconAuthenticationFailedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="RconAuthenticationFailedException"/> with the given message and inner exception.</summary>
    public RconAuthenticationFailedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when the peer's framing or packet sequence does not conform to the Source RCON protocol.</summary>
public sealed class RconProtocolException : RconException
{
    /// <summary>Creates an <see cref="RconProtocolException"/> with a default message.</summary>
    public RconProtocolException()
        : base("The peer did not speak the Source RCON protocol.")
    {
    }

    /// <summary>Creates an <see cref="RconProtocolException"/> with the given message.</summary>
    public RconProtocolException(string message) : base(message) { }

    /// <summary>Creates an <see cref="RconProtocolException"/> with the given message and inner exception.</summary>
    public RconProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when the endpoint could not be reached or the connection was lost mid-exchange.</summary>
public sealed class RconUnreachableException : RconException
{
    /// <summary>Creates an <see cref="RconUnreachableException"/> with a default message.</summary>
    public RconUnreachableException()
        : base("The RCON endpoint could not be reached.")
    {
    }

    /// <summary>Creates an <see cref="RconUnreachableException"/> with the given message.</summary>
    public RconUnreachableException(string message) : base(message) { }

    /// <summary>Creates an <see cref="RconUnreachableException"/> with the given message and inner exception.</summary>
    public RconUnreachableException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when an exchange did not complete inside the connector-style timeout it was given.
/// </summary>
/// <remarks>
/// Distinct from <see cref="OperationCanceledException"/> on purpose: a caller's own cancellation and a
/// server that accepted the connection and then went silent are different events, and only the second one
/// means "this control channel is not usable". A hung read must never hang the caller forever, so every
/// read in this assembly is bounded by <see cref="Domain.Connectors.TimeoutPolicy"/>.
/// </remarks>
public sealed class RconTimeoutException : RconException
{
    /// <summary>Creates an <see cref="RconTimeoutException"/> with a default message.</summary>
    public RconTimeoutException()
        : base("The RCON exchange did not complete within its timeout.")
    {
    }

    /// <summary>Creates an <see cref="RconTimeoutException"/> with the given message.</summary>
    public RconTimeoutException(string message) : base(message) { }

    /// <summary>Creates an <see cref="RconTimeoutException"/> with the given message and inner exception.</summary>
    public RconTimeoutException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a caller asks for a command id the definition's catalogue does not declare.
/// </summary>
/// <remarks>
/// Refusing an unknown id is what makes <see cref="Domain.Rcon.IRconSession.InvokeAsync"/> a safe surface:
/// every id that reaches the wire came from the definition, carrying the definition's <c>readOnly</c>
/// classification with it. An unknown id has no classification at all, so there is nothing for the write
/// guard to gate and no safe way to send it.
/// </remarks>
public sealed class RconUnknownCommandException : RconException
{
    /// <summary>Creates an <see cref="RconUnknownCommandException"/> with a default message.</summary>
    public RconUnknownCommandException()
        : base("The requested command id is not declared by the definition's control-command catalogue.")
    {
    }

    /// <summary>Creates an <see cref="RconUnknownCommandException"/> with the given message.</summary>
    public RconUnknownCommandException(string message) : base(message) { }

    /// <summary>Creates an <see cref="RconUnknownCommandException"/> with the given message and inner exception.</summary>
    public RconUnknownCommandException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates an <see cref="RconUnknownCommandException"/> carrying the refused id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="commandId">The command id that is not in the catalogue.</param>
    public RconUnknownCommandException(string message, string commandId) : base(message) => CommandId = commandId;

    /// <summary>The command id that was refused, if known.</summary>
    public string? CommandId { get; }
}

/// <summary>
/// Thrown when an argument is missing, unexpected, or contains a character that could change the shape of
/// the rendered command rather than merely fill a slot in it.
/// </summary>
/// <remarks>
/// The rule is the RCON analogue of <see cref="Domain.Transport.CommandSpec"/>'s argv rule: arguments are
/// values, never syntax. Source RCON has no shell, but it does have a line-oriented command parser and a
/// null-terminated packet body, so a newline, a carriage return, a NUL or a double quote inside an argument
/// is enough to append a second command or to escape a quoted parameter. Rather than escape such a value —
/// which needs the game's own quoting rules, which the definition does not state — Servyx refuses it.
/// </remarks>
public sealed class RconArgumentException : RconException
{
    /// <summary>Creates an <see cref="RconArgumentException"/> with a default message.</summary>
    public RconArgumentException()
        : base("An RCON command argument was missing, unexpected, or contained a character that could alter the command.")
    {
    }

    /// <summary>Creates an <see cref="RconArgumentException"/> with the given message.</summary>
    public RconArgumentException(string message) : base(message) { }

    /// <summary>Creates an <see cref="RconArgumentException"/> with the given message and inner exception.</summary>
    public RconArgumentException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates an <see cref="RconArgumentException"/> carrying the offending parameter name.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="commandId">The command whose rendering was refused.</param>
    /// <param name="parameterName">The parameter that was refused.</param>
    public RconArgumentException(string message, string commandId, string? parameterName) : base(message)
    {
        CommandId = commandId;
        ParameterName = parameterName;
    }

    /// <summary>The command whose rendering was refused, if known.</summary>
    public string? CommandId { get; }

    /// <summary>The parameter that was refused, if known.</summary>
    public string? ParameterName { get; }
}
