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
    /// Logs whatever the combination of <paramref name="authentication"/> and <paramref name="provisioning"/>
    /// deserves, and nothing at all when authentication is enabled.
    /// </summary>
    /// <param name="logger">Where the warning goes.</param>
    /// <param name="authentication">This process's authentication gate.</param>
    /// <param name="provisioning">This process's provisioning gate.</param>
    public static void LogDangerousCombinations(
        ILogger logger, AuthenticationGate authentication, ProvisioningGate provisioning)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(provisioning);

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
}
