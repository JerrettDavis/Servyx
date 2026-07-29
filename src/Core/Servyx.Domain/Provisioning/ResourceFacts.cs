namespace Servyx.Domain.Provisioning;

/// <summary>
/// Facts observed about a provisioned resource, as reported live by the provider.
/// </summary>
/// <param name="PublicAddress">The resource's publicly routable address, if it has one.</param>
/// <param name="PrivateAddress">The resource's private/internal network address, if it has one.</param>
/// <param name="Cost">The best available cost figure for the resource. Use <see cref="CostEstimate.Unknown"/> when none is available.</param>
/// <param name="CreatedAt">When the provider reports the resource was created.</param>
public sealed record ResourceFacts(
    string? PublicAddress,
    string? PrivateAddress,
    CostEstimate Cost,
    DateTimeOffset CreatedAt);
