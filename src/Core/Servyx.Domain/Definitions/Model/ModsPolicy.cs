namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// The parsed shape of a definition's <c>mods</c> block: whether mod management is supported for this
/// definition at all. <c>definitions/palworld-docker.yaml</c> declares <c>supported: false</c>; a definition
/// that supports mods is expected to grow this record with install/list/remove details in a later phase.
/// </summary>
/// <param name="Supported">Whether mod management is supported for this definition.</param>
public sealed record ModsPolicy(bool Supported);
