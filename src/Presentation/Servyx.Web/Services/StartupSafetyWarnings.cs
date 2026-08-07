using Servyx.Domain.Transport;
using Servyx.Web.Authentication;

namespace Servyx.Web.Services;

/// <summary>
/// The startup-time cross-check between the two gates. Each one is defensible on its own; one particular
/// combination of them is not, and this is where that is said out loud.
/// </summary>
/// <remarks>
/// Kept as a static function over the two gates and an <see cref="ILogger"/> — rather than inline in
/// <c>Program.cs</c> — so the dangerous-combination rule is covered by a test instead of only being observed
/// on an operator's console the day it matters.
/// </remarks>
public static class StartupSafetyWarnings
{
    /// <summary>
    /// The message logged at <see cref="LogLevel.Critical"/> when authentication is off and provisioning is
    /// on. Exposed as a constant so a test asserts the exact text an operator will see.
    /// </summary>
    public const string UnauthenticatedProvisioningMessage =
        "SERVYX IS RUNNING UNAUTHENTICATED WITH PROVISIONING ENABLED. "
        + "{AuthenticationKey} is false and {ProvisioningKey} is true, so ANY caller who can reach this web "
        + "port can create real infrastructure and — at any provider other than a local Docker daemon — spend "
        + "real money, with no login, no session and no record of who did it. "
        + "Re-enable authentication, or shut this process down.";

    /// <summary>
    /// The message logged at <see cref="LogLevel.Warning"/> when authentication alone is off. Less severe
    /// than the combination above, and still not something that should pass unremarked.
    /// </summary>
    public const string AuthenticationDisabledMessage =
        "Authentication is DISABLED: {AuthenticationKey} is false, so every page in this process is reachable "
        + "by anyone who can reach the web port. This is only defensible on a host that is not reachable from "
        + "an untrusted network.";

    /// <summary>
    /// The message logged at <see cref="LogLevel.Warning"/> naming every server this process granted a
    /// non-read-only <see cref="WriteMode"/>. Exposed as a constant for the same reason the other messages
    /// are: a test asserts the exact text an operator will see.
    /// </summary>
    public const string WriteModeGrantedMessage =
        "Write mode is granted to {GrantCount} server(s): {Grants}. A mutating command CAN reach these "
        + "targets — each is still subject to the write guard's own per-call checks, but the process is no "
        + "longer read-only for them.";

    /// <summary>
    /// The message logged at <see cref="LogLevel.Critical"/> when authentication is off and at least one
    /// server is granted <see cref="WriteMode.Enabled"/> — the write-mode equivalent of
    /// <see cref="UnauthenticatedProvisioningMessage"/>.
    /// </summary>
    public const string UnauthenticatedWriteAccessMessage =
        "SERVYX IS RUNNING UNAUTHENTICATED WITH WRITE ACCESS ENABLED. "
        + "{AuthenticationKey} is false and the following server(s) are granted WriteMode = Enabled: {Grants}. "
        + "ANY caller who can reach this web port can mutate them, with no login, no session and no record of "
        + "who did it. Re-enable authentication, or shut this process down.";

    /// <summary>
    /// Logs whatever the combination of <paramref name="authentication"/>, <paramref name="provisioning"/>
    /// and <paramref name="writeGrants"/> deserves. Nothing at all is logged when authentication is enabled
    /// and no server carries a write grant.
    /// </summary>
    /// <param name="logger">Where the warning goes.</param>
    /// <param name="authentication">This process's authentication gate.</param>
    /// <param name="provisioning">This process's provisioning gate.</param>
    /// <param name="writeGrants">
    /// Every <see cref="WriteModeGrant"/> registered in this process, across every transport — <see langword="null"/>
    /// or empty means no server was granted anything beyond the default <see cref="WriteMode.ReadOnly"/>.
    /// </param>
    public static void LogDangerousCombinations(
        ILogger logger,
        AuthenticationGate authentication,
        ProvisioningGate provisioning,
        IReadOnlyList<WriteModeGrant>? writeGrants = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(provisioning);

        var grants = writeGrants ?? [];

        if (grants.Count > 0)
        {
            var described = string.Join(", ", grants.Select(DescribeGrant));

            logger.LogWarning(
                AuthenticationAudit.WriteModeGranted,
                WriteModeGrantedMessage,
                grants.Count,
                described);

            if (!authentication.Enabled && grants.Any(g => g.Mode == WriteMode.Enabled))
            {
                var enabledOnly = string.Join(", ", grants.Where(g => g.Mode == WriteMode.Enabled).Select(DescribeGrant));

                logger.LogCritical(
                    AuthenticationAudit.UnauthenticatedWriteAccess,
                    UnauthenticatedWriteAccessMessage,
                    AuthenticationGate.ConfigurationKey,
                    enabledOnly);
            }
        }

        if (authentication.Enabled)
        {
            return;
        }

        if (provisioning.Enabled)
        {
            logger.LogCritical(
                AuthenticationAudit.UnauthenticatedProvisioning,
                UnauthenticatedProvisioningMessage,
                AuthenticationGate.ConfigurationKey,
                ProvisioningGate.ConfigurationKey);
            return;
        }

        logger.LogWarning(
            AuthenticationAudit.AuthenticationDisabled,
            AuthenticationDisabledMessage,
            AuthenticationGate.ConfigurationKey);
    }

    /// <summary>
    /// A short, human-readable description of what a grant names — the same container/endpoint priority
    /// <see cref="WriteGuardedTransport"/> uses in its own refusal messages, so an operator sees the same
    /// name in the log they would see in a thrown exception.
    /// </summary>
    private static string DescribeGrant(WriteModeGrant grant)
    {
        foreach (var key in (string[])["containerName", "containerId", "container"])
        {
            if (grant.RequiredOptions.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return $"{value} ({grant.Mode}, {grant.TransportId})";
            }
        }

        var name = grant.Endpoint ?? "(unscoped)";
        return $"{name} ({grant.Mode}, {grant.TransportId})";
    }
}
