using Servyx.Domain.Connectors;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// Parses a <see cref="ConnectorDescriptor.Endpoint"/> string of the form
/// <c>["ssh:"] [user "@"] host [":" port]</c> (e.g. <c>"ssh:steam@10.0.0.4:22"</c>,
/// <c>"10.0.0.4"</c>, <c>"[::1]:2222"</c>) into a structured <see cref="EndpointDescriptor"/> plus an
/// optional username hint, used when no explicit <c>username</c> credential is resolved.
/// </summary>
public static class SshEndpoint
{
    /// <summary>The default SSH port, used when <paramref name="endpoint"/> does not specify one.</summary>
    public const int DefaultPort = 22;

    /// <summary>
    /// Parses <paramref name="endpoint"/>. Bracketed IPv6 literals (<c>[::1]:2222</c>) are supported.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is null, empty, whitespace, or has no host component.</exception>
    public static (EndpointDescriptor Endpoint, string? UsernameHint) Parse(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var remainder = endpoint;

        if (remainder.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder["ssh:".Length..];
        }

        // Strip a leading "//" if present (e.g. "ssh://host:22").
        if (remainder.StartsWith("//", StringComparison.Ordinal))
        {
            remainder = remainder[2..];
        }

        string? username = null;
        var atIndex = remainder.LastIndexOf('@');
        if (atIndex >= 0)
        {
            username = remainder[..atIndex];
            remainder = remainder[(atIndex + 1)..];
        }

        string host;
        var port = DefaultPort;

        if (remainder.StartsWith('['))
        {
            var closeBracket = remainder.IndexOf(']');
            if (closeBracket < 0)
            {
                throw new ArgumentException($"'{endpoint}' has an unterminated IPv6 literal.", nameof(endpoint));
            }

            host = remainder[1..closeBracket];
            var afterBracket = remainder[(closeBracket + 1)..];
            if (afterBracket.StartsWith(':'))
            {
                port = ParsePort(endpoint, afterBracket[1..]);
            }
        }
        else
        {
            var colonIndex = remainder.LastIndexOf(':');
            if (colonIndex >= 0)
            {
                host = remainder[..colonIndex];
                port = ParsePort(endpoint, remainder[(colonIndex + 1)..]);
            }
            else
            {
                host = remainder;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException($"'{endpoint}' does not contain a host.", nameof(endpoint));
        }

        return (new EndpointDescriptor(host, port), string.IsNullOrEmpty(username) ? null : username);
    }

    private static int ParsePort(string original, string portText)
    {
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"'{original}' has an invalid port '{portText}'.", nameof(original));
        }

        return port;
    }
}
