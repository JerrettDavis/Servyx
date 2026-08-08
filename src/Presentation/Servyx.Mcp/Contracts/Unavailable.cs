using Servyx.Composition;
using Servyx.Mcp;

namespace Servyx.Mcp.Contracts;

/// <summary>Why a capability this server would normally offer is not offered right now.
/// Never substituted with an empty list: "this server has no control commands" and "this process
/// cannot know what this server's control commands are" are different, incompatible facts.</summary>
/// <param name="Capability">Which <see cref="ServyxCapability"/> this describes, in lower-kebab.</param>
/// <param name="ReasonCode">One of <see cref="UnavailableReason"/>'s constants.</param>
/// <param name="Explanation">A human-readable sentence, safe to surface to an MCP client verbatim.</param>
/// <param name="Contributing">Supporting facts — e.g. the ids of every game definition that made a multi-definition capability unavailable.</param>
public sealed record Unavailable(
    string Capability, string ReasonCode, string Explanation, IReadOnlyList<string> Contributing);

/// <summary>Builds an <see cref="Unavailable"/> from a <see cref="ServyxCoreComposition"/>'s capability report.</summary>
public static class UnavailableFactory
{
    /// <summary>
    /// Maps <paramref name="status"/> — a <see cref="CapabilityStatus"/> already known to be unavailable
    /// (<see cref="CapabilityStatus.Available"/> is <see langword="false"/>) — into an <see cref="Unavailable"/>
    /// a tool can return verbatim as part of a normal (not erroring) result.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="status"/>.Available is <see langword="true"/> — there is nothing to explain.</exception>
    public static Unavailable From(CapabilityStatus status)
    {
        if (status.Available)
        {
            throw new ArgumentException(
                $"'{status.Capability}' is available; there is no unavailability to describe.", nameof(status));
        }

        return new Unavailable(
            Capability: KebabCase.From(status.Capability.ToString()),
            ReasonCode: status.ReasonCode ?? "unknown",
            Explanation: status.Explanation ?? "No further explanation was recorded.",
            Contributing: status.Contributing);
    }
}
