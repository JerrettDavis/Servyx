namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// A definition-authored reference to a secret, e.g. <c>passwordRef: "secret:admin-password"</c> parses to
/// <see cref="Scheme"/> <c>"secret"</c>, <see cref="Key"/> <c>"admin-password"</c>.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="Servyx.Domain.Secrets.SecretUrn"/>: a <see cref="Servyx.Domain.Secrets.SecretUrn"/>
/// is a fully-qualified, server-scoped locator (<c>secret://server/{id}/{category}/{name}</c>) resolved
/// through <see cref="Servyx.Domain.Secrets.ISecretStore"/> at the point of use, whereas a
/// <see cref="SecretRef"/> is the loose, unscoped reference a definition author writes into YAML — turning
/// one into the other (supplying the scope, server id, and category) is a future component's job, not this
/// type's. The two are unrelated formats, not two representations of the same thing: a <see cref="SecretRef"/>
/// never parses to, and is never parsed from, <see cref="Servyx.Domain.Secrets.SecretUrn"/>'s <c>secret://</c> syntax.
/// </remarks>
/// <param name="Scheme">The scheme segment before the colon, e.g. <c>secret</c>.</param>
/// <param name="Key">The key segment after the colon, e.g. <c>admin-password</c>.</param>
public sealed record SecretRef(string Scheme, string Key);
